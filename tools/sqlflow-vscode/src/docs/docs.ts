import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';

interface DocMeta {
    id: string;
    path: string;
    title: string;
    type: string;
    summary?: string;
    keywords?: string[];
    yamlPath?: string;
    cliCommand?: string;
}

/**
 * The bundled reference corpus. The extension packages `docs/reference` into
 * `dist/reference`; this reads its manifest and opens pages in VS Code's own
 * markdown preview, so "Search Docs" and "Open Docs for Key" work fully offline
 * with no external renderer.
 */
export class Docs {
    private readonly root: string;
    private readonly docs: DocMeta[];

    constructor(extensionPath: string) {
        this.root = path.join(extensionPath, 'dist', 'reference');
        this.docs = this.loadManifest();
    }

    private loadManifest(): DocMeta[] {
        try {
            const raw = fs.readFileSync(path.join(this.root, 'manifest.json'), 'utf8');
            const parsed = JSON.parse(raw) as { docs?: DocMeta[] };
            return parsed.docs ?? [];
        } catch {
            return [];
        }
    }

    get available(): boolean {
        return this.docs.length > 0;
    }

    /** Quick-pick search over titles/summaries/keywords, then open the page. */
    async search(): Promise<void> {
        if (!this.available) {
            void vscode.window.showWarningMessage('SQLFlow docs are not bundled in this build.');
            return;
        }
        const picks = this.docs.map((d) => ({
            label: d.title,
            description: d.type,
            detail: d.summary,
            id: d.id,
        }));
        const chosen = await vscode.window.showQuickPick(picks, {
            placeHolder: 'Search SQLFlow reference docs',
            matchOnDetail: true,
            matchOnDescription: true,
        });
        if (chosen) {
            await this.open(chosen.id);
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

    private async open(id: string): Promise<void> {
        const meta = this.docs.find((d) => d.id === id);
        if (!meta) {
            return;
        }
        const file = vscode.Uri.file(path.join(this.root, meta.path));
        try {
            await vscode.commands.executeCommand('markdown.showPreview', file);
        } catch {
            const doc = await vscode.workspace.openTextDocument(file);
            await vscode.window.showTextDocument(doc);
        }
    }
}
