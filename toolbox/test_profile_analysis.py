import json
import contextlib
import sqlite3
import tempfile
import unittest
from pathlib import Path

from profile_analysis import count_labels, is_recent_like, load_labels, portrait


class ProfileAnalysisTests(unittest.TestCase):
    def test_recent_boundary_requires_like(self):
        now = 2_000_000_000_000
        cutoff = now - 30 * 86400 * 1000
        self.assertTrue(is_recent_like({"liked": True, "likedAt": cutoff}, cutoff, now))
        self.assertFalse(is_recent_like({"liked": True, "likedAt": cutoff - 1}, cutoff, now))
        self.assertFalse(is_recent_like({"liked": False, "likedAt": cutoff}, cutoff, now))
        self.assertFalse(is_recent_like({"liked": True, "likedAt": now + 1}, cutoff, now))

    def test_multi_label_coverage_and_comparison(self):
        now = 2_000_000_000_000
        songs = [
            {"songId": 1, "liked": True, "likedAt": now, "title": "A", "artist": "X"},
            {"songId": 2, "liked": False, "likedAt": None, "title": "B", "artist": "Y"},
            {"songId": 3, "liked": False, "likedAt": None, "title": "C", "artist": "Z"},
        ]
        labels = {
            "1": {"genres": ["流行", "摇滚", "流行"], "moods": ["开心"]},
            "2": {"genres": ["流行"], "moods": []},
        }
        counts, covered = count_labels(songs, labels, "genres")
        self.assertEqual(covered, 2)
        self.assertEqual(counts["流行"], 2)
        self.assertEqual(counts["摇滚"], 1)
        summary, rows = portrait({"songs": songs}, labels, "recent", now_ms=now)
        self.assertIn("新增红心 1 首", summary)
        rock = next(row for row in rows if row[0] == "摇滚")
        self.assertEqual(rock[1:5], (1, 100.0, 1, 50.0))
        self.assertEqual(rock[5], 50.0)

    def test_manual_genre_override_keeps_wiki_mood(self):
        with tempfile.TemporaryDirectory() as temp:
            db_path = Path(temp) / "labels.sqlite3"
            with contextlib.closing(sqlite3.connect(db_path)) as db:
                db.execute("CREATE TABLE song_wiki(song_id TEXT, genres TEXT, moods TEXT, status TEXT)")
                db.execute("CREATE TABLE analysis(audio_path TEXT, genre TEXT, influences TEXT, mood TEXT, status TEXT, manual INTEGER)")
                db.execute("INSERT INTO song_wiki VALUES(?,?,?,?)", ("1", json.dumps(["流行"]), json.dumps(["治愈"]), "百科标签"))
                db.execute("INSERT INTO analysis VALUES(?,?,?,?,?,?)", ("netease://song/1", "摇滚", "民谣 / 摇滚", "", "手动修正", 1))
                db.commit()
            labels = load_labels(db_path, [{"songId": 1, "title": "A", "artist": "X"}])
            self.assertEqual(labels["1"]["genres"], ["摇滚", "民谣"])
            self.assertEqual(labels["1"]["moods"], ["治愈"])

    def test_analysis_rows_work_without_song_wiki_table(self):
        with tempfile.TemporaryDirectory() as temp:
            db_path = Path(temp) / "labels.sqlite3"
            with contextlib.closing(sqlite3.connect(db_path)) as db:
                db.execute("CREATE TABLE analysis(audio_path TEXT, genre TEXT, influences TEXT, mood TEXT, status TEXT, manual INTEGER)")
                db.execute("INSERT INTO analysis VALUES(?,?,?,?,?,?)", ("netease://song/1", "流行", "摇滚 / 民谣", "开心 / 治愈", "百科标签", 0))
                db.commit()
            labels = load_labels(db_path, [{"songId": 1, "title": "A", "artist": "X"}])
            self.assertEqual(labels["1"]["genres"], ["流行", "摇滚", "民谣"])
            self.assertEqual(labels["1"]["moods"], ["开心", "治愈"])

    def test_empty_database_reports_missing_labels(self):
        with tempfile.TemporaryDirectory() as temp:
            db_path = Path(temp) / "labels.sqlite3"
            with contextlib.closing(sqlite3.connect(db_path)):
                pass
            with self.assertRaisesRegex(RuntimeError, "没有可读取的标签表"):
                load_labels(db_path, [])


if __name__ == "__main__":
    unittest.main()
