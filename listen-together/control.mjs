import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { parseInvitation, targetSeconds } from './protocol.mjs';
import { version } from './version.mjs';

const mode = process.argv[2] || 'status';
async function connect() {
  if (process.env.NCM_MULTI_TRANSPORT !== 'native') {
    let page;
    try {
      const targets = await (await fetch('http://127.0.0.1:9222/json/list', { signal: AbortSignal.timeout(2000) })).json();
      page = targets.find(t => t.type === 'page' && t.url === 'orpheus://orpheus/pub/app.html');
    } catch (_) { /* A normally started client has no TCP debugging endpoint. */ }
    if (page) {
      const url = new URL(page.webSocketDebuggerUrl);
      if (url.protocol !== 'ws:' || !['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname) || url.port !== '9222') throw new Error('拒绝连接非本机网易云调试目标');
      const socket = new WebSocket(url);
      await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
      socket.kind = 'cdp';
      return socket;
    }
  }
  const { connectNative } = await import('./native-client.mjs');
  return connectNative({ start: mode === 'watch', owner: mode === 'watch' });
}
const ws = await connect();
const pending = new Map();
let sequence = 0;
function send(method, params = {}, timeout = 20000) {
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(method + ' 超时')); }, timeout);
    pending.set(id, { resolve, reject, timer });
    ws.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }, 45000);
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
  return r.result.value;
}
ws.addEventListener('message', event => {
  const m = JSON.parse(event.data);
  if (m.id && pending.has(m.id)) {
    const p = pending.get(m.id); pending.delete(m.id); clearTimeout(p.timer);
    m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result);
  }
  if (mode === 'watch' && m.method === 'Runtime.bindingCalled' && m.params.name === '__ncmMultiTokenRequest') {
    void tokenReply(m.params).catch(e => console.error('令牌桥失败：' + e.message));
  }
  if (mode === 'watch' && m.method === 'Runtime.bindingCalled' && m.params.name === '__ncmMultiStopBridge' && m.params.payload === 'stop') {
    // Let the separate shutdown caller receive Runtime.evaluate's result first.
    setTimeout(() => ws.close(), 150);
  }
  if (mode === 'watch' && m.method === 'Debugger.paused') void send('Debugger.resume').catch(() => {});
});
ws.addEventListener('close', () => {
  for (const p of pending.values()) { clearTimeout(p.timer); p.reject(new Error('CDP 已断开')); }
  pending.clear();
});

