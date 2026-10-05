"""Local, image-free search across playback history and LifeRecall context events.

Context sources are registered by event kind. Photo events intentionally have no
registered provider; adding a future provider must return textual evidence only
and must not change playback search or ranking contracts.
"""

import base64
import csv
import datetime as dt
import json
import os
import pathlib
import re
import sys
import unicodedata


DEFAULT_LIFE_RECALL = pathlib.Path.home() / "Documents" / "LifeRecall"
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from netease_auth import account_folder
DEFAULT_PLAYBACK = account_folder("Playback") / "play-history.csv"
from portable_paths import input_directory, music_directories
DEFAULT_MUSIC_ROOT = input_directory()
MAX_RESULTS = 40


def norm(value):
    value = unicodedata.normalize("NFKC", str(value or "")).casefold()
    return re.sub(r"[^\w\u3400-\u9fff]+", "", value, flags=re.UNICODE)


def parse_ms(value):
    try:
        number = int(value)
        if number <= 0:
            return None
        return dt.datetime.fromtimestamp(number / 1000).astimezone()
    except (TypeError, ValueError, OSError, OverflowError):
        return None


def parse_int(value):
    try:
        return int(value)
    except (TypeError, ValueError, OverflowError):
        return None


def load_playback(path):
    rows = []
    warnings = []
    path = pathlib.Path(path)
    if not path.is_file():
        return rows, ["找不到播放记录文件：{}".format(path)]
    try:
        with path.open("r", encoding="utf-8-sig", newline="") as handle:
            for row in csv.DictReader(handle):
                played_ms = parse_int(row.get("播放时间Unix毫秒"))
                played_at = parse_ms(played_ms)
                if not played_ms or not played_at or not row.get("歌曲ID"):
                    continue
                rows.append({
                    "key": "{}:{}".format(row.get("歌曲ID", ""), played_ms),
                    "songId": str(row.get("歌曲ID", "")),
                    "playedMs": played_ms,
                    "playedAt": played_at,
                    "title": (row.get("歌曲") or "").strip(),
                    "artist": (row.get("艺人") or "").strip(),
                    "album": (row.get("专辑") or "").strip(),
                    "durationMs": parse_int(row.get("歌曲时长毫秒")),
                    "deviceSystem": (row.get("设备系统") or "").strip(),
                    "deviceName": (row.get("设备名称") or "").strip(),
                })
    except (OSError, UnicodeError, csv.Error) as exc:
        warnings.append("播放记录读取失败：{}".format(type(exc).__name__))
    rows.sort(key=lambda item: item["playedMs"], reverse=True)
    return rows, warnings


def _location_event(row):
    when = parse_ms(row.get("capturedAt"))
    lat, lon = row.get("latitude"), row.get("longitude")
    if not when or lat is None or lon is None:
        return None
    try:
        lat, lon = float(lat), float(lon)
    except (TypeError, ValueError):
        return None
    return {"kind": "location", "at": when, "latitude": lat, "longitude": lon,
            "accuracy": parse_int(row.get("accuracyMeters")), "source": str(row.get("source") or "")}


def _movement_event(row):
    start = parse_ms(row.get("from"))
    end = parse_ms(row.get("to"))
    if not start or not end:
        return None
    sessions = row.get("exerciseSessions")
    session_count = len(sessions) if isinstance(sessions, list) else 0
    return {"kind": "movement", "start": start, "end": end,
            "steps": parse_int(row.get("steps")), "distanceMeters": parse_int(row.get("distanceMeters")),
            "sessionCount": session_count, "source": str(row.get("source") or "")}


# Providers return normalized context records. Photo has deliberately not been
# registered; its JSON metadata and image file are ignored by this version.
CONTEXT_EVENT_PROVIDERS = {
    "location": _location_event,
    "movement": _movement_event,
}


