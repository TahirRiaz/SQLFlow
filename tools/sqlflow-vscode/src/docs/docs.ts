import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import { McpServer } from '../mcp/mcpServer';

interface DocMeta {
    id: string;
    path: string;
    title: string;
    type: string;
    summary?: string;
    keywords?: string[];
    yamlPath?: string;
    cliCommand?: string;
    /** Which corpus of the manifest a page belongs to: a wiki page's path starts at the wiki beside the reference. */
    corpus?: string;
}

interface LocatedDoc extends DocMeta {
    /** The folder the page's `path` is relative to. */
    root: string;
}

/** How long a person's typing rests before the server is asked. */
const SEARCH_DELAY_MS = 200;

/**
 * The bundled reference corpus. A host packages each corpus it documents (its own
 * beside SQLFlow's) into the extension; this reads their manifests and opens pages
 * in VS Code's own markdown preview, so "Search Docs" and "Open Docs for Key" work
 * fully offline with no external renderer. A search is ranked by the MCP server's
 * `search_docs`, which reads the pages' text; without the server the list filters
 * on titles, summaries and keywords.
 */
export class Docs {
    private readonly docs: LocatedDoc[];
    private readonly mcp: McpServer;

    constructor(corpora: string[], mcp: McpServer) {
        this.docs = corpora.flatMap((root) => this.loadManifest(root));
        this.mcp = mcp;
    }

    private loadManifest(root: string): LocatedDoc[] {
        try {
            const raw = fs.readFileSync(path.join(root, 'manifest.json'), 'utf8');
            const parsed = JSON.parse(raw) as { docs?: DocMeta[] };
            return (parsed.docs ?? []).map((doc) => ({ ...doc, root: doc.corpus === 'wiki' ? path.join(root, '..', 'wiki') : root }));
        } catch {
            return [];
        }
    }

    get available(): boolean {
        return this.docs.length > 0;
    }

    /** Search the pages as the person types, then open the one picked. */
    async search(): Promise<void> {
        if (!this.available) {
            void vscode.window.showWarningMessage('The reference docs are not bundled in this build.');
            return;
        }

        const all = this.docs.map((d) => toPick(d));
        const quickPick = vscode.window.createQuickPick<DocPick>();
        quickPick.placeholder = 'Search the reference docs';
        quickPick.matchOnDescription = true;
        quickPick.matchOnDetail = true;
        quickPick.items = all;

        let timer: NodeJS.Timeout | undefined;
        quickPick.onDidChangeValue((value) => {
            clearTimeout(timer);
            if (value.trim().length < 3) {
                quickPick.items = all;
                return;
            }
            timer = setTimeout(() => void this.ranked(value).then((hits) => {
                if (hits.length > 0 && quickPick.value === value) {
                    quickPick.items = hits;
                }
            }), SEARCH_DELAY_MS);
        });
        quickPick.onDidAccept(() => {
            const chosen = quickPick.selectedItems[0];
            quickPick.hide();
            if (chosen) {
                void this.open(chosen.id);
            }
        });
        quickPick.onDidHide(() => quickPick.dispose());
        quickPick.show();
    }

    /** The server's ranked hits. Silent when it has none or cannot be reached: the list keeps filtering as the person
     * types, so nothing is lost. */
    private async ranked(query: string): Promise<DocPick[]> {
        try {
            const answer = await this.mcp.call<{ results: { id: string }[] }>('search_docs', { query });
            return answer.results
                .map((hit) => this.docs.find((doc) => doc.id === hit.id))
                .filter((doc): doc is LocatedDoc => doc !== undefined)
                .map((doc) => ({ ...toPick(doc), alwaysShow: true }));
        } catch {
            return [];
        }
    }

    /** Open the page whose `yamlPath` matches (exact, then longest prefix). */
    async openForYamlPath(yamlPath: string): Promise<boolean> {
        const exact = this.docs.find((d) => d.yamlPath === yamlPath);
        const match =
            exact ??
            this.docs
                .filter((d) => d.yamlPath && (yamlPath === d.yamlPath || yamlPath.startsWith(d.yamlPath + '.')))
                .sort((a, b) => (b.yamlPath?.length ?? 0) - (a.yamlPath?.length ?? 0))[0];
        if (!match) {
            return false;
        }
        await this.open(match.id);
        return true;
    }

    async open(id: string): Promise<void> {
        const meta = this.docs.find((d) => d.id === id);
        if (!meta) {
            void vscode.window.showWarningMessage(`The reference docs hold no page '${id}'.`);
            return;
        }
        const file = vscode.Uri.file(path.join(meta.root, meta.path));
        try {
            await vscode.commands.executeCommand('markdown.showPreview', file);
        } catch {
            const doc = await vscode.workspace.openTextDocument(file);
            await vscode.window.showTextDocument(doc);
        }
    }
}

interface DocPick extends vscode.QuickPickItem {
    id: string;
}

function toPick(doc: LocatedDoc): DocPick {
    return { label: doc.title, description: doc.type, detail: doc.summary, id: doc.id };
}
