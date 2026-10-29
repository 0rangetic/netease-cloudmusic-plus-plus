import json
import os
import struct
from pathlib import Path
import tempfile
import unittest
from compatibility import inspect_client


class CompatibilityTests(unittest.TestCase):
    def test_installed_and_unknown(self):
        registry = Path(__file__).with_name('compatibility_profiles.json')
        configured = os.environ.get('NCM_TEST_CLIENT_DLL')
        if not configured:
            self.skipTest('Set NCM_TEST_CLIENT_DLL to a supported cloudmusic.dll for the optional integration check')
        binary = Path(configured)
        profile, report = inspect_client(binary, registry)
        self.assertIsNotNone(profile)
        self.assertEqual(report['status'], 'verified')
        matched_id = profile['id']
        with tempfile.TemporaryDirectory() as folder:
            changed = Path(folder) / 'changed.dll'
            changed.write_bytes(binary.read_bytes() + b'unknown-version')
            profile, report = inspect_client(changed, registry)
            self.assertIsNone(profile)
            self.assertEqual(report['status'], 'unsupported')
            damaged = Path(folder) / 'profiles.json'
            config = json.loads(registry.read_text(encoding='utf-8'))
            matched = next(item for item in config['profiles'] if item['id'] == matched_id)
            matched['signatures']['vectorOf'] = '00' * 48
            damaged.write_text(json.dumps(config))
            self.assertIsNone(inspect_client(binary, damaged)[0])

    def test_invalid_binary(self):
        with tempfile.TemporaryDirectory() as folder:
            binary = Path(folder) / 'invalid.dll'
            binary.write_bytes(b'bad')
            with self.assertRaises((ValueError, struct.error)):
                inspect_client(binary, Path(__file__).with_name('compatibility_profiles.json'))
