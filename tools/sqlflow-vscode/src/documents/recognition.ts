/** What a document is: a flow, one of SQLFlow's library files, or a document a module adds, named by `documentType`. */
export type DocumentKind = 'flow' | 'schedules' | 'subscribers' | 'ai-context' | 'document';

export interface RecognisedDocument {
    kind: DocumentKind;
    /** A flow's `flowType`; absent on a file flow, which declares none. */
    flowType?: string;
    /** A document's `documentType`. */
    documentType?: string;
    /** The document's own name: `name`, or an AI context's `aiContext`. */
    name?: string;
}

/** A root key's scalar value, with its quotes and any comment off. */
function rootValue(text: string, key: string): string | undefined {
    const match = new RegExp(`^${key}[ \\t]*:[ \\t]*([^\\r\\n]*)`, 'm').exec(text);
    if (!match) {
        return undefined;
    }
    const value = match[1].trim();
    const quoted = /^(["'])(.*?)\1/.exec(value);
    return quoted ? quoted[2] : value.replace(/\s+#.*$/, '').trim();
}

function hasRootKey(text: string, key: string): boolean {
    return new RegExp(`^${key}[ \\t]*:`, 'm').test(text);
}

/**
 * Whether a YAML file is a pipeline document, and which. Discovery is by content, as the estate's own is: a repository's
 * flows are plain `*.yaml` files (the `.flow.yaml` suffix is historical and optional), so the name says little and the
 * root keys say what the file is. This follows `FlowDocumentRecognition` and the language engine's document kinds, reading
 * root keys at the start of a line so that a file with a syntax error is still recognised: a half-typed flow is exactly
 * the file that needs the editor's help. Anything else, a compose file or a CI workflow, answers undefined and stays
 * plain YAML.
 */
export function recogniseDocument(path: string, text: string): RecognisedDocument | undefined {
    const name = rootValue(text, 'name') || undefined;

    const flowType = rootValue(text, 'flowType');
    if (flowType) {
        return { kind: 'flow', flowType, name };
    }

    const documentType = rootValue(text, 'documentType');
    if (documentType) {
        return { kind: 'document', documentType, name };
    }

    if (hasRootKey(text, 'schedules')) {
        return { kind: 'schedules', name };
    }
    if (hasRootKey(text, 'subscribers')) {
        return { kind: 'subscribers', name };
    }
    if (hasRootKey(text, 'aiContext')) {
        return { kind: 'ai-context', name: rootValue(text, 'aiContext') || name };
    }

    const flowSuffix = path.toLowerCase().endsWith('.flow.yaml');
    if (flowSuffix || (hasRootKey(text, 'source') && hasRootKey(text, 'target'))) {
        return { kind: 'flow', name };
    }

    return undefined;
}

/** The word a person reads for a document's kind: its `flowType`, `documentType` or library. */
export function kindLabel(document: RecognisedDocument): string {
    switch (document.kind) {
        case 'flow':
            return document.flowType ?? 'file';
        case 'document':
            return document.documentType ?? 'document';
        default:
            return document.kind;
    }
}
