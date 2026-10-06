#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""直接计时 modDetails（后端 sys.modDetails → mod_details_headless），检验注释里的性能断言。

背景：ModsPage.xaml.cs:863 的注释声称「林可儿 Mod 3.0」639 个文件
「耗时 2 秒以上」。本脚本按 sidecar 完全相同的调用方式计时，给出真实数字。

运行方式（必须用随包内嵌运行时，理由见 test_reorder_sandbox.py）：

    publish/win-x64/runtime/python39/python.exe tools/time_moddetails.py
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

modpack_dir = bcml.util.get_modpack_dir()
target = None
for name in os.listdir(modpack_dir):
    # 目录名被 BCML 去掉了空格与括号：0100_林可儿Mod3.0TheLinkleMod
    if "林可儿Mod3.0" in name.replace(" ", ""):
        target = os.path.join(modpack_dir, name)
        break

if not target:
    sys.stderr.write("找不到「林可儿 Mod 3.0」目录，跳过\n")
    sys.exit(3)

print(f"目标目录：{os.path.basename(target)}")
print(f"解释器  ：{sys.executable}\n")

# 1) 仅元数据（不遍历改动清单）
t0 = time.perf_counter()
r_meta = rpc_server.mod_details_headless({"mod": target, "meta": True, "edits": False})
t1 = time.perf_counter()
print(f"仅元数据（edits=False）      ：{(t1 - t0) * 1000:7.1f} ms")

# 2) 完整详情（元数据 + 选项 + 改动清单）—— 这才是详情页真正调用的形态
t2 = time.perf_counter()
r_full = rpc_server.mod_details_headless({"mod": target, "meta": True, "edits": True})
t3 = time.perf_counter()
print(f"完整详情（edits=True，首次）  ：{(t3 - t2) * 1000:7.1f} ms")

# 3) 再做一次（检验是否有内部缓存）
t4 = time.perf_counter()
r_full2 = rpc_server.mod_details_headless({"mod": target, "meta": True, "edits": True})
t5 = time.perf_counter()
print(f"完整详情（edits=True，再次）  ：{(t5 - t4) * 1000:7.1f} ms")

total = (r_full.get("edits") or {})
n_files = 0
if isinstance(total, dict):
    for v in total.values():
        if isinstance(v, list):
            n_files += len(v)
print(f"\n改动清单条目数合计           ：{n_files}")

print("\n--- 断言核对 ---")
print("注释称：「639 个文件，耗时 2 秒以上」")
print(f"实测冷调用                   ：{(t3 - t2) * 1000:.0f} ms")
print(f"结论：{'符合' if (t3 - t2) * 1000 >= 2000 else '不符（实际远快于断言）'}")
