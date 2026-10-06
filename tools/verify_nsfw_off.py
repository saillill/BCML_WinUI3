#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""验证 NSFW 下线在「后端往返」层面是否真的成立。

流程：
  1) 读当前 settings；
  2) 人为塞回一个 nsfw=True（模拟上游默认值表带回来的情况）；
  3) 走 C# 侧同样的做法：删掉 nsfw 后调 save_settings；
  4) 重新读，确认 nsfw 不存在（即被下线、不再落盘）。

运行（需内嵌 py39）：
    publish/win-x64/runtime/python39/python.exe tools/verify_nsfw_off.py
"""
import os
import sys

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

settings_path = os.path.join(os.environ["LOCALAPPDATA"], "bcml", "settings.json")
print(f"settings.json = {settings_path}\n")

before = rpc_server._call_api("get_settings", {})
bdata = before.get("data") if isinstance(before, dict) else before
print(f"1) 读回：含 nsfw? {'nsfw' in bdata if isinstance(bdata, dict) else '?'}")

if isinstance(bdata, dict):
    bdata["nsfw"] = True            # 人为塞回
    print("2) 人为塞回 nsfw=True")

# C# 侧 SaveSettingsAsync 等价动作
bdata.pop("nsfw", None)
print("3) 剔除 nsfw → save_settings")
rpc_server._call_api("save_settings", {"settings": bdata})

after = rpc_server._call_api("get_settings", {})
adata = after.get("data") if isinstance(after, dict) else after
has = isinstance(adata, dict) and "nsfw" in adata
print(f"4) 重新读回：含 nsfw? {has}")

raw = open(settings_path, encoding="utf-8").read()
print(f"   磁盘原文含 nsfw? {'nsfw' in raw}")
print(f"\n结论：{'失败（nsfw 仍在）' if (has or 'nsfw' in raw) else '成功（nsfw 已彻底下线）'}")
