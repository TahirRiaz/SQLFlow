import * as crypto from 'crypto';
import * as vscode from 'vscode';
import { PanelContent, PanelLink, renderPanel } from './panelContent';

/**
 * The extension's one reading surface: a run, a mapping check, a repository's order, a history. Each is a `PanelContent`,
 * so a module shows what it reads the way SQLFlow's own views do, in the editor's colours and with its links working.
 * Showing a panel of the same key brings it forward with the new content.
 */
export class Panels {
    private readonly open = new Map<string, vscode.WebviewPanel>();

    show(key: string, content: PanelContent): void {
        const panel = this.open.get(key) ?? this.create(key, content.title);
        panel.title = content.title;
        const { html, links } = renderPanel(content, crypto.randomBytes(16).toString('hex'), panel.webview.cspSource);
        panel.webview.html = html;
        (panel as PanelWithLinks).links = links;
        panel.reveal(undefined, true);
    }

    private create(key: string, title: string): vscode.WebviewPanel {
        const panel = vscode.window.createWebviewPanel('sqlflow.panel', title, { viewColumn: vscode.ViewColumn.Beside, preserveFocus: true }, {
            enableScripts: true,
            localResourceRoots: [],
        });
        panel.webview.onDidReceiveMessage((message: { link?: number }) => {
            const link = (panel as PanelWithLinks).links?.[message.link ?? -1];
            if (link) {
                void follow(link);
            }
        });
        panel.onDidDispose(() => this.open.delete(key));
        this.open.set(key, panel);
        return panel;
    }
}

type PanelWithLinks = vscode.WebviewPanel & { links?: PanelLink[] };

async function follow(link: PanelLink): Promise<void> {
    if (link.command) {
        await vscode.commands.executeCommand(link.command, ...(link.args ?? []));
    } else if (link.url) {
        await vscode.env.openExternal(vscode.Uri.parse(link.url));
    }
}
