import assert from 'node:assert/strict';
import { test } from 'node:test';
import { renderPanel } from '../src/views/panelContent.ts';

test('a value from the estate is shown, never run', () => {
    const { html } = renderPanel({
        title: '<script>alert(1)</script>',
        sections: [
            { heading: 'Facts', facts: [['Name', '<img src=x onerror=alert(1)>']] },
            { heading: 'Rows', columns: ['A'], rows: [['"><b>x</b>']] },
            { heading: 'Code', code: '</pre><script>alert(2)</script>' },
            { heading: 'Note', note: "it's <b>bold</b>" },
        ],
    }, 'n0nce', 'vscode-resource:');

    assert.ok(!html.includes('<img src=x'));
    assert.ok(!html.includes('<script>alert'));
    assert.ok(!html.includes('<b>'));
    assert.ok(html.includes('&lt;script&gt;alert(1)&lt;/script&gt;'));
});

test('only the page\'s own script runs', () => {
    const { html } = renderPanel({ title: 't', sections: [] }, 'n0nce', 'vscode-resource:');

    assert.ok(html.includes("default-src 'none'"));
    assert.ok(html.includes("script-src 'nonce-n0nce'"));
    assert.equal(html.match(/<script/g)?.length, 1);
});

test('a link carries its place and no command of its own', () => {
    const { html, links } = renderPanel({
        title: 't',
        actions: [{ text: 'Open', url: 'https://gui.example.com/runs/1' }],
        sections: [{ heading: 'Flows', columns: ['Flow'], rows: [[{ text: 'wells', command: 'sqlflow.openFlowByName', args: ['wells'] }]] }],
    }, 'n', 'c');

    assert.equal(links.length, 2);
    assert.equal(links[1].command, 'sqlflow.openFlowByName');
    assert.ok(html.includes('data-link="1"'));
    assert.ok(!html.includes('sqlflow.openFlowByName'));
    assert.ok(!html.includes('gui.example.com'));
});

test('an empty value reads as empty, zero as zero', () => {
    const { html } = renderPanel({ title: 't', sections: [{ heading: 'F', facts: [['None', null], ['Zero', 0]] }] }, 'n', 'c');

    assert.ok(html.includes('<td><span class="quiet">-</span></td>'));
    assert.ok(html.includes('<td>0</td>'));
});

test('a table with no rows says so', () => {
    const { html } = renderPanel({ title: 't', sections: [{ heading: 'R', columns: ['A'], rows: [], empty: 'Nothing ran.' }] }, 'n', 'c');

    assert.ok(html.includes('Nothing ran.'));
    assert.ok(!html.includes('<table'));
});
