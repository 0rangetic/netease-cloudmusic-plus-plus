#!/usr/bin/env python3
import base64
import json
import os
import subprocess
import sys
import urllib.parse
import urllib.request


sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from netease_auth import load_session, load_cookie
from portable_paths import node_executable
UID = None
BASE = "https://music.163.com"


def emit(kind, *values):
    encoded = [base64.urlsafe_b64encode(str(value).encode("utf-8")).decode("ascii").rstrip("=") for value in values]
    print("\t".join([kind] + encoded), flush=True)




def api(path, cookie, params=None):
    url = BASE + path
    if params:
        url += "?" + urllib.parse.urlencode(params)
    request = urllib.request.Request(url, headers={
        "Cookie": cookie,
        "Referer": BASE + "/",
        "User-Agent": "Mozilla/5.0 NeteaseToolbox/1.0",
        "Accept": "application/json,text/plain,*/*",
    })
    with urllib.request.urlopen(request, timeout=25) as response:
        data = json.loads(response.read().decode("utf-8"))
        if data.get("code") != 200:
            raise RuntimeError("网易云请求失败（{}），请检查登录状态或重新扫码。".format(data.get("code", "未知")))
        return data


def artists(song):
    people = song.get("ar") or song.get("artists") or []
    return " / ".join(person.get("name", "") for person in people if person.get("name"))


def album(song):
    return (song.get("al") or song.get("album") or {}).get("name", "")


def main():
    global UID
    session = load_session()
    UID = str(session["userId"])
    cookie = session["cookie"]
    record = api("/api/v1/play/record", cookie, {"uid": UID, "type": 0})
    records = record.get("allData") or []

    playlists = api("/api/user/playlist/", cookie, {"uid": UID, "limit": 1000, "offset": 0}).get("playlist") or []
    liked_playlist = next((item for item in playlists if item.get("specialType") == 5), None)

    liked_times = {}
    liked_songs = {}
    if liked_playlist:
        detail = api("/api/v6/playlist/detail", cookie, {"id": liked_playlist["id"], "n": 100000, "s": 0})
        playlist = detail.get("playlist") or {}
        for track_id in playlist.get("trackIds") or []:
            liked_times[str(track_id.get("id"))] = track_id.get("at")
        for song in playlist.get("tracks") or []:
            liked_songs[str(song.get("id"))] = song

    merged = {}
    for row in records:
        song = row.get("song") or {}
        song_id = str(song.get("id", ""))
        if song_id:
            merged[song_id] = {
                "songId": song_id,
                "title": song.get("name", ""),
                "artist": artists(song),
                "album": album(song),
                "playCount": row.get("playCount"),
                "score": row.get("score"),
                "liked": song_id in liked_times,
                "likedAt": liked_times.get(song_id),
            }
    for song_id, song in liked_songs.items():
        if song_id not in merged:
            merged[song_id] = {
                "songId": song_id,
                "title": song.get("name", ""),
                "artist": artists(song),
                "album": album(song),
                "playCount": None,
                "score": None,
                "liked": True,
                "likedAt": liked_times.get(song_id),
            }

    cache_path = os.path.join(os.environ.get("LOCALAPPDATA", os.getcwd()), "NeteaseToolbox", "Profile", UID, "first-listen-cache.json")
    cache = {}
    try:
        with open(cache_path, "r", encoding="utf-8") as handle:
            cache = json.load(handle)
    except (OSError, ValueError):
        pass
    helper = os.path.join(os.path.dirname(os.path.abspath(__file__)), "profile_first_listen.js")
    missing = [song_id for song_id in merged if song_id not in cache]
    if missing and os.path.isfile(helper):
        try:
            helper_env = os.environ.copy()
            helper_env["NETEASE_TOOLBOX_COOKIE"] = cookie
            process = subprocess.Popen(
                [node_executable(), helper], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                text=True, encoding="utf-8", errors="replace", cwd=os.path.dirname(helper), env=helper_env,
            )
            stdout, stderr = process.communicate(json.dumps(missing), timeout=max(90, len(missing)))
            for line in stdout.splitlines():
                try:
                    result = json.loads(line)
                    if result.get("data") is not None:
                        cache[str(result["songId"])] = result["data"]
                except (ValueError, KeyError):
                    continue
            if process.returncode != 0 and stderr.strip():
                emit("PROGRESS", "单曲听歌档案部分读取失败：" + stderr.strip().splitlines()[-1])
        except (OSError, subprocess.SubprocessError) as exc:
            emit("PROGRESS", "无法调用单曲听歌档案：" + str(exc))
    os.makedirs(os.path.dirname(cache_path), exist_ok=True)
    with open(cache_path, "w", encoding="utf-8") as handle:
        json.dump(cache, handle, ensure_ascii=False, indent=2)

    for song_id, row in merged.items():
        archive = cache.get(song_id) or {}
        first = archive.get("musicFirstListenDto") or {}
        total = archive.get("musicTotalPlayDto") or {}
        liked = archive.get("musicLikeSongDto") or {}
        row["firstListenAt"] = first.get("listenTime")
        row["totalPlayCount"] = total.get("playCount")
        if liked.get("redTimeStamp"):
            row["likedAt"] = liked["redTimeStamp"]
            row["liked"] = bool(liked.get("like", True))

    rows = sorted(merged.values(), key=lambda item: (-(item.get("playCount") or -1), item.get("title") or ""))
    output_dir = os.path.join(os.environ.get("LOCALAPPDATA", os.getcwd()), "NeteaseToolbox", "Profile", UID)
    os.makedirs(output_dir, exist_ok=True)
    output_path = os.path.join(output_dir, "netease-profile.json")
    payload = {
        "userId": UID,
        "generatedAt": __import__("datetime").datetime.now().astimezone().isoformat(),
        "source": "music.163.com authenticated API",
        "limitations": [
            "累计播放和首次收听来自手机版歌曲百科的回忆坐标接口。",
            "红心时间优先采用单曲回忆坐标，缺失时使用‘我喜欢的音乐’歌单添加时间。",
        ],
        "songs": rows,
    }
    with open(output_path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2)

    emit("META", len(rows), sum(1 for row in rows if row["liked"]), output_path)
    for row in rows:
        liked_at = ""
        if row.get("likedAt"):
            liked_at = __import__("datetime").datetime.fromtimestamp(row["likedAt"] / 1000).astimezone().strftime("%Y-%m-%d %H:%M:%S")
        first_at = ""
        if row.get("firstListenAt"):
            first_at = __import__("datetime").datetime.fromtimestamp(row["firstListenAt"] / 1000).astimezone().strftime("%Y-%m-%d %H:%M:%S")
        count = row.get("totalPlayCount")
        if count is None:
            count = row.get("playCount")
        emit("SONG", row["title"], row["artist"], row["album"], "" if count is None else count, first_at, "是" if row["liked"] else "否", liked_at, row["songId"])


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        emit("FATAL", str(exc))
        sys.exit(1)
