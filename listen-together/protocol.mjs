// Independent protocol implementation; references are documented in README.md.
export function parseInvitation(raw) {
  let value = String(raw || '').trim().replace(/&amp;/g, '&').replace(/\\&/g, '&');
  if (value.length > 16000) throw new Error('邀请链接过长');
  for (let depth = 0; depth < 6; depth++) {
    const u = new URL(value);
    if (u.protocol === 'orpheus:' && u.hostname === 'open') {
      if (u.searchParams.getAll('url1').length !== 1) throw new Error('邀请目标不唯一');
      value = u.searchParams.get('url1');
      continue;
    }
    const native = u.protocol === 'orpheus:' && u.hostname === 'nm' && u.pathname.toLowerCase() === '/multilistentogether/joinroom';
    const web = ['https:', 'http:'].includes(u.protocol) && u.hostname === 'st.music.163.com' && u.pathname === '/listen-together/multishare/index.html' && u.searchParams.get('isFLT') !== 'true';
    if ((!native && !web) || u.username || u.password || u.port) throw new Error('不是官方多人一起听邀请');
    const key = native ? 'inviterId' : 'inviterUid';
    if (u.searchParams.getAll('roomId').length !== 1 || u.searchParams.getAll(key).length !== 1) throw new Error('房间参数不唯一');
    const roomId = u.searchParams.get('roomId'), inviterUid = u.searchParams.get(key);
    if (!/^[a-zA-Z0-9_-]{1,160}$/.test(roomId) || !/^[1-9]\d{0,23}$/.test(inviterUid)) throw new Error('房间参数无效');
    return { roomId, inviterUid };
  }
  throw new Error('邀请嵌套过深');
}

export function targetSeconds(info, sampledAt, now) {
  const played = Number(info.playedTime), duration = Number(info.songDuration);
  if (!Number.isFinite(played) || played < 0 || !Number.isFinite(duration) || duration <= 0) throw new Error('房间播放进度无效');
  return Math.max(0, Math.min(duration - 250, played + Math.max(0, now - sampledAt))) / 1000;
}
