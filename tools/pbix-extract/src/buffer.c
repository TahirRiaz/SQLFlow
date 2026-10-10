#include "buffer.h"

#include <stdlib.h>
#include <string.h>

void buffer_init(Buffer *buffer)
{
    buffer->data = NULL;
    buffer->size = 0;
    buffer->capacity = 0;
}

int buffer_reserve(Buffer *buffer, size_t additional)
{
    size_t needed;
    size_t capacity;
    unsigned char *grown;

    if (additional == 0) {
        return 0;
    }

    /* A .pbix data model reaches tens of megabytes, so the overflow check is a real bound
     * rather than a formality on 32-bit builds. */
    if (additional > (size_t)-1 - buffer->size) {
        return -1;
    }

    needed = buffer->size + additional;
    if (needed <= buffer->capacity) {
        return 0;
    }

    capacity = buffer->capacity == 0 ? 64 * 1024 : buffer->capacity;
    while (capacity < needed) {
        if (capacity > ((size_t)-1) / 2) {
            capacity = needed;
            break;
        }
        capacity *= 2;
    }

    grown = (unsigned char *)realloc(buffer->data, capacity);
    if (grown == NULL) {
        return -1;
    }

    buffer->data = grown;
    buffer->capacity = capacity;
    return 0;
}

int buffer_append(Buffer *buffer, const void *bytes, size_t count)
{
    if (count == 0) {
        return 0;
    }
    if (buffer_reserve(buffer, count) != 0) {
        return -1;
    }
    memcpy(buffer->data + buffer->size, bytes, count);
    buffer->size += count;
    return 0;
}

int buffer_append_str(Buffer *buffer, const char *text)
{
    return buffer_append(buffer, text, strlen(text));
}

void buffer_free(Buffer *buffer)
{
    free(buffer->data);
    buffer_init(buffer);
}
