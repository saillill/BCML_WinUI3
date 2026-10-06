#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在**窗口坐标系**里点一下 / 滚动（纯 ctypes）。

注意：截图（PrintWindow）拿到的是窗口内容，坐标原点是窗口左上角；
而 SetCursorPos 用的是**屏幕**坐标。两者差一个窗口位置，
所以这里必须先把窗口的屏幕原点加上 —— 否则点击会落到别的地方。

用法：
    python tools/click.py <窗口内x> <窗口内y> [进程名]
    python tools/wheel.py <窗口内x> <窗口内y> <滚格数> [进程名]
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time
from pathlib import Path

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()
user32.FindWindowW.restype = ctypes.c_void_p


def window_origin(proc: str = "BCML-WinUI3.exe"):
    """返回 (hwnd, left, top)。找不到返回 (0,0,0)。"""
    target = proc.lower()
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
                if Path(nb.value).name.lower() == target:
                    found.append(hwnd)
            ctypes.windll.kernel32.CloseHandle(h)
        return True

    user32.EnumWindows(cb, 0)
    if not found:
        return 0, 0, 0
    hwnd = found[0]
    rect = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    return hwnd, rect.left, rect.top


def activate(hwnd) -> None:
    """把目标窗口弄到前台。

    只调 SetForegroundWindow 经常**无效** —— Windows 会拒绝把前台权交给一个
    当前没有前台权的进程，返回值也不一定说明问题（点下去就落到别的窗口上了）。
    惯用绕法：先发一下 ALT（系统认为"用户有输入"），再 SetForegroundWindow，
    然后 BringWindowToTop + SetActiveWindow 兜底。

    另外**不要** ShowWindow(SW_RESTORE)：窗口是最大化的，restore 会改变尺寸，
    之前按截图量好的坐标就全废了。
    """
    VK_MENU, KEYEVENTF_KEYUP = 0x12, 0x0002
    user32.keybd_event(VK_MENU, 0, 0, 0)
    user32.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0)

    user32.SetForegroundWindow(hwnd)
    user32.BringWindowToTop(hwnd)
    user32.SetActiveWindow(hwnd)
    time.sleep(0.4)


def main() -> int:
    mode = sys.argv[0]
    if "wheel" in mode:
        wx, wy, ticks = int(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3])
        proc = sys.argv[4] if len(sys.argv) > 4 else "BCML-WinUI3.exe"
    else:
        wx, wy = int(sys.argv[1]), int(sys.argv[2])
        ticks = 0
        proc = sys.argv[3] if len(sys.argv) > 3 else "BCML-WinUI3.exe"

    hwnd, left, top = window_origin(proc)
    if not hwnd:
        print("NO_WINDOW")
        return 1
    activate(hwnd)

    sx, sy = left + wx, top + wy
    user32.SetCursorPos(sx, sy)
    time.sleep(0.3)

    if "wheel" in mode:
        delta = -120 if ticks < 0 else 120
        for _ in range(abs(ticks)):
            user32.mouse_event(0x0800, 0, 0, ctypes.c_ulong(delta & 0xFFFFFFFF), 0)
            time.sleep(0.12)
        print(f"OK scrolled {ticks} at window({wx},{wy}) -> screen({sx},{sy})")
    else:
        user32.mouse_event(0x0002, 0, 0, 0, 0)      # LEFTDOWN
        time.sleep(0.08)
        user32.mouse_event(0x0004, 0, 0, 0, 0)      # LEFTUP
        print(f"OK clicked window({wx},{wy}) -> screen({sx},{sy})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