def load_life_context(path):
    path = pathlib.Path(path)
    context, kind_counts, warnings = [], {}, []
    events_dir = path / "Events"
    if not events_dir.is_dir():
        return context, kind_counts, ["LifeRecall 目录中没有 Events 子目录：{}".format(events_dir)]
    try:
        for file_path in events_dir.glob("*.json"):
            try:
                row = json.loads(file_path.read_text(encoding="utf-8-sig"))
            except (OSError, UnicodeError, ValueError):
                warnings.append("有事件记录无法读取：{}".format(file_path.name))
                continue
            if not isinstance(row, dict):
                continue
            kind = str(row.get("kind") or "unknown")
            kind_counts[kind] = kind_counts.get(kind, 0) + 1
            provider = CONTEXT_EVENT_PROVIDERS.get(kind)
            if provider is None:
                continue
            event = provider(row)
            if event:
                context.append(event)
    except OSError as exc:
        warnings.append("LifeRecall 事件读取失败：{}".format(type(exc).__name__))
    return context, kind_counts, warnings


class LocalLyricProvider:
    """Find a local .lrc file by song title and, when present, artist name."""

    def __init__(self, music_root):
        self.music_root = pathlib.Path(music_root)
        self.files = []
        self._index_files()

    def _index_files(self):
        if not self.music_root.is_dir():
            return
        for directory, dirnames, filenames in os.walk(str(self.music_root)):
            dirnames[:] = [name for name in dirnames if name not in {".git", "node_modules", "runtime"}]
            for filename in filenames:
                if filename.lower().endswith(".lrc"):
                    path = pathlib.Path(directory) / filename
                    self.files.append((path, norm(path.stem)))
                    if len(self.files) >= 10000:
                        return

    def find(self, song):
        title = norm(song.get("title"))
        artist_parts = [norm(part) for part in re.split(r"[/、;；]", song.get("artist", "")) if norm(part)]
        if len(title) < 2:
            return ""
        ranked = []
        for path, stem in self.files:
            if title not in stem:
                continue
            artist_match = any(part in stem for part in artist_parts if len(part) > 1)
            ranked.append((2 if artist_match else 1, path))
        if not ranked:
            return ""
        best_score = max(item[0] for item in ranked)
        best_paths = [item[1] for item in ranked if item[0] == best_score]
        if best_score == 1 and len(best_paths) != 1:
            return ""
        contents = []
        for path in best_paths:
            try:
                contents.append(path.read_text(encoding="utf-8-sig", errors="replace")[:250000])
            except OSError:
                continue
        if not contents:
            return ""
        if len({norm(value) for value in contents}) > 1:
            return ""
        return contents[0]


def _lyric_lines(value):
    output = []
    for line in value.splitlines():
        line = re.sub(r"\[[0-9]{1,2}:[0-9]{2}(?:[.:][0-9]{1,3})?\]", "", line)
        if line.strip() and not re.match(r"^\[[a-zA-Z]+:", line.strip()):
            output.append(line.strip())
    return output


def _infer_month_day(month, day, rows):
    years = {row["playedAt"].year for row in rows} | {dt.datetime.now().year}
    candidates = []
    for year in years:
        try:
            candidate = dt.date(year, month, day)
        except ValueError:
            continue
        candidates.append(candidate)
    if not candidates:
        return None
    today = dt.date.today()
    in_archive = [value for value in candidates if any(row["playedAt"].date() == value for row in rows)]
    return min(in_archive or candidates, key=lambda value: abs((today - value).days))


