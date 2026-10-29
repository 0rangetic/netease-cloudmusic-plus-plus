"""Private JSONL bridge to this exact client's in-process CEF DevTools API."""
from __future__ import annotations

import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import struct
import sys
import threading
import time

# Client builds verified for this in-process transport. 3.1.42.205594 ships the
# identical libcef.dll (same CEF 91 / branch 4472 layouts), so only the client
# version string gate widens; CEF_SHA256 below still rejects any other CEF.
SUPPORTED_VERSIONS = ("3.1.41.205529", "3.1.42.205594")
CEF_SHA256 = "a2a441490a9600f7e06b8c4dc609354ff7214add445a55c8fa699760d264e210"


def file_version(path: Path) -> str:
    version = ctypes.WinDLL("version", use_last_error=True)
    version.GetFileVersionInfoSizeW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD)]
    version.GetFileVersionInfoSizeW.restype = wintypes.DWORD
    version.GetFileVersionInfoW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
    version.VerQueryValueW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wintypes.UINT)]
    size = version.GetFileVersionInfoSizeW(str(path), None)
    if not size:
        raise ValueError("无法读取网易云文件版本")
    data = ctypes.create_string_buffer(size)
    value = ctypes.c_void_p()
    length = wintypes.UINT()
    if not version.GetFileVersionInfoW(str(path), 0, size, data) or not version.VerQueryValueW(data, "\\", ctypes.byref(value), ctypes.byref(length)):
        raise ValueError("无法核对网易云文件版本")
    # Build 205529 exceeds the 16-bit fixed-version field (which reads 8921).
    # Match the same FileVersion string used by Windows and the toolbox.
    if not version.VerQueryValueW(data, "\\VarFileInfo\\Translation", ctypes.byref(value), ctypes.byref(length)) or length.value < 4:
        raise ValueError("网易云缺少文件版本语言信息")
    translation = ctypes.cast(value, ctypes.POINTER(wintypes.WORD))
    key = "\\StringFileInfo\\%04x%04x\\FileVersion" % (translation[0], translation[1])
    if not version.VerQueryValueW(data, key, ctypes.byref(value), ctypes.byref(length)):
        raise ValueError("网易云缺少文件版本字符串")
    return ctypes.wstring_at(value, length.value).rstrip("\0").strip()


def validate_client(path: Path) -> None:
    actual = file_version(path)
    if actual not in SUPPORTED_VERSIONS:
        raise ValueError(
            "热接入仅适配网易云 " + " / ".join(SUPPORTED_VERSIONS)
            + " x64；当前为 " + actual)
    with path.open("rb") as source:
        source.seek(0x3C)
        source.seek(struct.unpack("<I", source.read(4))[0])
        if source.read(6) != b"PE\0\0\x64\x86":
            raise ValueError("热接入需要网易云 x64 客户端")
    cef = path.parent / "libcef.dll"
    with cef.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    if digest != CEF_SHA256:
        raise ValueError("CEF 版本不匹配，已停止热接入；需要重新适配客户端")


def check_process_access(pid: int) -> None:
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x1010, False, pid)  # QUERY_LIMITED_INFORMATION | VM_READ
    if not handle:
        raise PermissionError("无法读取网易云进程；请使用与客户端相同的 Windows 用户和权限运行工具箱")
    kernel.CloseHandle(handle)


def find_client(psutil):
    expected = os.environ.get("NCM_LISTEN_CLIENT_EXE", "")
    expected = os.path.normcase(os.path.abspath(expected)) if expected else ""
    found = []
    inaccessible = False
    requested_pid = os.environ.get("NCM_LISTEN_CLIENT_PID", "")
    if requested_pid:
        if not requested_pid.isdecimal() or int(requested_pid) < 1:
            raise ValueError("无效的网易云主进程编号")
        process = psutil.Process(int(requested_pid))
        process.info = process.as_dict(attrs=["name", "exe", "cmdline"])
        processes = [process]
    else:
        processes = psutil.process_iter(["name", "exe", "cmdline"])
    for process in processes:
        try:
            if (process.info["name"] or "").lower() != "cloudmusic.exe":
                continue
            args = process.info["cmdline"]
            if not args or not process.info["exe"]:
                inaccessible = True
                continue
            if any(arg.lower() == "--type" or arg.lower().startswith("--type=") for arg in args[1:]):
                continue
            actual = os.path.normcase(os.path.abspath(process.info["exe"]))
            if expected and actual != expected:
                raise ValueError("正在运行的网易云安装目录与工具箱选择不一致")
            found.append((process, Path(process.info["exe"])))
        except (psutil.AccessDenied, psutil.NoSuchProcess):
            inaccessible = True
    if len(found) > 1:
        raise ValueError("检测到多个网易云主进程，无法确定热接入目标")
    if not found:
        raise ValueError("无法读取网易云主进程，请使用同一 Windows 用户和权限运行" if inaccessible else "网易云未运行，请先打开客户端")
    process, path = found[0]
    validate_client(path)
    return process


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    output_lock = threading.Lock()

    def emit(message):
        with output_lock:
            print(json.dumps(message, ensure_ascii=False, separators=(",", ":")), flush=True)

    session = script = None
    closing = threading.Event()
    try:
        # Frida 17's Win32 injector can reject Windows' short 8.3 TEMP path.
        # Use an application-owned long path in this helper's environment only.
        temporary = Path(os.environ["LOCALAPPDATA"]) / "NeteaseToolbox" / "MultiListen" / "native" / "temporary"
        temporary.mkdir(parents=True, exist_ok=True)
        os.environ["TEMP"] = os.environ["TMP"] = str(temporary)
        import frida
        import psutil

        process = find_client(psutil)
        check_process_access(process.pid)
        session = frida.attach(process.pid)
        session.on("detached", lambda reason, *_: None if closing.is_set() else emit({"type": "error", "message": "网易云进程内连接已断开：" + str(reason)}))
        source = Path(__file__).with_name("native-agent.js").read_text(encoding="utf-8")
        script = session.create_script(source)

        def message_received(message, _data):
            if message["type"] == "send":
                emit(message["payload"])
            elif message["type"] == "error":
                emit({"type": "error", "message": "热接入脚本失败：" + message.get("description", "未知错误")})

        script.on("message", message_received)
        script.load()
        deadline = time.monotonic() + 22
        while True:
            try:
                script.exports_sync.initialize()
                break
            except frida.core.RPCException as error:
                if "主页面尚未就绪" not in str(error) or time.monotonic() >= deadline:
                    raise
                time.sleep(0.5)
        emit({"type": "ready", "pid": process.pid})
        for line in sys.stdin:
            request = json.loads(line)
            if request.get("type") == "stop":
                break
            if request.get("type") != "cdp" or not isinstance(request.get("message"), dict):
                raise ValueError("无效的本机桥接请求")
            script.exports_sync.dispatch(request["message"])
        return 0
    except Exception as error:
        emit({"type": "error", "message": "热接入失败：" + str(error)})
        return 1
    finally:
        closing.set()
        if script is not None:
            try:
                script.exports_sync.dispose()
                script.unload()
            except Exception:
                # Its unload hook retains the native callbacks until the UI
                # thread has removed the observer and released pending tasks.
                pass
        if session is not None:
            try:
                session.detach()
            except Exception:
                pass


if __name__ == "__main__":
    raise SystemExit(main())
