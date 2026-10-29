import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { parseInvitation, targetSeconds } from './protocol.mjs';
import { version } from './version.mjs';
const source = await readFile(new URL('./runtime.js', import.meta.url), 'utf8');
function fixture({ exitSuccess = true, initialPaused = false, fakeClock = false, playedTime = 42000, songDuration = 180000, advancing = false, clientLayout = '3.1.42', skipInstall = false } = {}) {
  const nodes = [], calls = [];
  let now = 1000000, timerId = 0, heartbeatError = null, heartbeatGate = null;
  const timers = new Map(), epoch = now;
  class ClockDate extends Date {
    constructor(...args) { super(...(args.length ? args : [now])); }
    static now() { return now; }
  }
  const clock = fakeClock ? {
    Date: ClockDate,
    setTimeout(callback, delay) { const id = ++timerId; timers.set(id, { callback, at: now + delay }); return id; },
    clearTimeout(id) { timers.delete(id); }, setInterval() { return ++timerId; }, clearInterval() {},
  } : { setTimeout, clearTimeout, setInterval, clearInterval };
  const store = { host: { uid: '123' }, playing: { resourceTrackId: '456', resourceName: '测试歌曲', playingState: initialPaused ? 1 : 2, isLoadingFirst: false } };
  const info = { playedTime, songDuration, version: 1, playSong: { songId: 456, songBizId: 987 } };
  const snapshot = { roomId: 'test_room_1', roomPlaySongInfo: info, multiLtRoomUserAgg: { onlineNums: 3, onlineUserInfos: [] } };
  let position = 42, stopped = false;
  const toast = { error() {} }, originalToast = toast.error;
  const modules = {
    3: { L: toast }, 8: { a: { getStore: () => store, getDispatch: () => async action => { calls.push(action); if (action.type === 'playing/pause') store.playing.playingState = 1; if (action.type === 'playing/resume') store.playing.playingState = 2; if (action.type === 'playing/setPlayingPosition') position = action.payload.duration; } } },
    12: { a: path => async args => {
      calls.push({ path, args });
      if (path.endsWith('status/get')) return { code: 200, data: { status: 'RECONNECT_SUCCESS', multiLtRoomSnapshot: snapshot } };
      if (path.endsWith('heartbeat')) {
        if (heartbeatGate) await heartbeatGate;
        if (heartbeatError) return heartbeatError;
        return { code: 200, data: { roomPlaySongInfo: { ...info, playedTime: info.playedTime + (advancing ? now - epoch : 0) } } };
      }
      if (path.endsWith('exit')) return { code: 200, data: { success: exitSuccess, failedMessage: '模拟退出拒绝' } };
      throw new Error('Unexpected API ' + path);
    } },
    75: { c: { utilStatus: 'alone' } }, 120: { a: async () => [{ id: '456' }] }, 11: { a: { listenTogether: 'listenTogether' } },
  };
  // Two real client layouts. 3.1.41.205529 kept the invitation parser in 2093 and the
  // playhead observable in 109.b; 3.1.42.205594 moved them to 2092.c and 110.b.
  // The patch must resolve both by capability, so the fixture can model either.
  const parserSample = 'orpheus://open?url1=orpheus%3A%2F%2Fnm%2FmultiListenTogether%2FjoinRoom%3FroomId%3D1%26inviterId%3D1';
  const makeParser = () => src => String(src).includes('multiListenTogether') ? { action: 'multiListenTogether' } : null;
  const layout = clientLayout || '3.1.42';
  if (layout === '3.1.41') {
    modules[2093] = { c: makeParser() };
    modules[109] = { b: { getValue: () => position } };
  } else {
    modules[2092] = { a: () => null, b: () => null, c: makeParser() };
    modules[110] = { b: { getValue: () => position } };
  }
  const r = id => modules[id];
  r.m = layout === '3.1.41'
    ? { 109: 'function(){/*position progress*/}', 2093: 'function(){/*multiListenTogether parser*/}' }
    : { 110: 'function(){/*position progress*/}', 2092: 'function(){/*multiListenTogether parser*/}' };
  const makeNode = () => { const n = { style: {}, children: [], append(...children) { this.children.push(...children); }, remove() { this.removed = true; } }; nodes.push(n); return n; };
  const window = { __wxr: r, __ncmMultiStopBridge: () => { stopped = true; } };
  const context = vm.createContext({ window, document: { createElement: makeNode, body: makeNode() }, ...clock, console });
  const install = vm.runInContext('(' + source + ')', context);
  let result = null;
  if (!skipInstall) result = install(parseInvitation, targetSeconds, version);
  return {
    s: window.__ncmMulti, window, nodes, calls, info, store, install, result, toast, originalToast, stopped: () => stopped,
    beats: () => calls.filter(x => x.path?.endsWith('heartbeat')).length,
    failHeartbeat(code = 500) { heartbeatError = { code, message: '模拟心跳失败' }; },
    recoverHeartbeat() { heartbeatError = null; },
    blockHeartbeats() { let release; heartbeatGate = new Promise(resolve => { release = resolve; }); return () => { heartbeatGate = null; release(); }; },
    async advance(ms) {
      assert.ok(fakeClock);
      const end = now + ms;
      for (;;) {
        const next = [...timers.entries()].sort((a, b) => a[1].at - b[1].at)[0];
        if (!next || next[1].at > end) break;
        now = next[1].at; timers.delete(next[0]); await next[1].callback();
      }
      now = end;
    },
  };
}
test('restore follows existing song without restarting it; complete shutdown restores hooks and stops bridge', async () => {
  const f = fixture();
  try {
    await f.s.restore();
    assert.equal(f.s.members, 3);
    assert.equal(f.s.status().songId, 456);
    assert.equal(f.calls.some(x => x.type === 'async:action/doAction'), false);
    await f.s.shutdown();
    assert.equal(f.stopped(), true);
    assert.equal(f.window.__ncmMulti, undefined);
    assert.equal(f.toast.error, f.originalToast);
    assert.ok(f.nodes.some(x => x.id === 'ncm-multi-panel' && x.removed));
  } finally { f.window.__ncmMulti?.dispose(); }
});
test('repeated installation does not create a second panel or state machine', () => {
  const f = fixture();
  try {
    const result = f.install(parseInvitation, targetSeconds, version);
    assert.equal(result.alreadyInstalled, true);
    assert.equal(f.nodes.filter(x => x.id === 'ncm-multi-panel').length, 1);
  } finally { f.s.dispose(); }
});

