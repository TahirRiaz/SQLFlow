#include "xpress9_decode.h"

#include <stdlib.h>
#include <string.h>

/* xpress.h defines XPRESS_CALL and the allocator callback types xpress9.h's API refers to; the
 * upstream wrapper includes the pair in this order. */
#include "xpress.h"
#include "xpress9.h"

struct Xpress9Decode {
    XPRESS9_DECODER decoder;
};

static void *XPRESS_CALL decode_alloc(void *context, int size)
{
    (void)context;
    return malloc((size_t)size);
}

static void XPRESS_CALL decode_free(void *context, void *address)
{
    (void)context;
    free(address);
}

Xpress9Decode *xpress9_decode_create(const char **error)
{
    XPRESS9_STATUS status;
    Xpress9Decode *wrapper;

    memset(&status, 0, sizeof(status));

    wrapper = (Xpress9Decode *)calloc(1, sizeof(*wrapper));
    if (wrapper == NULL) {
        if (error != NULL) {
            *error = "out of memory allocating the XPress9 decoder wrapper";
        }
        return NULL;
    }

    wrapper->decoder = Xpress9DecoderCreate(
        &status, NULL, decode_alloc, XPRESS9_WINDOW_SIZE_LOG2_MAX, 0);
    if (wrapper->decoder == NULL || status.m_uStatus != Xpress9Status_OK) {
        if (error != NULL) {
            *error = "the XPress9 decoder could not be created";
        }
        free(wrapper);
        return NULL;
    }

    /* One session spans every block of the stream: Power BI wrote the blocks as one XPress9
     * stream, so the decoder's history window must persist across them. Starting a session per
     * block would decode the first block and then produce garbage. */
    Xpress9DecoderStartSession(&status, wrapper->decoder, 1);
    if (status.m_uStatus != Xpress9Status_OK) {
        if (error != NULL) {
            *error = "the XPress9 decoder session could not be started";
        }
        Xpress9DecoderDestroy(&status, wrapper->decoder, NULL, decode_free);
        free(wrapper);
        return NULL;
    }

    return wrapper;
}

size_t xpress9_decode_block(
    Xpress9Decode *decoder,
    const unsigned char *compressed,
    size_t compressed_size,
    unsigned char *out,
    size_t out_capacity,
    const char **error)
{
    XPRESS9_STATUS status;
    size_t written = 0;
    unsigned int remaining;

    memset(&status, 0, sizeof(status));

    Xpress9DecoderAttach(&status, decoder->decoder, compressed, (unsigned int)compressed_size);
    if (status.m_uStatus != Xpress9Status_OK) {
        if (error != NULL) {
            *error = "the compressed block could not be attached to the XPress9 decoder";
        }
        return 0;
    }

    /* The decoder hands back its output in installments, so one attached block is drained until
     * it reports nothing remaining. */
    do {
        unsigned int bytes_written = 0;
        unsigned int bytes_consumed = 0;

        remaining = Xpress9DecoderFetchDecompressedData(
            &status, decoder->decoder,
            out + written, (unsigned int)(out_capacity - written),
            &bytes_written, &bytes_consumed);

        if (status.m_uStatus != Xpress9Status_OK) {
            if (error != NULL) {
                *error = "the XPress9 decoder rejected the block as malformed";
            }
            Xpress9DecoderDetach(&status, decoder->decoder, compressed, (unsigned int)compressed_size);
            return 0;
        }

        if (bytes_written == 0) {
            break;
        }

        written += bytes_written;
        if (written > out_capacity) {
            /* Cannot happen while the caller sizes the buffer from the block's declared
             * uncompressed size, and is checked because the alternative to detecting it is a
             * heap overflow rather than an error. */
            if (error != NULL) {
                *error = "the decompressed block exceeded the declared uncompressed size";
            }
            Xpress9DecoderDetach(&status, decoder->decoder, compressed, (unsigned int)compressed_size);
            return 0;
        }
    } while (remaining != 0);

    Xpress9DecoderDetach(&status, decoder->decoder, compressed, (unsigned int)compressed_size);
    if (status.m_uStatus != Xpress9Status_OK) {
        if (error != NULL) {
            *error = "the XPress9 decoder could not be detached after the block";
        }
        return 0;
    }

    return written;
}

void xpress9_decode_destroy(Xpress9Decode *decoder)
{
    XPRESS9_STATUS status;

    if (decoder == NULL) {
        return;
    }

    memset(&status, 0, sizeof(status));
    if (decoder->decoder != NULL) {
        Xpress9DecoderDestroy(&status, decoder->decoder, NULL, decode_free);
    }
    free(decoder);
}
