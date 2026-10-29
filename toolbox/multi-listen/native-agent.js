// CEF 91 (branch 4472) x64 C API. native_bridge.py verifies the exact DLL hash.
// Layouts: https://github.com/chromiumembedded/cef/tree/4472/include/capi
'use strict';
const cef = Process.getModuleByName('libcef.dll');
if (Process.platform !== 'windows' || Process.pointerSize !== 8) throw new Error('热接入仅支持 Windows x64');
const browserCallbacks = [0x5b30, 0x5b40, 0x5b60, 0x5b80, 0x6af0, 0x5f70, 0x6be0, 0x6c50, 0x6cc0, 0x6d30, 0x6da0, 0x6e10, 0x6e80, 0x6ef0, 0x6f60, 0x7000, 0x7070, 0x70e0, 0x71d0, 0x72c0, 0x73c0, 0x74f0, 0x7560, 0x77b0];
const native = (address, result, args) => new NativeFunction(address, result, args);
const method = (object, offset, result, args = []) => native(object.add(offset).readPointer(), result, ['pointer', ...args]);
const release = object => { if (object && !object.isNull()) method(object, 16, 'int')(object); };
const postTask = native(cef.getExportByName('cef_post_task'), 'int', ['int', 'pointer']);
const freeString = native(cef.getExportByName('cef_string_userfree_utf16_free'), 'void', ['pointer']);
const objects = new Set();
const cleanupReplies = new Map();
let host = null, registration = null, observer = null, closing = false, disposed = null;
function emit(value) { try { send(value); } catch (_) {} }

