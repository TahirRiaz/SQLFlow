/* A growable byte buffer, used for the decompressed data model image and for building text. */

#ifndef PBIX_BUFFER_H
#define PBIX_BUFFER_H

#include <stddef.h>

typedef struct {
    unsigned char *data;
    size_t size;
    size_t capacity;
} Buffer;

/* Zero-initializes a buffer. Does not allocate. */
void buffer_init(Buffer *buffer);

/*
 * Ensures room for at least `additional` further bytes, growing geometrically.
 * Returns 0 on success, -1 if the allocation failed (the buffer is left usable and unchanged).
 */
int buffer_reserve(Buffer *buffer, size_t additional);

/* Appends bytes. Returns 0 on success, -1 on allocation failure. */
int buffer_append(Buffer *buffer, const void *bytes, size_t count);

/* Appends a NUL-terminated string (without its terminator). */
int buffer_append_str(Buffer *buffer, const char *text);

/* Frees the buffer's storage and re-zeroes it. Safe on an already-freed buffer. */
void buffer_free(Buffer *buffer);

#endif /* PBIX_BUFFER_H */