def parse_query_time(query, rows, manual_start=None, manual_end=None):
    text = query or ""
    text = text.replace("昨晚", "昨天晚上").replace("昨夜", "昨天晚上").replace("今早", "今天早上").replace("今晨", "今天清晨")
    clean = text
    start_date = end_date = None
    time_start = time_end = None
    has_date = has_time = False
    manual_start_date = manual_end_date = None

    if manual_start:
        try:
            manual_start_date = dt.date.fromisoformat(manual_start)
            start_date = manual_start_date
            has_date = True
        except ValueError:
            pass
    if manual_end:
        try:
            manual_end_date = dt.date.fromisoformat(manual_end)
            end_date = manual_end_date
            has_date = True
        except ValueError:
            pass

    match = re.search(r"(20\d{2})\s*[-/.年]\s*(\d{1,2})\s*[-/.月]\s*(\d{1,2})\s*日?", clean)
    if match:
        try:
            day = dt.date(int(match.group(1)), int(match.group(2)), int(match.group(3)))
            start_date = end_date = day
            has_date = True
            clean = clean.replace(match.group(0), " ")
        except ValueError:
            pass
    else:
        match = re.search(r"(\d{1,2})\s*月\s*(\d{1,2})\s*日?", clean)
        if match:
            day = _infer_month_day(int(match.group(1)), int(match.group(2)), rows)
            if day:
                start_date = end_date = day
                has_date = True
            clean = clean.replace(match.group(0), " ")

    today = dt.date.today()
    relative_days = {"大前天": 3, "前天": 2, "昨天": 1, "今天": 0, "明天": -1}
    for word, offset in relative_days.items():
        if word in clean:
            day = today - dt.timedelta(days=offset)
            start_date = end_date = day
            has_date = True
            clean = clean.replace(word, " ")
            break
    days_ago = re.search(r"(\d{1,3})\s*天前", clean)
    if days_ago:
        day = today - dt.timedelta(days=int(days_ago.group(1)))
        start_date = end_date = day
        has_date = True
        clean = clean.replace(days_ago.group(0), " ")
    recent_days = re.search(r"最近\s*(\d{1,3})\s*天", clean)
    if recent_days:
        end_date = today
        start_date = today - dt.timedelta(days=max(0, int(recent_days.group(1)) - 1))
        has_date = True
        clean = clean.replace(recent_days.group(0), " ")

    weekdays = {"周一": 0, "星期一": 0, "周二": 1, "星期二": 1, "周三": 2, "星期三": 2,
                "周四": 3, "星期四": 3, "周五": 4, "星期五": 4, "周六": 5, "星期六": 5,
                "周日": 6, "星期日": 6, "周天": 6, "星期天": 6}
    week_matches = [(word, weekday) for word, weekday in weekdays.items() if word in clean]
    if "周末" in clean or "星期末" in clean:
        candidate_days = sorted({row["playedAt"].date() for row in rows if row["playedAt"].date().weekday() >= 5 and row["playedAt"].date() <= today})
        if candidate_days:
            latest = candidate_days[-1]
            saturday = latest if latest.weekday() == 5 else latest - dt.timedelta(days=1)
            start_date, end_date = saturday, saturday + dt.timedelta(days=1)
            has_date = True
        clean = clean.replace("周末", " ").replace("星期末", " ")
    elif week_matches and not has_date:
        _, weekday = week_matches[0]
        candidate_days = sorted({row["playedAt"].date() for row in rows if row["playedAt"].date().weekday() == weekday and row["playedAt"].date() <= today})
        day = candidate_days[-1] if candidate_days else today - dt.timedelta(days=(today.weekday() - weekday) % 7)
        start_date = end_date = day
        has_date = True
    for word, _ in week_matches:
        clean = clean.replace(word, " ")

    daypart = None
    buckets = (("凌晨", 0, 360), ("清晨", 300, 480), ("早上", 300, 660),
               ("上午", 360, 720), ("中午", 660, 840), ("下午", 720, 1080),
               ("傍晚", 1020, 1200), ("晚上", 1080, 1440), ("深夜", 1260, 1440))
    for word, start, end in buckets:
        if word in clean:
            daypart = (word, start, end)
            clean = clean.replace(word, " ")
            break

    clock_time = re.search(r"(?<!\d)([01]?\d|2[0-3]):([0-5]\d)(?!\d)", clean)
    exact_time = None if clock_time else re.search(r"(?<!\d)(\d{1,2})\s*(?:点|时)(?:半|(\d{1,2})\s*分?)?", clean)
    if clock_time:
        center = int(clock_time.group(1)) * 60 + int(clock_time.group(2))
        time_start, time_end = max(0, center - 30), min(1440, center + 30)
        has_time = True
        clean = clean.replace(clock_time.group(0), " ")
    elif exact_time:
        hour = int(exact_time.group(1))
        minute = 30 if exact_time.group(0).endswith("半") else int(exact_time.group(2) or 0)
        if daypart and daypart[0] in {"下午", "傍晚", "晚上", "深夜"} and 1 <= hour < 12:
            hour += 12
        elif daypart and daypart[0] == "中午" and 1 <= hour < 11:
            hour += 12
        elif daypart and daypart[0] == "凌晨" and hour == 12:
            hour = 0
        if 0 <= hour <= 23 and 0 <= minute < 60:
            center = hour * 60 + minute
            width = 30 if exact_time.group(2) or exact_time.group(0).endswith("半") else 60
            time_start, time_end = max(0, center - width), min(1440, center + width)
            has_time = True
        clean = clean.replace(exact_time.group(0), " ")
    elif daypart:
        _, time_start, time_end = daypart
        has_time = True

    for phrase in ("我记得", "我好像", "好像是", "好像", "当时", "那天", "那首", "一首", "听到的", "听过的", "播放的", "听的", "歌曲", "音乐", "那时候", "的时候", "是哪首", "哪一首", "播放记录"):
        clean = clean.replace(phrase, " ")
    if manual_start_date and (start_date is None or manual_start_date > start_date):
        start_date = manual_start_date
    if manual_end_date and (end_date is None or manual_end_date < end_date):
        end_date = manual_end_date
    return {"startDate": start_date, "endDate": end_date, "timeStart": time_start,
            "timeEnd": time_end, "hasDate": has_date, "hasTime": has_time,
            "hasTemporal": has_date or has_time, "text": re.sub(r"\s+", " ", clean).strip()}


