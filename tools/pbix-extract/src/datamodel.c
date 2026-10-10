#include "datamodel.h"

#include <expat.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <zip.h>

#include "xpress9_decode.h"

/* The DataModel stream begins with a 102-byte UTF-16LE signature naming its compression. */
#define SIGNATURE_BYTES 102

/* The ABF image's header is one 4096-byte page; its XML starts after the stream-storage marker. */
#define ABF_HEADER_PAGE 4096
#define ABF_HEADER_XML_OFFSET 72

static const char SIGNATURE_SINGLE_THREADED[] = "This backup was created using XPress9 compression.";
static const char SIGNATURE_MULTI_THREADED[] = "This backup was created using multithreaded XPrs9.";
static const char SIGNATURE_UNCOMPRESSED[] = "STREAM_STORAGE_SIGNATURE_)!@#$%^&*(";

static void set_error(char *error, size_t error_size, const char *message)
{
    if (error != NULL && error_size > 0) {
        snprintf(error, error_size, "%s", message);
    }
}

static void set_errorf(char *error, size_t error_size, const char *format, ...)
{
    va_list args;

    if (error == NULL || error_size == 0) {
        return;
    }
    va_start(args, format);
    vsnprintf(error, error_size, format, args);
    va_end(args);
}

/*
 * Converts UTF-16LE to UTF-8.
 *
 * Surrogate pairs are decoded properly rather than passed through: a report name or a file path
 * can contain an emoji or a rare CJK character, and emitting unpaired surrogates would produce
 * XML that expat rejects. Trailing NUL padding (the header page is zero-filled) is dropped.
 * Returns a NUL-terminated malloc'd string, or NULL on allocation failure or malformed input.
 */
char *pbix_utf16le_to_utf8(const unsigned char *data, size_t size)
{
    Buffer out;
    size_t i;

    buffer_init(&out);

    /* A UTF-16LE byte order mark is metadata, not content. */
    if (size >= 2 && data[0] == 0xFF && data[1] == 0xFE) {
        data += 2;
        size -= 2;
    }

    for (i = 0; i + 1 < size; i += 2) {
        unsigned int code = (unsigned int)data[i] | ((unsigned int)data[i + 1] << 8);
        unsigned char encoded[4];
        size_t length;

        /* The header page is padded with NULs after the XML; stop at the first one. */
        if (code == 0) {
            break;
        }

        if (code >= 0xD800 && code <= 0xDBFF) {
            unsigned int low;

            if (i + 3 >= size) {
                buffer_free(&out);
                return NULL;
            }
            low = (unsigned int)data[i + 2] | ((unsigned int)data[i + 3] << 8);
            if (low < 0xDC00 || low > 0xDFFF) {
                buffer_free(&out);
                return NULL;
            }
            code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
            i += 2;
        } else if (code >= 0xDC00 && code <= 0xDFFF) {
            /* A low surrogate with no preceding high surrogate is malformed. */
            buffer_free(&out);
            return NULL;
        }

        if (code < 0x80) {
            encoded[0] = (unsigned char)code;
            length = 1;
        } else if (code < 0x800) {
            encoded[0] = (unsigned char)(0xC0 | (code >> 6));
            encoded[1] = (unsigned char)(0x80 | (code & 0x3F));
            length = 2;
        } else if (code < 0x10000) {
            encoded[0] = (unsigned char)(0xE0 | (code >> 12));
            encoded[1] = (unsigned char)(0x80 | ((code >> 6) & 0x3F));
            encoded[2] = (unsigned char)(0x80 | (code & 0x3F));
            length = 3;
        } else {
            encoded[0] = (unsigned char)(0xF0 | (code >> 18));
            encoded[1] = (unsigned char)(0x80 | ((code >> 12) & 0x3F));
            encoded[2] = (unsigned char)(0x80 | ((code >> 6) & 0x3F));
            encoded[3] = (unsigned char)(0x80 | (code & 0x3F));
            length = 4;
        }

        if (buffer_append(&out, encoded, length) != 0) {
            buffer_free(&out);
            return NULL;
        }
    }

    if (buffer_append(&out, "", 1) != 0) {
        buffer_free(&out);
        return NULL;
    }

    return (char *)out.data;
}

/*
 * Copies a byte range and NUL-terminates it, for a document that is already UTF-8.
 * Trailing NUL padding is dropped the same way the UTF-16 path drops it.
 */
