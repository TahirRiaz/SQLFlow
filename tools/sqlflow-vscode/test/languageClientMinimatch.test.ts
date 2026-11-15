import assert from 'node:assert/strict';
import { test } from 'node:test';
import { rewriteMakeReChecks } from '../languageClientMinimatch.mts';

// The client's two checks, as vscode-languageclient 10.1.2 and 11.0.0 publish them.
const globPattern = [
    '    const matcher = new minimatch.Minimatch(miniMatchPattern, { noext: true });',
    '    if (!matcher.makeRe()) {',
    '        return false;',
    '    }',
    '    return matcher.match(resource.fsPath);',
].join('\n');

test('a negated makeRe() becomes the test that the pattern left an empty set', () => {
    assert.equal(
        rewriteMakeReChecks(globPattern, 'globPattern.js'),
        globPattern.replace('if (!matcher.makeRe())', 'if ((matcher.set.length === 0))'),
    );
});

test('every negated makeRe() in a module is rewritten, whatever its matcher is called', () => {
    const source = 'if (!first.makeRe()) { a(); }\nif (! second.makeRe()) { b(); }\nconst ok = !!third.makeRe();\n';
    assert.equal(
        rewriteMakeReChecks(source, 'fileOperations.js'),
        'if ((first.set.length === 0)) { a(); }\nif ((second.set.length === 0)) { b(); }\nconst ok = !(third.set.length === 0);\n',
    );
});

test('a module that never calls makeRe() is left as it is', () => {
    const source = 'const minimatch = require("minimatch");\nconst makeRegex = (p) => new RegExp(p);\n';
    assert.equal(rewriteMakeReChecks(source, 'other.js'), source);
});

test('makeRe() used for its RegExp fails the bundle, naming the file and line', () => {
    assert.throws(
        () => rewriteMakeReChecks('const a = 1;\nreturn matcher.makeRe().test(path);\n', 'lib/common/x.js'),
        /^Error: lib\/common\/x\.js:2 uses Minimatch#makeRe\(\) in a form the minimatch 11 rewrite does not know/,
    );
});
