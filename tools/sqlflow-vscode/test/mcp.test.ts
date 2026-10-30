import assert from 'node:assert/strict';
import { test } from 'node:test';
import { JsonRpcClient, RpcError } from '../src/mcp/jsonRpc.ts';
import { jsonOf, parseAuthStatus, parseSignIn, SignInNeeded, textOf, ToolFailed } from '../src/mcp/toolAnswer.ts';

function client() {
    const sent: unknown[] = [];
    return { sent, rpc: new JsonRpcClient((line) => sent.push(JSON.parse(line))) };
}

test('an answer finds its request, however the stream cuts it', async () => {
    const { rpc } = client();
    const answer = rpc.request('tools/list');
    rpc.receive('{"jsonrpc":"2.0","id":1,');
    rpc.receive('"result":{"tools":[]}}\n');
    assert.deepEqual(await answer, { tools: [] });
});

test('a server error is the request\'s failure, with its code', async () => {
    const { rpc } = client();
    const answer = rpc.request('nothing');
    rpc.receive('{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"no such method"}}\n');
    await assert.rejects(answer, (error: unknown) => error instanceof RpcError && error.code === -32601);
});

test('a ping is answered and anything else the server asks is refused', () => {
    const { rpc, sent } = client();
    rpc.receive('{"jsonrpc":"2.0","id":9,"method":"ping"}\n{"jsonrpc":"2.0","id":10,"method":"sampling/createMessage"}\n');
    assert.deepEqual(sent[0], { jsonrpc: '2.0', id: 9, result: {} });
    assert.equal((sent[1] as { error: { code: number } }).error.code, -32601);
});

test('a line that is not protocol is skipped', async () => {
    const { rpc } = client();
    const answer = rpc.request('x');
    rpc.receive('a log line\n{"jsonrpc":"2.0","id":1,"result":1}\n');
    assert.equal(await answer, 1);
});

test('closing fails what waits and everything after', async () => {
    const { rpc } = client();
    const answer = rpc.request('x');
    rpc.close('The server stopped.');
    await assert.rejects(answer, /The server stopped/);
    await assert.rejects(rpc.request('y'), /The server stopped/);
});

const text = (value: string, isError = false) => ({ content: [{ type: 'text', text: value }], isError });

test('a tool\'s text comes back as it is', () => {
    assert.equal(textOf('list_repos', text('{"items":[]}')), '{"items":[]}');
});

test('a failure either way MCP allows is a failure', () => {
    assert.throws(() => textOf('get_run', text('Error: /api/v1/runs/x returned 404')), ToolFailed);
    assert.throws(() => textOf('get_run', text('list_runs is not offered here.', true)), ToolFailed);
});

test('every way a tool says nobody is signed in asks for a sign-in', () => {
    for (const answer of [
        'Error: not authenticated: run the `login` tool or set an access token',
        'Error: the stored session ended (expired); run the `login` tool to sign in again',
        'Error: control plane rejected the token (401): the credential is invalid or expired',
    ]) {
        assert.throws(() => textOf('list_repos', text(answer)), SignInNeeded);
    }
});

test('text that is not JSON is the reason there is no answer', () => {
    assert.throws(() => jsonOf('validate_flow', 'OK: no problems found.'), /OK: no problems found/);
    assert.deepEqual(jsonOf('x', '{"a":1}'), { a: 1 });
});

test('the sign-in answer gives the address to open and the code to compare', () => {
    const answer = 'To sign in, open:\n  https://cp/device\nand enter the code:\n  ABCD-EFGH\n\n(Direct link: https://cp/device?code=ABCD-EFGH)\nThe code expires in 600s.';
    assert.deepEqual(parseSignIn(answer), { url: 'https://cp/device?code=ABCD-EFGH', code: 'ABCD-EFGH' });
    assert.throws(() => parseSignIn('Error starting sign-in: connection refused'), /connection refused/);
});

test('the sign-in sentences read as where the sign-in stands', () => {
    assert.deepEqual(parseAuthStatus('Authenticated as alice@example.com, role admin, scopes: read.'), { state: 'signed-in', who: 'alice@example.com' });
    assert.deepEqual(parseAuthStatus('Signed in. Scopes: read operate.'), { state: 'signed-in', who: undefined });
    assert.deepEqual(parseAuthStatus('Still waiting for approval. Approve in the browser, then check again.'), { state: 'waiting' });
    assert.deepEqual(parseAuthStatus('Sign-in was denied.'), { state: 'denied' });
    assert.deepEqual(parseAuthStatus('The sign-in code expired. Call login again.'), { state: 'expired' });
    assert.equal(parseAuthStatus('Not authenticated and no sign-in in progress. Call login first.').state, 'signed-out');
});