test('module ids are resolved by capability on both 3.1.41 and 3.1.42 layouts', async () => {
  for (const [clientLayout, expected] of [['3.1.41', '2093.c'], ['3.1.42', '2092.c']]) {
    const f = fixture({ clientLayout });
    try {
      assert.equal(f.s.version, version);
      assert.equal(f.s.status().moduleMap.parser, expected, clientLayout + ' parser id');
      assert.equal(f.s.status().position, 42, clientLayout + ' playhead resolves');
      await f.s.restore();
      assert.equal(f.s.roomId, 'test_room_1');
      assert.equal(f.s.members, 3);
    } finally { f.s.dispose(); }
  }
});

test('an unrecognised client layout stops installation instead of using wrong modules', () => {
  // Build a bundle whose parser export no longer returns the multi action, i.e. a
  // genuinely different renderer build, and confirm installation refuses to proceed.
  const broken = fixture({ clientLayout: '3.1.42', skipInstall: true });
  try {
    broken.window.__wxr(2092).c = () => null;
    assert.throws(() => broken.install(parseInvitation, targetSeconds, version), /客户端模块结构不匹配/);
    assert.equal(broken.window.__ncmMulti, undefined);
  } finally { broken.s?.dispose(); }
});
test('failed room exit preserves visible error and prevents a false successful shutdown', async () => {
  const f = fixture({ exitSuccess: false });
  try {
    await f.s.restore();
    await assert.rejects(f.s.shutdown(), /模拟退出拒绝/);
    assert.equal(f.stopped(), false);
    assert.equal(f.window.__ncmMulti, f.s);
    assert.equal(f.s.log[f.s.log.length - 1].error, true);
    assert.equal(f.s.syncing, false);
  } finally { f.s.dispose(); }
});
test('account change stops old-room work without sending exit as the new account', async () => {
  const f = fixture();
  try {
    await f.s.restore();
    f.window.__wxr(8).a.getStore().host.uid = 'another_account';
    await f.s.tick();
    assert.equal(f.s.roomId, '');
    assert.equal(f.calls.some(x => x.path?.endsWith('exit')), false);
    assert.match(f.s.notice, /账号已切换/);
  } finally { f.s.dispose(); }
});
test('host disconnect remains visible and exposes the reconnect signal to the toolbox', () => {
  const f = fixture();
  try {
    f.s.clickHook = true; f.s.clickHookReady = true;
    f.s.hostDisconnected();
    assert.equal(f.s.status().bridgeDisconnected, true);
    assert.equal(f.s.status().clickHook, false);
    assert.match(f.s.status().notice, /重新连接/);
    assert.equal(f.s.log.at(-1).error, true);
  } finally { f.s.dispose(); }
});

