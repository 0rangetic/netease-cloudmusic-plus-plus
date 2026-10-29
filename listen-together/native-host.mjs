import net from 'node:net';
import path from 'node:path';
import { randomBytes, createHash, timingSafeEqual } from 'node:crypto';
import { readFile, writeFile, mkdir, rename, unlink, open, stat } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { nativeStateDirectory } from './native-client.mjs';

const MAX_FRAME = 8 * 1024 * 1024;
const SECRET_PATTERN = /^[a-f0-9]{64}$/;

function safeSecret(actual, expected) {
  return typeof actual === 'string' && SECRET_PATTERN.test(actual) &&
    timingSafeEqual(Buffer.from(actual, 'hex'), Buffer.from(expected, 'hex'));
}

function listen(server, pipe) {
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(pipe, () => { server.off('error', reject); resolve(); });
  });
}

function probePython(candidate) {
  return new Promise(resolve => {
    let child;
    try {
      child = spawn(candidate, ['-c', 'import frida, psutil'], { windowsHide: true, stdio: 'ignore' });
    } catch { resolve(false); return; }
    const timer = setTimeout(() => { child.kill(); resolve(false); }, 4000);
    child.once('error', () => { clearTimeout(timer); resolve(false); });
    child.once('exit', code => { clearTimeout(timer); resolve(code === 0); });
  });
}

export async function findNativePython() {
  const explicit = process.env.NCM_MULTI_PYTHON;
  const candidates = explicit ? [explicit] : [
    fileURLToPath(new URL('../runtime/python/python.exe', import.meta.url)),
    fileURLToPath(new URL('../toolbox/runtime/python/python.exe', import.meta.url)),
  ];
  for (const candidate of candidates) {
    if (!path.isAbsolute(candidate)) continue;
    try { if (!(await stat(candidate)).isFile()) continue; } catch { continue; }
    if (await probePython(candidate)) return candidate;
  }
  throw new Error(explicit
    ? 'NCM_MULTI_PYTHON 指定的 Python 不可用或缺少 frida、psutil，请修复该运行环境后重试。'
    : '本机热接入缺少包含 frida、psutil 的 Python 运行环境。请补齐工具箱 runtime/python，或用 NCM_MULTI_PYTHON 指定完整 python.exe 路径。');
}

