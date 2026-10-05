"""Migration regression checks with synthetic data, without login or network."""
import json
import os
from pathlib import Path
import shutil
import sqlite3
import contextlib
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

import portable_paths
import portable_report
import memory_recall


class PortableTests(unittest.TestCase):
    def test_paths_follow_the_moved_folder_and_preserve_external_paths(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch)
            first, moved = root / "first", root / "第二台电脑"
            first.mkdir()
            (first / "input-folder.txt").write_text("music", encoding="utf-8")
            (first / "music").mkdir()
            shutil.copytree(first, moved)
            with patch.object(portable_paths, "HERE", moved):
                self.assertEqual(portable_paths.input_directory(), moved / "music")
                external = root / "external"
                (moved / "output-folder.txt").write_text(str(external), encoding="utf-8")
                self.assertEqual(portable_paths.saved_directory("output-folder.txt"), external)

    def test_report_cli_works_with_empty_path_and_no_codex_project(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch)
            local = root / "user"
            profile_dir = local / "NeteaseToolbox/Profile/unlogged"
            profile_dir.mkdir(parents=True)
            profile = {"generatedAt": "2026-10-05T00:00:00+08:00", "songs": [
                {"songId": "1", "title": "<script>alert(1)</script>", "artist": "测试艺人", "liked": True,
                 "likedAt": int(time.time()*1000), "totalPlayCount": 10},
                {"songId": "2", "title": "Old", "artist": "测试艺人", "liked": True,
                 "likedAt": int((time.time()-45*86400)*1000), "totalPlayCount": 5}]}
            (profile_dir / "netease-profile.json").write_text(json.dumps(profile), encoding="utf-8")
            config_dir = local / "NeteaseToolbox/Profile"
            (config_dir / "portrait-ai.json").write_text(json.dumps({"enabled": False, "model":"fixture", "url":"https://example.invalid/v1", "secret":""}), encoding="utf-8")
            genre_dir = local / "NeteaseToolbox/GenreAnalysis"
            genre_dir.mkdir()
            with contextlib.closing(sqlite3.connect(genre_dir / "analysis.sqlite3")) as db:
                db.execute("CREATE TABLE song_wiki(song_id TEXT,genres TEXT,moods TEXT,status TEXT)")
                db.executemany("INSERT INTO song_wiki VALUES(?,?,?,?)", [
                    ("1", '["Pop"]', '["Happy"]', "ok"), ("2", '["Rock"]', '["Calm"]', "ok")])
                db.commit()
            env = dict(os.environ, LOCALAPPDATA=str(local), PATH="")
            result = subprocess.run([sys.executable, str(Path(portable_report.__file__)), "--scope", "both",
                "--output-dir", str(root / "reports"), "--ai"], env=env, cwd=root,
                capture_output=True, text=True, encoding="utf-8", timeout=30)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("REPORT\tboth\t", result.stdout)
            recent = (root / "reports/.data-app-offline/exports/music-profile-report-recent.html").read_text(encoding="utf-8")
            self.assertIn("&lt;script&gt;", recent)
            self.assertNotIn("<script>alert", recent)
            self.assertIn("100.0%", recent)
            self.assertIn("AI 已停用", recent)
            self.assertNotIn('src="http', recent)
            all_report = (root / "reports/.data-app-offline/exports/music-profile-report-all.html").read_text(encoding="utf-8")
            self.assertIn("50.0%", all_report)

    def test_memory_uses_custom_output_and_rejects_conflicting_lyrics(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch)
            one, two = root / "input", root / "output"
            one.mkdir(); two.mkdir()
            (two / "测试艺人 - 测试歌曲.lrc").write_text("[00:01.00]森林深处的旋律", encoding="utf-8")
            from datetime import datetime, timezone
            play = {"key":"1", "songId":"1", "title":"测试歌曲", "artist":"测试艺人", "album":"测试专辑",
                    "playedAt":datetime.now(timezone.utc), "playedMs":int(time.time()*1000), "deviceSystem":"Windows", "deviceName":"test", "durationMs":1000}
            with patch.object(memory_recall, "load_playback", return_value=([play], [])), patch.object(
                    memory_recall, "load_life_context", return_value=([], {}, [])):
                result = memory_recall.search({"query":"森林深处", "musicRoots":[str(one),str(two)], "limit":40})
                self.assertEqual(len(result["results"]), 1)
                (one / "测试艺人 - 测试歌曲.lrc").write_text("[00:01.00]其他歌词", encoding="utf-8")
                result = memory_recall.search({"query":"森林深处", "musicRoots":[str(one),str(two)], "limit":40})
                self.assertEqual(result["results"], [])


if __name__ == "__main__":
    unittest.main()