test('ordinary playback polls every ten seconds and wakes at the five-second tail boundary', async () => {
  const f = fixture({ fakeClock: true, playedTime: 158000, advancing: true });
  try {
    await f.s.restore();
    assert.equal(f.s.status().heartbeatDelayMs, 10000);
    await f.advance(9999); assert.equal(f.beats(), 1);
    await f.advance(1); assert.equal(f.beats(), 2);
    assert.equal(f.s.status().heartbeatDelayMs, 7000);
    await f.advance(6999); assert.equal(f.beats(), 2);
    await f.advance(1); assert.equal(f.beats(), 3);
    assert.equal(f.s.status().heartbeatDelayMs, 1000);
    await f.advance(5000); assert.equal(f.beats(), 8);
    await f.advance(1000); assert.equal(f.beats(), 9);
    assert.equal(f.s.status().heartbeatDelayMs, 10000);
  } finally { f.s.dispose(); }
});

test('stalled tail snapshots cannot keep one-second polling alive indefinitely', async () => {
  const f = fixture({ fakeClock: true, playedTime: 175000 });
  try {
    await f.s.restore();
    await f.advance(6000);
    assert.equal(f.beats(), 7);
    assert.equal(f.s.status().heartbeatDelayMs, 10000);
    await f.advance(9999); assert.equal(f.beats(), 7);
    await f.advance(1); assert.equal(f.beats(), 8);
    assert.equal(f.s.status().heartbeatDelayMs, 10000);
  } finally { f.s.dispose(); }
});

test('stagnation just before the tail and jitter around five seconds cannot renew the burst', async () => {
  for (const playedTime of [174999, 175000]) {
    const f = fixture({ fakeClock: true, playedTime });
    try {
      await f.s.restore();
      for (let i = 0; i < 24; i++) {
        f.info.playedTime = i % 6 === 5 ? 174999 : playedTime;
        await f.advance(1000);
      }
      assert.ok(f.beats() <= 9, 'tail jitter must not sustain one-second network polling');
      assert.equal(f.s.status().heartbeatDelayMs, 10000);
      f.info.playSong = { songId: 456, songBizId: 988 }; f.info.playedTime = 175000;
      await f.s.tick();
      assert.equal(f.s.status().heartbeatDelayMs, 1000, 'a new playback instance gets its own burst');
    } finally { f.s.dispose(); }
  }
});

test('the next song uses its opening window then ordinary polling; local pause does not accelerate', async () => {
  const f = fixture({ fakeClock: true, playedTime: 175000 });
  try {
    await f.s.restore();
    assert.equal(f.s.status().heartbeatDelayMs, 1000);
    f.info.playedTime = 1000; f.info.playSong = { songId: 456, songBizId: 988 }; ++f.info.version;
    await f.advance(1000);
    assert.equal(f.s.status().heartbeatDelayMs, 1000);
    await f.advance(2000);
    assert.equal(f.s.status().heartbeatDelayMs, 10000);
    f.s.syncing = false; f.info.playedTime = 178000; ++f.info.version;
    await f.advance(10000);
    assert.equal(f.s.status().heartbeatDelayMs, 10000);
  } finally { f.s.dispose(); }
});

test('opening playback polls each second for the first three seconds then every ten seconds', async () => {
  const f = fixture({ fakeClock: true, playedTime: 0, advancing: true });
  try {
    await f.s.restore();
    assert.equal(f.s.status().heartbeatDelayMs, 1000);
    await f.advance(999); assert.equal(f.beats(), 1);
    await f.advance(1); assert.equal(f.beats(), 2);
    await f.advance(1000); assert.equal(f.beats(), 3);
    assert.equal(f.s.status().heartbeatDelayMs, 1000);
    await f.advance(1000); assert.equal(f.beats(), 4);
    assert.equal(f.s.status().heartbeatDelayMs, 10000);
    await f.advance(9999); assert.equal(f.beats(), 4);
    await f.advance(1); assert.equal(f.beats(), 5);
  } finally { f.s.dispose(); }
});

test('joining two seconds into a song only uses the remaining opening second', async () => {
  const f = fixture({ fakeClock: true, playedTime: 2000, advancing: true });
  try {
    await f.s.restore(); assert.equal(f.s.status().heartbeatDelayMs, 1000);
    await f.advance(1000); assert.equal(f.s.status().heartbeatDelayMs, 10000);
  } finally { f.s.dispose(); }
});