/** One helper is shared by watch and short-lived status/shutdown callers. */
export async function startNativeHost(options = {}) {
  const directory = options.directory || nativeStateDirectory();
  const instance = randomBytes(16).toString('hex');
  const secret = randomBytes(32).toString('hex');
  const pipe = options.pipe || '\\\\.\\pipe\\NcmMultiListen-' + instance;
  // A held named pipe provides a crash-safe process mutex with no stale lock to
  // delete. The random authenticated pipe below carries all actual requests.
  const mutexPipe = options.mutexPipe || '\\\\.\\pipe\\NcmMultiListenMutex-' + createHash('sha256').update(directory.toLowerCase()).digest('hex').slice(0, 32);
  const stateFile = path.join(directory, 'state.json');
  const tempFile = path.join(directory, 'state-' + instance + '.tmp');
  const peers = new Set();
  const pending = new Map();
  let owner = null, sequence = 0, child, log, ready = false, stopping = false;
  let tempCreated = false;
  let readyTimer, orphanTimer, closeResolve;
  const closed = new Promise(resolve => { closeResolve = resolve; });
  const mutex = net.createServer(socket => socket.destroy());
  const server = net.createServer();

  const send = (peer, frame) => {
    if (peer.socket.destroyed || peer.socket.writableEnded) return;
    if (peer.socket.writableLength > MAX_FRAME) { peer.socket.destroy(); return; }
    peer.socket.write(JSON.stringify(frame) + '\n');
  };
  const replyError = (peer, id, message) => send(peer, { type: 'cdp', message: { id, error: { code: -32000, message } } });
  const dropPending = peer => {
    for (const [id, request] of pending) {
      if (peer && request.peer !== peer) continue;
      clearTimeout(request.timer);
      pending.delete(id);
    }
  };
  const clearOwnState = async () => {
    try {
      const state = JSON.parse(await readFile(stateFile, 'utf8'));
      if (state.instance === instance && state.pid === process.pid) await unlink(stateFile);
    } catch {}
    if (tempCreated) { try { await unlink(tempFile); } catch {} }
  };
  const stop = async message => {
    if (stopping) return closed;
    stopping = true;
    clearTimeout(readyTimer);
    clearTimeout(orphanTimer);
    for (const peer of peers) {
      if (message) send(peer, { type: 'error', code: 'BRIDGE_STOPPED', message });
      peer.socket.end();
      const timer = setTimeout(() => peer.socket.destroy(), 200);
      timer.unref();
    }
    dropPending();
    server.close();
    if (child && child.exitCode === null && child.signalCode === null) {
      await new Promise(resolve => {
        const timer = setTimeout(() => { child.kill(); resolve(); }, options.stopTimeout ?? 3000);
        child.once('exit', () => { clearTimeout(timer); resolve(); });
        try { child.stdin.end(JSON.stringify({ type: 'stop' }) + '\n'); } catch { child.kill(); }
      });
    }
    await clearOwnState();
    mutex.close();
    try { await log?.close(); } catch {}
    closeResolve();
  };

  try {
    await listen(mutex, mutexPipe);
  } catch (error) {
    if (error.code === 'EADDRINUSE') return { existing: true, closed: Promise.resolve(), stop: async () => {} };
    throw new Error('无法创建本机热接入启动互斥，请检查当前用户的本机管道权限。');
  }
  mutex.on('error', () => { void stop('本机热接入启动互斥异常，请重新启动补丁。'); });
  server.on('error', () => { void stop('本机热接入通道发生错误，请重新启动补丁。'); });
  server.on('connection', socket => {
    if (stopping || peers.size >= 16) { socket.destroy(); return; }
    const peer = { socket, authenticated: false, announced: false, owner: false, buffer: '' };
    peers.add(peer);
    const helloTimer = setTimeout(() => socket.destroy(), 5000);
    socket.setEncoding('utf8');
    socket.on('error', () => {});
    socket.on('close', () => {
      clearTimeout(helloTimer);
      peers.delete(peer);
      dropPending(peer);
      if (owner === peer) { owner = null; void stop(); }
    });
    socket.on('end', () => socket.end());
    socket.on('data', chunk => {
      peer.buffer += chunk;
      if (Buffer.byteLength(peer.buffer) > (peer.authenticated ? MAX_FRAME : 4096)) { socket.destroy(); return; }
      let newline;
      while ((newline = peer.buffer.indexOf('\n')) >= 0) {
        const line = peer.buffer.slice(0, newline);
        peer.buffer = peer.buffer.slice(newline + 1);
        let frame;
        try { frame = JSON.parse(line); } catch { socket.destroy(); return; }
        if (!peer.authenticated) {
          if (!frame || frame.type !== 'hello' || !safeSecret(frame.secret, secret) || typeof frame.owner !== 'boolean') {
            send(peer, { type: 'error', code: 'AUTH_FAILED', message: '本机热接入认证失败，请重新启动补丁。' });
            socket.end();
            return;
          }
          if (frame.owner && owner) {
            send(peer, { type: 'error', code: 'OWNER_BUSY', message: '多人一起听补丁已在运行，请勿重复启动。' });
            socket.end();
            return;
          }
          clearTimeout(helloTimer);
          peer.authenticated = true;
          peer.owner = frame.owner;
          if (frame.owner) { owner = peer; clearTimeout(orphanTimer); }
          if (ready) { peer.announced = true; send(peer, { type: 'ready', pid: child.pid }); }
          continue;
        }
        if (stopping || !ready || !peer.announced) continue;
        const request = frame?.message;
        if (frame?.type !== 'cdp' || !request || !Number.isSafeInteger(request.id) || request.id < 0 || typeof request.method !== 'string') {
          socket.destroy(); return;
        }
        if (pending.size >= 512) { replyError(peer, request.id, '本机热接入请求过多，请稍后重试。'); continue; }
        // Client counters can both begin at 1; CEF sees one global counter.
        const id = ++sequence;
        const timer = setTimeout(() => {
          pending.delete(id);
          replyError(peer, request.id, '网易云本机 CDP 请求超时，请重试。');
        }, options.requestTimeout ?? 60000);
        pending.set(id, { peer, id: request.id, timer });
        try {
          if (!child.stdin.writable || child.stdin.writableLength > MAX_FRAME) throw new Error();
          child.stdin.write(JSON.stringify({ type: 'cdp', message: { ...request, id } }) + '\n');
        } catch {
          clearTimeout(timer);
          pending.delete(id);
          replyError(peer, request.id, '网易云本机热接入已断开。');
          void stop('网易云本机热接入已断开，请重新启动补丁。');
        }
      }
    });
  });

  try {
    await mkdir(directory, { recursive: true });
    await listen(server, pipe);
    const state = { protocol: 1, instance, secret, pipe, pid: process.pid };
    await writeFile(tempFile, JSON.stringify(state), { flag: 'wx', mode: 0o600 });
    tempCreated = true;
    await rename(tempFile, stateFile);
    tempCreated = false;
    orphanTimer = setTimeout(() => { void stop('启动补丁的进程未连接，本机热接入已自动结束。'); }, options.orphanTimeout ?? 30000);
    const python = options.spawnBridge ? null : await findNativePython();
    if (stopping) return { closed, stop };
    log = await open(path.join(directory, 'native-bridge.log'), 'a');
    child = options.spawnBridge ? options.spawnBridge() : spawn(python, [fileURLToPath(new URL('./native_bridge.py', import.meta.url))], {
      windowsHide: true, stdio: ['pipe', 'pipe', log.fd], env: { ...process.env, PYTHONIOENCODING: 'utf-8', PYTHONUNBUFFERED: '1' },
    });
    child.on('error', () => { void stop('无法启动本机热接入 helper，请检查 Python 运行环境。'); });
    child.stdin.on('error', () => { if (!stopping) void stop('本机热接入 helper 已断开，请重新启动补丁。'); });
    child.once('exit', () => { if (!stopping) void stop('网易云本机热接入已结束，请重新启动补丁。'); });
    let buffer = '';
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', chunk => {
      buffer += chunk;
      if (Buffer.byteLength(buffer) > MAX_FRAME) { void stop('本机热接入 helper 返回数据过大。'); return; }
      let newline;
      while ((newline = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, newline);
        buffer = buffer.slice(newline + 1);
        let frame;
        try { frame = JSON.parse(line); } catch { void stop('本机热接入 helper 返回了无效数据。'); return; }
        if (frame?.type === 'error') { void stop(frame.message || '网易云本机热接入失败。'); return; }
        if (frame?.type === 'ready') {
          if (ready) continue;
          ready = true;
          clearTimeout(readyTimer);
          for (const peer of peers) {
            if (peer.authenticated) { peer.announced = true; send(peer, { type: 'ready', pid: frame.pid }); }
          }
          continue;
        }
        const message = frame?.message;
        if (frame?.type !== 'cdp' || !message || typeof message !== 'object') continue;
        if (Object.hasOwn(message, 'id')) {
          const request = pending.get(message.id);
          if (!request) continue;
          pending.delete(message.id);
          clearTimeout(request.timer);
          send(request.peer, { type: 'cdp', message: { ...message, id: request.id } });
        } else if (typeof message.method === 'string') {
          for (const peer of peers) if (peer.announced) send(peer, { type: 'cdp', message });
        }
      }
    });
    child.stdout.once('end', () => { if (!stopping) void stop('本机热接入 helper 输出已关闭，请重新启动补丁。'); });
    readyTimer = setTimeout(() => { void stop('等待网易云本机热接入超时，请确认客户端已启动完成。'); }, options.startupTimeout ?? 25000);
    return { closed, stop, state, existing: false };
  } catch (error) {
    await stop(error.message);
    throw error;
  }
}

async function main() {
  let host;
  const signal = () => { void host?.stop(); };
  process.once('SIGINT', signal);
  process.once('SIGTERM', signal);
  try {
    host = await startNativeHost();
    if (process.connected) process.send({ type: 'started' });
    if (process.connected) process.disconnect();
    await host.closed;
  } catch (error) {
    if (process.connected) process.send({ type: 'startupError', message: error.message });
    if (process.connected) process.disconnect();
    process.exitCode = 1;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) await main();
