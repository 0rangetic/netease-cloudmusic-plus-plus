import net from 'node:net';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const MAX_FRAME = 8 * 1024 * 1024;
const WAIT_MS = 30000;

export function nativeStateDirectory() {
  if (!process.env.LOCALAPPDATA) throw new Error('未找到本机 LOCALAPPDATA，无法连接多人一起听补丁。');
  return path.join(process.env.LOCALAPPDATA, 'NeteaseToolbox', 'MultiListen', 'native');
}

// Runtime state contains a local capability; never copy it into a release or log it.
export async function readNativeState(directory = nativeStateDirectory()) {
  try {
    const raw = await readFile(path.join(directory, 'state.json'), 'utf8');
    if (raw.length > 4096) return null;
    const state = JSON.parse(raw);
    if (state.protocol !== 1 || !/^[a-f0-9]{32}$/.test(state.instance) ||
        !/^[a-f0-9]{64}$/.test(state.secret) || !Number.isSafeInteger(state.pid) || state.pid < 1 ||
        typeof state.pipe !== 'string' || !/^\\\\\.\\pipe\\NcmMultiListen-[a-f0-9]{32}$/.test(state.pipe)) return null;
    return state;
  } catch { return null; }
}

export class NativeConnection extends EventTarget {
  kind = 'native';
  readyState = 1;
  #socket;
  #closed = false;
  constructor(socket) {
    super();
    this.#socket = socket;
    socket.on('close', () => {
      if (this.#closed) return;
      this.#closed = true;
      this.readyState = 3;
      this.dispatchEvent(new Event('close'));
    });
    // Errors are surfaced by pending CDP requests on close, without an unhandled
    // EventEmitter error crashing a status query or the persistent watch process.
    socket.on('error', () => {});
  }
  receive(message) {
    if (!this.#closed) this.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(message) }));
  }
  send(value) {
    if (this.readyState !== 1 || this.#socket.destroyed) throw new Error('网易云本机热接入通道已断开。');
    const message = JSON.parse(String(value));
    if (!message || typeof message !== 'object' || !Number.isSafeInteger(message.id) || message.id < 0 || typeof message.method !== 'string') {
      throw new Error('本机热接入收到无效的 CDP 请求。');
    }
    const frame = JSON.stringify({ type: 'cdp', message }) + '\n';
    if (Buffer.byteLength(frame) > MAX_FRAME) throw new Error('本机热接入请求过大。');
    if (this.#socket.writableLength > MAX_FRAME) throw new Error('网易云本机热接入通道繁忙，请稍后重试。');
    this.#socket.write(frame);
  }
  close() {
    if (this.readyState !== 1) return;
    this.readyState = 2;
    this.#socket.end();
    const timer = setTimeout(() => this.#socket.destroy(), 500);
    timer.unref();
    this.#socket.once('close', () => clearTimeout(timer));
  }
}

// Exported for focused transport tests; production callers use connectNative.
export function connectNativeState(state, { owner = false, timeout = WAIT_MS } = {}) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(state.pipe);
    let buffer = '', settled = false, connection;
    const fail = error => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.destroy();
      reject(error);
    };
    const timer = setTimeout(() => fail(new Error('连接网易云本机热接入超时，请在客户端启动完成后重试。')), timeout);
    socket.setEncoding('utf8');
    socket.once('connect', () => socket.write(JSON.stringify({ type: 'hello', secret: state.secret, owner }) + '\n'));
    socket.once('error', () => fail(new Error('网易云本机热接入通道暂不可用。')));
    socket.once('close', () => fail(new Error('网易云本机热接入已结束；请重新启动补丁。')));
    socket.on('data', chunk => {
      buffer += chunk;
      if (Buffer.byteLength(buffer) > MAX_FRAME) {
        if (settled) socket.destroy();
        else fail(new Error('本机热接入返回的数据过大。'));
        return;
      }
      let newline;
      while ((newline = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, newline);
        buffer = buffer.slice(newline + 1);
        let frame;
        try { frame = JSON.parse(line); } catch {
          if (settled) socket.destroy();
          else fail(new Error('本机热接入返回了无效数据。'));
          return;
        }
        if (!settled) {
          if (frame.type === 'error') {
            const error = new Error(frame.message || '网易云本机热接入失败。');
            error.code = frame.code;
            fail(error);
            return;
          }
          if (frame.type !== 'ready') { fail(new Error('本机热接入握手失败。')); return; }
          settled = true;
          clearTimeout(timer);
          connection = new NativeConnection(socket);
          resolve(connection);
        } else if (frame.type === 'cdp' && frame.message && typeof frame.message === 'object') {
          connection.receive(frame.message);
        } else if (frame.type === 'error') {
          // Active callers receive a normal close and reject all pending requests.
          socket.end();
        }
      }
    });
  });
}

function launchHost() {
  let error;
  const child = spawn(process.execPath, [fileURLToPath(new URL('./native-host.mjs', import.meta.url))], {
    detached: true, windowsHide: true, stdio: ['ignore', 'ignore', 'ignore', 'ipc'],
  });
  child.on('error', () => { error = new Error('无法启动网易云本机热接入进程，请检查运行环境。'); });
  child.on('message', message => {
    if (message?.type === 'startupError') error = new Error(message.message || '网易云本机热接入启动失败。');
    if (message?.type === 'started' && child.connected) child.disconnect();
  });
  child.unref();
  return {
    error: () => error,
    disconnect: () => { if (child.connected) child.disconnect(); },
  };
}

export async function connectNative({ start = false, owner = false } = {}) {
  if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('网易云本机热接入目前仅支持 Windows x64。');
  if (start && !owner) throw new Error('只有启动补丁时可以创建本机热接入，状态查询不会启动后台补丁。');
  const directory = nativeStateDirectory();
  const deadline = Date.now() + WAIT_MS;
  let launched, lastError;
  try {
    do {
      if (launched?.error()) throw launched.error();
      const state = await readNativeState(directory);
      if (state) {
        try {
          return await connectNativeState(state, { owner, timeout: Math.max(1, deadline - Date.now()) });
        } catch (error) {
          lastError = error;
          if (error.code || !start) throw error;
        }
      } else if (!start) {
        throw new Error('当前没有本机热接入补丁；请先点击“启动并连接”。');
      }
      if (!launched) launched = launchHost();
      await new Promise(resolve => setTimeout(resolve, 100));
    } while (Date.now() < deadline);
    throw launched?.error() || lastError || new Error('网易云本机热接入启动超时，请确认客户端已启动并查看任务消息。');
  } finally { launched?.disconnect(); }
}
