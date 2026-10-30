import * as vscode from 'vscode';
import { DocumentIndex, IndexedDocument } from '../documents/documentIndex';
import { keyPathAtLine, scalarKeys } from '../documents/yamlPath';
import { Estate } from '../mcp/estate';

/** What a key's value refers to, in what a document's author can be helped with: what may be written, and where it is. */
export interface ReferenceProvider {
    /** Whether this provider answers the key at a dotted path of a document. */
    answers(path: string, document: IndexedDocument | undefined): boolean;
    /** The values that may be written there. */
    values?(document: IndexedDocument | undefined): Promise<ValueOption[]>;
    /** The file a value refers to. */
    target?(value: string, document: IndexedDocument): Promise<vscode.Uri | undefined>;
}

export interface ValueOption {
    value: string;
    detail?: string;
    documentation?: string;
}

/**
 * What the language server cannot know: it sees one file, and a value in a flow names something elsewhere, a schedule in
 * a library, a mapping in the next folder, a connection other flows use. Providers answer that from the repository and
 * the estate, and this turns them into the editor's completion and its links. A module adds a provider per key it owns.
 */
export class References implements vscode.CompletionItemProvider, vscode.DocumentLinkProvider, vscode.DefinitionProvider {
    private readonly providers: ReferenceProvider[] = [];

    constructor(private readonly documents: DocumentIndex) {}

    register(provider: ReferenceProvider): void {
        this.providers.push(provider);
    }

    async provideCompletionItems(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.CompletionItem[]> {
        const lines = document.getText().split(/\r?\n/);
        const key = keyPathAtLine(lines, position.line);
        const colon = lines[position.line].indexOf(':');
        if (!key || position.character <= colon) {
            return [];
        }
        const indexed = await this.documents.at(document.uri);
        const answers = await Promise.all(
            this.providers
                .filter((provider) => provider.answers(key.path, indexed) && provider.values)
                .map((provider) => provider.values?.(indexed).catch(() => [] as ValueOption[]) ?? Promise.resolve([]))
        );
        const seen = new Set<string>();
        return answers.flat().filter((option) => !seen.has(option.value) && seen.add(option.value)).map((option) => {
            const item = new vscode.CompletionItem(option.value, vscode.CompletionItemKind.Reference);
            item.detail = option.detail;
            item.documentation = option.documentation;
            item.sortText = `0${option.value}`;
            return item;
        });
    }

    async provideDocumentLinks(document: vscode.TextDocument): Promise<vscode.DocumentLink[]> {
        const indexed = await this.documents.at(document.uri);
        if (!indexed) {
            return [];
        }
        const links: vscode.DocumentLink[] = [];
        for (const key of scalarKeys(document.getText().split(/\r?\n/))) {
            const target = await this.targetOf(key.path, key.value, indexed);
            if (target) {
                links.push(new vscode.DocumentLink(
                    new vscode.Range(key.line, key.valueStart, key.line, key.valueEnd),
                    target
                ));
            }
        }
        return links;
    }

    async provideDefinition(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.Location | undefined> {
        const key = keyPathAtLine(document.getText().split(/\r?\n/), position.line);
        const indexed = await this.documents.at(document.uri);
        if (!key || !indexed) {
            return undefined;
        }
        const target = await this.targetOf(key.path, key.value, indexed);
        return target ? new vscode.Location(target, new vscode.Position(0, 0)) : undefined;
    }

    private async targetOf(path: string, value: string, document: IndexedDocument): Promise<vscode.Uri | undefined> {
        for (const provider of this.providers) {
            if (provider.answers(path, document) && provider.target) {
                const target = await provider.target(value, document);
                if (target) {
                    return target;
                }
            }
        }
        return undefined;
    }
}

/** The names a schedule library defines: its keys one level under `schedules:`. */
function scheduleNames(text: string): string[] {
    const names: string[] = [];
    let inside = false;
    for (const line of text.split(/\r?\n/)) {
        if (/^schedules\s*:/.test(line)) {
            inside = true;
        } else if (/^\S/.test(line) && !line.startsWith('#')) {
            inside = false;
        } else if (inside) {
            const name = /^ {2}(?:["']?)([\w.-]+)(?:["']?)\s*:/.exec(line);
            if (name) {
                names.push(name[1]);
            }
        }
    }
    return names;
}

/** The references every SQLFlow repository has: a flow's schedule and its connections. */
export function builtInReferences(documents: DocumentIndex, estate: Estate): ReferenceProvider[] {
    const libraries = async (): Promise<{ document: IndexedDocument; names: string[] }[]> => {
        const held = (await documents.all()).filter((document) => document.kind === 'schedules');
        return Promise.all(held.map(async (document) => ({
            document,
            names: scheduleNames(Buffer.from(await vscode.workspace.fs.readFile(document.uri)).toString('utf8')),
        })));
    };

    return [
        {
            answers: (path) => path === 'schedule' || path === 'schedule.name',
            values: async () => {
                const local = (await libraries()).flatMap(({ document, names }) => names.map((value) => ({ value, detail: document.relativePath })));
                const estateSchedules = await estate.schedules().catch(() => []);
                return [...local, ...estateSchedules.filter((s) => s.name).map((s) => ({ value: s.name as string, detail: s.cron ?? 'in the estate' }))];
            },
            target: async (value) => (await libraries()).find(({ names }) => names.includes(value))?.document.uri,
        },
        {
            answers: (path) => /(^|\.)(connection|server)$/.test(path),
            values: async () => (await estate.datasources()).map((datasource) => ({
                value: datasource.reference,
                detail: datasource.kind ?? undefined,
                documentation: `${datasource.sourcePipelines} flows read from it, ${datasource.targetPipelines} write to it.`,
            })),
        },
    ];
}
