"""Shared paths for bundled workers, independent of drive letters and PATH."""
import json
import os
from pathlib import Path
import shutil

HERE = Path(__file__).resolve().parent


def saved_directory(name, fallback=None):
    setting = HERE / name
    if setting.is_file():
        value = setting.read_text(encoding="utf-8-sig").strip()
        if value:
            path = Path(value)
            return path if path.is_absolute() else (HERE / path).resolve()
    return fallback


def input_directory():
    return saved_directory("input-folder.txt", HERE / "music")


def music_directories():
    return list(dict.fromkeys(p for p in [input_directory(), saved_directory("output-folder.txt")]
                              if p is not None and p.is_dir()))


def node_executable():
    bundled = HERE / "runtime/node/node.exe"
    return str(bundled) if bundled.is_file() else (shutil.which("node") or "node")


def report_directory():
    return saved_directory("chart-report-project.txt", HERE / "reports")