static char *utf8_to_cstring(const unsigned char *data, size_t size)
{
    Buffer out;
    size_t length = 0;

    while (length < size && data[length] != '\0') {
        length++;
    }

    buffer_init(&out);
    if (buffer_append(&out, data, length) != 0 || buffer_append(&out, "", 1) != 0) {
        buffer_free(&out);
        return NULL;
    }
    return (char *)out.data;
}

/*
 * Decodes one of the ABF container's XML documents to a UTF-8 C string.
 *
 * The documents inside one image do NOT share an encoding: the header page is UTF-16 while the
 * virtual directory is UTF-8. Rather than assume either, the encoding is sniffed. A UTF-16LE
 * document either starts with a byte order mark or, since every one of these begins with the
 * ASCII '<' of its root element, has a NUL as its second byte; UTF-8 never does.
 */
static char *decode_xml(const unsigned char *data, size_t size)
{
    if (size >= 2 && data[0] == 0xFF && data[1] == 0xFE) {
        return pbix_utf16le_to_utf8(data, size);
    }
    if (size >= 2 && data[0] == '<' && data[1] == '\0') {
        return pbix_utf16le_to_utf8(data, size);
    }
    return utf8_to_cstring(data, size);
}

/* ---- Minimal XML element/text collection over expat -------------------------------------- */

/*
 * The ABF directory documents are shallow and read positionally: a handful of scalar elements at
 * the top level, plus repeated BackupFile / FileGroup records. Rather than build a general DOM,
 * the parser below records a path-addressed list of (element path, text) pairs, which the three
 * document readers then query. This keeps one small parser for all three documents.
 */

typedef struct {
    char *path;  /* slash-joined element path, e.g. "FileGroups/FileGroup/FileList/BackupFile/Path" */
    char *text;
} XmlNode;

typedef struct {
    XmlNode *nodes;
    size_t count;
    size_t capacity;
    Buffer path;   /* the current element path */
    Buffer text;   /* the current element's accumulated character data */
    int failed;
} XmlCollector;

static void xml_collector_init(XmlCollector *collector)
{
    collector->nodes = NULL;
    collector->count = 0;
    collector->capacity = 0;
    collector->failed = 0;
    buffer_init(&collector->path);
    buffer_init(&collector->text);
}

static void xml_collector_free(XmlCollector *collector)
{
    size_t i;

    for (i = 0; i < collector->count; i++) {
        free(collector->nodes[i].path);
        free(collector->nodes[i].text);
    }
    free(collector->nodes);
    collector->nodes = NULL;
    collector->count = 0;
    collector->capacity = 0;
    buffer_free(&collector->path);
    buffer_free(&collector->text);
}

static int xml_collector_push(XmlCollector *collector, const char *path, const char *text)
{
    if (collector->count == collector->capacity) {
        size_t capacity = collector->capacity == 0 ? 64 : collector->capacity * 2;
        XmlNode *grown = (XmlNode *)realloc(collector->nodes, capacity * sizeof(*grown));

        if (grown == NULL) {
            return -1;
        }
        collector->nodes = grown;
        collector->capacity = capacity;
    }

    collector->nodes[collector->count].path = strdup(path);
    collector->nodes[collector->count].text = strdup(text);
    if (collector->nodes[collector->count].path == NULL
        || collector->nodes[collector->count].text == NULL) {
        free(collector->nodes[collector->count].path);
        free(collector->nodes[collector->count].text);
        return -1;
    }
    collector->count++;
    return 0;
}

static void XMLCALL xml_start_element(void *user_data, const XML_Char *name, const XML_Char **attrs)
{
    XmlCollector *collector = (XmlCollector *)user_data;

    (void)attrs;
    if (collector->failed) {
        return;
    }

    if (collector->path.size > 0 && buffer_append_str(&collector->path, "/") != 0) {
        collector->failed = 1;
        return;
    }
    if (buffer_append_str(&collector->path, name) != 0) {
        collector->failed = 1;
        return;
    }

    collector->text.size = 0;
}

static void XMLCALL xml_char_data(void *user_data, const XML_Char *text, int length)
{
    XmlCollector *collector = (XmlCollector *)user_data;

    if (collector->failed) {
        return;
    }
    if (buffer_append(&collector->text, text, (size_t)length) != 0) {
        collector->failed = 1;
    }
}