def _text_terms(text):
    value = norm(text)
    terms = set(re.findall(r"[a-z0-9]{2,}", value))
    cjk_runs = re.findall(r"[\u3400-\u9fff]+", value)
    for run in cjk_runs:
        if 2 <= len(run) <= 18:
            terms.add(run)
        if len(run) > 18:
            terms.update(run[index:index + 5] for index in range(len(run) - 4))
        if len(run) >= 4:
            terms.update(run[index:index + 4] for index in range(len(run) - 3))
    generic = {"歌曲", "音樂", "音乐", "聽歌", "听歌", "那首", "一首", "當時", "当时", "記得", "记得", "好像", "播放", "那天"}
    return [term for term in terms if term not in generic and len(term) >= 2]


def _time_matches(played_at, parsed):
    day = played_at.date()
    if parsed["startDate"] and day < parsed["startDate"]:
        return False
    if parsed["endDate"] and day > parsed["endDate"]:
        return False
    if parsed["timeStart"] is not None:
        minute = played_at.hour * 60 + played_at.minute
        if not parsed["timeStart"] <= minute < parsed["timeEnd"]:
            return False
    return True


def _contexts_for(play, context):
    when = play["playedAt"]
    related = []
    for event in context:
        if event["kind"] == "movement":
            if event["start"].date() == when.date():
                pieces = []
                if event["steps"] is not None:
                    pieces.append("{} 步".format(event["steps"]))
                if event["distanceMeters"] is not None:
                    pieces.append("{:.1f} 公里".format(event["distanceMeters"] / 1000.0))
                if event["sessionCount"]:
                    pieces.append("{} 条运动记录".format(event["sessionCount"]))
                detail = "、".join(pieces) if pieces else "有运动汇总"
                related.append({"kind": "movement", "text": "当天运动汇总：{}（按全天统计，不代表播放时正在运动）".format(detail), "distanceMs": 0})
        elif event["kind"] == "location":
            delta = abs((event["at"] - when).total_seconds())
            if delta <= 60 * 60:
                accuracy = "精度约 {} 米".format(event["accuracy"]) if event["accuracy"] else "精度未知"
                related.append({"kind": "location", "text": "附近位置记录：{:.2f}, {:.2f}（{}；相差 {} 分钟）".format(
                    event["latitude"], event["longitude"], accuracy, int(delta // 60)), "distanceMs": int(delta * 1000)})
    related.sort(key=lambda item: item["distanceMs"])
    return related[:4]


def _find_lyric_hit(lines, terms):
    normalized_terms = sorted((norm(term) for term in terms if len(norm(term)) >= 3), key=len, reverse=True)
    for term in normalized_terms:
        for line in lines:
            if term in norm(line):
                return line[:180]
    return ""


def _base_lyric_excerpt(lines):
    useful = [line for line in lines if len(norm(line)) >= 3]
    return " / ".join(useful[:2])[:220]


def search(request):
    playback_path = request.get("playbackPath") or str(DEFAULT_PLAYBACK)
    life_path = request.get("lifeRecallPath") or str(DEFAULT_LIFE_RECALL)
    music_root = request.get("musicRoot") or str(DEFAULT_MUSIC_ROOT)
    plays, warnings = load_playback(playback_path)
    contexts, life_kinds, life_warnings = load_life_context(life_path)
    warnings.extend(life_warnings)
    roots = request.get("musicRoots") if "musicRoots" in request else [music_root]
    providers = [LocalLyricProvider(root) for root in roots if root and pathlib.Path(root).is_dir()]
    if not providers:
        warnings.append("本地音乐目录不可用，歌词检索已跳过。")
    for play in plays:
        matches = list(dict.fromkeys(provider.find(play) for provider in providers))
        matches = [value for value in matches if value]
        play["lyrics"] = matches[0] if len(matches) == 1 else ""
        play["lyricLines"] = _lyric_lines(play["lyrics"])

    parsed = parse_query_time(request.get("query", ""), plays, request.get("startDate"), request.get("endDate"))
    terms = _text_terms(parsed["text"])
    query_norm = norm(parsed["text"])
    activity_words = ("运动", "步行", "走路", "散步", "跑步", "锻炼", "骑车", "步数")
    mentions_activity = any(word in norm(request.get("query", "")) for word in activity_words)
    location_words = ("位置", "地点", "哪里", "在哪", "附近", "坐标", "定位", "地方")
    mentions_location = any(word in norm(request.get("query", "")) for word in location_words)
    results = []
    for play in plays:
        time_hit = _time_matches(play["playedAt"], parsed)
        if parsed["hasTemporal"] and not time_hit:
            continue
        title_n, artist_n, album_n = norm(play["title"]), norm(play["artist"]), norm(play["album"])
        lyric_n = norm("\n".join(play["lyricLines"]))
        matches = []
        score = 0
        if query_norm:
            if query_norm in title_n:
                matches.append("歌曲名命中“{}”".format(parsed["text"][:60]))
                score += 80
            elif query_norm in artist_n:
                matches.append("艺人名命中“{}”".format(parsed["text"][:60]))
                score += 70
            elif query_norm in album_n:
                matches.append("专辑名命中“{}”".format(parsed["text"][:60]))
                score += 55
            elif query_norm in lyric_n:
                matches.append("歌词命中“{}”".format(parsed["text"][:60]))
                score += 72
            else:
                matched_terms = [term for term in terms if term in title_n or term in artist_n or term in album_n or term in lyric_n]
                if matched_terms:
                    best = max(matched_terms, key=len)
                    where = "歌词" if best in lyric_n else "歌曲信息"
                    matches.append("{}包含线索“{}”".format(where, best))
                    score += 12 + min(35, len(best) * 4 + len(matched_terms) * 3)
        device_hint = norm(request.get("query", ""))
        device_text = norm(play["deviceSystem"] + " " + play["deviceName"])
        if any(word in device_hint for word in ("手机", "安卓", "android", "iphone", "ios", "移动端")) and any(word in device_text for word in ("android", "iphone", "ios", "mobile")):
            matches.append("设备记录符合手机端线索")
            score += 24
        elif any(word in device_hint for word in ("电脑", "pc", "桌面端", "windows", "mac")) and any(word in device_text for word in ("windows", "mac", "pc", "desktop")):
            matches.append("设备记录符合电脑端线索")
            score += 24
        if parsed["hasTemporal"] and time_hit:
            matches.append("播放时间符合日期/时段线索")
            score += 45 if parsed["hasDate"] else 25

        related = _contexts_for(play, contexts)
        movement_context = next((item for item in related if item["kind"] == "movement"), None)
        location_context = next((item for item in related if item["kind"] == "location"), None)
        if mentions_activity and movement_context:
            matches.append("当天有 LifeRecall 运动汇总")
            score += 18
        if mentions_location and location_context:
            matches.append("播放前后约一小时内有位置记录")
            score += 18

        textual_hit = any(item.startswith(("歌曲名", "艺人名", "专辑名", "歌词", "歌曲信息", "设备记录")) for item in matches)
        context_only_query = (mentions_activity and movement_context is not None) or (mentions_location and location_context is not None)
        if not parsed["hasTemporal"] and not textual_hit and not context_only_query:
            continue
        if not parsed["hasTemporal"] and query_norm and not textual_hit and not context_only_query:
            continue

        evidence = list(matches)
        if not evidence:
            evidence.append("未命中歌名或歌词；结果按播放时间列出供回忆")
        lyric_hit = _find_lyric_hit(play["lyricLines"], terms)
        results.append({
            "key": play["key"], "songId": play["songId"], "playedAt": play["playedAt"].isoformat(timespec="seconds"),
            "playedText": play["playedAt"].strftime("%Y-%m-%d %H:%M:%S"), "title": play["title"],
            "artist": play["artist"], "album": play["album"], "deviceSystem": play["deviceSystem"],
            "deviceName": play["deviceName"], "score": score, "evidence": evidence,
            "contexts": [item["text"] for item in related], "lyricExcerpt": lyric_hit or _base_lyric_excerpt(play["lyricLines"]),
        })

    results.sort(key=lambda item: (item["score"], item["playedAt"]), reverse=True)
    results = results[:min(MAX_RESULTS, max(1, int(request.get("limit") or MAX_RESULTS)))]
    play_dates = [row["playedAt"] for row in plays]
    summary = {
        "playCount": len(plays),
        "playFrom": min(play_dates).strftime("%Y-%m-%d") if play_dates else "",
        "playTo": max(play_dates).strftime("%Y-%m-%d") if play_dates else "",
        "lifeKinds": life_kinds,
        "lifeContextCount": len(contexts),
        "lyricFileCount": len({str(path) for provider in providers for path, _ in provider.files}),
        "lyricsMatchedSongs": sum(1 for row in plays if row["lyrics"]),
    }
    if life_kinds.get("photo"):
        summary["photoEventsSkipped"] = life_kinds["photo"]
    return {"summary": summary, "warnings": warnings, "results": results,
            "queryText": parsed["text"], "temporalFilter": parsed["hasTemporal"]}


def main():
    if len(sys.argv) != 2 or sys.argv[1] != "search":
        raise SystemExit("usage: memory_recall.py search")
    try:
        payload = json.loads(base64.b64decode(sys.stdin.read()).decode("utf-8"))
        result = search(payload if isinstance(payload, dict) else {})
        print(json.dumps(result, ensure_ascii=True, separators=(",", ":")))
    except Exception as exc:
        print(json.dumps({"error": "记忆检索失败（{}）".format(type(exc).__name__)}, ensure_ascii=True))
        print("记忆检索失败（{}）".format(type(exc).__name__), file=sys.stderr)
        raise SystemExit(2)


if __name__ == "__main__":
    main()
