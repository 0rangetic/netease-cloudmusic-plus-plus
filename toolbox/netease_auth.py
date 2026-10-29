"""Local credential reader and QR matrix generator. Never print credentials."""
import ctypes
from ctypes import wintypes
import json
import os
import pathlib
import sys


def load_session():
    path = pathlib.Path(os.environ['LOCALAPPDATA']) / 'NeteaseToolbox/Auth/session.dat'
    if not path.exists():
        raise RuntimeError('请点击工具箱左侧“登录网易云”，用手机扫码登录。')
    class Blob(ctypes.Structure):
        _fields_ = [('size', wintypes.DWORD), ('data', ctypes.POINTER(ctypes.c_ubyte))]
    data = path.read_bytes()
    buffer = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
    source, output = Blob(len(data), buffer), Blob()
    crypt = ctypes.WinDLL('crypt32', use_last_error=True)
    crypt.CryptUnprotectData.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.c_void_p,
                                       ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
    if not crypt.CryptUnprotectData(ctypes.byref(source), None, None, None, None, 1, ctypes.byref(output)):
        raise RuntimeError('无法解密本机登录状态，请重新扫码登录。')
    try:
        return json.loads(ctypes.string_at(output.data, output.size).decode('utf-8'))
    finally:
        kernel = ctypes.WinDLL('kernel32')
        kernel.LocalFree.argtypes = [ctypes.c_void_p]
        kernel.LocalFree(output.data)


def load_cookie():
    return load_session()['cookie']


def account_folder(category):
    try:
        uid = str(load_session()['userId'])
        if not uid.isdecimal(): raise ValueError('Invalid account ID')
    except (OSError, RuntimeError, ValueError, KeyError):
        uid = 'unlogged'
    return pathlib.Path(os.environ['LOCALAPPDATA']) / 'NeteaseToolbox' / category / uid


def liked_playlist_id():
    import urllib.request
    import urllib.parse
    session = load_session()
    url = 'https://music.163.com/api/user/playlist/?' + urllib.parse.urlencode({'uid': session['userId'], 'limit': 1000, 'offset': 0})
    request = urllib.request.Request(url, headers={'Cookie': session['cookie'], 'Referer': 'https://music.163.com/', 'User-Agent': 'Mozilla/5.0'})
    with urllib.request.urlopen(request, timeout=25) as response:
        result = json.load(response)
    if result.get('code') != 200:
        raise RuntimeError('无法读取红心歌单，请重新扫码登录。')
    return next((str(item['id']) for item in result.get('playlist', [])
                 if item.get('specialType') == 5 and str(item.get('userId')) == str(session['userId'])), None)


if __name__ == '__main__':
    # QR contents arrive on stdin, not through a command line or an external service.
    sys.path.insert(0, str(pathlib.Path(__file__).parent / 'vendor'))
    import qrcode
    qr = qrcode.QRCode(border=4, error_correction=qrcode.constants.ERROR_CORRECT_M)
    qr.add_data(sys.stdin.read().strip())
    qr.make(fit=True)
    print(json.dumps(qr.get_matrix()))