static void XMLCALL xml_end_element(void *user_data, const XML_Char *name)
{
    XmlCollector *collector = (XmlCollector *)user_data;
    size_t name_length;
    char *path;

    if (collector->failed) {
        return;
    }

    /* Record this element's text against its full path before popping it. */
    if (buffer_append(&collector->text, "", 1) != 0) {
        collector->failed = 1;
        return;
    }
    if (buffer_append(&collector->path, "", 1) != 0) {
        collector->failed = 1;
        return;
    }
    path = (char *)collector->path.data;
    collector->path.size--; /* drop the terminator again */

    if (xml_collector_push(collector, path, (const char *)collector->text.data) != 0) {
        collector->failed = 1;
        return;
    }
    collector->text.size = 0;

    /* Pop this element (and the separator that preceded it, when it was not the root). */
    name_length = strlen(name);
    if (collector->path.size >= name_length) {
        collector->path.size -= name_length;
        if (collector->path.size > 0 && collector->path.data[collector->path.size - 1] == '/') {
            collector->path.size--;
        }
    }
}

static int xml_parse(const char *utf8, XmlCollector *collector, char *error, size_t error_size)
{
    XML_Parser parser;
    enum XML_Status status;

    parser = XML_ParserCreate("UTF-8");
    if (parser == NULL) {
        set_error(error, error_size, "the XML parser could not be created");
        return -1;
    }

    XML_SetUserData(parser, collector);
    XML_SetElementHandler(parser, xml_start_element, xml_end_element);
    XML_SetCharacterDataHandler(parser, xml_char_data);

    status = XML_Parse(parser, utf8, (int)strlen(utf8), 1);
    if (status != XML_STATUS_OK) {
        set_errorf(error, error_size,
            "the data model's XML directory is malformed at line %lu: %s",
            (unsigned long)XML_GetCurrentLineNumber(parser),
            XML_ErrorString(XML_GetErrorCode(parser)));
        XML_ParserFree(parser);
        return -1;
    }

    XML_ParserFree(parser);

    if (collector->failed) {
        set_error(error, error_size, "out of memory reading the data model's XML directory");
        return -1;
    }

    return 0;
}

/* Returns the text of the nth (0-based) node at `path`, or NULL when absent. */
static const char *xml_nth(const XmlCollector *collector, const char *path, size_t index)
{
    size_t i;
    size_t seen = 0;

    for (i = 0; i < collector->count; i++) {
        if (strcmp(collector->nodes[i].path, path) == 0) {
            if (seen == index) {
                return collector->nodes[i].text;
            }
            seen++;
        }
    }
    return NULL;
}

static const char *xml_first(const XmlCollector *collector, const char *path)
{
    return xml_nth(collector, path, 0);
}

static size_t xml_count(const XmlCollector *collector, const char *path)
{
    size_t i;
    size_t count = 0;

    for (i = 0; i < collector->count; i++) {
        if (strcmp(collector->nodes[i].path, path) == 0) {
            count++;
        }
    }
    return count;
}

/* Parses an unsigned decimal from XML text. Returns -1 when absent or not a number. */
static long long xml_number(const XmlCollector *collector, const char *path)
{
    const char *text = xml_first(collector, path);
    char *end;
    long long value;

    if (text == NULL || *text == '\0') {
        return -1;
    }
    value = strtoll(text, &end, 10);
    if (*end != '\0' || value < 0) {
        return -1;
    }
    return value;
}

/* ---- The DataModel member: zip extraction and XPress9 decompression ---------------------- */

