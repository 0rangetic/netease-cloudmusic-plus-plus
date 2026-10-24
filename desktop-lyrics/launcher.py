"""Start NetEase Cloud Music and attach the three-line native desktop lyric patch."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from compatibility import inspect_client
parser = argparse.ArgumentParser(description="三行歌词启动器")
parser.add_argument("--app-dir", type=Path, default=HERE.parent, help="网易云音乐安装目录")
parser.add_argument("--check-only", action="store_true", help="只读兼容性检测，不启动或注入客户端")
ARGS = parser.parse_args()
APP = ARGS.app_dir.resolve() / "cloudmusic.exe"
DLL = ARGS.app_dir.resolve() / "cloudmusic.dll"
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
    print("正在检测三行歌词兼容性…", flush=True)
    try:
        profile, report = inspect_client(DLL, HERE / "compatibility_profiles.json")
        report_path = HERE / "compatibility-report.json"
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    except (OSError, ValueError, KeyError) as error:
        print("版本已变化：兼容性检测失败：" + str(error), flush=True)
        return 2
    if profile is None:
        print("版本已变化：" + report["reason"], flush=True)
        print("兼容性报告：" + str(report_path), flush=True)
        return 2
    print("兼容性检测通过：" + profile["id"], flush=True)
    if ARGS.check_only:
        return 0

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
    source = "const LYRIC_PROFILE = " + json.dumps(profile) + ";\n" + source
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
