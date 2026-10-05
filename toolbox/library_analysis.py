import csv
import json
import pathlib
import sqlite3
import sys
import time
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import genre_analysis as genre


from netease_auth import liked_playlist_id
OUTPUT = pathlib.Path(__file__).resolve().parent / "网易云曲风分析-红心及下载.csv"
DB_PATH = genre.cache_dir() / "analysis.sqlite3"


def get_json(url):
    request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0", "Referer": "https://music.163.com/", "Cookie": genre.cookie_text()})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def playlist_tracks():
    playlist_id = liked_playlist_id()
    if not playlist_id: return [], {}
    detail = get_json("https://music.163.com/api/v6/playlist/detail?id={}&n=1000&s=0".format(playlist_id))
    ids = [str(item["id"]) for item in detail["playlist"]["trackIds"]]
    tracks = {}
    for start in range(0, len(ids), 100):
        batch = ids[start:start + 100]
        url = "https://music.163.com/api/song/detail?ids=" + urllib.parse.quote(json.dumps(batch, separators=(",", ":")))
        for song in get_json(url).get("songs", []):
            artists = song.get("ar") or song.get("artists") or []
            tracks[str(song["id"])] = (song.get("name", ""), "/".join(x.get("name", "") for x in artists))
    return ids, tracks


def downloaded_tracks(AES):
    result = {}
    if not genre.INPUT_ROOT.is_dir(): return result
    for path in genre.INPUT_ROOT.rglob("*.ncm"):
        try:
            song_id = genre.read_song_id(path, AES)
            result[song_id] = path.stem
        except Exception as error:
            print("下载歌曲读取失败：{} · {}".format(path.name, error), flush=True)
    return result


def prepare_db():
    db = sqlite3.connect(str(DB_PATH))
    db.execute("""CREATE TABLE IF NOT EXISTS song_wiki (
      song_id TEXT PRIMARY KEY, genres TEXT NOT NULL, moods TEXT NOT NULL,
      status TEXT NOT NULL, updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP)""")
    db.commit(); return db


def main():
    AES = genre.get_aes(); saved = genre.cookie_text(); liked_ids, metadata = playlist_tracks()
    downloads = downloaded_tracks(AES); all_ids = list(dict.fromkeys(liked_ids + list(downloads)))
    db = prepare_db()
    cached = {row[0]: (json.loads(row[1]), json.loads(row[2]), row[3]) for row in db.execute(
        "SELECT song_id,genres,moods,status FROM song_wiki")}
    pending = [song_id for song_id in all_ids if song_id not in cached]
    print("红心 {} 首，下载 {} 首，去重后 {} 首；需联网查询 {} 首。".format(
        len(liked_ids), len(downloads), len(all_ids), len(pending)), flush=True)

    def fetch(song_id):
        for attempt in range(3):
            try:
                genres, moods = genre.wiki_labels(song_id, saved, AES)
                return song_id, genres, moods, "百科标签" if genres else "百科未收录"
            except Exception as error:
                if attempt == 2: return song_id, [], [], "查询失败：" + str(error)
                time.sleep(1.0 + attempt)

    completed = 0
    with ThreadPoolExecutor(max_workers=4) as pool:
        futures = [pool.submit(fetch, song_id) for song_id in pending]
        for future in as_completed(futures):
            song_id, genres, moods, status = future.result(); completed += 1
            cached[song_id] = (genres, moods, status)
            db.execute("INSERT INTO song_wiki(song_id,genres,moods,status) VALUES(?,?,?,?) "
              "ON CONFLICT(song_id) DO UPDATE SET genres=excluded.genres,moods=excluded.moods,status=excluded.status,updated_at=CURRENT_TIMESTAMP",
              (song_id, json.dumps(genres, ensure_ascii=False), json.dumps(moods, ensure_ascii=False), status))
            if completed % 10 == 0 or completed == len(pending):
                db.commit(); print("已查询 {}/{}".format(completed, len(pending)), flush=True)
    db.commit(); db.close()

    liked = set(liked_ids)
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    with OUTPUT.open("w", newline="", encoding="utf-8-sig") as output:
        writer = csv.writer(output); writer.writerow(["歌曲ID","歌曲","艺人","来源","主曲风","其他曲风","情绪","状态"])
        for song_id in all_ids:
            title, artist = metadata.get(song_id, (downloads.get(song_id, ""), ""))
            sources = []
            if song_id in liked: sources.append("红心歌单")
            if song_id in downloads: sources.append("本地下载")
            genres, moods, status = cached.get(song_id, ([], [], "无结果"))
            writer.writerow([song_id,title,artist," + ".join(sources),genres[0] if genres else "暂无标签",
              " / ".join(genres[1:])," / ".join(moods),status])
    print("完成：" + str(OUTPUT), flush=True)


if __name__ == "__main__": sys.exit(main())
