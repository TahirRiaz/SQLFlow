import { build, context } from 'esbuild';
import { cpSync, existsSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, '..', '..');
const watch = process.argv.includes('--watch');
const production = process.argv.includes('--production');

/** Copy the reference corpus into dist so the docs browser works offline. */
function copyDocs() {
    const src = join(repoRoot, 'docs', 'reference');
    const dst = join(here, 'dist', 'reference');
    if (!existsSync(src)) {
        console.warn(`[esbuild] docs/reference not found at ${src}; docs browser will be empty.`);
        return;
    }
    rmSync(dst, { recursive: true, force: true });
    mkdirSync(dst, { recursive: true });
    cpSync(src, dst, { recursive: true });
    console.log('[esbuild] copied docs/reference -> dist/reference');
}

/** Stage the built Rust binaries into bin/ if they exist (release preferred). */
function copyBinaries() {
    const exe = process.platform === 'win32' ? '.exe' : '';
    const bin = join(here, 'bin');
    mkdirSync(bin, { recursive: true });
    for (const name of ['sqlflow-lsp', 'sqlflow-mcp']) {
        for (const profile of ['release', 'debug']) {
            const candidate = join(here, '..', 'target', profile, `${name}${exe}`);
            if (existsSync(candidate)) {
                cpSync(candidate, join(bin, `${name}${exe}`));
                console.log(`[esbuild] staged ${name} (${profile})`);
                break;
            }
        }
    }
}

const options = {
    entryPoints: [join(here, 'src', 'extension.ts')],
    bundle: true,
    outfile: join(here, 'dist', 'extension.js'),
    platform: 'node',
    target: 'node18',
    format: 'cjs',
    external: ['vscode'],
    sourcemap: !production,
    minify: production,
    logLevel: 'info',
};

copyDocs();
copyBinaries();

if (watch) {
    const ctx = await context(options);
    await ctx.watch();
    console.log('[esbuild] watching…');
} else {
    await build(options);
    console.log('[esbuild] build complete');
}
