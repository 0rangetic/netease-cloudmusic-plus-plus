// Local desktop play boundaries. No cookies, network calls or writes to client data.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';
import { pathToFileURL } from 'node:url';

export const defaultRoot = () => path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'NeteaseToolbox', 'LocalPlayback');
export function findSources() {
  return [...new Set([process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), process.env.APPDATA].filter(Boolean))]
    .flatMap(root => ['CloudMusic', 'CloudMusicX64'].map(client => path.join(root, 'Netease', client, 'Library', 'webdb.dat')))
    .filter(file => fs.existsSync(file));
}
const suffixes = ['', '-wal', '-shm', '-journal'];
function signature(file) {
  return suffixes.map(suffix => {
    try { const stat = fs.statSync(file + suffix); return `${stat.size}:${stat.mtimeMs}:${stat.ctimeMs}`; }
    catch (e) { if (e.code === 'ENOENT') return null; throw e; }
  });
}

// Copy all sidecars only while the source is unchanged, then validate the copy.
// Opening this writable *temporary copy* allows SQLite to recover a hot journal.
export function readSnapshot(source) {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'ncm-local-plays-'));
  const target = path.join(temporary, 'webdb.dat');
  try {
    let last;
    for (let attempt = 0; attempt < 3; attempt++) {
      let db;
      try {
        for (const suffix of suffixes) fs.rmSync(target + suffix, { force: true });
        const before = signature(source);
        for (let i = 0; i < suffixes.length; i++) if (before[i] !== null) fs.copyFileSync(source + suffixes[i], target + suffixes[i]);
        if (JSON.stringify(before) !== JSON.stringify(signature(source))) throw new Error('客户端数据库正在写入，快照已丢弃');
        const head = Buffer.alloc(16);
        const fd = fs.openSync(target, 'r');
        try { fs.readSync(fd, head, 0, 16, 0); } finally { fs.closeSync(fd); }
        if (head.toString('latin1') !== 'SQLite format 3\0') throw new Error('数据库快照的 SQLite 文件头无效');
        db = new DatabaseSync(target);
        if (db.prepare('PRAGMA quick_check').all().some(row => Object.values(row)[0] !== 'ok')) throw new Error('数据库快照完整性检查失败');
        const tables = new Set(db.prepare("SELECT name FROM sqlite_master WHERE type='table'").all().map(row => row.name));
        if (!tables.has('playingCount')) throw new Error('客户端数据库缺少 playingCount，当前版本不兼容');
        const tracks = new Map();
        if (tables.has('dbTrack')) for (const row of db.prepare('SELECT id,jsonStr FROM dbTrack').iterate()) {
          try {
            const meta = JSON.parse(row.jsonStr);
            tracks.set(String(row.id), { name: meta.name || '', artists: (meta.artists || meta.ar || []).map(a => a.name || '').filter(Boolean).join(' / '), album: (meta.album || meta.al || {}).name || '', span: Number(meta.duration || meta.dt || 0) / 1000 });
          } catch { /* missing metadata does not invalidate a play boundary */ }
        }
        const events = [];
        let invalid = 0;
        for (const table of ['playingCount', 'tsPlayingCount']) if (tables.has(table)) {
          for (const row of db.prepare(`SELECT id,resourceId,playDuration,updateTime,source,uid,resourceType FROM ${table} ORDER BY id`).iterate()) {
            if (row.resourceType && row.resourceType !== 'track') continue;
            const song = String(row.resourceId || ''), ts = Number(row.updateTime), seconds = Number(row.playDuration || 0);
            if (!/^\d+$/.test(song) || Number(song) <= 0 || !Number.isSafeInteger(ts) || ts <= 0 || !Number.isFinite(seconds) || seconds < 0) { invalid++; continue; }
            const uid = /^\d+$/.test(String(row.uid || '')) && Number(row.uid) > 0 ? String(row.uid) : 'unknown';
            events.push({ uid, song, ts, seconds, rowId: Number(row.id), table, source: String(row.source || ''), ...(tracks.get(song) || { name: '', artists: '', album: '', span: 0 }) });
          }
        }
        return { events, invalid };
      } catch (e) { last = e; }
      finally { if (db) db.close(); }
    }
    throw last;
  } finally { fs.rmSync(temporary, { recursive: true, force: true }); }
}

