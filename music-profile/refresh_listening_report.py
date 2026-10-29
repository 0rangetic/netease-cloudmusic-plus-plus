"""Refresh the existing local chart report from the toolbox's two caches."""

from __future__ import annotations

import json
import os
import sys
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path


HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(ROOT / "toolbox"))
from profile_analysis import count_labels, is_recent_like, load_labels  # noqa: E402

from netease_auth import account_folder
PROFILE = account_folder("Profile") / "netease-profile.json"
GENRE_DB = Path(os.environ.get("LOCALAPPDATA", str(Path.home() / "AppData/Local"))) / "NeteaseToolbox/GenreAnalysis/analysis.sqlite3"
APP_DATA = HERE / "report-app/src/data.json"


def source(label: str, definition: str, component_ids: list[str], profile_at: str) -> dict:
    return {
        "label": label,
        "provider": "本机网易云工具箱缓存",
        "executedAt": profile_at,
        "filters": ["按网易云歌曲 ID 关联听歌档案与曲风分析标签", "无标签歌曲不计入标签占比的分母"],
        "evidenceFlow": [
            {"title": "听歌档案", "detail": "读取工具箱保存的网易云听歌档案 JSON；仅将分析所需字段写入报告，不包含账号 ID 和本机路径。"},
            {"title": "曲风与情绪", "detail": "读取工具箱“曲风分析”页使用的 SQLite 标签缓存；analysis 表中的结果优先，song_wiki 表只作补足。"},
            {"title": "统计", "detail": "近30天按红心新增时间筛选；多标签逐首去重后分别计数。曲风或情绪占比各以有该类标签的歌曲为分母。"},
        ],
        "metricDefinitions": [{"label": label, "definition": definition, "componentIds": component_ids}],
    }


def query(rows: list[dict], label: str, definition: str, component_ids: list[str], profile_at: str) -> dict:
    return {"rows": rows, "source": source(label, definition, component_ids, profile_at)}


def label_rows(songs: list[dict], labels: dict, field: str) -> tuple[list[dict], int]:
    counts, covered = count_labels(songs, labels, field)
    return ([{"label": name, "tracks": count, "share": round(count / covered, 4)}
             for name, count in counts.most_common()] if covered else []), covered


def artist_rows(songs: list[dict]) -> tuple[list[dict], int]:
    plays = Counter()
    tracks = Counter()
    covered = 0
    for song in songs:
        value = song.get("totalPlayCount")
        if not isinstance(value, (int, float)):
            value = song.get("playCount")
        if not isinstance(value, (int, float)):
            continue
        covered += 1
        artist = (song.get("artist") or "未标注").split(" / ")[0]
        plays[artist] += value
        tracks[artist] += 1
    return ([{"artist": artist, "plays": int(count), "tracks": tracks[artist]}
             for artist, count in plays.most_common()], covered)


def recent_track_rows(songs: list[dict], labels: dict) -> list[dict]:
    rows = []
    for song in sorted(songs, key=lambda row: row["likedAt"], reverse=True):
        info = labels.get(str(song.get("songId")), {})
        rows.append({
            "title": song.get("title") or "未标注",
            "artist": song.get("artist") or "未标注",
            "likedAt": datetime.fromtimestamp(song["likedAt"] / 1000, timezone.utc).astimezone().strftime("%Y-%m-%d"),
            "genres": "、".join(info.get("genres") or []) or "未标注",
            "moods": "、".join(info.get("moods") or []) or "未标注",
        })
    return rows


