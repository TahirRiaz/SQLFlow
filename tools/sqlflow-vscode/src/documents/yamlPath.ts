/** A YAML line's key: how far it is indented, the key, and what stands after the colon. */
interface KeyLine {
    indent: number;
    key: string;
    value: string;
}

function keyLine(line: string): KeyLine | undefined {
    const lead = line.length - line.trimStart().length;
    const marker = /^-\s+/.exec(line.slice(lead))?.[0].length ?? 0;
    // A list item's first key sits at its siblings' column, past the marker, so it is never read as their parent.
    const indent = lead + marker;
    const match = /^["']?([\w.$-]+)["']?\s*:(?:\s+(.*))?$/.exec(line.slice(indent));
    return match ? { indent, key: match[1], value: (match[2] ?? '').trim() } : undefined;
}

/** A key on a line, with the value it holds and where that value sits on the line. */
export interface KeyAtLine {
    /** The dotted path of the key from the document's root. */
    path: string;
    value: string;
    /** Where the value starts and ends on its line. */
    valueStart: number;
    valueEnd: number;
}

/**
 * The dotted key path of a line, rebuilt from indentation. A half-typed document has no parse tree, so this works on the
 * lines alone: each line less indented than the last, walking up, is a parent. List items are transparent, as the census
 * paths a key is looked up by write them.
 */
export function keyPathAtLine(lines: readonly string[], line: number): KeyAtLine | undefined {
    const here = keyLine(lines[line] ?? '');
    if (!here) {
        return undefined;
    }

    const segments = [here.key];
    let indent = here.indent;
    for (let index = line - 1; index >= 0 && indent > 0; index--) {
        const text = lines[index];
        if (text.trim() === '' || text.trimStart().startsWith('#')) {
            continue;
        }
        const parent = keyLine(text);
        if (parent && parent.indent < indent) {
            segments.unshift(parent.key);
            indent = parent.indent;
        }
    }

    const raw = lines[line];
    const colon = raw.indexOf(':', here.indent);
    const after = raw.slice(colon + 1);
    const lead = after.length - after.trimStart().length;
    const value = unquote(here.value.replace(/\s+#.*$/, ''));
    const valueStart = colon + 1 + lead + (here.value.startsWith('"') || here.value.startsWith("'") ? 1 : 0);
    return { path: segments.join('.'), value, valueStart, valueEnd: valueStart + value.length };
}

function unquote(value: string): string {
    const quoted = /^(["'])(.*?)\1$/.exec(value);
    return quoted ? quoted[2] : value;
}

/** Every `key: value` line of a document that holds a scalar value, for what reads references across a document. */
export function scalarKeys(lines: readonly string[]): (KeyAtLine & { line: number })[] {
    const keys: (KeyAtLine & { line: number })[] = [];
    lines.forEach((_, line) => {
        const key = keyPathAtLine(lines, line);
        if (key && key.value !== '' && !/^[|>[{]/.test(key.value)) {
            keys.push({ ...key, line });
        }
    });
    return keys;
}