function accountDirectory(root, uid) {
  if (uid !== 'unknown' && !/^\d+$/.test(uid)) throw new Error('账号 ID 无效');
  return path.join(root, uid);
}
function openArchive(file) {
  const db = new DatabaseSync(file);
  db.exec(`PRAGMA busy_timeout=5000;
    CREATE TABLE IF NOT EXISTS events (
      seq INTEGER PRIMARY KEY AUTOINCREMENT, event_key TEXT UNIQUE NOT NULL,
      client TEXT NOT NULL, table_name TEXT NOT NULL, row_id INTEGER NOT NULL,
      song TEXT NOT NULL, ts INTEGER NOT NULL, seconds REAL NOT NULL,
      source TEXT NOT NULL, name TEXT NOT NULL, artists TEXT NOT NULL, album TEXT NOT NULL, span REAL NOT NULL,
      observed_at INTEGER NOT NULL);
    CREATE TABLE IF NOT EXISTS state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
    CREATE UNIQUE INDEX IF NOT EXISTS boundary_identity ON events(song,ts);`);
  return db;
}

export function saveEvents(root, source, events) {
  const client = crypto.createHash('sha256').update(path.resolve(source).toLowerCase()).digest('hex').slice(0, 16);
  const groups = Map.groupBy(events, event => event.uid);
  let added = 0;
  for (const [uid, group] of groups) {
    const directory = accountDirectory(root, uid);
    fs.mkdirSync(directory, { recursive: true });
    const db = openArchive(path.join(directory, 'plays.sqlite3'));
    try {
      db.exec('BEGIN IMMEDIATE');
      const insert = db.prepare(`INSERT INTO events(event_key,client,table_name,row_id,song,ts,seconds,source,name,artists,album,span,observed_at)
        VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT DO NOTHING`);
      const update = db.prepare(`UPDATE events SET seconds=MAX(seconds,?),source=?,name=CASE WHEN ?<>'' THEN ? ELSE name END,
        artists=CASE WHEN ?<>'' THEN ? ELSE artists END,album=CASE WHEN ?<>'' THEN ? ELSE album END,span=CASE WHEN ?>0 THEN ? ELSE span END WHERE song=? AND ts=?`);
      for (const event of group) {
        // The event timestamp survives table pruning / row-id resets. Ignore row id for deduplication.
        // Both client tables can carry the same boundary; prefer the first (playingCount).
        const key = `${client}:${event.song}:${event.ts}`;
        added += Number(insert.run(key, client, event.table, event.rowId, event.song, event.ts, event.seconds, event.source, event.name, event.artists, event.album, event.span, Date.now()).changes);
        update.run(event.seconds, event.source, event.name, event.name, event.artists, event.artists, event.album, event.album, event.span, event.span, event.song, event.ts);
      }
      db.prepare('INSERT OR REPLACE INTO state VALUES(?,?)').run('last_collected_at', String(Date.now()));
      db.exec('COMMIT');
    } catch (e) { try { db.exec('ROLLBACK'); } catch {} throw e; }
    finally { db.close(); }
  }
  return added;
}

