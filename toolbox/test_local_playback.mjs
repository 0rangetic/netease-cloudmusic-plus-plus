import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { collect, report } from './local_playback.mjs';

const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'ncm-local-test-'));
const source = path.join(temporary, 'webdb.dat'), root = path.join(temporary, 'archive');
const db = new DatabaseSync(source);
const base = Date.UTC(2026, 9, 5, 4);
try {
  db.exec(`PRAGMA journal_mode=WAL;
    CREATE TABLE playingCount(id INTEGER PRIMARY KEY,resourceId TEXT,playDuration REAL,updateTime INTEGER,source TEXT,uid TEXT,resourceType TEXT);
    CREATE TABLE tsPlayingCount(id INTEGER PRIMARY KEY,resourceId TEXT,playDuration REAL,updateTime INTEGER,source TEXT,uid TEXT,resourceType TEXT);
    CREATE TABLE dbTrack(id TEXT,jsonStr TEXT);`);
  db.prepare('INSERT INTO dbTrack VALUES(?,?)').run('100', JSON.stringify({ name: '歌曲,带逗号', ar: [{ name: '歌手' }], dt: 200000 }));
  const add = db.prepare('INSERT INTO playingCount VALUES(?,?,?,?,?,?,?)');
  add.run(1,'100',200,base,'likeMusic','111','track');
  add.run(2,'100',15,base+200000,'likeMusic','111','track');
  add.run(3,'100',10,base+215000,'search','111','track');
  add.run(4,'200',10,base+225000,'search','111','track');
  add.run(5,'100',200,base+235000,'search','111','track');
  add.run(6,'100',200,base+435000,'search','222','track');
  add.run(7,'100',5,base+436000,'search','','track');
  add.run(8,'100',5,base+437000,'search','222','track');
  add.run(9,'100',200,base+3000000,'search','111','track');
  add.run(10,'100',200,base+3200000,'search','111','track');
  // Clock moves backwards: insertion order must prevent a false chronological session.
  add.run(11,'100',5,base+3100000,'search','111','track');
  add.run(12,'300',10,base+3300000,'search','111','audio');
  add.run(13,'bad',10,base+3300000,'search','111','track');
  db.exec('INSERT INTO tsPlayingCount SELECT * FROM playingCount WHERE id=1');
  let result = collect(root, [source]);
  assert.equal(result.added, 11); // invalid/non-track discarded, duplicate second table collapsed
  assert.equal(result.warnings.length, 1);
  assert.deepEqual(report(root, '111').accounts, ['111','222','unknown']);
  let first = report(root, '111');
  assert.equal(first.total, 8);
  assert.equal(first.songs[0].count, 7);
  assert.deepEqual(first.sessions.map(s => s.count), [3,2]);
  assert.deepEqual(first.sessions[0].gaps_sec, [200,15]);
  assert.equal(first.sessions[0].evidence[2].row_id, 3);
  assert.equal(report(root, 'unknown').total, 1);
  assert.equal(report(root, '222').total, 2);
  assert.equal(report(root, '333').total, 0, 'unseen current account must not display another account');
  assert.equal(report(root, '333').uid, '333');
  assert.equal(report(root, '222').sessions.length, 0, 'missing/interleaved row ids break continuity');
  assert.equal(collect(root, [source]).added, 0, 'resampling must not increment counts');
  const migrated = path.join(temporary, 'migrated.dat');
  db.prepare('VACUUM INTO ?').run(migrated);
  assert.equal(collect(root, [source, migrated]).added, 0, 'client migration / duplicated data directory must not duplicate plays');
  db.prepare('UPDATE playingCount SET playDuration=150 WHERE id=3').run();
  collect(root, [source]);
  assert.equal(report(root, '111').sessions[0].evidence[2].seconds, 150);
  // Pruning and ID reuse cannot erase accumulated records or cause duplicate counts.
  db.exec('DELETE FROM playingCount; DELETE FROM tsPlayingCount;');
  add.run(1,'100',10,base+4000000,'search','111','track');
  assert.equal(collect(root, [source]).added, 1);
  assert.equal(report(root, '111').total, 9);
  assert.deepEqual(report(root, '111').sessions.map(s => s.count), [3,2]);
  result = collect(root, [source, path.join(temporary, 'missing.dat')]);
  assert.equal(result.errors.length, 1, 'partial failure must be reported');
  assert.throws(() => collect(root, [path.join(temporary, 'missing.dat')]), /ENOENT/);
  assert.throws(() => report(root, '../bad'), /账号 ID 无效/);

  // Phone NetEase replay merge: event-id idempotency, package filter, and +1 inferred first play per session.
  const phoneEvents = [
    { eventId: 'p1', deviceId: 'phone-1', deviceName: 'Test Phone', song: 'media:other_id__100', mediaId: 'other_id__100', packageName: 'com.netease.cloudmusic', trigger: 'repeat', startedAt: base + 10_000_000, endedAt: base + 10_200_000, listenedMs: 200_000, durationMs: 200_000, title: '歌曲,带逗号', artist: '歌手', completed: true, receivedAt: base + 10_300_000 },
    { eventId: 'p2', deviceId: 'phone-1', deviceName: 'Test Phone', song: 'media:other_id__100', mediaId: 'other_id__100', packageName: 'com.netease.cloudmusic', trigger: 'repeat', startedAt: base + 10_200_000, endedAt: base + 10_400_000, listenedMs: 200_000, durationMs: 200_000, title: '歌曲,带逗号', artist: '歌手', completed: true, receivedAt: base + 10_300_000 },
    { eventId: 'p2', deviceId: 'phone-1', deviceName: 'Test Phone', song: 'media:other_id__100', mediaId: 'other_id__100', packageName: 'com.netease.cloudmusic', trigger: 'repeat', startedAt: base + 10_200_000, endedAt: base + 10_400_000, listenedMs: 200_000, durationMs: 200_000, title: '歌曲,带逗号', artist: '歌手', completed: true, receivedAt: base + 10_300_000 },
    { eventId: 'q1', deviceId: 'phone-1', deviceName: 'Test Phone', song: '100', packageName: 'com.tencent.qqmusic', trigger: 'repeat', startedAt: base + 10_600_000, endedAt: base + 10_800_000, listenedMs: 200_000, durationMs: 200_000, title: '其他媒体', artist: '歌手', completed: true, receivedAt: base + 10_900_000 }
  ];
  fs.writeFileSync(path.join(root, '111', 'phone-repeats.jsonl'), phoneEvents.map(event => JSON.stringify(event)).join('\n') + '\n');
  const merged = report(root, '111');
  assert.equal(merged.total, 9, 'desktop total stays separate');
  assert.equal(merged.phone_total, 2, 'duplicate and other-media events are excluded');
  assert.equal(merged.phone_sessions, 1);
  assert.equal(merged.phone_plays, 3, 'two repeats plus one inferred first play');
  const mergedSong = merged.songs.find(song => song.song_id === '100');
  assert.equal(mergedSong.phone_count, 3);
  assert.equal(mergedSong.count, 11, 'desktop count plus inferred phone passes');
  const phoneSession = merged.sessions.find(session => session.source === 'phone');
  assert.equal(phoneSession.count, 3);
  assert.equal(phoneSession.repeat_count, 2);
  assert.deepEqual(phoneSession.gaps_sec, [200]);
  assert.ok(merged.last_phone_sync_at.length > 0);

  fs.mkdirSync(path.join(root, '444'), { recursive: true });
  fs.writeFileSync(path.join(root, '444', 'phone-repeats.jsonl'), JSON.stringify({
    eventId: 'only-phone', deviceId: 'phone-2', deviceName: 'Phone 2', song: '', mediaId: 'other_id__900',
    packageName: 'com.netease.cloudmusic', trigger: 'repeat',
    startedAt: base + 20_000_000, endedAt: base + 20_180_000,
    listenedMs: 180_000, durationMs: 180_000, title: '手机独有', artist: '歌手',
    completed: true, receivedAt: base + 20_200_000
  }) + '\n');
  const phoneOnly = report(root, '444');
  assert.equal(phoneOnly.total, 0, 'phone-only account needs no desktop database');
  assert.equal(phoneOnly.phone_total, 1);
  assert.equal(phoneOnly.phone_plays, 2);
  assert.equal(phoneOnly.songs[0].song_id, '900', 'other_id media ids normalize to the numeric song id');
  assert.equal(phoneOnly.songs[0].count, 2);
  assert.equal(phoneOnly.sessions.length, 1);
  assert.equal(phoneOnly.sessions[0].source, 'phone');
} finally {
  db.close();
  fs.rmSync(temporary, { recursive: true, force: true });
}
console.log('PASS: WAL snapshot, deduplication, account isolation, short replays, clock rollback, pruning, row reset, metadata update, partial failures and phone NetEase replay merge');
