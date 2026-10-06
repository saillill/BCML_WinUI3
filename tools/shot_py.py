#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""给正在运行的应用窗口拍一张图（纯 ctypes + GDI，不需要 .NET / Add-Type）。

用途：验证新加的界面元素是否真的渲染出来了。
用法：python tools/shot_py.py <输出png> [进程名]
"""
import ctypes
import ctypes.wintypes as wt
import sys
from pathlib import Path

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
user32.SetProcessDPIAware()

PROC = (sys.argv[2] if len(sys.argv) > 2 else "BCML-WinUI3.exe")
OUT = Path(sys.argv[1] if len(sys.argv) > 1 else "shot.png")


def find_window() -> int:
    """找到该进程第一个可见的顶层窗口。"""
    target = PROC.lower()
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _lp):
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if not user32.IsWindowVisible(hwnd):
            return True
        buf = ctypes.create_unicode_buffer(260)
        user32.GetWindowTextW(hwnd, buf, 260)
        if not buf.value:
            return True
        # 取进程名比对
        h = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid.value)
        if h:
            size = wt.DWORD(260)
            nb = ctypes.create_unicode_buffer(260)
            if ctypes.windll.kernel32.QueryFullProcessImageNameW(h, 0, nb, ctypes.byref(size)):
                if Path(nb.value).name.lower() == target:
                    found.append(hwnd)
            ctypes.windll.kernel32.CloseHandle(h)
        return True

    user32.EnumWindows(cb, 0)
    return found[0] if found else 0


hwnd = find_window()
if not hwnd:
    print("NO_WINDOW")
    sys.exit(1)

user32.SetForegroundWindow(hwnd)
ctypes.windll.user32.ShowWindow(hwnd, 9)          # SW_RESTORE

rect = wt.RECT()
user32.GetWindowRect(hwnd, ctypes.byref(rect))
w, h = rect.right - rect.left, rect.bottom - rect.top

hdc = user32.GetWindowDC(hwnd)
memdc = gdi32.CreateCompatibleDC(hdc)
bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
gdi32.SelectObject(memdc, bmp)
# PW_RENDERFULLCONTENT = 2 —— WinUI 走 DirectComposition，不带上这个标志会拍到黑图
ok = user32.PrintWindow(hwnd, memdc, 2)

class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]

bi = BITMAPINFOHEADER()
bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
bi.biWidth, bi.biHeight = w, -h          # 负数 = 自上而下
bi.biPlanes, bi.biBitCount = 1, 24
buf = ctypes.create_string_buffer(w * h * 3)
gdi32.GetDIBits(memdc, bmp, 0, h, buf, ctypes.byref(bi), 0)

# 写成 BMP（PNG 需要额外依赖，BMP 纯手写）
row_pad = (4 - (w * 3) % 4) % 4
pixels = bytearray()
for y in range(h):
    row = buf.raw[y * w * 3:(y + 1) * w * 3]
    pixels += row + b"\x00" * row_pad

file_size = 14 + 40 + len(pixels)
header = bytearray()
header += b"BM" + file_size.to_bytes(4, "little") + b"\x00\x00\x00\x00" + (54).to_bytes(4, "little")
header += (40).to_bytes(4, "little") + w.to_bytes(4, "little", signed=True)
header += (-h).to_bytes(4, "little", signed=True) + (1).to_bytes(2, "little")
header += (24).to_bytes(2, "little") + (0).to_bytes(4, "little") + len(pixels).to_bytes(4, "little")
header += (2835).to_bytes(4, "little") * 2 + (0).to_bytes(4, "little") * 2
OUT.write_bytes(bytes(header) + bytes(pixels))

gdi32.DeleteObject(bmp)
gdi32.DeleteDC(memdc)
user32.ReleaseDC(hwnd, hdc)

print(f"OK {OUT} {w}x{h} printwindow={ok}")