int pbix_read_member(
    const char *pbix_path, const char *member_name, Buffer *member,
    char *error, size_t error_size)
{
    int zip_error = 0;
    zip_t *archive;
    zip_file_t *file;
    zip_stat_t stat;
    zip_int64_t remaining;

    archive = zip_open(pbix_path, ZIP_RDONLY, &zip_error);
    if (archive == NULL) {
        zip_error_t details;

        zip_error_init_with_code(&details, zip_error);
        set_errorf(error, error_size,
            "'%s' could not be opened as a .pbix (zip) file: %s",
            pbix_path, zip_error_strerror(&details));
        zip_error_fini(&details);
        return -1;
    }

    if (zip_stat(archive, member_name, 0, &stat) != 0) {
        /* The explanation is specific to the part asked for: only a missing DataModel means a live
         * connection to a published dataset. Attaching that reason to any other absent part (the
         * report layout, say) sends a reader looking for the wrong problem. */
        if (strcmp(member_name, "DataModel") == 0) {
            set_errorf(error, error_size,
                "'%s' contains no 'DataModel' part. A report with a live connection to a published "
                "dataset keeps its model on the server rather than in the file.",
                pbix_path);
        } else {
            set_errorf(error, error_size, "'%s' contains no '%s' part.", pbix_path, member_name);
        }
        zip_close(archive);
        return -1;
    }

    file = zip_fopen(archive, member_name, 0);
    if (file == NULL) {
        set_errorf(error, error_size,
            "the '%s' part of '%s' could not be read: %s",
            member_name, pbix_path, zip_strerror(archive));
        zip_close(archive);
        return -1;
    }

    remaining = (stat.valid & ZIP_STAT_SIZE) ? (zip_int64_t)stat.size : -1;
    if (remaining < 0) {
        set_errorf(error, error_size, "the '%s' part does not declare its size", member_name);
        zip_fclose(file);
        zip_close(archive);
        return -1;
    }

    if (buffer_reserve(member, (size_t)remaining) != 0) {
        set_errorf(error, error_size, "out of memory reading the '%s' part", member_name);
        zip_fclose(file);
        zip_close(archive);
        return -1;
    }

    while (remaining > 0) {
        zip_int64_t chunk = zip_fread(file, member->data + member->size, (zip_uint64_t)remaining);

        if (chunk < 0) {
            set_errorf(error, error_size,
                "the '%s' part of '%s' ended unexpectedly: %s",
                member_name, pbix_path, zip_file_strerror(file));
            zip_fclose(file);
            zip_close(archive);
            return -1;
        }
        if (chunk == 0) {
            break;
        }
        member->size += (size_t)chunk;
        remaining -= chunk;
    }

    zip_fclose(file);
    zip_close(archive);

    if (remaining != 0) {
        set_errorf(error, error_size,
            "the '%s' part was shorter than its declared size", member_name);
        return -1;
    }

    return 0;
}

void pbix_member_names_free(char **names, size_t count)
{
    size_t i;

    for (i = 0; i < count; i++) {
        free(names[i]);
    }
    free(names);
}

int pbix_list_members(
    const char *pbix_path, const char *prefix, const char *suffix,
    char ***names, size_t *count, char *error, size_t error_size)
{
    int zip_error = 0;
    zip_t *archive;
    zip_int64_t entry_count;
    zip_int64_t index;
    size_t prefix_length = prefix != NULL ? strlen(prefix) : 0;
    size_t suffix_length = suffix != NULL ? strlen(suffix) : 0;

    *names = NULL;
    *count = 0;

    archive = zip_open(pbix_path, ZIP_RDONLY, &zip_error);
    if (archive == NULL) {
        zip_error_t details;

        zip_error_init_with_code(&details, zip_error);
        set_errorf(error, error_size,
            "'%s' could not be opened as a .pbix (zip) file: %s",
            pbix_path, zip_error_strerror(&details));
        zip_error_fini(&details);
        return -1;
    }

    entry_count = zip_get_num_entries(archive, 0);
    for (index = 0; index < entry_count; index++) {
        const char *name = zip_get_name(archive, (zip_uint64_t)index, 0);
        size_t name_length;
        char **grown;

        if (name == NULL) {
            continue;
        }
        name_length = strlen(name);

        if (prefix_length > 0 && strncmp(name, prefix, prefix_length) != 0) {
            continue;
        }
        if (suffix_length > 0
            && (name_length < suffix_length
                || strcmp(name + name_length - suffix_length, suffix) != 0)) {
            continue;
        }

        grown = (char **)realloc(*names, (*count + 1) * sizeof(*grown));
        if (grown == NULL) {
            set_error(error, error_size, "out of memory listing the archive's members");
            pbix_member_names_free(*names, *count);
            *names = NULL;
            *count = 0;
            zip_close(archive);
            return -1;
        }
        *names = grown;
        (*names)[*count] = strdup(name);
        if ((*names)[*count] == NULL) {
            set_error(error, error_size, "out of memory listing the archive's members");
            pbix_member_names_free(*names, *count);
            *names = NULL;
            *count = 0;
            zip_close(archive);
            return -1;
        }
        (*count)++;
    }

    zip_close(archive);
    return 0;
}

