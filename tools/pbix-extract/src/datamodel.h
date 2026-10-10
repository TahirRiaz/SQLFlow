/*
 * Reads a .pbix's DataModel part into its decompressed Analysis Services backup (ABF) image, and
 * locates the files inside that image.
 *
 * The chain, all of which this header covers:
 *   .pbix (zip)  ->  DataModel member  ->  XPress9 block stream  ->  ABF image  ->  named files
 *
 * The ABF image's own directory is UTF-16 XML, not a binary structure, which is what makes this
 * tractable in C: the offsets come from parsed XML elements rather than from guessed struct
 * layouts.
 */

#ifndef PBIX_DATAMODEL_H
#define PBIX_DATAMODEL_H

#include <stddef.h>

#include "buffer.h"

/* One file inside the ABF image. */
typedef struct {
    char *name;          /* the file's base name, e.g. "metadata.sqlitedb" */
    size_t offset;       /* its start within the decompressed image */
    size_t size;         /* its length in the image */
    size_t size_from_log; /* its uncompressed length per the backup log */
} DataModelFile;

typedef struct {
    Buffer image;            /* the decompressed ABF image */
    DataModelFile *files;
    size_t file_count;
    int error_code;          /* the header's ErrorCode flag: trims 4 trailing bytes off slices */
    int apply_compression;   /* the header's ApplyCompression flag: inner files are XPress8 */
} DataModel;

/*
 * Opens a .pbix and produces its decompressed data model plus file directory.
 *
 * Returns 0 on success. On failure returns -1 and writes a message into `error` (a caller-provided
 * buffer of `error_size` bytes); the DataModel is left safe to pass to data_model_free.
 */
int data_model_open(const char *pbix_path, DataModel *model, char *error, size_t error_size);

/*
 * Finds a file in the image by base name and yields a pointer into the image plus its length.
 * Returns 0 on success, -1 when the name is absent or its extent falls outside the image.
 *
 * The returned pointer aliases the model's image and stays valid until data_model_free.
 */
int data_model_find(
    const DataModel *model, const char *name,
    const unsigned char **data, size_t *size,
    char *error, size_t error_size);

/* Releases everything the model owns. Safe on a zeroed or partially-built model. */
void data_model_free(DataModel *model);

/*
 * Reads one member of a .pbix (a zip) into `member`, which the caller initializes and frees.
 *
 * Shared with the report-layout reader, which needs the `Report/Layout` member the same way this
 * module needs `DataModel`. Returns 0 on success; on failure returns -1 with a message in
 * `error`. A missing member is a failure, since every caller asks for one it needs.
 */
int pbix_read_member(
    const char *pbix_path, const char *member_name, Buffer *member,
    char *error, size_t error_size);

/*
 * Lists the archive's member names matching an optional `prefix` and `suffix` (either may be NULL
 * or empty to match everything), in the archive's own order.
 *
 * Needed because a report saved in the newer split format spreads its visuals across one member per
 * visual, at paths whose middle segment is a generated id: they can only be found by enumerating
 * the archive, not by asking for a name known in advance. Returns 0 on success, including when
 * nothing matches; the caller frees with `pbix_member_names_free`.
 */
int pbix_list_members(
    const char *pbix_path, const char *prefix, const char *suffix,
    char ***names, size_t *count, char *error, size_t error_size);

/* Releases a name list from `pbix_list_members`. Safe on NULL. */
void pbix_member_names_free(char **names, size_t count);

/*
 * Converts UTF-16LE to UTF-8, returning a NUL-terminated malloc'd string the caller frees, or
 * NULL on malformed input or allocation failure. Surrogate pairs are decoded; a leading byte
 * order mark and trailing NUL padding are dropped.
 *
 * Exposed because the report layout is UTF-16LE with no byte order mark, the same encoding this
 * module's container documents use.
 */
char *pbix_utf16le_to_utf8(const unsigned char *data, size_t size);

#endif /* PBIX_DATAMODEL_H */
