/*
 * Decode-only front end for the vendored XPress9 reference decoder.
 *
 * The upstream xpress9-python wrapper exposes both Compress and Decompress, but a .pbix is only
 * ever READ here, so the encoder translation units (Xpress9EncLz77.c, Xpress9EncHuffman.c: ~127 KB
 * of the ~235 KB total) are deliberately not vendored. That means the upstream wrapper cannot be
 * used as-is: its Compress path references Xpress9Encoder* symbols that would not link. This is
 * the decode half of that wrapper, and nothing else.
 */

#ifndef PBIX_XPRESS9_DECODE_H
#define PBIX_XPRESS9_DECODE_H

#include <stddef.h>

/* Opaque decoder session. One instance decodes any number of consecutive blocks. */
typedef struct Xpress9Decode Xpress9Decode;

/*
 * Creates a decoder with the maximum window size, matching how Power BI wrote the stream.
 * Returns NULL on allocation or decoder-creation failure, with a message in *error when error is
 * non-NULL (a pointer to a static string; never freed by the caller).
 */
Xpress9Decode *xpress9_decode_create(const char **error);

/*
 * Decompresses ONE block. XPress9 is a block codec: the caller reads the framing (each block's
 * uncompressed and compressed sizes) and hands over one compressed block at a time, along with an
 * output buffer large enough for that block's declared uncompressed size.
 *
 * Returns the number of bytes written, or 0 on failure with *error set. A short read is a failure,
 * not a partial success: a truncated block means the rest of the stream cannot be trusted either,
 * and silently returning fewer bytes would corrupt every offset the ABF container computes from
 * the decompressed image.
 */
size_t xpress9_decode_block(
    Xpress9Decode *decoder,
    const unsigned char *compressed,
    size_t compressed_size,
    unsigned char *out,
    size_t out_capacity,
    const char **error);

/* Releases the decoder. Safe to call with NULL. */
void xpress9_decode_destroy(Xpress9Decode *decoder);

#endif /* PBIX_XPRESS9_DECODE_H */