/* Identifies the stream's compression from its leading signature. */
typedef enum {
    COMPRESSION_UNCOMPRESSED,
    COMPRESSION_SINGLE_THREADED,
    COMPRESSION_MULTI_THREADED,
    COMPRESSION_UNKNOWN
} Compression;

static Compression detect_compression(const Buffer *member)
{
    char *signature;
    Compression result = COMPRESSION_UNKNOWN;

    if (member->size >= ABF_HEADER_XML_OFFSET) {
        /* An uncompressed image starts with the stream-storage marker, in UTF-16. */
        char *marker = pbix_utf16le_to_utf8(member->data, ABF_HEADER_XML_OFFSET);

        if (marker != NULL) {
            if (strstr(marker, SIGNATURE_UNCOMPRESSED) != NULL) {
                free(marker);
                return COMPRESSION_UNCOMPRESSED;
            }
            free(marker);
        }
    }

    if (member->size < SIGNATURE_BYTES) {
        return COMPRESSION_UNKNOWN;
    }

    signature = pbix_utf16le_to_utf8(member->data, SIGNATURE_BYTES);
    if (signature == NULL) {
        return COMPRESSION_UNKNOWN;
    }

    if (strstr(signature, SIGNATURE_SINGLE_THREADED) != NULL) {
        result = COMPRESSION_SINGLE_THREADED;
    } else if (strstr(signature, SIGNATURE_MULTI_THREADED) != NULL) {
        result = COMPRESSION_MULTI_THREADED;
    }

    free(signature);
    return result;
}

static unsigned int read_u32le(const unsigned char *bytes)
{
    return (unsigned int)bytes[0]
        | ((unsigned int)bytes[1] << 8)
        | ((unsigned int)bytes[2] << 16)
        | ((unsigned int)bytes[3] << 24);
}

static unsigned long long read_u64le(const unsigned char *bytes)
{
    unsigned long long value = 0;
    int i;

    for (i = 7; i >= 0; i--) {
        value = (value << 8) | (unsigned long long)bytes[i];
    }
    return value;
}

/*
 * Decompresses a run of consecutive XPress9 blocks starting at *cursor.
 *
 * Each block is framed as a little-endian uint32 uncompressed size, a uint32 compressed size, and
 * then that many compressed bytes. `block_count` of 0 means "until the member is exhausted", which
 * is how the single-threaded layout is written; the multi-threaded layout states its counts up
 * front instead.
 */
static int decompress_blocks(
    Xpress9Decode *decoder, const Buffer *member, size_t *cursor, size_t block_count,
    Buffer *image, char *error, size_t error_size)
{
    size_t decoded_blocks = 0;

    while (block_count == 0 ? (*cursor < member->size) : (decoded_blocks < block_count)) {
        unsigned int uncompressed_size;
        unsigned int compressed_size;
        const char *decode_error = NULL;
        size_t written;

        if (*cursor + 8 > member->size) {
            set_error(error, error_size,
                "the data model stream ends mid-block header; the file is truncated");
            return -1;
        }

        uncompressed_size = read_u32le(member->data + *cursor);
        compressed_size = read_u32le(member->data + *cursor + 4);
        *cursor += 8;

        if (compressed_size == 0 || uncompressed_size == 0) {
            set_error(error, error_size, "the data model stream declares a zero-length block");
            return -1;
        }
        if (*cursor + compressed_size > member->size) {
            set_error(error, error_size,
                "a data model block claims more bytes than the stream holds; the file is truncated");
            return -1;
        }

        if (buffer_reserve(image, uncompressed_size) != 0) {
            set_error(error, error_size, "out of memory decompressing the data model");
            return -1;
        }

        written = xpress9_decode_block(
            decoder, member->data + *cursor, compressed_size,
            image->data + image->size, uncompressed_size, &decode_error);
        if (written == 0) {
            set_errorf(error, error_size, "the data model could not be decompressed: %s",
                decode_error != NULL ? decode_error : "unknown decoder failure");
            return -1;
        }
        if (written != uncompressed_size) {
            /* The block header states the exact expected size; a mismatch means the decoder and
             * the framing disagree, and every later ABF offset would be wrong. */
            set_errorf(error, error_size,
                "a data model block decompressed to %zu bytes but declared %u; refusing to "
                "continue with a corrupt image",
                written, uncompressed_size);
            return -1;
        }

        image->size += written;
        *cursor += compressed_size;
        decoded_blocks++;
    }

    return 0;
}

