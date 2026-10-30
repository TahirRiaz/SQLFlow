import assert from 'node:assert/strict';
import { test } from 'node:test';
import { kindLabel, recogniseDocument } from '../src/documents/recognition.ts';
import { keyPathAtLine, scalarKeys } from '../src/documents/yamlPath.ts';

test('a flow is told by its flowType, whatever the file is called', () => {
    assert.deepEqual(recogniseDocument('flows/wells.yaml', 'flowType: ing\nname: wells\n'), { kind: 'flow', flowType: 'ing', name: 'wells' });
    assert.deepEqual(recogniseDocument('x.yaml', '# a comment\nflowType: "delivery" # kept\nname: \'w\'\n'), { kind: 'flow', flowType: 'delivery', name: 'w' });
});

test('a document names itself and a flowType still wins', () => {
    assert.deepEqual(recogniseDocument('m.yaml', 'documentType: mapping\nname: Well\n'), { kind: 'document', documentType: 'mapping', name: 'Well' });
    assert.equal(recogniseDocument('m.yaml', 'flowType: ing\ndocumentType: mapping\n')?.kind, 'flow');
});

test('the library files are told by their root key', () => {
    assert.equal(recogniseDocument('anything.yaml', 'schedules:\n  nightly:\n    cron: "0 1 * * *"\n')?.kind, 'schedules');
    assert.equal(recogniseDocument('anything.yaml', 'subscribers:\n  - name: a\n')?.kind, 'subscribers');
    assert.deepEqual(recogniseDocument('a.yaml', 'aiContext: sales\n'), { kind: 'ai-context', name: 'sales', });
});

test('a file flow has a source and a target, or the historical suffix', () => {
    assert.equal(recogniseDocument('a.yaml', 'source:\n  type: csv\ntarget:\n  table: t\n')?.kind, 'flow');
    assert.equal(recogniseDocument('a.flow.yaml', 'description: nothing yet\n')?.kind, 'flow');
});

test('a companion document is never read as a file flow', () => {
    assert.equal(recogniseDocument('a.yaml', 'documentType: mapping\nsource:\n  a: 1\ntarget:\n  b: 2\n')?.kind, 'document');
});

test('a file that does not parse is still recognised', () => {
    assert.equal(recogniseDocument('a.yaml', 'flowType: ing\nsource: [unclosed\n')?.kind, 'flow');
});

test('other YAML stays plain YAML', () => {
    assert.equal(recogniseDocument('docker-compose.yaml', 'services:\n  db:\n    image: x\n'), undefined);
    assert.equal(recogniseDocument('ci.yaml', 'on: push\njobs:\n  build:\n    source: a\n'), undefined);
});

test('a nested key is no root key', () => {
    assert.equal(recogniseDocument('a.yaml', 'jobs:\n  flowType: ing\n  schedules: x\n'), undefined);
});

test('the kind reads as the word a document uses', () => {
    assert.equal(kindLabel({ kind: 'flow' }), 'file');
    assert.equal(kindLabel({ kind: 'flow', flowType: 'ing' }), 'ing');
    assert.equal(kindLabel({ kind: 'document', documentType: 'mapping' }), 'mapping');
    assert.equal(kindLabel({ kind: 'schedules' }), 'schedules');
});

const flow = [
    'flowType: delivery',
    'name: wells',
    'render:',
    '  mapping: Well@1.0.0',
    '',
    '  # a comment between keys',
    '  nested:',
    '    - key: "quoted" # note',
    '      other: value',
    'schedule: nightly',
];

test('a key\'s path is rebuilt from indentation, through comments and list items', () => {
    assert.equal(keyPathAtLine(flow, 3)?.path, 'render.mapping');
    assert.equal(keyPathAtLine(flow, 7)?.path, 'render.nested.key');
    assert.equal(keyPathAtLine(flow, 8)?.path, 'render.nested.other');
    assert.equal(keyPathAtLine(flow, 9)?.path, 'schedule');
    assert.equal(keyPathAtLine(flow, 4), undefined);
});

test('a value is read and placed, quoted or not', () => {
    const plain = keyPathAtLine(flow, 3)!;
    assert.equal(plain.value, 'Well@1.0.0');
    assert.equal(flow[3].slice(plain.valueStart, plain.valueEnd), 'Well@1.0.0');
    const quoted = keyPathAtLine(flow, 7)!;
    assert.equal(quoted.value, 'quoted');
    assert.equal(flow[7].slice(quoted.valueStart, quoted.valueEnd), 'quoted');
});

test('scalar keys leave out what opens a block', () => {
    const keys = scalarKeys(['a: 1', 'b:', '  c: |', '    text', 'd: [1, 2]', 'e: "x"']).map((key) => key.path);
    assert.deepEqual(keys, ['a', 'e']);
});