def main(verbose: bool = True, destination: Path | None = None) -> dict:
    if not PROFILE.is_file() or not GENRE_DB.is_file():
        raise RuntimeError("需要先在网易云工具箱中取得听歌档案和曲风分析缓存。")
    profile = json.loads(PROFILE.read_text(encoding="utf-8-sig"))
    songs = profile.get("songs") or []
    labels = load_labels(GENRE_DB, songs)
    now = datetime.now(timezone.utc)
    cutoff = now - timedelta(days=30)
    recent = [song for song in songs if is_recent_like(song, int(cutoff.timestamp() * 1000), int(now.timestamp() * 1000))]
    all_genres, all_genre_covered = label_rows(songs, labels, "genres")
    all_moods, all_mood_covered = label_rows(songs, labels, "moods")
    recent_genres, recent_genre_covered = label_rows(recent, labels, "genres")
    recent_moods, recent_mood_covered = label_rows(recent, labels, "moods")
    artists, play_covered = artist_rows(songs)
    all_map = {row["label"]: row for row in all_genres}
    recent_map = {row["label"]: row for row in recent_genres}
    comparison = []
    for label in all_map.keys() | recent_map.keys():
        all_row = all_map.get(label, {})
        recent_row = recent_map.get(label, {})
        recent_share = recent_row.get("share", 0)
        all_share = all_row.get("share", 0)
        comparison.append({"label": label, "recentTracks": recent_row.get("tracks", 0),
                           "recentShare": recent_share, "allTracks": all_row.get("tracks", 0),
                           "allShare": all_share, "deltaPp": round((recent_share - all_share) * 100, 1)})
    comparison.sort(key=lambda row: (-abs(row["deltaPp"]), -row["recentTracks"], row["label"]))
    profile_at = profile.get("generatedAt") or now.isoformat()
    snapshot = json.loads(APP_DATA.read_text(encoding="utf-8")) if destination is None else {"queries": {}}
    snapshot["generatedAt"] = now.isoformat()
    snapshot["buildStatus"] = "updating"
    snapshot["title"] = "网易云听歌画像：长期偏好与近30天新红心"
    snapshot["report"] = {**snapshot.get("report", {}), "asOf": now.astimezone().date().isoformat()}
    queries = snapshot["queries"]
    queries["listening_summary"] = query([{
        "allTracks": len(songs), "likedTracks": sum(bool(s.get("liked")) for s in songs),
        "recentLikedTracks": len(recent), "allGenreCovered": all_genre_covered,
        "allMoodCovered": all_mood_covered, "recentGenreCovered": recent_genre_covered,
        "recentMoodCovered": recent_mood_covered, "playCovered": play_covered,
        "recentStart": cutoff.astimezone().strftime("%Y-%m-%d %H:%M"),
        "recentEnd": now.astimezone().strftime("%Y-%m-%d %H:%M"),
        "profileAt": profile_at,
    }], "歌曲与标签覆盖", "全部为听歌档案歌曲；近期为最近30天内新增红心的歌曲。标签覆盖数按歌曲计算。",
        ["listen-all-count", "listen-recent-count", "listen-all-intro", "listen-recent-intro"], profile_at)
    queries["listening_all_genres"] = query(all_genres, "全部曲风", "每首有曲风标签的歌曲在每个曲风最多计一次；占比以有曲风标签的歌曲数为分母，类别互不排斥。", ["listen-all-genres"], profile_at)
    queries["listening_all_moods"] = query(all_moods, "全部情绪", "每首有情绪标签的歌曲在每个情绪最多计一次；占比以有情绪标签的歌曲数为分母，类别互不排斥。", ["listen-all-moods"], profile_at)
    queries["listening_artists"] = query(artists, "首位艺人累计播放", "对档案中可得的累计播放次数按首位艺人求和；合作曲归于首位艺人，不代表各艺人真实独唱播放。", ["listen-artist-plays"], profile_at)
    queries["listening_recent_genres"] = query(recent_genres, "近期曲风", "近期为最近30天内新增红心的歌曲；按有曲风标签的近期歌曲数计算占比，多标签可重叠。", ["listen-recent-genres"], profile_at)
    queries["listening_recent_moods"] = query(recent_moods, "近期情绪", "近期为最近30天内新增红心的歌曲；按有情绪标签的近期歌曲数计算占比，多标签可重叠。", ["listen-recent-moods"], profile_at)
    queries["listening_genre_comparison"] = query(comparison, "近期与全局曲风对比", "近期占比减全局占比，单位为百分点；双方分别以有曲风标签的歌曲数为分母，近期样本属于全局。", ["listen-genre-compare", "listen-genre-table"], profile_at)
    queries["listening_recent_tracks"] = query(recent_track_rows(recent, labels), "近期新增红心曲目", "按红心新增时间倒序；曲风和情绪来自同 ID 的曲风分析标签缓存。", ["listen-recent-tracks"], profile_at)
    target = destination or APP_DATA
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(snapshot, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if verbose:
        print(json.dumps({"all": len(songs), "recent": len(recent), "allGenres": all_genre_covered,
                          "recentGenres": recent_genre_covered, "allMoods": all_mood_covered,
                          "recentMoods": recent_mood_covered, "topGenres": all_genres[:8],
                          "topRecentGenres": recent_genres[:8], "topMoods": all_moods[:5],
                          "topRecentMoods": recent_moods[:5], "topArtists": artists[:5]}, ensure_ascii=True))

    return snapshot


if __name__ == "__main__":
    main()
