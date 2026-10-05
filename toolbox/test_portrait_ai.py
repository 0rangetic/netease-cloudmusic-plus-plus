import base64
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import portrait_ai


class PortraitAiTests(unittest.TestCase):
    def test_windows_secret_roundtrip(self):
        secret = b"local-test-key"
        self.assertEqual(portrait_ai._crypt(portrait_ai._crypt(secret), decrypt=True), secret)

    def test_config_scopes_and_no_plaintext_secret(self):
        with tempfile.TemporaryDirectory() as temp:
            config_path = Path(temp) / "config.json"
            with patch.object(portrait_ai, "CONFIG_PATH", config_path), patch.object(
                portrait_ai, "TODO_CONFIG", Path(temp) / "missing.json"
            ):
                portrait_ai.save({"url": portrait_ai.DEFAULT_URL, "model": "test-model",
                                  "key": "local-test-key", "enabled": True,
                                  "prompts": {"all": "长期", "recent": "近期"}})
                saved = json.loads(config_path.read_text(encoding="utf-8"))
                self.assertNotIn("local-test-key", config_path.read_text(encoding="utf-8"))
                self.assertEqual(saved["prompts"], {"all": "长期", "recent": "近期"})
                self.assertTrue(saved["enabled"])
                self.assertEqual(portrait_ai._crypt(base64.b64decode(saved["secret"]), decrypt=True), b"local-test-key")

    def test_existing_config_defaults_to_enabled(self):
        with tempfile.TemporaryDirectory() as temp:
            config_path = Path(temp) / "config.json"
            config_path.write_text(json.dumps({"url": portrait_ai.DEFAULT_URL, "model": "test",
                                               "prompts": portrait_ai.PROMPTS, "secret": ""}), encoding="utf-8")
            with patch.object(portrait_ai, "CONFIG_PATH", config_path):
                self.assertTrue(portrait_ai.config()["enabled"])

    def test_disabled_blocks_both_scopes_before_data_or_network(self):
        with tempfile.TemporaryDirectory() as temp:
            config_path = Path(temp) / "config.json"
            state_path = Path(temp) / "results.json"
            config_path.write_text(json.dumps({"url": portrait_ai.DEFAULT_URL, "model": "test",
                                               "prompts": portrait_ai.PROMPTS, "secret": "",
                                               "enabled": False}), encoding="utf-8")
            with patch.object(portrait_ai, "CONFIG_PATH", config_path), patch.object(
                portrait_ai, "STATE_PATH", state_path
            ), patch.object(portrait_ai, "urlopen", side_effect=AssertionError("network called")):
                for scope in ("all", "recent"):
                    result = portrait_ai.generate(scope, Path(temp) / "missing-profile", Path(temp) / "missing-db")
                    self.assertEqual(result["status"], "disabled")
                self.assertEqual(set(json.loads(state_path.read_text(encoding="utf-8"))), {"all", "recent"})

    def test_song_payload_excludes_ids_and_paths(self):
        rows = portrait_ai._song_payload([{"songId": 7, "title": "A", "artist": "B",
                                           "path": "private", "liked": True}],
                                         {"7": {"genres": ["pop"], "moods": ["warm"]}})
        self.assertEqual(rows[0]["genres"], ["pop"])
        self.assertNotIn("songId", rows[0])
        self.assertNotIn("path", rows[0])

    def test_cli_save_preserves_chinese_prompts(self):
        with tempfile.TemporaryDirectory() as temp:
            environment = dict(os.environ, LOCALAPPDATA=temp)
            config_path = Path(temp) / "NeteaseToolbox/Profile/portrait-ai.json"
            config_path.parent.mkdir(parents=True)
            config_path.write_text(json.dumps({"url": portrait_ai.DEFAULT_URL, "model": "test-model",
                                               "prompts": portrait_ai.PROMPTS, "secret": "", "enabled": True}), encoding="utf-8")
            payload = {"url": portrait_ai.DEFAULT_URL, "model": "test-model", "key": "",
                       "enabled": False, "prompts": {"all": "长期画像", "recent": "近三十天"}}
            result = subprocess.run([sys.executable, str(Path(portrait_ai.__file__)), "save"],
                                    input=base64.b64encode(json.dumps(payload, ensure_ascii=False).encode("utf-8")).decode("ascii"), text=True,
                                    encoding="utf-8", env=environment, capture_output=True)
            self.assertEqual(result.returncode, 0)
            self.assertEqual(json.loads(config_path.read_text(encoding="utf-8"))["prompts"], payload["prompts"])


if __name__ == "__main__":
    unittest.main()
