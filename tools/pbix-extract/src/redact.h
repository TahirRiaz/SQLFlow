/*
 * Removes the report author's local filesystem paths and credentials from Power Query (M) source
 * expressions.
 *
 * A .pbix records the exact location its author loaded data from, which routinely means a personal
 * path: "C:\Users\<name>\OneDrive - <employer>\...". The extracted specification is a text file
 * meant to be committed, diffed, and read by a language model, so carrying that verbatim would
 * publish someone's username, employer, and folder layout into a repository, for no benefit: what
 * a SQL author needs from an M expression is the SHAPE of the source (an Excel workbook, a SQL
 * database, a web endpoint) and the transform steps applied to it, never the author's home
 * directory.
 *
 * What is redacted is deliberately narrow: the first STRING LITERAL argument of the M functions
 * that name a location. A function whose first argument is another call is left alone, because the
 * path it eventually resolves to belongs to the inner call, and that inner call is redacted on its
 * own terms. So in
 *
 *     Excel.Workbook(File.Contents("C:\Users\..."), null, true)
 *
 * the workbook call is untouched and File.Contents' literal becomes "<redacted>": one substitution,
 * at the one place the path actually appears.
 *
 * Inline data is likewise untouched. A table built from a base64 blob
 * (Json.Document(Binary.Decompress(Binary.FromText("i45W...")))) names no location and carries real
 * schema information, so redacting it would destroy meaning to protect nothing.
 */

#ifndef PBIX_REDACT_H
#define PBIX_REDACT_H

/*
 * Returns a newly allocated copy of `expression` with location literals replaced, or NULL if
 * allocation fails. The caller frees it. A NULL input yields NULL.
 *
 * The result is always a fresh allocation, even when nothing matched, so callers can free the
 * original unconditionally.
 */
char *redact_m_expression(const char *expression);

#endif /* PBIX_REDACT_H */
