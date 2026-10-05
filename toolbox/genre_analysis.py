import argparse, base64, csv, hashlib, importlib, json, os, pathlib, sqlite3, struct, subprocess, sys, time
import urllib.parse, urllib.request

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

VERSION = "netease-song-wiki-v2"
from portable_paths import input_directory
INPUT_ROOT = input_directory()
EAPI_KEY = b"e82ckenh8dichen8"
META_KEY = bytes.fromhex("2331346C6A6B5F215C5D2630553C2728")
SIGN_PATH = "/api/song/play/about/block/page"
WIKI_URL = "https://interface3.music.163.com/eapi/music/wiki/home/song/get"



def emit(kind, *fields): print("\t".join([kind, *map(str, fields)]), flush=True)
def b64(value): return base64.urlsafe_b64encode(str(value).encode()).decode()


def cache_dir():
    path = pathlib.Path(os.environ.get("LOCALAPPDATA", pathlib.Path.home() / "AppData/Local")) / "NeteaseToolbox/GenreAnalysis"
    path.mkdir(parents=True, exist_ok=True)
    return path


def get_aes():
    bundled = pathlib.Path(__file__).resolve().parent / "runtime/python/packages"
    packages = cache_dir() / "python-packages"
    sys.path.insert(0, str(packages))
    sys.path.insert(0, str(bundled))
    try:
        from Crypto.Cipher import AES
    except ImportError:
        emit("PROGRESS", b64("首次准备网易云元数据读取组件…"))
        packages.mkdir(parents=True, exist_ok=True)
        result = subprocess.run([sys.executable, "-m", "pip", "install", "--disable-pip-version-check",
            "--only-binary=:all:", "--target", str(packages), "pycryptodome==3.23.0"],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        if result.returncode: raise RuntimeError("组件下载失败：\n" + result.stdout[-2000:])
        importlib.invalidate_caches()
        from Crypto.Cipher import AES
    return AES


def database(path):
    path.parent.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(str(path))
    db.execute("""CREATE TABLE IF NOT EXISTS analysis (
      audio_path TEXT PRIMARY KEY,file_size INTEGER NOT NULL,modified_ns INTEGER NOT NULL,
      model_version TEXT NOT NULL,genre TEXT NOT NULL,influences TEXT NOT NULL,score REAL NOT NULL,
      status TEXT NOT NULL,manual INTEGER NOT NULL DEFAULT 0,analyzed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP)""")
    columns = {row[1] for row in db.execute("PRAGMA table_info(analysis)")}
    if "manual" not in columns: db.execute("ALTER TABLE analysis ADD COLUMN manual INTEGER NOT NULL DEFAULT 0")
    if "mood" not in columns: db.execute("ALTER TABLE analysis ADD COLUMN mood TEXT NOT NULL DEFAULT ''")
    db.commit()
    return db


def source_name(score): return "人工" if score == -1 else "网易云" if score == -2 else "旧模型"


def emit_result(row):
    path, genre, others, mood, score, status = row
    emit("RESULT", *(b64(x) for x in (path, genre, others, mood, source_name(score), status)))


def list_results(db, paths):
    for path in paths:
        key = path if path.startswith("netease://song/") else str(pathlib.Path(path).resolve())
        row = db.execute("SELECT audio_path,genre,influences,mood,score,status FROM analysis WHERE audio_path=?",
                         (key,)).fetchone()
        if row: emit_result(row)


def export_csv(db, destination):
    rows = db.execute("SELECT audio_path,genre,influences,mood,score,status,analyzed_at FROM analysis ORDER BY audio_path")
    with pathlib.Path(destination).open("w", newline="", encoding="utf-8-sig") as output:
        writer = csv.writer(output); writer.writerow(["音频文件","主曲风","其他曲风","情绪","来源","状态","获取时间"])
        for path, genre, others, mood, score, status, at in rows:
            writer.writerow([path, genre, others, mood, "人工确认" if score == -1 else "网易云歌曲百科", status, at])


def cookie_text():
    from netease_auth import load_cookie
    return load_cookie()


def cookies(text):
    result = {}
    for part in text.split(";"):
        if "=" in part:
            key, value = part.strip().split("=", 1); result[urllib.parse.unquote(key)] = urllib.parse.unquote(value)
    return result


def encrypt(AES, value, key):
    data = value if isinstance(value, bytes) else value.encode(); padding = 16 - len(data) % 16
    return AES.new(key, AES.MODE_ECB).encrypt(data + bytes([padding]) * padding)


def read_song_id(path, AES):
    with path.open("rb") as source:
        if source.read(8) != b"CTENFDAM": raise RuntimeError("NCM 文件无效：" + path.name)
        source.read(2); source.seek(struct.unpack("<I", source.read(4))[0], 1)
        meta = bytearray(source.read(struct.unpack("<I", source.read(4))[0]))
    for i in range(len(meta)): meta[i] ^= 0x63
    plain = AES.new(META_KEY, AES.MODE_ECB).decrypt(base64.b64decode(meta[22:])); plain = plain[:-plain[-1]].decode()
    values = json.loads(plain[6:]); song_id = values.get("musicId") or values.get("id")
    if not song_id: raise RuntimeError("NCM 元数据没有歌曲 ID：" + path.name)
    return str(song_id)


def build_ncm_index():
    result = {}
    if INPUT_ROOT.is_dir():
        for path in INPUT_ROOT.rglob("*.ncm"): result.setdefault(path.stem.casefold(), []).append(path)
    return result


def public_json(url):
    request = urllib.request.Request(url, headers={"User-Agent":"Mozilla/5.0", "Referer":"https://music.163.com/", "Cookie":cookie_text()})
    with urllib.request.urlopen(request, timeout=30) as response:
        data = json.loads(response.read().decode())
        if data.get("code") != 200: raise RuntimeError("网易云请求失败，请检查登录状态或重新扫码。")
        return data


def emit_library_tracks(AES):
    from netease_auth import liked_playlist_id
    playlist_id = liked_playlist_id()
    if not playlist_id: return
    detail = public_json("https://music.163.com/api/v6/playlist/detail?id={}&n=1000&s=0".format(playlist_id))
    ids = [str(item["id"]) for item in detail["playlist"]["trackIds"]]
    downloaded = set()
    if INPUT_ROOT.is_dir():
        for path in INPUT_ROOT.rglob("*.ncm"):
            try: downloaded.add(read_song_id(path, AES))
            except Exception: pass
    for start in range(0, len(ids), 100):
        batch = ids[start:start+100]
        url = "https://music.163.com/api/song/detail?ids=" + urllib.parse.quote(json.dumps(batch,separators=(",",":")))
        for song in public_json(url).get("songs",[]):
            song_id = str(song["id"])
            if song_id in downloaded: continue
            artists = song.get("ar") or song.get("artists") or []
            title = song.get("name","") + (" - " + "/".join(x.get("name","") for x in artists) if artists else "")
            emit("TRACK", b64("netease://song/"+song_id), b64(title), b64("红心歌单"))


def source_ncm(audio_path, index):
    matches = index.get(pathlib.Path(audio_path).stem.casefold(), [])
    if len(matches) == 1: return matches[0]
    if not matches: raise RuntimeError("找不到对应 NCM 源文件，无法取得歌曲 ID。")
    raise RuntimeError("存在多个同名 NCM 源文件，无法确定歌曲 ID。")


def wiki_labels(song_id, saved_cookie, AES):
    saved = cookies(saved_cookie); now = int(time.time())
    header = {"osver":"","deviceId":"","appver":"8.9.70","versioncode":"140","mobilename":"",
      "buildver":str(now),"resolution":"1920x1080","__csrf":saved.get("_csrf", saved.get("__csrf", "")),
      "os":"android","channel":"","requestId":str(int(time.time()*1000))+"_0123"}
    for key in ("MUSIC_U", "MUSIC_A"):
        if saved.get(key): header[key] = saved[key]
    payload = json.dumps({"songId":song_id,"header":header}, ensure_ascii=False, separators=(",",":"))
    digest = hashlib.md5(("nobody"+SIGN_PATH+"use"+payload+"md5forencrypt").encode()).hexdigest()
    message = SIGN_PATH+"-36cd479b6b5-"+payload+"-36cd479b6b5-"+digest
    body = urllib.parse.urlencode({"params":encrypt(AES, message, EAPI_KEY).hex().upper()}).encode()
    cookie = "; ".join(urllib.parse.quote(str(k))+"="+urllib.parse.quote(str(v)) for k,v in header.items())
    request = urllib.request.Request(WIKI_URL, data=body, headers={"User-Agent":"Mozilla/5.0 (Linux; Android 10) Chrome/119 Mobile",
      "Referer":"https://music.163.com/","Cookie":cookie,"Content-Type":"application/x-www-form-urlencoded"})
    with urllib.request.urlopen(request, timeout=25) as response: result = json.loads(response.read().decode())
    if result.get("code") != 200: raise RuntimeError(result.get("message") or "歌曲百科请求失败")
    genres, moods = [], []
    for block in result.get("data",{}).get("blocks",[]):
        if block.get("code") != "SONG_PLAY_ABOUT_SONG_BASIC": continue
        for creative in block.get("creatives") or []:
            for resource in creative.get("resources") or []:
                if resource.get("resourceType") != "melody_style": continue
                main = (resource.get("uiElement") or {}).get("mainTitle") or {}; title = main.get("title")
                target = (((main.get("action") or {}).get("clickAction") or {}).get("targetUrl") or "")
                if not title: continue
                if "component=rn-genre" in target: genres.append(title)
                elif "component=rn-tag-detail" in target: moods.append(title)
    return list(dict.fromkeys(genres)), list(dict.fromkeys(moods))


def analyze(db, audio_path, index, saved_cookie, AES):
    remote = audio_path.startswith("netease://song/")
    source = audio_path if remote else str(pathlib.Path(audio_path).resolve())
    if remote:
        size, modified, song_id = 0, 0, audio_path.rsplit("/",1)[-1]
    else:
        stat = pathlib.Path(source).stat(); size, modified = stat.st_size, stat.st_mtime_ns
    row = db.execute("SELECT audio_path,genre,influences,mood,score,status FROM analysis WHERE audio_path=? AND file_size=? AND modified_ns=? AND model_version=?",
      (source,size,modified,VERSION)).fetchone()
    if row: emit_result(row); return
    if not remote: song_id = read_song_id(source_ncm(source,index), AES)
    cached_song = None
    try: cached_song = db.execute("SELECT genres,moods FROM song_wiki WHERE song_id=?",(song_id,)).fetchone()
    except sqlite3.OperationalError: pass
    if cached_song:
        genres, moods = json.loads(cached_song[0]), json.loads(cached_song[1])
    else:
        genres, moods = wiki_labels(song_id,saved_cookie,AES)
    primary = genres[0] if genres else "暂无标签"; others = " / ".join(genres[1:]); mood = " / ".join(moods)
    status = "百科标签" if genres else "百科未收录"
    row = (source,primary,others,mood,-2.0,status)
    db.execute("INSERT INTO analysis(audio_path,file_size,modified_ns,model_version,genre,influences,mood,score,status,manual) VALUES(?,?,?,?,?,?,?,?,?,0) "
      "ON CONFLICT(audio_path) DO UPDATE SET file_size=excluded.file_size,modified_ns=excluded.modified_ns,model_version=excluded.model_version,"
      "genre=excluded.genre,influences=excluded.influences,mood=excluded.mood,score=-2,status=excluded.status,manual=0,analyzed_at=CURRENT_TIMESTAMP",
      (source,size,modified,VERSION,primary,others,mood,-2.0,status)); db.commit(); emit_result(row)


def set_manual(db, audio_path, genre, others):
    source=pathlib.Path(audio_path).resolve(); stat=source.stat(); row=(str(source),genre.strip(),others.strip(),"",-1.0,"手动确认")
    db.execute("INSERT INTO analysis(audio_path,file_size,modified_ns,model_version,genre,influences,mood,score,status,manual) VALUES(?,?,?,?,?,?,?,?,?,1) "
      "ON CONFLICT(audio_path) DO UPDATE SET file_size=excluded.file_size,modified_ns=excluded.modified_ns,model_version=excluded.model_version,"
      "genre=excluded.genre,influences=excluded.influences,mood='',score=-1,status=excluded.status,manual=1,analyzed_at=CURRENT_TIMESTAMP",
      (str(source),stat.st_size,stat.st_mtime_ns,VERSION,row[1],row[2],"",-1.0,row[5])); db.commit(); emit_result(row)


def main():
    global INPUT_ROOT
    parser=argparse.ArgumentParser(); parser.add_argument("--input-root"); parser.add_argument("--db",required=True); parser.add_argument("--list",action="store_true")
    parser.add_argument("--export"); parser.add_argument("--set",action="store_true"); parser.add_argument("--clear-old",action="store_true")
    parser.add_argument("--library-list",action="store_true")
    parser.add_argument("--library-analyze",action="store_true")
    parser.add_argument("--primary",default=""); parser.add_argument("--influences",default=""); parser.add_argument("files",nargs="*"); args=parser.parse_args()
    if args.input_root is not None: INPUT_ROOT = pathlib.Path(args.input_root) if args.input_root else pathlib.Path(__file__).resolve().parent / "music"
    db=database(pathlib.Path(args.db))
    try:
        if args.clear_old: db.execute("DELETE FROM analysis WHERE model_version<>?",(VERSION,)); db.commit()
        if args.library_list: emit_library_tracks(get_aes()); return 0
        if args.library_analyze:
            AES=get_aes(); saved=cookie_text(); index=build_ncm_index()
            downloaded=set()
            for paths in index.values():
                for path in paths:
                    try: downloaded.add(read_song_id(path,AES))
                    except Exception: pass
            ids=[row[0] for row in db.execute("SELECT song_id FROM song_wiki ORDER BY song_id") if row[0] not in downloaded]
            for number,song_id in enumerate(ids,1):
                if number%25==0 or number==len(ids): emit("PROGRESS",b64("正在载入红心标签 ({}/{})".format(number,len(ids))))
                analyze(db,"netease://song/"+song_id,index,saved,AES)
            return 0
        if args.export: export_csv(db,args.export); emit("PROGRESS",b64("已导出："+args.export)); return 0
        if args.list: list_results(db,args.files); return 0
        if args.set:
            if len(args.files)!=1 or not args.primary.strip(): raise RuntimeError("手动修正需要选择一首歌曲并填写主曲风。")
            set_manual(db,args.files[0],args.primary,args.influences); return 0
        if not args.files: return 0
        AES=get_aes(); saved=cookie_text(); index=build_ncm_index()
        for number,path in enumerate(args.files,1):
            emit("PROGRESS",b64("正在读取歌曲百科 ({}/{}): {}".format(number,len(args.files),pathlib.Path(path).name)))
            try: analyze(db,path,index,saved,AES)
            except Exception as error: emit("ERROR",b64(path),b64(str(error)))
    except Exception as error: emit("FATAL",b64(str(error))); return 2
    finally: db.close()
    return 0


if __name__ == "__main__": raise SystemExit(main())
