import { readFile } from 'node:fs/promises';
import type { Plugin } from 'esbuild';

// vscode-languageclient (10.x and 11.0.0) asks whether a glob pattern can match anything by calling `Minimatch#makeRe()`
// and testing the answer for false: in a file operation filter, and in a diagnostic or notebook filter's pattern.
// minimatch's `makeRe()` answers false exactly when the pattern left an empty set (a comment, an empty pattern, or one
// whose every part failed to compile). minimatch 11, the first release past the globparts denial of service advisory
// GHSA-g3hv-75wg-m249, matches without a RegExp and removed `makeRe()`, so the client as published throws a TypeError
// there. Both lines keep the set, so the rewrite below asks it the same question when the extension is bundled. The
// rewrite and the package's `minimatch` override go once the client asks for minimatch 11 itself
// (microsoft/vscode-languageserver-node issue 1938).

/** A negated call on a matcher: the client's one use of `makeRe()`, a test that the pattern can match nothing. */
const negatedMakeRe = /!\s*([A-Za-z_$][\w$]*)\.makeRe\(\)/g;

const anyMakeRe = /\bmakeRe\b/;

/**
 * The source of a client module with each `!matcher.makeRe()` turned into `(matcher.set.length === 0)`, the same answer
 * on minimatch 10 and 11. Throws, naming the file and line, when `makeRe` is left in any other form, since minimatch 11
 * would fail it at run time.
 */
export function rewriteMakeReChecks(source: string, file: string): string {
    const rewritten = source.replace(negatedMakeRe, (_call: string, matcher: string) => `(${matcher}.set.length === 0)`);
    const left = anyMakeRe.exec(rewritten);
    if (left) {
        const line = rewritten.slice(0, left.index).split('\n').length;
        throw new Error(
            `${file}:${line} uses Minimatch#makeRe() in a form the minimatch 11 rewrite does not know, and minimatch 11 has no makeRe(); ` +
                'extend rewriteMakeReChecks in sqlflow/tools/sqlflow-vscode/languageClientMinimatch.mts for it.',
        );
    }
    return rewritten;
}

/** Bundles vscode-languageclient against minimatch 11: each client module that calls `makeRe()` is loaded rewritten. */
export const languageClientOnMinimatch11: Plugin = {
    name: 'vscode-languageclient-on-minimatch-11',
    setup(build) {
        build.onLoad({ filter: /[\\/]vscode-languageclient[\\/]lib[\\/].*\.js$/ }, async (args) => {
            const source = await readFile(args.path, 'utf8');
            return anyMakeRe.test(source) ? { contents: rewriteMakeReChecks(source, args.path), loader: 'js' } : undefined;
        });
    },
};
