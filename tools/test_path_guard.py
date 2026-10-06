"""回归测试：sidecar 的 `_safe_child_dir` 路径守卫。

`sys.reorderMods` / `sys.applyProfile` 会按传入的目录名去 rename.mod 目录，
`sys.updateMod` 还会 rmtree —— 所以「目录名里带路径分隔符」必须被挡住，
否则一个畸形请求就可能在模组根目录之外做移动或删除。

用法：python tools/test_path_guard.py    （退出码 0 = 全部符合预期）
"""
import importlib.util
import os
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
RPC = HERE.parent / "sidecar" / "rpc_server.py"

# rpc_server 导入时会抢占 stdout（os.dup(1) + os.dup2(2, 1)）—— 直接 import 会把
# 本脚本的输出搅乱，所以改为「源码级」提取：只把守卫函数那一段 exec 出来。
spec = importlib.util.spec_from_file_location("rpc_probe", RPC)
mod = importlib.util.module_from_spec(spec)
src = RPC.read_text(encoding="utf-8")
start = src.index("def _safe_child_dir")
end = src.index("def _resequence")
ns = {"Path": Path}
exec(src[start:end], ns)
safe_child_dir = ns["_safe_child_dir"]

root = Path(os.environ["LOCALAPPDATA"]) / "bcml" / "mods_nx"
print("root =", root)
print("root 存在:", root.is_dir())
print()

BACKSLASH = chr(92)
COLON = chr(58)

cases = [
    ("正常目录名",              "0100_ok",                       True),
    ("上跳一级",                "..",                            False),
    ("当前目录",                ".",                             False),
    ("空字符串",                "",                              False),
    ("相对上跳两级",            ".." + BACKSLASH + "..",         False),
    ("反斜杠子路径",            "a" + BACKSLASH + "b",           False),
    ("正斜杠子路径",            "a/b",                           False),
    ("绝对盘符路径",            "C" + COLON + BACKSLASH + "Windows", False),
    ("UNC 前缀",                BACKSLASH + BACKSLASH + "srv",   False),
    ("NUL 截断",                "abc" + chr(0) + "def",          False),
]

fail = 0
for label, raw, should_pass in cases:
    try:
        got = safe_child_dir(root, raw)
        ok = should_pass
        verdict = "放行" if ok else "!! 不该放行"
        detail = f"→ {got}"
    except ValueError as exc:
        ok = not should_pass
        verdict = "拒绝" if ok else "!! 不该拒绝"
        detail = str(exc)[:56]
    except Exception as exc:                                  # noqa: BLE001
        ok = False
        verdict = "!! 意外异常"
        detail = f"{type(exc).__name__}: {exc}"[:56]
    if not ok:
        fail += 1
    print(f"  {label:14s} {verdict:12s} {detail}")

print()
print("全部符合预期" if fail == 0 else f"有 {fail} 项不符合预期")
sys.exit(1 if fail else 0)
