import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import path from 'node:path';
import vm from 'node:vm';

// Source-level JavaScript probe only. No browser, WebView2, clipboard, network,
// pairing account, or running Harness is involved.
const checkout = process.argv[2];
if (!checkout) throw new Error('Usage: node probe-remote-upload-boot.mjs <dsh-web-checkout>');
const sourceRoot = path.resolve(checkout, 'packages/dsh-remote-web-ui/src');
const remoteMethods = readFileSync(path.join(sourceRoot, 'remote-methods.ts'), 'utf8');
const header = remoteMethods.match(/REMOTE_DEVICE_HEADER = '([^']+)'/)[1];
const query = remoteMethods.match(/REMOTE_DEVICE_QUERY = '([^']+)'/)[1];
function bareSource(file) {
  const source = readFileSync(path.join(sourceRoot, file), 'utf8')
    .replace(/^import .+$/gm, '')
    .replace(/^export \{.+$/gm, '');
  return stripTypeScriptTypes(source, { mode: 'strip' }).replace(/\bexport /g, '');
}
const builder = vm.createContext({ REMOTE_DEVICE_HEADER: header, REMOTE_DEVICE_QUERY: query });
vm.runInContext(bareSource('remote-channel-rules.ts') + '\n' + bareSource('remote-channel-boot.ts'), builder);
const boot = vm.runInContext('REMOTE_CHANNEL_BOOT_SCRIPT', builder);
const cases = [];
function fixture() {
  const calls = [];
  const storage = new Map();
  const context = vm.createContext({ URL, Request, Headers, Promise });
  const window = {
    location: new URL('http://192.0.2.10:3000/'),
    sessionStorage: { getItem: key => storage.get(key) ?? null },
    WebSocket: class WebSocket {},
    fetch(input, init) { calls.push({ input, init }); return Promise.resolve(new Response('{}')); },
  };
  context.window = window;
  const startRemote = () => vm.runInContext(boot, context);
  const pairAfterBoot = () => storage.set('dsh-remote-device', 'fixture-device-not-a-secret');
  return { calls, window, startRemote, pairAfterBoot };
}
async function check(name, run) { await run(); cases.push({ name, result: 'PASS' }); }

await check('plain object headers: rewrite once, late device, body/query/signal preserved', async () => {
  const f = fixture(); f.startRemote(); f.pairAfterBoot();
  const body = new Blob(['hello']); const signal = new AbortController().signal;
  await f.window.fetch('/api/session/uploadFileBinary?sessionId=s1&name=a%20b.txt', {
    method: 'POST', body, signal, headers: { 'content-type': 'application/octet-stream' },
  });
  const [c] = f.calls;
  assert.equal(new URL(c.input).pathname, '/remote/api/session/uploadFileBinary');
  assert.equal(new URL(c.input).searchParams.get('name'), 'a b.txt');
  assert.equal(c.init.headers[header], 'fixture-device-not-a-secret');
  assert.equal(c.init.body, body); assert.equal(c.init.signal, signal);
});
await check('Headers instance: device and original content-type retained', async () => {
  const f = fixture(); f.startRemote(); f.pairAfterBoot();
  await f.window.fetch('/api/session/uploadFileBinary', { headers: new Headers({ 'content-type': 'application/octet-stream' }) });
  assert.equal(f.calls[0].init.headers.get(header), 'fixture-device-not-a-secret');
  assert.equal(f.calls[0].init.headers.get('content-type'), 'application/octet-stream');
});
await check('upstream limitation reproduced: missing init.headers omits device header', async () => {
  const f = fixture(); f.startRemote(); f.pairAfterBoot();
  await f.window.fetch('/api/session/uploadFileBinary', { method: 'POST' });
  assert.equal(f.calls[0].init.headers, undefined);
});
await check('upstream limitation reproduced: tuple headers become numeric object keys', async () => {
  const f = fixture(); f.startRemote(); f.pairAfterBoot();
  await f.window.fetch('/api/session/uploadFileBinary', { headers: [['content-type', 'application/octet-stream']] });
  assert.equal(f.calls[0].init.headers['content-type'], undefined);
  assert.ok(f.calls[0].init.headers['0']);
});
await check('upstream limitation reproduced: pre-rewritten path bypasses device injection', async () => {
  const f = fixture(); f.startRemote(); f.pairAfterBoot();
  await f.window.fetch('/remote/api/session/uploadFileBinary', { headers: {} });
  assert.equal(f.calls[0].init.headers[header], undefined);
});
await check('foreign origin receives no device header', async () => {
  const f = fixture(); f.startRemote(); f.pairAfterBoot();
  await f.window.fetch('https://example.invalid/api/session/uploadFileBinary', { headers: {} });
  assert.equal(f.calls[0].init.headers[header], undefined);
});
await check('original boot captures its own initial fetch; adapter must read current fetch at request time', async () => {
  for (const addonFirst of [true, false]) {
    const f = fixture(); let upload;
    const installAddonCandidate = () => {
      upload = (input, init) => {
        const url = new URL(input, f.window.location.href);
        if (url.origin !== f.window.location.origin || url.pathname !== '/api/session/uploadFileBinary') throw new Error('blocked URL');
        if (!f.window.__DSH_REMOTE_CHANNEL_BOOT__) throw new Error('remote channel unavailable');
        return f.window.fetch(url.href, { ...init, headers: new Headers(init?.headers) });
      };
    };
    if (addonFirst) installAddonCandidate();
    f.startRemote();
    if (!addonFirst) installAddonCandidate();
    f.pairAfterBoot();
    await upload('/api/session/uploadFileBinary?sessionId=s2', { headers: [['content-type', 'application/octet-stream']] });
    assert.equal(new URL(f.calls[0].input).pathname, '/remote/api/session/uploadFileBinary');
    assert.equal(f.calls[0].init.headers.get(header), 'fixture-device-not-a-secret');
    assert.equal(f.calls[0].init.headers.get('content-type'), 'application/octet-stream');
    f.window.__DSH_REMOTE_CHANNEL_BOOT__.restore();
    assert.throws(() => upload('/api/session/uploadFileBinary', {}), /unavailable/);
    assert.equal(f.calls.length, 1);
  }
});

process.stdout.write(JSON.stringify({ runtime: process.version, kind: 'source-level-node-vm-probe', network: false, cases }, null, 2) + '\n');
