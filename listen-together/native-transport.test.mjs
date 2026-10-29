import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, unlink, rmdir, stat } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { randomBytes } from 'node:crypto';
import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';
import { startNativeHost } from './native-host.mjs';
import { connectNativeState, readNativeState } from './native-client.mjs';

const helper = `
const readline = require('node:readline');
console.log(JSON.stringify({type:'ready',pid:process.pid}));
readline.createInterface({input:process.stdin}).on('line', line => {
  const frame = JSON.parse(line);
  if (frame.type === 'stop') process.exit(0);
  if (frame.message?.method === 'NoReply') return;
  if (frame.message?.method === 'EmitEvent') console.log(JSON.stringify({type:'cdp',message:{method:'Runtime.test',params:{value:1}}}));
  const {id, params} = frame.message;
  setTimeout(() => console.log(JSON.stringify({type:'cdp',message:{id,result:{value:params?.value}}})), params?.delay || 0);
});
`;

function deadline(promise, ms = 5000) {
  let timer;
  return Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('test timeout')), ms); })]).finally(() => clearTimeout(timer));
}

function onceMessage(connection, predicate = () => true) {
  return deadline(new Promise(resolve => {
    const listener = event => {
      const message = JSON.parse(event.data);
      if (!predicate(message)) return;
      connection.removeEventListener('message', listener);
      resolve(message);
    };
    connection.addEventListener('message', listener);
  }));
}

async function fixture(t, overrides = {}) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'ncm-native-test-'));
  const nonce = randomBytes(16).toString('hex');
  const pipe = process.platform === 'win32' ? '\\\\.\\pipe\\NcmMultiListen-' + nonce : path.join(directory, 'session.sock');
  const mutexPipe = process.platform === 'win32' ? '\\\\.\\pipe\\NcmMultiListenMutex-' + nonce : path.join(directory, 'mutex.sock');
  const options = { directory, pipe, mutexPipe, spawnBridge: () => spawn(process.execPath, ['-e', helper], { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] }), startupTimeout: 3000, orphanTimeout: 5000, stopTimeout: 1000, requestTimeout: 100, ...overrides };
  const host = await startNativeHost(options);
  t.after(async () => {
    await host.stop();
    // Only remove exact files created by this fixture, never recursively.
    for (const name of await readdir(directory)) await unlink(path.join(directory, name));
    await rmdir(directory);
  });
  return { host, options, state: JSON.parse(await readFile(path.join(directory, 'state.json'), 'utf8')) };
}

test('auth rejects wrong secret and startup mutex permits only one helper', async t => {
  const { host, options, state } = await fixture(t);
  if (process.platform === 'win32') assert.equal((await readNativeState(options.directory))?.instance, state.instance);
  let spawned = false;
  const duplicate = await startNativeHost({ ...options, spawnBridge: () => { spawned = true; throw new Error('must not spawn'); } });
  assert.equal(duplicate.existing, true);
  assert.equal(spawned, false);
  await assert.rejects(connectNativeState({ ...state, secret: '0'.repeat(64) }, { timeout: 3000 }), /认证失败/);
  const owner = await connectNativeState(state, { owner: true });
  await assert.rejects(connectNativeState(state, { owner: true }), /已在运行/);
  owner.close();
  await deadline(host.closed);
  assert.equal(await readNativeState(options.directory), null);
});

test('client id collisions route responses correctly and events broadcast', async t => {
  const { host, state } = await fixture(t);
  const owner = await connectNativeState(state, { owner: true });
  const status = await connectNativeState(state);
  assert.equal(owner.kind, 'native');
  const a = onceMessage(owner, m => m.id === 1);
  const b = onceMessage(status, m => m.id === 1);
  owner.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: { value: 'owner', delay: 30 } }));
  status.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: { value: 'status' } }));
  assert.equal((await b).result.value, 'status');
  assert.equal((await a).result.value, 'owner');
  const eventA = onceMessage(owner, m => m.method === 'Runtime.test');
  const eventB = onceMessage(status, m => m.method === 'Runtime.test');
  owner.send(JSON.stringify({ id: 2, method: 'EmitEvent' }));
  await Promise.all([eventA, eventB]);
  status.close();
  await new Promise(resolve => status.addEventListener('close', resolve, { once: true }));
  const stillAlive = onceMessage(owner, m => m.id === 3);
  owner.send(JSON.stringify({ id: 3, method: 'Runtime.evaluate', params: { value: 'alive' } }));
  assert.equal((await stillAlive).result.value, 'alive');
  owner.close();
  await deadline(host.closed);
});

