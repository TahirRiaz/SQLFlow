/** A link in a panel: to a command of this extension, or to an address. */
export interface PanelLink {
    text: string;
    command?: string;
    args?: unknown[];
    url?: string;
}

export type PanelCell = string | number | PanelLink | null | undefined;

export type PanelSection =
    | { heading: string; facts: [string, PanelCell][] }
    | { heading: string; columns: string[]; rows: PanelCell[][]; empty?: string }
    | { heading: string; code: string }
    | { heading: string; note: string };

/** What a panel shows: a title, the actions on it, and sections of facts, rows, code or prose. */
export interface PanelContent {
    title: string;
    subtitle?: string;
    actions?: PanelLink[];
    sections: PanelSection[];
}

function escapeHtml(text: string): string {
    return text
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

/** A link's place in the page: its index in `links`, which the page posts back rather than carrying a command itself. */
function renderLink(link: PanelLink, links: PanelLink[]): string {
    links.push(link);
    return `<a href="#" data-link="${links.length - 1}">${escapeHtml(link.text)}</a>`;
}

function renderCell(cell: PanelCell, links: PanelLink[]): string {
    if (cell === null || cell === undefined || cell === '') {
        return '<span class="quiet">-</span>';
    }
    return typeof cell === 'object' ? renderLink(cell, links) : escapeHtml(String(cell));
}

function renderSection(section: PanelSection, links: PanelLink[]): string {
    const heading = `<h2>${escapeHtml(section.heading)}</h2>`;
    if ('facts' in section) {
        const rows = section.facts
            .map(([name, value]) => `<tr><th scope="row">${escapeHtml(name)}</th><td>${renderCell(value, links)}</td></tr>`)
            .join('');
        return `${heading}<table class="facts">${rows}</table>`;
    }
    if ('columns' in section) {
        if (section.rows.length === 0) {
            return `${heading}<p class="quiet">${escapeHtml(section.empty ?? 'Nothing to show.')}</p>`;
        }
        const head = section.columns.map((column) => `<th scope="col">${escapeHtml(column)}</th>`).join('');
        const rows = section.rows
            .map((row) => `<tr>${row.map((cell) => `<td>${renderCell(cell, links)}</td>`).join('')}</tr>`)
            .join('');
        return `${heading}<div class="scroll"><table><thead><tr>${head}</tr></thead><tbody>${rows}</tbody></table></div>`;
    }
    if ('code' in section) {
        return `${heading}<pre>${escapeHtml(section.code)}</pre>`;
    }
    return `${heading}<p>${escapeHtml(section.note)}</p>`;
}

const STYLE = `
body { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size); color: var(--vscode-foreground);
    padding: 0 16px 24px; line-height: 1.45; }
h1 { font-size: 1.3em; font-weight: 600; margin: 16px 0 2px; }
h2 { font-size: 1.05em; font-weight: 600; margin: 22px 0 6px; }
.subtitle, .quiet { color: var(--vscode-descriptionForeground); }
.actions { margin-top: 10px; display: flex; flex-wrap: wrap; gap: 12px; }
a { color: var(--vscode-textLink-foreground); text-decoration: none; }
a:hover { text-decoration: underline; }
table { border-collapse: collapse; width: 100%; }
th, td { text-align: left; vertical-align: top; padding: 3px 12px 3px 0; }
thead th { font-weight: 600; border-bottom: 1px solid var(--vscode-panel-border); }
tbody td { border-bottom: 1px solid var(--vscode-editorWidget-border, transparent); }
.facts th { font-weight: normal; color: var(--vscode-descriptionForeground); white-space: nowrap; width: 1%; }
.scroll { overflow-x: auto; }
pre { font-family: var(--vscode-editor-font-family); font-size: var(--vscode-editor-font-size);
    background: var(--vscode-textCodeBlock-background); padding: 8px 10px; overflow-x: auto; white-space: pre; }
`;

/** The page of a panel. Everything from a tool's answer is escaped, and the page runs only the one script its nonce
 * admits, so a value in the estate can never run in the editor. */
export function renderPanel(content: PanelContent, nonce: string, cspSource: string): { html: string; links: PanelLink[] } {
    const links: PanelLink[] = [];
    const actions = (content.actions ?? []).map((action) => renderLink(action, links)).join('');
    const sections = content.sections.map((section) => renderSection(section, links)).join('');
    const subtitle = content.subtitle ? `<div class="subtitle">${escapeHtml(content.subtitle)}</div>` : '';
    const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${cspSource} 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
<style nonce="${nonce}">${STYLE}</style>
<title>${escapeHtml(content.title)}</title>
</head>
<body>
<h1>${escapeHtml(content.title)}</h1>
${subtitle}
${actions ? `<div class="actions">${actions}</div>` : ''}
${sections}
<script nonce="${nonce}">
const vscode = acquireVsCodeApi();
document.addEventListener('click', (event) => {
    const link = event.target.closest('a[data-link]');
    if (link) {
        event.preventDefault();
        vscode.postMessage({ link: Number(link.dataset.link) });
    }
});
</script>
</body>
</html>`;
    return { html, links };
}
