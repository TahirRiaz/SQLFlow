/*
 * YAML emission for the extracted specification.
 *
 * Hand-rolled rather than linked against libyaml, because the output shape is fixed and small
 * while the values are hostile: DAX and M expressions are multi-line, contain quotes, tabs, and
 * non-ASCII text, and a mis-quoted scalar would produce a file that looks fine and parses wrong.
 * The two emitters below are the only scalar forms used, and each states exactly what it escapes.
 */

#ifndef PBIX_YAMLOUT_H
#define PBIX_YAMLOUT_H

#include "buffer.h"

/*
 * Appends a double-quoted YAML scalar, escaping backslash, double quote, and the control
 * characters YAML requires as escapes. Safe for any single-line value, including one that looks
 * like a number, a bool, or a YAML keyword.
 */
int yaml_quoted(Buffer *out, const char *value);

/*
 * Appends a scalar for a value that may span lines: a literal block (`|-`) when the value
 * contains a newline, and a double-quoted scalar otherwise. `indent` is the number of spaces the
 * block's content is indented by, which must exceed the parent key's indentation.
 *
 * A literal block is used because it keeps DAX and M readable in the generated file, which is
 * the point of emitting a specification a person can review.
 */
int yaml_text(Buffer *out, const char *value, int indent);

/* Appends `count` spaces. */
int yaml_indent(Buffer *out, int count);

#endif /* PBIX_YAMLOUT_H */
