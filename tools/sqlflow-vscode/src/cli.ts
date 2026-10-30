import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

/** The command line beside this extension's other programs, when the package holds one. */
export function bundledCli(extensionPath: string): string | undefined {
    const candidate = path.join(extensionPath, 'bin', process.platform === 'win32' ? 'sqlflow.exe' : 'sqlflow');
    return fs.existsSync(candidate) ? candidate : undefined;
}

/** The command line to run: the setting, else the packaged one, else whatever `sqlflow` names on this machine's PATH. */
export function cliCommand(extensionPath: string): string {
    const configured = vscode.workspace.getConfiguration('sqlflow').get<string>('cliPath', '').trim();
    return configured || bundledCli(extensionPath) || 'sqlflow';
}

/**
 * Makes the command line a person's terminal, and the agent working in one, can simply run: the packaged `sqlflow` on the
 * PATH of every integrated terminal, pointed at the control plane the extension uses, so a script needs no setup of its
 * own. It signs in with the same credential as the extension, so nothing here carries one.
 */
export function offerCliInTerminals(context: vscode.ExtensionContext, product: string): void {
    const environment = context.environmentVariableCollection;
    environment.clear();
    environment.description = `${product}: the sqlflow command line`;

    const bundled = bundledCli(context.extensionPath);
    if (bundled) {
        environment.prepend('PATH', `${path.dirname(bundled)}${path.delimiter}`);
    }
    const controlPlane = vscode.workspace.getConfiguration('sqlflow').get<string>('controlPlaneUrl', '').trim();
    if (controlPlane !== '') {
        environment.replace('SQLFLOW_URL', controlPlane);
    }
}