// Keep native callbacks alive until CEF has released every reference. A weak
// unload hook below pins the script while cleanup runs on CEF's UI thread.
function structure(size, methods) {
  const pointer = Memory.alloc(size); pointer.writeByteArray(new Uint8Array(size)); pointer.writeU64(size);
  const object = { pointer, callbacks: [], refs: 1, owner: true };
  objects.add(object);
  const deref = () => {
    if (--object.refs === 0) {
      // Defer dropping NativeCallbacks until the release trampoline returns.
      Script.nextTick(() => objects.delete(object));
      return 1;
    }
    return 0;
  };
  const callbacks = [
    [8, () => { object.refs++; }, 'void', ['pointer']],
    [16, deref, 'int', ['pointer']],
    [24, () => Number(object.refs === 1), 'int', ['pointer']],
    [32, () => Number(object.refs > 0), 'int', ['pointer']],
    ...methods,
  ];
  for (const [offset, fn, result, args] of callbacks) {
    const callback = new NativeCallback(fn, result, args);
    object.callbacks.push(callback); pointer.add(offset).writePointer(callback);
  }
  object.dropOwner = () => { if (object.owner) { object.owner = false; deref(); } };
  return object;
}
function onUi(fn) {
  return new Promise((resolve, reject) => {
    const task = structure(0x30, [[40, () => { try { resolve(fn()); } catch (e) { reject(e); } }, 'void', ['pointer']]]);
    try { if (!postTask(0, task.pointer)) reject(new Error('CEF UI 线程拒绝连接任务')); }
    catch (e) { reject(e); }
    finally { task.dropOwner(); }
  });
}
function validBrowser(p) {
  try {
    if (p.readU64().toNumber() !== 0xc8) return false;
    // Freed CppToC wrappers can leave the callback table intact. CEF's
    // add_ref dereferences this owner pointer, so a table match alone is unsafe.
    const owner = p.sub(8).readPointer(), value = p.sub(16).readPointer();
    if (!owner.equals(p.sub(0x20)) || value.isNull() || p.sub(0x18).readU32() !== 10 || p.add(0xc8).readU32() === 0) return false;
    if (!owner.readPointer().equals(cef.base.add(0x7a263e0))) return false;
    // Verify the native AddRef virtual-base chain before crossing into CEF.
    const vbtable = value.add(8).readPointer();
    const refBase = value.add(vbtable.add(4).readS32() + 8);
    const refVtable = refBase.readPointer(), addRef = refVtable.readPointer();
    if (refVtable.compare(cef.base) < 0 || refVtable.compare(cef.base.add(cef.size)) >= 0 ||
        addRef.compare(cef.base) < 0 || addRef.compare(cef.base.add(cef.size)) >= 0) return false;
    return browserCallbacks.every((offset, index) => p.add((index + 1) * 8).readPointer().equals(cef.base.add(offset)));
  } catch (_) { return false; }
}
async function candidates() {
  const pattern = 'c8 00 00 00 00 00 00 00 ' + browserCallbacks.slice(0, 5).map(offset => cef.base.add(offset).toMatchPattern()).join(' ');
  const found = [];
  for (const range of Process.enumerateRanges('rw-')) {
    if (range.file) continue;
    // Chunk very large reserved regions; never skip an active allocation.
    for (let offset = 0; offset < range.size; offset += 16 * 1024 * 1024) {
      const length = Math.min(16 * 1024 * 1024 + 48, range.size - offset);
      await new Promise(resolve => Memory.scan(range.base.add(offset), length, pattern, {
        onMatch(p) { if (validBrowser(p) && found.length < 512) found.push(p); },
        onError() {}, onComplete: resolve,
      }));
    }
  }
  return found;
}
function mainHost(found) {
  const browsers = new Map();
  try {
    for (const p of found) {
      // The scan and this UI task are separated in time: recheck before retain.
      if (!validBrowser(p)) continue;
      method(p, 8, 'void')(p);
      let frame = null;
      try {
        frame = method(p, 0x90, 'pointer')(p);
        if (frame.isNull() || frame.readU64().toNumber() !== 0xf0) continue;
        const value = method(frame, 0xc0, 'pointer')(frame);
        let url = '';
        if (!value.isNull()) {
          try { url = value.readPointer().readUtf16String(value.add(8).readU64().toNumber()); }
          finally { freeString(value); }
        }
        if (url !== 'orpheus://orpheus/pub/app.html') continue;
        const id = method(p, 0x70, 'int')(p);
        if (!browsers.has(id)) { method(p, 8, 'void')(p); browsers.set(id, p); }
      } finally { release(frame); release(p); }
    }
    if (browsers.size !== 1) throw new Error(browsers.size ? '找到多个网易云主页面，停止热接入' : '网易云主页面尚未就绪');
    const browser = browsers.values().next().value;
    const result = method(browser, 0x28, 'pointer')(browser);
    if (result.isNull()) throw new Error('无法取得网易云主页面宿主');
    if (result.readU64().toNumber() !== 0x210 || !result.add(0xd0).readPointer().equals(cef.base.add(0x94f0)) || !result.add(0xe0).readPointer().equals(cef.base.add(0x9670))) {
      release(result); throw new Error('CEF 宿主结构变化，停止热接入');
    }
    return result;
  } finally { for (const p of browsers.values()) release(p); }
}
async function initialize() {
  if (host) return { pid: Process.id };
  const found = await candidates();
  await onUi(() => {
    host = mainHost(found);
    observer = structure(0x50, [
      [40, (_self, _browser, message, size) => {
        try {
          const value = JSON.parse(message.readUtf8String(Number(size)));
          if (cleanupReplies.has(value.id)) { cleanupReplies.get(value.id)(); cleanupReplies.delete(value.id); }
          else emit({ type: 'cdp', message: value });
        }
        catch (_) { emit({ type: 'error', message: 'CEF 调试消息格式异常' }); }
        return 1;
      }, 'int', ['pointer', 'pointer', 'pointer', 'size_t']],
      [72, () => { if (!closing) emit({ type: 'error', message: '网易云页面调试会话已断开，请重新连接补丁' }); }, 'void', ['pointer', 'pointer']],
    ]);
    registration = method(host, 0xe0, 'pointer', ['pointer'])(host, observer.pointer);
    if (registration.isNull()) throw new Error('CEF 拒绝注册进程内消息观察器');
  });
  return { pid: Process.id };
}
async function dispose() {
  if (disposed) return disposed;
  closing = true;
  disposed = (async () => {
    if (host && registration && !registration.isNull()) {
      // Unlike a WebSocket session, removing the last CEF observer does NOT
      // detach the internal DevTools client. Explicitly clear our session.
      const commands = [
        ['Runtime.evaluate', { expression: 'window.__ncmMulti?.hostDisconnected()' }],
        ['Debugger.resume', {}], ['Debugger.disable', {}],
        ['Runtime.removeBinding', { name: '__ncmMultiTokenRequest' }],
        ['Runtime.removeBinding', { name: '__ncmMultiStopBridge' }],
        ['Runtime.disable', {}],
      ];
      const replies = commands.map((_, index) => new Promise(resolve => cleanupReplies.set(2147483000 + index, resolve)));
      await onUi(() => {
        for (const [index, [command, params]] of commands.entries()) {
          const text = JSON.stringify({ id: 2147483000 + index, method: command, params });
          const buffer = Memory.allocUtf8String(text);
          method(host, 0xd0, 'int', ['pointer', 'size_t'])(host, buffer, unescape(encodeURIComponent(text)).length);
        }
      });
      let timer;
      await Promise.race([Promise.all(replies), new Promise(resolve => { timer = setTimeout(resolve, 1000); })]);
      clearTimeout(timer); cleanupReplies.clear();
    }
    await onUi(() => {
      release(registration); registration = null;
      if (observer) { observer.dropOwner(); observer = null; }
      release(host); host = null;
    });
    while (objects.size) await new Promise(resolve => setTimeout(resolve, 5));
    return true;
  })();
  return disposed;
}
const lifetime = {};
Script.bindWeak(lifetime, () => {
  Script.pin();
  dispose().then(() => Script.unpin(), () => {
    // If CEF's UI thread cannot finish cleanup, retaining the callbacks is safer
    // than allowing CEF to jump into freed memory. The client owns their end.
    emit({ type: 'error', message: 'CEF 清理尚未完成，已保留原生回调以保护客户端；请退出网易云后重试' });
  });
});
rpc.exports = {
  initialize,
  async dispatch(message) {
    if (!host || closing) throw new Error('进程内调试通道未连接');
    if (!Number.isSafeInteger(message.id) || message.id < 1 || typeof message.method !== 'string') throw new Error('无效调试请求');
    const json = JSON.stringify(message);
    const buffer = Memory.allocUtf8String(json);
    const length = unescape(encodeURIComponent(json)).length;
    return onUi(() => {
      if (!method(host, 0xd0, 'int', ['pointer', 'size_t'])(host, buffer, length)) throw new Error('CEF 拒绝调试请求');
      return true;
    });
  },
  dispose,
};
