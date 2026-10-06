#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把应用窗口置前并按一个键（纯 ctypes）。用法：python tools/key.py ESC|ENTER"""

import ctypes
import ctypes.wintypes as wt
import sys
import time
from pathlib import Path

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()

VK = {"ESC": 0x1B, "ENTER": 0x0D, "TAB": 0x09}
name = (sys.argv[1] if len(sys.argv) > 1 else "ESC").upper()
vk = VK[name]
proc = (sys.argv[2] if len(sys.argv) > 2 else "BCML-WinUI3.exe").lower()

found = []


@ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
def cb(hwnd, _lp):
    if not user32.IsWindowVisible(hwnd):
        return True
    buf = ctypes.create_unicode_buffer(260)
    user32.GetWindowTextW(hwnd, buf, 260)
    if not buf.value:
        return True
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    h = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid.value)
    if h:
        size = wt.DWORD(260)
        nb = ctypes.create_unicode_buffer(260)
        if ctypes.windll.kernel32.QueryFullProcessImageNameW(h, 0, nb, ctypes.byref(size)):
            if Path(nb.value).name.lower() == proc:
                found.append(hwnd)
        ctypes.windll.kernel32.CloseHandle(h)
    return True


user32.EnumWindows(cb, 0)
if not found:
    print("NO_WINDOW")
    sys.exit(1)

user32.SetForegroundWindow(found[0])
time.sleep(0.4)
# KEYEVENTF_KEYUP = 2
user32.keybd_event(vk, 0, 0, 0)
time.sleep(0.05)
user32.keybd_event(vk, 0, 2, 0)
print(f"OK sent {name}")
