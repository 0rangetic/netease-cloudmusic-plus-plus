function installMulti(parseInvitation, targetSeconds, version) {
  'use strict';
  version = version || '0.4.3';
  if (window.__ncmMulti) return { installed: true, alreadyInstalled: true, version: window.__ncmMulti.version };
  if (!window.__wxr) {
    window.webpackJsonp.push([['ncm_multi_probe_' + Date.now()], { ncm_multi_require: function(m, e, r) { window.__wxr = r; } }, [['ncm_multi_require']]]);
  }
  const r = window.__wxr;
  if (!r || !r.m || !r(8).a.getStore || !r(12).a || !r(120).a) throw new Error('客户端模块结构不匹配，停止安装');
  // The renderer bundle ships as a hot-updated webpack build, so module ids move
  // between client releases: 3.1.41.205529 -> 3.1.42.205594 shifted the invitation
  // parser 2093 -> 2092 and the playhead observable 109.b -> 110.b. Resolve both by
  // capability, validating each candidate before use, and stop if nothing matches.
  const modules = resolveClientModules(r);
  if (!modules) throw new Error('客户端模块结构不匹配，停止安装');
  const store = () => r(8).a.getStore(), dispatch = x => r(8).a.getDispatch()(x);
  const uid = () => String(store().host.uid || '');
  const api = async (path, data) => {
    const res = await r(12).a('/api/listen/together/multi/' + path)(data || {}, { cacheStrategy: { enable: false } });
    if (res?.code !== 200) throw Object.assign(new Error(res?.message || res?.body?.message || '多人接口请求失败'), { code: res?.code });
    return res.data;
  };
  const s = { version, roomId: '', account: uid(), generation: 0, syncing: true, timer: null, busy: false, pendingJoin: false, info: null, sampledAt: 0, lastBeat: 0, lastMembers: 0, members: 0, people: [], log: [], notice: '多人补丁已加载', selected: null, clickHook: false, clickHookReady: false, failures: 0, loadedKey: '', stale: false, hostLastSeen: 0 };
  const playhead = () => {
    try { return Number(modules.playheadModule.get()); } catch (_) { return 0; }
  };
  let tokenSeq = 0;
  const tokens = new Map();
  window.__ncmMultiTokenReply = (id, token, error) => {
    const p = tokens.get(id); if (!p) return;
    tokens.delete(id); clearTimeout(p.timer);
    error ? p.reject(new Error(error)) : p.resolve(token);
  };
  const freshToken = () => new Promise((resolve, reject) => {
    const id = ++tokenSeq;
    const timer = setTimeout(() => { tokens.delete(id); reject(new Error('令牌桥未响应，请重新启动实验补丁')); }, 15000);
    tokens.set(id, { resolve, reject, timer });
    try { window.__ncmMultiTokenRequest(JSON.stringify({ id })); } catch (e) { clearTimeout(timer); tokens.delete(id); reject(e); }
  });
  const textError = e => e?.msg || e?.message || e?.body?.message || ('接口错误 ' + (e?.code || '未知'));
  const note = (message, error = false) => {
    s.notice = message; s.log.push({ time: new Date().toLocaleTimeString(), message, error });
    if (s.log.length > 100) s.log.shift();
    render();
  };
  const guarded = (fn) => async () => { try { await fn(); } catch (e) { note(textError(e), true); } };
  const panel = document.createElement('div'); panel.id = 'ncm-multi-panel';
  Object.assign(panel.style, { position: 'fixed', right: '18px', top: '80px', width: '316px', zIndex: '2147483645', background: '#20232b', color: '#f5f5f7', padding: '14px', borderRadius: '12px', boxShadow: '0 6px 30px #0008', font: '13px/1.6 Microsoft YaHei,sans-serif' });
  const title = document.createElement('div'); title.textContent = '多人一起听 · 实验补丁 ' + version; title.style.fontWeight = 'bold';
  const fold = document.createElement('button'); fold.textContent = '收起'; fold.style.cssFloat = 'right';
  const body = document.createElement('div');
  fold.onclick = () => { body.hidden = !body.hidden; fold.textContent = body.hidden ? '展开' : '收起'; };
  title.append(fold); panel.append(title, body); document.body.append(panel);
  const statusLine = document.createElement('div'), songLine = document.createElement('div'), notice = document.createElement('div');
  const buttons = document.createElement('div'); buttons.style.margin = '10px 0';
  const inviteInput = document.createElement('input'); inviteInput.placeholder = '粘贴官方多人邀请链接'; inviteInput.style.cssText = 'width:100%;box-sizing:border-box;background:#303440;color:white;border:1px solid #555;border-radius:5px;padding:5px';
  const details = document.createElement('details'), summary = document.createElement('summary'), history = document.createElement('div'); summary.textContent = '任务消息记录';
  history.style.cssText = 'max-height:150px;overflow:auto;white-space:pre-wrap;font-size:12px'; details.append(summary, history);
  body.append(statusLine, songLine, notice, buttons, inviteInput, details);
  function button(label, fn) {
    const b = document.createElement('button'); b.textContent = label; b.style.cssText = 'margin:3px;padding:4px 8px;border-radius:5px;border:1px solid #555;color:white;background:#373d4a;cursor:pointer';
    b.onclick = guarded(fn); buttons.append(b); return b;
  }
  button('读取私信邀请', async () => {
    const messages = store()['page:messagesheet']?.talkmsgs || [];
    const invitations = [];
    for (const m of messages) { try { const p = JSON.parse(m.msg), raw = p.generalMsg?.nativeUrl; if (raw) { const a = parseInvitation(raw); if (!invitations.some(x => x.roomId === a.roomId)) invitations.push(a); } } catch (_) {} }
    if (invitations.length !== 1) throw new Error(invitations.length ? '当前对话有多个房间，请粘贴目标邀请链接' : '请先打开含多人邀请的私信对话');
    s.selected = invitations[0]; inviteInput.value = ''; note('已读取当前私信的多人邀请；点击“加入邀请房间”');
  });
  button('加入邀请房间', () => join(inviteInput.value ? parseInvitation(inviteInput.value) : s.selected));
  button('恢复我的房间', () => restore());
  button('重新同步', async () => { s.syncing = true; s.loadedKey = ''; await tick(true); });
  button('本机暂停', async () => { s.syncing = false; await dispatch({ type: 'playing/pause' }); note('已暂停本机，其他成员继续播放'); });
  button('退出房间', () => leave());
  button('退出并停用补丁', () => shutdown());
  function render() {
    statusLine.textContent = s.roomId ? `在线 ${s.members} 人 · ${s.stale ? '同步中断' : s.syncing ? '跟随房间' : '本机暂停'}` : '尚未连接房间';
    songLine.textContent = s.roomId ? (store().playing.resourceName || '正在读取歌曲') : '';
    notice.textContent = s.notice;
    notice.style.color = s.log[s.log.length - 1]?.error ? '#ffb6b6' : '#a6dfbd';
    history.textContent = s.log.map(x => x.time + ' ' + x.message).join('\n');
  }
  function valid(g) { return g === s.generation && uid() === s.account && !!s.roomId; }
  let heartbeatKey = '', headUntil = 0, headClosed = false, tailUntil = 0;
  function nextHeartbeatDelay() {
    // A failed sync must never keep the one-second cadence alive.
    if (s.failures) return Math.min(30000, 10000 * s.failures);
    if (!s.syncing || !s.info) return 10000;
    const now = Date.now(), info = s.info;
    const duration = Number(info.songDuration), played = Number(info.playedTime);
    if (!Number.isFinite(duration) || duration <= 0 || !Number.isFinite(played) || played < 0) return 10000;
    const key = s.generation + ':' + String(info.playSong?.songBizId || info.playSong?.songId || '');
    if (key !== heartbeatKey) { heartbeatKey = key; headUntil = 0; headClosed = false; tailUntil = 0; }
    const position = played + Math.max(0, now - s.sampledAt), remaining = duration - position;
    let headFast = false;
    if (!headClosed) {
      // The opening window cannot be renewed by a stalled or jittery snapshot.
      if (position < 3000) {
        if (!headUntil) headUntil = now + Math.min(3000, duration + 1000) - position;
        headFast = now < headUntil;
      }
      if (!headFast) headClosed = true;
    }
    // Calculate both windows together so short tracks do not stack their budgets.
    let delay = 10000;
    if (!tailUntil || now < tailUntil) {
      if (remaining > 5000) {
        // Wake at the tail boundary even when a ten-second wait would skip it.
        delay = Math.max(1000, Math.min(10000, remaining - 5000));
        if (delay < 10000 && !tailUntil) tailUntil = now + remaining + 1000;
      } else {
        // Include one poll just after the estimated end to observe the next song.
        if (!tailUntil && remaining > 0) tailUntil = now + remaining + 1000;
        if (tailUntil) tailUntil = Math.min(tailUntil, now + 6000);
        if (tailUntil > now && remaining >= -1000) delay = 1000;
      }
    }
    return headFast ? 1000 : delay;
  }
  function adopt(snapshot) {
    if (!snapshot?.roomId) throw new Error('服务端没有返回房间快照');
    s.roomId = String(snapshot.roomId); s.account = uid(); s.generation++; s.info = null; s.syncing = true;
    const playing = snapshot.roomPlaySongInfo;
    s.loadedKey = playing?.playSong && String(store().playing.resourceTrackId) === String(playing.playSong.songId) && !store().playing.isLoadingFirst ? String(playing.playSong.songBizId || playing.playSong.songId) : '';
    const agg = snapshot.multiLtRoomUserAgg;
    if (agg) { s.members = agg.onlineNums; s.people = agg.onlineUserInfos || []; }
    s.lastMembers = Date.now();
  }
  async function restore() {
    if (s.pendingJoin) return;
    if (s.busy) throw new Error('正在同步，请稍后再恢复房间');
    const account = uid(), g = s.generation;
    note('正在查询当前账号的多人房间…');
    const data = await api('match/status/get');
    if (account !== uid() || g !== s.generation) return;
    if (data.multiLtRoomSnapshot?.roomId && data.status === 'RECONNECT_SUCCESS') {
      adopt(data.multiLtRoomSnapshot); note('已恢复多人房间，正在同步播放'); await tick(true);
    } else { note('当前账号没有可恢复的多人房间'); }
  }
  async function join(invite) {
    if (!invite) throw new Error('请先读取或粘贴邀请');
    if (s.pendingJoin) return;
    if (s.roomId === invite.roomId) { note('已经在此房间'); return; }
    if (s.roomId) throw new Error('请先退出当前房间，再加入另一个房间');
    if (r(75).c.utilStatus !== 'alone') throw new Error('请先退出原版双人一起听');
    s.pendingJoin = true;
    const account = uid(), g = s.generation;
    try {
      note('正在检查多人邀请…');
      const preview = await api('landing/info/get', invite);
      if (preview.roomStatus !== 'AVAILABLE') throw new Error('多人邀请当前不可用：' + preview.roomStatus);
      const checkToken = await freshToken();
      if (account !== uid() || g !== s.generation) throw new Error('账号或房间发生变化，请重新操作');
      note('正在加入多人房间…');
      const data = await api('match/ack', { ...invite, agree: true, checkToken });
      if (account !== uid() || g !== s.generation) throw new Error('加入结果已过期，请恢复当前房间核对');
      if (!data.success || String(data.multiLtRoomSnapshot?.roomId) !== invite.roomId) throw new Error(data.failedMessage || data.failedType || '服务端未确认加入');
      adopt(data.multiLtRoomSnapshot); note('加入成功，正在同步播放'); await tick(true);
    } catch (e) { note('加入失败：' + textError(e), true); throw e; }
    finally { s.pendingJoin = false; }
  }
  async function follow(g, force) {
    const info = s.info, key = String(info.playSong?.songBizId || info.playSong?.songId || '');
    const songId = String(info.playSong?.songId || '');
    if (!/^[1-9]\d*$/.test(songId)) throw new Error('房间尚无可播放歌曲');
    if (!s.syncing || !valid(g)) return;
    const changed = s.loadedKey !== key || String(store().playing.resourceTrackId) !== songId;
    if (changed) {
      const tracks = await r(120).a(songId);
      if (!valid(g) || !s.syncing || key !== String(s.info?.playSong?.songBizId || s.info?.playSong?.songId || '')) return;
      if (!tracks?.[0]) throw new Error('无法读取房间歌曲');
      await dispatch({ type: 'async:action/doAction', payload: { actionId: 'play', data: { resource: tracks[0], resourceType: 'track', from: { scene: r(11).a.listenTogether } } } });
      const deadline = Date.now() + 15000;
      while (valid(g) && s.syncing && (String(store().playing.resourceTrackId) !== songId || store().playing.isLoadingFirst)) {
        if (Date.now() > deadline) throw new Error('歌曲加载超时，请点击重新同步');
        await new Promise(resolve => setTimeout(resolve, 150));
      }
      if (!valid(g) || !s.syncing) return;
      s.loadedKey = key;
    }
    if (!valid(g) || !s.syncing) return;
    const target = targetSeconds(s.info, s.sampledAt, Date.now());
    const drift = Math.abs(Number(modules.playheadModule.get()) - target);
    if (changed || force || drift > 2.5) await dispatch({ type: 'playing/setPlayingPosition', payload: { duration: target } });
    if (store().playing.playingState !== 2) await dispatch({ type: 'playing/resume', payload: { triggerScene: r(11).a.listenTogether } });
    if (changed || force) note('同步完成：' + store().playing.resourceName);
    render();
  }
  async function tick(force = false) {
    if (s.busy || !s.roomId) return;
    if (uid() !== s.account) { ++s.generation; clearTimeout(s.timer); s.roomId = ''; note('账号已切换，同步已停止', true); return; }
    s.busy = true; clearTimeout(s.timer);
    const g = s.generation, started = Date.now();
    try {
      const data = await api('match/heartbeat', { roomId: s.roomId });
      if (!valid(g)) return;
      s.lastBeat = Date.now(); s.failures = 0; s.stale = false;
      const info = data.roomPlaySongInfo;
      if (info && (!s.info || Number(info.version) >= Number(s.info.version))) { s.info = info; s.sampledAt = (started + Date.now()) / 2; }
      if (Date.now() - s.lastMembers > 15000) {
        try {
          const status = await api('match/status/get');
          if (!valid(g)) return;
          if (status.multiLtRoomSnapshot?.roomId !== s.roomId) {
            ++s.generation; s.roomId = ''; s.syncing = false;
            await dispatch({ type: 'playing/pause' }); note('账号的多人房间已改变；请恢复当前房间', true); return;
          }
          const agg = status.multiLtRoomSnapshot.multiLtRoomUserAgg;
          if (agg) { s.members = agg.onlineNums; s.people = agg.onlineUserInfos || []; }
          s.lastMembers = Date.now();
        } catch (e) {
          if ([301, 302, 488].includes(e.code)) throw e;
          note('播放仍在同步；成员刷新失败：' + textError(e), true);
        }
      }
      if (s.info) await follow(g, force);
    } catch (e) {
      s.failures++; s.stale = true; note('同步失败：' + textError(e), true);
      if ([301, 302, 488].includes(e.code)) { ++s.generation; s.roomId = ''; s.syncing = false; await dispatch({ type: 'playing/pause' }); }
    } finally {
      s.busy = false; render();
      // Heartbeat snapshots are the initial sync channel. Room changes can take one poll.
      if (valid(g)) {
        s.heartbeatDelayMs = nextHeartbeatDelay();
        s.timer = setTimeout(() => tick(), s.heartbeatDelayMs);
      }
    }
  }
  async function leave() {
    if (s.pendingJoin) throw new Error('正在加入房间，请等待加入结果后再退出');
    if (!s.roomId) { note('当前没有已连接的多人房间'); return; }
    const roomId = s.roomId, account = s.account;
    ++s.generation; clearTimeout(s.timer); s.syncing = false;
    note('正在退出多人房间…');
    try {
      if (uid() !== account) { s.roomId = ''; throw new Error('账号已改变；已停止本机同步，未替其他账号提交退出'); }
      const data = await api('match/exit', { roomId, exitType: 'NORMAL_END' });
      if (!data.success) throw new Error(data.failedMessage || data.failedType || '服务端未确认退出');
      s.roomId = ''; s.info = null; await dispatch({ type: 'playing/pause' }); note('已退出多人房间');
    } catch (e) { note('退出失败，同步已停止：' + textError(e) + '；请恢复房间核对', true); throw e; }
  }
  async function shutdown() {
    await leave();
    s.dispose();
    window.__ncmMultiStopBridge?.('stop');
    return { stopped: true };
  }
  // Do not tie this to clickHook: a start that failed before enabling the
  // debugger still leaves a live page patch that must report itself orphaned.
  const hostWatch = setInterval(() => {
    if (Date.now() - s.hostLastSeen > 25000) s.hostDisconnected();
  }, 10000);
  let suppressUntil = 0;
  const originalToast = r(3).L.error;
  r(3).L.error = function(message, ...args) {
    if (Date.now() < suppressUntil && typeof message === 'string' && message.includes('暂不支持多人一起听')) return;
    return originalToast.call(this, message, ...args);
  };
  Object.assign(s, {
    restore, leave, tick, shutdown,
    reportFailure(message) { note(message, true); },
    receive(raw) { let invite; try { invite = parseInvitation(raw); } catch (_) { return; } suppressUntil = Date.now() + 1000; void join(invite).catch(() => {}); },
    hostDisconnected() { s.clickHook = false; s.bridgeDisconnected = true; note('工具箱连接已断开；已加入房间仍继续同步，请在工具箱重新连接', true); },
    dispose() {
      ++s.generation; s.roomId = ''; s.syncing = false; clearTimeout(s.timer); clearInterval(hostWatch);
      r(3).L.error = originalToast; panel.remove();
      for (const p of tokens.values()) { clearTimeout(p.timer); p.reject(new Error('补丁已更新')); }
      tokens.clear(); delete window.__ncmMulti; delete window.__ncmMultiTokenReply;
    },
    status() { return { version: s.version, installed: true, transport: s.transport, bridgeDisconnected: !!s.bridgeDisconnected, hostAlive: Date.now() - s.hostLastSeen < 20000, roomId: s.roomId, members: s.members, syncing: s.syncing, stale: s.stale, busy: s.busy, lastBeat: s.lastBeat, heartbeatDelayMs: s.heartbeatDelayMs, songId: s.info?.playSong?.songId, nativeSongId: store().playing.resourceTrackId, position: playhead(), target: s.info ? targetSeconds(s.info, s.sampledAt, Date.now()) : null, notice: s.notice, clickHook: s.clickHook, clickHookReady: s.clickHookReady, moduleMap: modules.label, messages: s.log.slice(-8) }; },
    // Resolved parser export for the debugger click hook (see control.mjs).
    parserTarget() { return modules.parserFunction(); },
  });
  window.__ncmMulti = s;
  clearInterval(window.__codexLtTimer); window.__codexLtTimer = null;
  render(); note('实验补丁已加载，正在连接官方多人房间');
  return { installed: true, version: s.version };

  // Locate the client renderer modules this patch needs. Module ids are not stable
  // across client releases, so each capability is resolved by shape and validated.
  // Returns null when any capability is missing, which stops installation instead of
  // running against the wrong module. Declared inside installMulti so this file stays
  // a single function expression, as control.mjs and the tests both evaluate it that way.
  function resolveClientModules(r) {
    'use strict';
    const ids = Object.keys(r.m || {}).map(Number).filter(id => Number.isFinite(id));
    const source = id => { try { return String(r.m[id] || ''); } catch (_) { return ''; } };
    const exportsOf = id => { try { const m = r(id); return m && typeof m === 'object' ? m : null; } catch (_) { return null; } };

    // 1. Invitation parser: parseInvitation() in this patch already handles the
    //    official links, so the module is only needed for the click hook binding.
    //    Prefer the module the client's own click handler uses (it returns an
    //    action object for a two-level orpheus://open?url1= wrapper).
    const parserSample = 'orpheus://open?url1=' + encodeURIComponent('orpheus://nm/multiListenTogether/joinRoom?roomId=1&inviterId=1');
    let parser = null;
    for (const id of ids) {
      const text = source(id);
      // The invitation parser is the only module that contains this literal.
      // Never instantiate or call unrelated modules: many of them open the
      // client's own update, install or risk-control dialogs when touched.
      if (!text.includes('multiListenTogether')) continue;
      const mod = exportsOf(id);
      if (!mod) continue;
      for (const ns of Object.keys(mod)) {
        const fn = mod[ns];
        if (typeof fn !== 'function') continue;
        let fnSource = '';
        try { fnSource = String(fn); } catch (_) { continue; }
        // Only the invitation parser references this action literal; do not
        // invoke any other function inside the matched module.
        if (!fnSource.includes('multiListenTogether')) continue;
        let value;
        try { value = fn(parserSample); } catch (_) { continue; }
        if (value && typeof value === 'object' && value.action === 'multiListenTogether') { parser = { id, ns }; break; }
      }
      if (parser) break;
    }
    if (!parser) return null;

    // 2. Playback position observable: a numeric getValue() exported from a module
    //    whose source mentions position/progress. The value is milliseconds.
    const playheadSource = () => {
      for (const id of ids) {
        const text = source(id);
        // Check the source before requiring the module. Installing every
        // webpack module executed unrelated client UI code and caused
        // update/install and risk-control popups.
        if (!/position|progress/i.test(text) || !text.includes('getValue')) continue;
        const mod = exportsOf(id);
        if (!mod) continue;
        for (const ns of Object.keys(mod)) {
          const value = mod[ns];
          if (!value || typeof value !== 'object' || typeof value.getValue !== 'function') continue;
          try { if (typeof value.getValue() === 'number') return { id, ns, get: () => value.getValue() }; } catch (_) {}
        }
      }
      return null;
    };
    const playheadModule = playheadSource();
    if (!playheadModule) return null;

    return {
      parser,
      playheadModule,
      // Expose the resolved parser function so the click hook binds to the module the
      // client actually uses, instead of a hard-coded id that moves between releases.
      parserFunction: () => { try { return r(parser.id)[parser.ns]; } catch (_) { return null; } },
      label: { parser: parser.id + '.' + parser.ns, playhead: playheadModule.id + '.' + playheadModule.ns },
    };
  }
}