static int decompress_member(
    const Buffer *member, Buffer *image, char *error, size_t error_size)
{
    Compression compression = detect_compression(member);
    Xpress9Decode *decoder;
    const char *create_error = NULL;
    size_t cursor;
    int status;

    if (compression == COMPRESSION_UNCOMPRESSED) {
        /* The member already IS the ABF image. */
        if (buffer_append(image, member->data, member->size) != 0) {
            set_error(error, error_size, "out of memory copying the data model");
            return -1;
        }
        return 0;
    }

    if (compression == COMPRESSION_UNKNOWN) {
        set_error(error, error_size,
            "the data model uses an unrecognized compression format. Only XPress9 (single- and "
            "multi-threaded) and uncompressed data models are supported.");
        return -1;
    }

    decoder = xpress9_decode_create(&create_error);
    if (decoder == NULL) {
        set_errorf(error, error_size, "the XPress9 decoder could not be started: %s",
            create_error != NULL ? create_error : "unknown failure");
        return -1;
    }

    cursor = SIGNATURE_BYTES;

    if (compression == COMPRESSION_SINGLE_THREADED) {
        status = decompress_blocks(decoder, member, &cursor, 0, image, error, error_size);
    } else {
        /*
         * The multi-threaded layout states its geometry in five little-endian uint64 counts, then
         * lays the prefix thread groups down before the main thread groups. Each group is an
         * independent run of blocks, and output order is simply stream order, so the groups are
         * decoded in sequence. Decoding them concurrently is what the upstream Python does for
         * speed; sequential decoding produces the identical image.
         */
        unsigned long long main_chunks_per_thread;
        unsigned long long prefix_chunks_per_thread;
        unsigned long long prefix_thread_count;
        unsigned long long main_thread_count;
        unsigned long long group;

        if (cursor + 40 > member->size) {
            set_error(error, error_size,
                "the multi-threaded data model header is truncated");
            xpress9_decode_destroy(decoder);
            return -1;
        }

        main_chunks_per_thread = read_u64le(member->data + cursor);
        prefix_chunks_per_thread = read_u64le(member->data + cursor + 8);
        prefix_thread_count = read_u64le(member->data + cursor + 16);
        main_thread_count = read_u64le(member->data + cursor + 24);
        /* The fifth count is the per-chunk uncompressed size, which each block header repeats. */
        cursor += 40;

        status = 0;
        for (group = 0; status == 0 && group < prefix_thread_count; group++) {
            status = decompress_blocks(
                decoder, member, &cursor, (size_t)prefix_chunks_per_thread,
                image, error, error_size);
        }
        for (group = 0; status == 0 && group < main_thread_count; group++) {
            status = decompress_blocks(
                decoder, member, &cursor, (size_t)main_chunks_per_thread,
                image, error, error_size);
        }
    }

    xpress9_decode_destroy(decoder);
    return status;
}

/* ---- The ABF image: header, virtual directory, backup log ------------------------------- */

/* One entry of the virtual directory: where a storage path's bytes live in the image. */
typedef struct {
    char *path;
    size_t offset;
    size_t size;
} VirtualFile;

static void free_virtual_files(VirtualFile *files, size_t count)
{
    size_t i;

    for (i = 0; i < count; i++) {
        free(files[i].path);
    }
    free(files);
}

/* Returns the base name of a backslash-separated ABF path. */
static const char *abf_base_name(const char *path)
{
    const char *slash = strrchr(path, '\\');

    return slash != NULL ? slash + 1 : path;
}

