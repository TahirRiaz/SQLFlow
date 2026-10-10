#include "yamlout.h"

#include <stdio.h>
#include <string.h>

int yaml_indent(Buffer *out, int count)
{
    int i;

    for (i = 0; i < count; i++) {
        if (buffer_append_str(out, " ") != 0) {
            return -1;
        }
    }
    return 0;
}

int yaml_quoted(Buffer *out, const char *value)
{
    const char *p;

    if (buffer_append_str(out, "\"") != 0) {
        return -1;
    }

    for (p = value; *p != '\0'; p++) {
        unsigned char c = (unsigned char)*p;
        int status;

        switch (c) {
        case '\\':
            status = buffer_append_str(out, "\\\\");
            break;
        case '"':
            status = buffer_append_str(out, "\\\"");
            break;
        case '\n':
            status = buffer_append_str(out, "\\n");
            break;
        case '\r':
            status = buffer_append_str(out, "\\r");
            break;
        case '\t':
            status = buffer_append_str(out, "\\t");
            break;
        default:
            if (c < 0x20 || c == 0x7F) {
                char escape[7];

                snprintf(escape, sizeof(escape), "\\x%02X", c);
                status = buffer_append_str(out, escape);
            } else {
                /* Bytes >= 0x80 are passed through: the input is already UTF-8 and YAML is a
                 * UTF-8 format, so re-escaping them would only make the output unreadable. */
                status = buffer_append(out, p, 1);
            }
            break;
        }

        if (status != 0) {
            return -1;
        }
    }

    return buffer_append_str(out, "\"");
}

int yaml_text(Buffer *out, const char *value, int indent)
{
    const char *p;
    int at_line_start = 1;

    if (strchr(value, '\n') == NULL) {
        return yaml_quoted(out, value);
    }

    /*
     * A literal block with the strip chomping indicator: the value's own trailing newline (if
     * any) is not part of the expression, and `|-` keeps the round trip faithful. Carriage
     * returns are dropped rather than emitted, since a literal block cannot represent a lone CR
     * and Power BI writes CRLF in some expressions.
     */
    if (buffer_append_str(out, "|-\n") != 0) {
        return -1;
    }

    for (p = value; *p != '\0'; p++) {
        if (*p == '\r') {
            continue;
        }

        if (at_line_start) {
            if (yaml_indent(out, indent) != 0) {
                return -1;
            }
            at_line_start = 0;
        }

        if (buffer_append(out, p, 1) != 0) {
            return -1;
        }

        if (*p == '\n') {
            at_line_start = 1;
        }
    }

    /* A block scalar must end with a newline so the next key starts on its own line. */
    if (!at_line_start && buffer_append_str(out, "\n") != 0) {
        return -1;
    }

    return 0;
}