test('pending requests expire visibly and helper EOF closes connected clients', async t => {
  // A fake child lets stdout reach EOF while the child remains alive (Windows
  // Node retains another stdout handle). Pipe, auth and CDP transport stay real.
  const { host, state } = await fixture(t, { spawnBridge: () => {
    const child = new EventEmitter();
    Object.assign(child, { pid: 123, exitCode: null, signalCode: null, stdout: new PassThrough() });
    child.kill = () => { child.exitCode = 0; child.emit('exit', 0); };
    child.stdin = new Writable({ write(chunk, encoding, callback) {
      const frame = JSON.parse(chunk.toString());
      if (frame.message?.method === 'CloseOutput') child.stdout.end();
      if (frame.type === 'stop') setImmediate(() => child.kill());
      callback();
    } });
    setImmediate(() => child.stdout.write(JSON.stringify({ type: 'ready', pid: 123 }) + '\n'));
    return child;
  } });
  const owner = await connectNativeState(state, { owner: true });
  const expired = onceMessage(owner, m => m.id === 7);
  owner.send(JSON.stringify({ id: 7, method: 'NoReply' }));
  assert.match((await expired).error.message, /请求超时/);
  const closed = new Promise(resolve => owner.addEventListener('close', resolve, { once: true }));
  owner.send(JSON.stringify({ id: 8, method: 'CloseOutput' }));
  await deadline(closed);
  await deadline(host.closed);
  assert.equal(owner.readyState, 3);
});

test('helper startup error reaches waiting caller and cleans runtime state', async t => {
  const { host, state, options } = await fixture(t, { spawnBridge: () => spawn(process.execPath, ['-e', "setTimeout(() => console.log(JSON.stringify({type:'error',message:'客户端版本不兼容，请更新补丁。'})),100)"], { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] }) });
  await assert.rejects(connectNativeState(state, { owner: true }), /客户端版本不兼容/);
  await deadline(host.closed);
  assert.equal(await readNativeState(options.directory), null);
});

test('public status never starts host; missing runtime returns a visible startup error', { skip: process.platform !== 'win32' }, async t => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'ncm-native-public-test-'));
  const nativeDirectory = path.join(directory, 'NeteaseToolbox', 'MultiListen', 'native');
  t.after(async () => {
    try {
      for (const file of await readdir(nativeDirectory)) await unlink(path.join(nativeDirectory, file));
      await rmdir(nativeDirectory);
      await rmdir(path.dirname(nativeDirectory));
      await rmdir(path.dirname(path.dirname(nativeDirectory)));
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
    await rmdir(directory);
  });
  const run = async options => {
    const source = `import {connectNative} from ${JSON.stringify(new URL('./native-client.mjs', import.meta.url).href)};
      try { await connectNative(${JSON.stringify(options)}); process.exitCode=2; }
      catch(error) { console.log(error.message); }`;
    const child = spawn(process.execPath, ['--input-type=module', '-e', source], {
      windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, LOCALAPPDATA: directory, NCM_MULTI_PYTHON: path.join(directory, 'missing-python.exe') },
    });
    let output = '';
    child.stdout.on('data', data => { output += data.toString(); });
    child.stderr.on('data', data => { output += data.toString(); });
    const code = await deadline(new Promise(resolve => child.once('exit', resolve)));
    assert.equal(code, 0, output);
    return output;
  };
  assert.match(await run({}), /当前没有本机热接入补丁/);
  await assert.rejects(stat(nativeDirectory), { code: 'ENOENT' });
  assert.match(await run({ start: true, owner: true }), /NCM_MULTI_PYTHON/);
  assert.equal(await readNativeState(nativeDirectory), null);
});