static int parse_image_directory(DataModel *model, char *error, size_t error_size)
{
    XmlCollector header;
    XmlCollector directory;
    XmlCollector log;
    char *header_xml = NULL;
    char *directory_xml = NULL;
    char *log_xml = NULL;
    VirtualFile *virtual_files = NULL;
    size_t virtual_count = 0;
    long long directory_offset;
    long long directory_size;
    long long log_offset;
    long long log_size;
    size_t i;
    size_t group_index;
    int status = -1;

    xml_collector_init(&header);
    xml_collector_init(&directory);
    xml_collector_init(&log);

    if (model->image.size < ABF_HEADER_PAGE) {
        set_error(error, error_size,
            "the decompressed data model is smaller than its own header page; it is not a "
            "recognizable Analysis Services backup");
        goto done;
    }

    /*
     * 1. The header page: fixed offset, one page long, zero-padded after its XML. Its root
     * element is named BackupLog, the same name the separate backup-log document uses, which is
     * why the paths below are rooted explicitly rather than matched by leaf name.
     */
    header_xml = decode_xml(
        model->image.data + ABF_HEADER_XML_OFFSET, ABF_HEADER_PAGE - ABF_HEADER_XML_OFFSET);
    if (header_xml == NULL) {
        set_error(error, error_size, "the data model's header page is not valid text");
        goto done;
    }
    if (xml_parse(header_xml, &header, error, error_size) != 0) {
        goto done;
    }

    model->error_code = (xml_first(&header, "BackupLog/ErrorCode") != NULL
        && strcmp(xml_first(&header, "BackupLog/ErrorCode"), "true") == 0);
    model->apply_compression = (xml_first(&header, "BackupLog/ApplyCompression") != NULL
        && strcmp(xml_first(&header, "BackupLog/ApplyCompression"), "true") == 0);

    directory_offset = xml_number(&header, "BackupLog/m_cbOffsetHeader");
    directory_size = xml_number(&header, "BackupLog/DataSize");
    if (directory_offset < 0 || directory_size <= 0) {
        set_error(error, error_size,
            "the data model's header page does not state where its file directory lives");
        goto done;
    }
    if ((size_t)directory_offset + (size_t)directory_size > model->image.size) {
        set_error(error, error_size,
            "the data model's file directory extends past the end of the image");
        goto done;
    }

    /* 2. The virtual directory: every stored file's offset and size. */
    directory_xml = decode_xml(
        model->image.data + directory_offset, (size_t)directory_size);
    if (directory_xml == NULL) {
        set_error(error, error_size, "the data model's file directory is not valid text");
        goto done;
    }
    if (xml_parse(directory_xml, &directory, error, error_size) != 0) {
        goto done;
    }

    virtual_count = xml_count(&directory, "VirtualDirectory/BackupFile/Path");
    if (virtual_count == 0) {
        set_error(error, error_size, "the data model's file directory lists no files");
        goto done;
    }

    virtual_files = (VirtualFile *)calloc(virtual_count, sizeof(*virtual_files));
    if (virtual_files == NULL) {
        set_error(error, error_size, "out of memory reading the data model's file directory");
        goto done;
    }

    for (i = 0; i < virtual_count; i++) {
        const char *path = xml_nth(&directory, "VirtualDirectory/BackupFile/Path", i);
        const char *size_text = xml_nth(&directory, "VirtualDirectory/BackupFile/Size", i);
        const char *offset_text =
            xml_nth(&directory, "VirtualDirectory/BackupFile/m_cbOffsetHeader", i);

        if (path == NULL || size_text == NULL || offset_text == NULL) {
            set_error(error, error_size,
                "an entry in the data model's file directory is missing its path, size, or offset");
            goto done;
        }

        virtual_files[i].path = strdup(path);
        if (virtual_files[i].path == NULL) {
            set_error(error, error_size, "out of memory reading the data model's file directory");
            goto done;
        }
        virtual_files[i].size = (size_t)strtoull(size_text, NULL, 10);
        virtual_files[i].offset = (size_t)strtoull(offset_text, NULL, 10);
    }

    /* 3. The backup log, named by the directory's LAST entry, maps storage paths to real names. */
    log_offset = (long long)virtual_files[virtual_count - 1].offset;
    log_size = (long long)virtual_files[virtual_count - 1].size;
    if (log_size <= 0 || (size_t)log_offset + (size_t)log_size > model->image.size) {
        set_error(error, error_size, "the data model's backup log lies outside the image");
        goto done;
    }

    /* With the header's ErrorCode flag set, the log carries 4 trailing bytes that are not XML. */
    if (model->error_code && log_size >= 4) {
        log_size -= 4;
    }

    log_xml = decode_xml(model->image.data + log_offset, (size_t)log_size);
    if (log_xml == NULL) {
        set_error(error, error_size, "the data model's backup log is not valid text");
        goto done;
    }
    if (xml_parse(log_xml, &log, error, error_size) != 0) {
        goto done;
    }

    /*
     * 4. Join the two: the backup log names each file and points at a storage path, and the
     * virtual directory says where that storage path's bytes are. Files appear under repeated
     * FileGroup/FileList/BackupFile elements, and the flat path-addressed node list preserves
     * their document order, so index i of Path lines up with index i of StoragePath.
     */
    group_index = xml_count(&log, "BackupLog/FileGroups/FileGroup/FileList/BackupFile/Path");
    if (group_index == 0) {
        set_error(error, error_size, "the data model's backup log lists no files");
        goto done;
    }

    model->files = (DataModelFile *)calloc(group_index, sizeof(*model->files));
    if (model->files == NULL) {
        set_error(error, error_size, "out of memory reading the data model's backup log");
        goto done;
    }

    for (i = 0; i < group_index; i++) {
        const char *path =
            xml_nth(&log, "BackupLog/FileGroups/FileGroup/FileList/BackupFile/Path", i);
        const char *storage_path =
            xml_nth(&log, "BackupLog/FileGroups/FileGroup/FileList/BackupFile/StoragePath", i);
        const char *size_text =
            xml_nth(&log, "BackupLog/FileGroups/FileGroup/FileList/BackupFile/Size", i);
        size_t j;

        if (path == NULL || storage_path == NULL) {
            continue;
        }

        for (j = 0; j < virtual_count; j++) {
            if (strcmp(virtual_files[j].path, storage_path) == 0) {
                DataModelFile *entry = &model->files[model->file_count];

                entry->name = strdup(abf_base_name(path));
                if (entry->name == NULL) {
                    set_error(error, error_size,
                        "out of memory reading the data model's backup log");
                    goto done;
                }
                entry->offset = virtual_files[j].offset;
                entry->size = virtual_files[j].size;
                entry->size_from_log =
                    size_text != NULL ? (size_t)strtoull(size_text, NULL, 10) : virtual_files[j].size;
                model->file_count++;
                break;
            }
        }
    }

    if (model->file_count == 0) {
        set_error(error, error_size,
            "none of the data model's logged files could be located in its file directory");
        goto done;
    }

    status = 0;

done:
    free(header_xml);
    free(directory_xml);
    free(log_xml);
    free_virtual_files(virtual_files, virtual_count);
    xml_collector_free(&header);
    xml_collector_free(&directory);
    xml_collector_free(&log);
    return status;
}