const time = ts => new Date(ts).toLocaleString('sv-SE', { timeZone: 'Asia/Shanghai' });
export function summarizeEvents(events) {
  const songs = new Map(), sessions = [];
  const streams = Map.groupBy(events, e => `${e.client}:${e.table_name}`);
  for (const stream of streams.values()) {
    let current;
    for (const event of stream) {
      let song = songs.get(event.song);
      if (!song) { song = { song_id: event.song, name: event.name || event.song, artists: event.artists, album: event.album, count: 0, first: event.ts, last: event.ts }; songs.set(event.song, song); }
      song.count++; song.first = Math.min(song.first, event.ts); song.last = Math.max(song.last, event.ts);
      if (event.name) song.name = event.name;
      if (event.artists) song.artists = event.artists;
      if (event.album) song.album = event.album;
      const gap = current ? (event.ts - current.events.at(-1).ts) / 1000 : -1;
      const span = event.span || (current && current.events.at(-1).span) || 240;
      // Partial plays and manual restarts count. Long pauses / song changes / ID resets break a run.
      if (current && current.song_id === event.song && event.row_id === current.events.at(-1).row_id + 1 && gap >= 0 && gap <= span * 1.5 + 25) {
        current.events.push(event); current.gaps_sec.push(Math.round(gap * 10) / 10);
      } else {
        current = { song_id: event.song, events: [event], gaps_sec: [] }; sessions.push(current);
      }
    }
  }
  return {
    total: events.length,
    songs: [...songs.values()].sort((a,b) => b.count-a.count || b.last-a.last).map(s => ({ ...s, first: time(s.first), last: time(s.last) })),
    sessions: sessions.filter(s => s.events.length >= 2).map(s => ({
      song_id: s.song_id, name: songs.get(s.song_id).name, artists: songs.get(s.song_id).artists,
      count: s.events.length, started_at: time(s.events[0].ts), ended_at: time(s.events.at(-1).ts), gaps_sec: s.gaps_sec,
      estimated_span: s.events.some(e => !e.span),
      evidence: s.events.map(e => ({ row_id: e.row_id, time: time(e.ts), seconds: e.seconds, source: e.source })),
    })).sort((a,b) => b.count-a.count || b.started_at.localeCompare(a.started_at)),
  };
}

export function report(root, uid) {
  if (uid) accountDirectory(root, uid);
  const accounts = fs.existsSync(root) ? fs.readdirSync(root).filter(id => (id === 'unknown' || /^\d+$/.test(id)) && fs.existsSync(path.join(root, id, 'plays.sqlite3'))).sort() : [];
  if (uid && !accounts.includes(uid)) accounts.push(uid);
  const selected = uid || accounts[0] || '';
  if (!selected) return { accounts, uid: '', total: 0, songs: [], sessions: [], last_collected_at: '' };
  if (!fs.existsSync(path.join(accountDirectory(root, selected), 'plays.sqlite3'))) return { accounts, uid: selected, total: 0, songs: [], sessions: [], last_collected_at: '' };
  const db = new DatabaseSync(path.join(accountDirectory(root, selected), 'plays.sqlite3'), { readOnly: true });
  try {
    const events = db.prepare('SELECT * FROM events ORDER BY seq').all();
    const last = db.prepare("SELECT value FROM state WHERE key='last_collected_at'").get();
    return { accounts, uid: selected, ...summarizeEvents(events), last_collected_at: last ? time(Number(last.value)) : '' };
  } finally { db.close(); }
}

export function collect(root, sources = findSources()) {
  if (!sources.length) throw new Error('未找到网易云桌面客户端 webdb.dat；请先运行客户端');
  fs.mkdirSync(root, { recursive: true });
  let added = 0, checked = 0, successful = 0;
  const errors = [], warnings = [];
  for (const source of sources) {
    try {
      const snapshot = readSnapshot(source);
      checked += snapshot.events.length;
      added += saveEvents(root, source, snapshot.events);
      successful++;
      if (snapshot.invalid) warnings.push(`跳过 ${snapshot.invalid} 条无效播放记录`);
    } catch (e) { errors.push(`${path.basename(path.dirname(path.dirname(source)))}：${e.message}`); }
  }
  if (!successful) throw new Error(errors.join('；'));
  return { added, checked, errors, warnings };
}

if (process.argv[1] && import.meta.url === pathToFileURL(fs.realpathSync(process.argv[1])).href) {
  try {
    const options = {};
    for (let i=3; i<process.argv.length; i+=2) options[process.argv[i]] = process.argv[i+1];
    const root = options['--root'] || defaultRoot(), uid = options['--uid'] || '';
    const result = process.argv[2] === 'collect' ? collect(root, options['--src'] ? [options['--src']] : undefined) : process.argv[2] === 'report' ? {} : (() => { throw new Error('用法：local_playback.mjs collect|report [--root DIR] [--uid UID]'); })();
    console.log(JSON.stringify({ ...result, ...report(root, uid) }));
  } catch (e) { console.error(e.message); process.exitCode = 1; }
}
