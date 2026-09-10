import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Resolve the bundled sqlflow-mcp binary the same way the LSP client resolves
 * its server: setting override, then `bin/`, then the Cargo target.
 */
function findMcpBinary(context: vscode.ExtensionContext): string | null {
    const explicit = vscode.workspace.getConfiguration('sqlflow').get<string>('mcpBinaryPath', '');
    if (explicit && fs.existsSync(explicit)) {
        return explicit;
    }
    const exe = process.platform === 'win32' ? 'sqlflow-mcp.exe' : 'sqlflow-mcp';
    const candidates = [
        path.join(context.extensionPath, 'bin', exe),
        path.join(context.extensionPath, '..', 'target', 'release', exe),
        path.join(context.extensionPath, '..', 'target', 'debug', exe),
    ];
    return candidates.find((p) => fs.existsSync(p)) ?? null;
}

/**
 * "Set Up MCP Server": generate ready-to-paste registration for the chosen AI
 * client, pointing at the bundled sqlflow-mcp binary and the configured control
 * plane. Never edits another tool's config silently — it renders a snippet the
 * user copies (matching the DeltaForge extension's approach).
 */
export async function setupMcp(context: vscode.ExtensionContext): Promise<void> {
    const binary = findMcpBinary(context);
    if (!binary) {
        void vscode.window.showErrorMessage(
            'sqlflow-mcp binary not found. Build it with: cargo build -p sqlflow-mcp, or set sqlflow.mcpBinaryPath.'
        );
        return;
    }
    const url = vscode.workspace.getConfiguration('sqlflow').get<string>('controlPlaneUrl', 'http://localhost:8080');

    const client = await vscode.window.showQuickPick(
        [
            { label: 'Claude Code', id: 'claude' },
            { label: 'Cursor', id: 'cursor' },
            { label: 'VS Code (GitHub Copilot)', id: 'vscode' },
            { label: 'Codex', id: 'codex' },
            { label: 'Claude Desktop', id: 'desktop' },
        ],
        { placeHolder: 'Which AI assistant should use the SQLFlow MCP server?' }
    );
    if (!client) {
        return;
    }

    const esc = binary.replace(/\\/g, '\\\\');
    let content: string;
    switch (client.id) {
        case 'claude':
            content = `# Register the SQLFlow MCP server with Claude Code\n\nRun:\n\n\`\`\`sh\nclaude mcp add sqlflow --env SQLFLOW_CONTROL_PLANE_URL=${url} -- "${binary}"\n\`\`\`\n`;
            break;
        case 'vscode':
            content = `# Register with VS Code (GitHub Copilot)\n\nRun:\n\n\`\`\`sh\ncode --add-mcp "{\\"name\\":\\"sqlflow\\",\\"command\\":\\"${esc}\\",\\"env\\":{\\"SQLFLOW_CONTROL_PLANE_URL\\":\\"${url}\\"}}"\n\`\`\`\n`;
            break;
        case 'codex':
            content = `# Add to ~/.codex/config.toml\n\n\`\`\`toml\n[mcp_servers.sqlflow]\ncommand = "${esc}"\nenv = { SQLFLOW_CONTROL_PLANE_URL = "${url}" }\n\`\`\`\n`;
            break;
        default:
            // Cursor / Claude Desktop share the JSON mcpServers shape.
            content = `# Add to your ${client.label} MCP config\n\n\`\`\`json\n{\n  "mcpServers": {\n    "sqlflow": {\n      "command": "${esc}",\n      "env": { "SQLFLOW_CONTROL_PLANE_URL": "${url}" }\n    }\n  }\n}\n\`\`\`\n`;
            break;
    }

    content += `\nAfter registering, restart the assistant, then run the \`login\` tool (or \`set_access_token\`) to authenticate against ${url}.\n`;

    const doc = await vscode.workspace.openTextDocument({ language: 'markdown', content });
    await vscode.window.showTextDocument(doc);
}