/* ---- Public interface -------------------------------------------------------------------- */

int data_model_open(const char *pbix_path, DataModel *model, char *error, size_t error_size)
{
    Buffer member;

    memset(model, 0, sizeof(*model));
    buffer_init(&model->image);
    buffer_init(&member);

    if (pbix_read_member(pbix_path, "DataModel", &member, error, error_size) != 0) {
        buffer_free(&member);
        return -1;
    }

    if (decompress_member(&member, &model->image, error, error_size) != 0) {
        buffer_free(&member);
        return -1;
    }

    buffer_free(&member);

    if (parse_image_directory(model, error, error_size) != 0) {
        return -1;
    }

    return 0;
}

int data_model_find(
    const DataModel *model, const char *name,
    const unsigned char **data, size_t *size,
    char *error, size_t error_size)
{
    size_t i;

    for (i = 0; i < model->file_count; i++) {
        if (strcmp(model->files[i].name, name) == 0) {
            size_t length = model->files[i].size;

            /* With ErrorCode set, every slice carries 4 trailing bytes that are not content. */
            if (model->error_code && length >= 4) {
                length -= 4;
            }

            if (model->files[i].offset + length > model->image.size) {
                set_errorf(error, error_size,
                    "'%s' lies outside the decompressed data model", name);
                return -1;
            }

            *data = model->image.data + model->files[i].offset;
            *size = length;
            return 0;
        }
    }

    set_errorf(error, error_size, "the data model contains no '%s'", name);
    return -1;
}

void data_model_free(DataModel *model)
{
    size_t i;

    for (i = 0; i < model->file_count; i++) {
        free(model->files[i].name);
    }
    free(model->files);
    model->files = NULL;
    model->file_count = 0;
    buffer_free(&model->image);
}