async function tokenReply(event) {
  let id;
  try { id = JSON.parse(event.payload).id; } catch { return; }
  if (!Number.isSafeInteger(id) || id < 1) return;
  let token = '', error = '';
  try {
    const r = await fetch('https://ac.dun.163yun.com/v3/b?pn=YD00000558929251', { signal: AbortSignal.timeout(10000) });
    if (!r.ok) throw new Error('官方令牌服务 HTTP ' + r.status);
    const match = (await r.text()).match(/null\(\[200,\d+,"([^"]+)"\]\)/);
    if (!match) throw new Error('官方令牌服务返回格式变化');
    token = match[1];
  } catch (e) { error = e.message; }
  // Credentials stay in memory and are never written to diagnostic output.
  await send('Runtime.evaluate', { expression: `window.__ncmMultiTokenReply?.(${id},${JSON.stringify(token)},${JSON.stringify(error)})`, contextId: event.executionContextId });
}

try {
  if (mode === 'status') {
    console.log(JSON.stringify(await evaluate('window.__ncmMulti?.status() || {installed:false}'), null, 2));
    ws.close();
  } else if (mode === 'watch') {
    if (ws.kind === 'native') {
      const existing = await evaluate('window.__ncmMulti && ({version:window.__ncmMulti.version,disconnected:window.__ncmMulti.bridgeDisconnected===true})');
      if (existing && !(existing.version === version && existing.disconnected)) throw new Error('已有补丁运行，版本 ' + existing.version + '；请勿重复启动');
      // The CEF internal DevTools client can outlive an older helper. Clear its
      // old domain state before binding this owner, even after an abrupt exit.
      await send('Debugger.disable');
      await send('Runtime.removeBinding', { name: '__ncmMultiTokenRequest' });
      await send('Runtime.removeBinding', { name: '__ncmMultiStopBridge' });
      await send('Runtime.disable');
    }
    await send('Runtime.enable');
    await send('Runtime.addBinding', { name: '__ncmMultiTokenRequest' });
    await send('Runtime.addBinding', { name: '__ncmMultiStopBridge' });
    const source = await readFile(new URL('./runtime.js', import.meta.url), 'utf8');
    const boot = `(${source})(${parseInvitation.toString()},${targetSeconds.toString()},${JSON.stringify(version)})`;
    const installed = await evaluate(boot);
    if (installed.alreadyInstalled && !(installed.version === version && await evaluate('window.__ncmMulti.bridgeDisconnected === true'))) {
      throw new Error('已有补丁运行，版本 ' + installed.version + '；请勿重复启动');
    }
    await evaluate(`Object.assign(window.__ncmMulti,{transport:${JSON.stringify(ws.kind)},bridgeDisconnected:false,clickHook:false,clickHookReady:false,hostLastSeen:Date.now()})`);
    console.log(JSON.stringify(installed));
    await send('Debugger.enable');
    // Bind the click hook to the parser module the patch resolved at runtime.
    // Webpack ids move between client releases (2093 on 3.1.41, 2092 on 3.1.42).
    const parser = await send('Runtime.evaluate', { expression: 'window.__ncmMulti.parserTarget?.() ?? null' });
    try {
      await send('Debugger.setBreakpointOnFunctionCall', {
        objectId: parser.result.objectId,
        condition: '(window.__ncmMulti.receive(e),false)',
      });
      console.log('邀请点击入口已连接');
      await evaluate('window.__ncmMulti.clickHook=true');
    } catch (e) {
      console.error('点击入口未连接，可用面板里的“读取私信邀请”：' + e.message);
    } finally { await evaluate('window.__ncmMulti.clickHookReady=true'); }
    try { await evaluate('window.__ncmMulti.roomId ? window.__ncmMulti.status() : window.__ncmMulti.restore()'); }
    catch (e) { await evaluate(`window.__ncmMulti.reportFailure(${JSON.stringify('恢复房间失败：' + e.message + '；可稍后在面板重试')})`); }
    console.log('多人实验补丁运行中；客户端退出后结束。');
    process.on('SIGINT', async () => { await evaluate('window.__ncmMulti.hostDisconnected()').catch(() => {}); ws.close(); });
    const pulse = setInterval(() => void evaluate('window.__ncmMulti && (window.__ncmMulti.hostLastSeen=Date.now())').catch(() => {}), 10000);
    if (ws.readyState !== 3) await new Promise(resolve => ws.addEventListener('close', resolve, { once: true }));
    clearInterval(pulse);
  } else if (mode === 'reload') {
    const source = await readFile(new URL('./runtime.js', import.meta.url), 'utf8');
    const clickHook = await evaluate('Boolean(window.__ncmMulti?.clickHook)');
    await evaluate(`(() => {
      const old = window.__ncmMulti;
      if (!old) return;
      if (typeof old.dispose === 'function') return old.dispose();
      // Migration from the first in-session prototype, which had no disposer.
      ++old.generation; old.roomId = ''; old.syncing = false; clearTimeout(old.timer);
      document.getElementById('ncm-multi-panel')?.remove(); delete window.__ncmMulti;
    })()`);
    console.log(await evaluate(`(${source})(${parseInvitation.toString()},${targetSeconds.toString()},${JSON.stringify(version)})`));
    await evaluate(`window.__ncmMulti.clickHook=${clickHook}`);
    await evaluate('window.__ncmMulti.restore()');
    ws.close();
  } else if (mode === 'stop') {
    console.log(await evaluate('window.__ncmMulti?.leave()'));
    ws.close();
  } else if (mode === 'shutdown') {
    console.log(await evaluate('window.__ncmMulti ? window.__ncmMulti.shutdown() : {stopped:true}'));
    ws.close();
  } else throw new Error('用法：node ' + fileURLToPath(import.meta.url) + ' watch|status|stop|shutdown|reload');
} catch (e) { ws.close(); throw e; }
