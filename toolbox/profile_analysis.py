"""Build listening portraits from the same caches used by the toolbox tabs."""

import argparse
import base64
import collections
import contextlib
import datetime
import json
import pathlib
import re
import sqlite3
import sys
import time
import unicodedata


def encode(value):
    return base64.urlsafe_b64encode(str(value).encode("utf-8")).decode("ascii").rstrip("=")


def emit(kind, *values):
    print("\t".join([kind] + [encode(value) for value in values]), flush=True)


def split_labels(value):
    return list(dict.fromkeys(part.strip() for part in (value or "").split(" / ") if part.strip()))


def normalize(value):
    value = unicodedata.normalize("NFKC", value or "").casefold()
    return re.sub(r"[\W_]+", "", value, flags=re.UNICODE)


def local_song_id(path, songs):
    stem = pathlib.Path(path).stem
    if " - " not in stem:
        return None
    artist, title = stem.split(" - ", 1)
    key = (normalize(artist), normalize(title))
    matches = [str(song.get("songId")) for song in songs
               if (normalize((song.get("artist") or "").split(" / ")[0]),
                   normalize(song.get("title"))) == key]
    return matches[0] if len(matches) == 1 else None


def load_labels(db_path, songs):
    labels = {}
    with contextlib.closing(sqlite3.connect(str(db_path))) as db:
        tables = {row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if not ({"song_wiki", "analysis"} & tables):
            raise RuntimeError("曲风缓存没有可读取的标签表；请先在“曲风分析”页读取标签。")
        if "song_wiki" in tables:
            for song_id, genres, moods, status in db.execute("SELECT song_id,genres,moods,status FROM song_wiki"):
                labels[str(song_id)] = {
                    "genres": list(dict.fromkeys(json.loads(genres))),
                    "moods": list(dict.fromkeys(json.loads(moods))),
                    "status": status,
                }
        # The analysis table is what the genre tab displays. It also supports
        # databases created without the optional song_wiki table.
        if "analysis" not in tables:
            return labels
        rows = db.execute("SELECT audio_path,genre,influences,mood,status,manual FROM analysis").fetchall()
        for path, genre, others, mood, status, manual in sorted(rows, key=lambda row: bool(row[5])):
            song_id = (path.rsplit("/", 1)[-1] if path.startswith("netease://song/")
                       else local_song_id(path, songs))
            if not song_id:
                continue
            current = labels.get(song_id, {})
            genres = ([] if not genre or genre == "暂无标签" else [genre]) + split_labels(others)
            labels[song_id] = {
                "genres": list(dict.fromkeys(genres)),
                "moods": split_labels(mood) if mood else current.get("moods", []),
                "status": status,
            }
    return labels


def is_recent_like(song, cutoff_ms, now_ms):
    liked_at = song.get("likedAt")
    return bool(song.get("liked") and isinstance(liked_at, (int, float))
                and cutoff_ms <= liked_at <= now_ms)


def count_labels(songs, labels, field):
    counts = collections.Counter()
    covered = 0
    for song in songs:
        values = labels.get(str(song.get("songId")), {}).get(field, [])
        if values:
            covered += 1
            counts.update(set(values))
    return counts, covered


def label_line(counts, covered, limit=5):
    if not covered:
        return "暂无可统计的标签。"
    return "、".join("{} {}首（{:.0f}%）".format(name, count, count * 100 / covered)
                    for name, count in counts.most_common(limit))


def portrait(profile, labels, scope, now_ms=None):
    now_ms = int(time.time() * 1000) if now_ms is None else now_ms
    cutoff_ms = now_ms - 30 * 86400 * 1000
    all_songs = list(profile.get("songs") or [])
    recent_songs = [song for song in all_songs if is_recent_like(song, cutoff_ms, now_ms)]
    selected = all_songs if scope == "all" else recent_songs
    genre_counts, genre_covered = count_labels(selected, labels, "genres")
    mood_counts, mood_covered = count_labels(selected, labels, "moods")
    global_genres, global_covered = count_labels(all_songs, labels, "genres")
    generated_at = profile.get("generatedAt") or "未知"
    lines = []
    if scope == "all":
        lines.append("所有歌曲画像")
        lines.append("听歌档案共 {} 首；曲风覆盖 {}/{} 首，情绪覆盖 {}/{} 首。".format(
            len(selected), genre_covered, len(selected), mood_covered, len(selected)))
        lines.append("\n曲风主线：" + label_line(genre_counts, genre_covered))
        lines.append("情绪标签：" + label_line(mood_counts, mood_covered))
        artist_plays = collections.Counter()
        play_coverage = 0
        for song in selected:
            count = song.get("totalPlayCount")
            if not isinstance(count, (int, float)):
                count = song.get("playCount")
            if isinstance(count, (int, float)):
                play_coverage += 1
                artist = (song.get("artist") or "未标注").split(" / ")[0]
                artist_plays[artist] += count
        if artist_plays:
            leaders = "、".join("{} {:,}次".format(name, int(count))
                               for name, count in artist_plays.most_common(5))
            lines.append("\n累计播放最多的首位艺人：" + leaders)
            lines.append("播放次数覆盖 {}/{} 首，合作曲按首位艺人归类。".format(play_coverage, len(selected)))
    else:
        start = datetime.datetime.fromtimestamp(cutoff_ms / 1000).strftime("%Y-%m-%d %H:%M")
        end = datetime.datetime.fromtimestamp(now_ms / 1000).strftime("%Y-%m-%d %H:%M")
        lines.append("近30天新增红心画像")
        lines.append("时间：{} 至 {}。新增红心 {} 首；曲风覆盖 {} 首，情绪覆盖 {} 首。".format(
            start, end, len(selected), genre_covered, mood_covered))
        lines.append("\n近期曲风：" + label_line(genre_counts, genre_covered))
        lines.append("近期情绪：" + label_line(mood_counts, mood_covered))
        lines.append("\n下方与全局曲风对比均按有曲风标签的歌曲计算；一首歌可有多个曲风，因此各项占比不会合计为100%。")
        if len(selected) < 30:
            lines.append("近期样本只有 {} 首，差值适合看方向，不宜解读为稳定偏好变化。".format(len(selected)))
    lines.append("\n来源：网易云听歌档案（{}）及“曲风分析”页共用的标签缓存。".format(generated_at))
    comparisons = []
    if scope == "recent" and genre_covered and global_covered:
        for name in set(global_genres) | set(genre_counts):
            recent_count = genre_counts[name]
            global_count = global_genres[name]
            recent_pct = recent_count * 100.0 / genre_covered
            global_pct = global_count * 100.0 / global_covered
            comparisons.append((name, recent_count, recent_pct, global_count, global_pct,
                                recent_pct - global_pct))
        comparisons.sort(key=lambda row: (-abs(row[5]), -row[1], row[0]))
    return "\n".join(lines), comparisons


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--scope", choices=("all", "recent"), required=True)
    parser.add_argument("--profile", type=pathlib.Path, required=True)
    parser.add_argument("--genre-db", type=pathlib.Path, required=True)
    args = parser.parse_args()
    if not args.profile.is_file():
        raise RuntimeError("尚无听歌档案；请先刷新网易云数据。")
    if not args.genre_db.is_file():
        raise RuntimeError("尚无曲风标签；请先在“曲风分析”页读取标签。")
    profile = json.loads(args.profile.read_text(encoding="utf-8-sig"))
    labels = load_labels(args.genre_db, profile.get("songs") or [])
    summary, comparisons = portrait(profile, labels, args.scope)
    emit("SUMMARY", summary)
    for row in comparisons:
        emit("COMPARE", row[0], row[1], "{:.1f}%".format(row[2]),
             row[3], "{:.1f}%".format(row[4]), "{:+.1f}百分点".format(row[5]))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        emit("FATAL", str(error))
        sys.exit(1)