test('a frozen opening or jitter around three seconds cannot renew the opening window', async () => {
  for (const playedTime of [0, 2999]) {
    const f = fixture({ fakeClock: true, playedTime });
    try {
      await f.s.restore();
      for (let i = 0; i < 23; i++) {
        f.info.playedTime = i % 2 ? playedTime : 3000;
        await f.advance(1000);
      }
      assert.ok(f.beats() <= 6);
      assert.equal(f.s.status().heartbeatDelayMs, 10000);
      f.info.playedTime = 0; await f.s.tick();
      assert.equal(f.s.status().heartbeatDelayMs, 10000, 'seeking back cannot reopen the same opening burst');
    } finally { f.s.dispose(); }
  }
});

test('a frozen opening independently expires even while playedTime stays zero', async () => {
  const f = fixture({ fakeClock: true, playedTime: 0 });
  try {
    await f.s.restore(); await f.advance(3000);
    assert.equal(f.beats(), 4);
    assert.equal(f.s.status().heartbeatDelayMs, 10000);
    await f.advance(20000); assert.equal(f.beats(), 6);
  } finally { f.s.dispose(); }
});

test('overlapping opening and tail windows use one bounded cadence for short songs', async () => {
  for (const songDuration of [2000, 4000, 8000]) {
    const f = fixture({ fakeClock: true, playedTime: 0, songDuration, advancing: true });
    try {
      await f.s.restore(); await f.advance(songDuration + 1000);
      assert.equal(f.beats(), (songDuration + 1000) / 1000 + 1);
      assert.equal(f.s.status().heartbeatDelayMs, 10000);
    } finally { f.s.dispose(); }
  }
  const frozen = fixture({ fakeClock: true, playedTime: 0, songDuration: 4000 });
  try {
    await frozen.s.restore(); await frozen.advance(5000);
    assert.equal(frozen.beats(), 6, 'overlapping frozen windows must not stack their budgets');
    assert.equal(frozen.s.status().heartbeatDelayMs, 10000);
  } finally { frozen.s.dispose(); }
});

test('opening acceleration preserves pause, failure backoff and room shutdown', async () => {
  const f = fixture({ fakeClock: true, playedTime: 0 });
  try {
    await f.s.restore(); f.failHeartbeat();
    await f.advance(1000); assert.equal(f.s.status().heartbeatDelayMs, 10000);
    f.recoverHeartbeat(); await f.advance(10000);
    assert.equal(f.s.status().heartbeatDelayMs, 10000);
    f.info.playSong = { songId: 456, songBizId: 988 }; f.s.syncing = false;
    await f.s.tick(); assert.equal(f.s.status().heartbeatDelayMs, 10000);
    await f.s.leave(); const count = f.beats();
    await f.advance(60000); assert.equal(f.beats(), count);
  } finally { f.s.dispose(); }
});

test('tail failures back off to ten, twenty and thirty seconds and remain visible', async () => {
  const f = fixture({ fakeClock: true, playedTime: 175000 });
  try {
    await f.s.restore(); f.failHeartbeat();
    await f.advance(1000);
    assert.equal(f.s.status().heartbeatDelayMs, 10000);
    assert.equal(f.s.status().stale, true);
    assert.match(f.s.status().notice, /同步失败.*模拟心跳失败/);
    assert.equal(f.s.log.at(-1).error, true);
    await f.advance(10000); assert.equal(f.s.status().heartbeatDelayMs, 20000);
    await f.advance(20000); assert.equal(f.s.status().heartbeatDelayMs, 30000);
    await f.advance(30000); assert.equal(f.s.status().heartbeatDelayMs, 30000);
    f.recoverHeartbeat(); f.info.playedTime = 1000; ++f.info.version;
    await f.advance(30000);
    assert.equal(f.s.status().heartbeatDelayMs, 10000);
    assert.equal(f.s.status().stale, false);
  } finally { f.s.dispose(); }
});

test('concurrent tick calls never send overlapping heartbeats; invalid login stops scheduling', async () => {
  const f = fixture({ fakeClock: true });
  try {
    await f.s.restore();
    const release = f.blockHeartbeats(), pending = f.s.tick();
    await f.s.tick(); assert.equal(f.beats(), 2);
    release(); await pending;
    f.failHeartbeat(301); await f.advance(10000);
    assert.equal(f.s.roomId, '');
    const count = f.beats(); await f.advance(60000); assert.equal(f.beats(), count);
  } finally { f.s.dispose(); }
});
