#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""直接计时重新合并（api.remerge），检验 ModsPage.xaml.cs:1478 的「实测 6 秒完成」断言。

⚠ 本脚本会**真的重跑一次合并**（写入 BCML 的合并输出目录），耗时约数秒到数十秒。
   它不改模组目录本身，但会刷新合并结果 —— 如果你正在游戏里测试，请先关掉游戏。

运行方式：

    publish/win-x64/runtime/python39/python.exe tools/time_remerge.py
"""
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

try:
    import bcml.util  # noqa: F401
except ImportError:
    sys.stderr.write(
        "需要随包内嵌运行时：\n"
        f'    "{os.path.join(ROOT, "publish", "win-x64", "runtime", "python39", "python.exe")}" '
        f"tools/{os.path.basename(__file__)}\n"
    )
    sys.exit(2)

sys.path.insert(0, os.path.join(ROOT, "sidecar"))
import rpc_server  # noqa: E402

print(f"解释器：{sys.executable}")
print("开始计时 api.remerge(name='all') …\n")

t0 = time.perf_counter()
try:
    result = rpc_server._call_api("remerge", {"name": "all"})
    ok = True
except Exception as exc:  # noqa: BLE001
    ok = False
    result = exc
t1 = time.perf_counter()

el = (t1 - t0)
print(f"\n返回：{'成功' if ok else '异常'}  内容={str(result)[:200]}")
print(f"合并耗时：{el:.2f} 秒")
print("\n--- 断言核对 ---")
print("注释称：「实测 6 秒完成」")
print(f"实测：{el:.2f} 秒")
print(f"结论：{'符合（约 6 秒量级）' if 2 <= el <= 15 else '不符'}")
