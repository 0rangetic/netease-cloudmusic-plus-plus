"""Start NetEase Cloud Music and attach the three-line native desktop lyric patch."""

from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

HERE = Path(__file__).resolve().parent
parser = argparse.ArgumentParser(description="三行歌词启动器")
parser.add_argument("--app-dir", type=Path, default=HERE.parent, help="网易云音乐安装目录")
ARGS = parser.parse_args()
APP = ARGS.app_dir.resolve() / "cloudmusic.exe"
DLL = ARGS.app_dir.resolve() / "cloudmusic.dll"
EXPECTED_DLL_SHA256 = "7f667f876caf94bc3dac3b0070d3e495117005fafdcf18b6aaa51eb004edac8c"
sys.path.insert(0, str(HERE / "vendor"))

import frida  # noqa: E402
import psutil  # noqa: E402


def main_process() -> psutil.Process | None:
    for proc in psutil.process_iter(["exe", "cmdline"]):
        try:
            if os.path.normcase(proc.info["exe"] or "") != os.path.normcase(str(APP)):
                continue
            if any(arg.startswith("--type=") for arg in (proc.info["cmdline"] or [])):
                continue
            return proc
        except (psutil.AccessDenied, psutil.NoSuchProcess):
            continue
    return None


def on_message(message: dict, data: bytes | None) -> None:
    if message["type"] == "error":
        print("补丁错误：", message.get("description", message), flush=True)
        return
    payload = message.get("payload", {})
    kind = payload.get("type")
    if kind == "ready":
        print("三行歌词补丁已连接。", flush=True)
    elif kind == "window-resized":
        print("网易云桌面歌词窗口已扩高。", flush=True)
    elif kind == "triline-rendered":
        print("已绘制日文、罗马音、中文三行。", flush=True)
    elif kind and "error" in kind:
        print("补丁警告：", payload.get("message", kind), flush=True)


def main() -> int:
    if not APP.is_file() or not DLL.is_file():
        print("未找到网易云音乐程序。请在工具箱设置中选择包含 cloudmusic.exe 和 cloudmusic.dll 的安装目录。")
        return 1
    with DLL.open("rb") as dll_file:
        actual_hash = hashlib.file_digest(dll_file, "sha256").hexdigest()
    if actual_hash != EXPECTED_DLL_SHA256:
        print("网易云版本已变化。补丁只适用于 3.1.41 x64 Build 205529。")
        print("请先更新补丁；原版网易云音乐可正常启动。")
        return 2

    proc = main_process()
    if proc is None:
        print("正在启动网易云音乐…", flush=True)
        subprocess.Popen([str(APP)], cwd=APP.parent)
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline:
            time.sleep(0.5)
            proc = main_process()
            if proc is not None:
                break
    if proc is None:
        print("未找到网易云主进程。")
        return 3

    source = (HERE / "triline_agent.js").read_text(encoding="utf-8")
    session = None
    for attempt in range(40):
        try:
            session = frida.attach(proc.pid)
            break
        except (frida.ProcessNotFoundError, frida.TransportError):
            time.sleep(0.5)
    if session is None:
        print("无法连接网易云主进程。")
        return 4

    try:
        script = session.create_script(source)
        script.on("message", on_message)
        script.load()
        print("保持此窗口打开；关闭网易云后补丁自动退出。", flush=True)
        while proc.is_running() and proc.status() != psutil.STATUS_ZOMBIE:
            time.sleep(1)
    except KeyboardInterrupt:
        print("正在停止补丁…", flush=True)
    except (frida.InvalidOperationError, psutil.NoSuchProcess):
        pass
    finally:
        try:
            session.detach()
        except frida.InvalidOperationError:
            pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
