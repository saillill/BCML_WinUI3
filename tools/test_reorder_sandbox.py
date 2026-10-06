#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在沙箱目录里验证 sidecar 的 sys.reorderMods 逻辑，**不触碰真实模组目录**。

做法：先把 bcml.util.get_modpack_dir 换成临时目录，再加载 rpc_server.py，
这样 reorder_mods 里的 bcml_util 就是被替换过的那个模块对象。

注意：rpc_server.py 在导入时会把 fd1 重定向到 stderr，所以用 `2>&1` 运行。

运行方式（重要）：本脚本依赖 BCML 本体，而 BCML 的原生扩展 `oead.pyd` 是
**针对 CPython 3.9 编译**的。所以必须用随包的内嵌运行时跑：

    publish/win-x64/runtime/python39/python.exe tools/test_reorder_sandbox.py

不要试图把 3.9 的 site-packages 挂到别的解释器上 —— 原生扩展的 ABI 不兼容，
会卡死或崩溃。下面 _require_bundled_runtime 会先做检查，版本不对就立刻给出指引退出。
"""
import importlib.util
import json
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SERVER = os.path.join(ROOT, "sidecar", "rpc_server.py")
BUNDLED_PY = os.path.join(ROOT, "publish", "win-x64", "runtime", "python39")
BUNDLED_EXE = os.path.join(BUNDLED_PY, "python.exe")


def _require_bundled_runtime() -> None:
    """检查当前解释器是否是随包的内嵌 3.9；不是就给出可执行的指引并退出。

    这里**不做**跨版本兼容（挂 sys.path 那种做法会因 oead.pyd 的 ABI 不匹配而卡死）。
    """
    try:
        import bcml.util  # noqa: F401
        return
    except ImportError:
        pass

    running = f"{sys.version_info.major}.{sys.version_info.minor}"
    sys.stderr.write(
        "找不到 bcml 模块。本测试需要随包的内嵌运行时（CPython 3.9 + 为 3.9 编译的 oead.pyd）。\n"
        f"当前解释器：{sys.executable}（Python {running}）\n"
        f"内嵌运行时：{BUNDLED_EXE}\n\n"
        "请这样运行：\n\n"
        f"    \"{BUNDLED_EXE}\" tools/{os.path.basename(__file__)}\n"
    )
    sys.exit(2)


_require_bundled_runtime()

failures = []


def check(cond, label, extra=""):
    print(("  OK   " if cond else "  FAIL ") + label + (("  " + extra) if extra else ""))
    if not cond:
        failures.append(label)


sandbox = tempfile.mkdtemp(prefix="bcml_sandbox_")
try:
    # ---- 1) 造 4 个假 mod ----
    names = ["甲套装", "乙套装", "丙套装", "丁套装"]
    for i, n in enumerate(names):
        p = os.path.join(sandbox, f"{100 + i:04d}_{n}")
        os.makedirs(p, exist_ok=True)
        json.dump({"name": n, "id": f"{100 + i:04d}_{n}", "priority": 100 + i},
                  open(os.path.join(p, "info.json"), "w", encoding="utf-8"),
                  ensure_ascii=False, indent=2)

    # ---- 2) 替换 BCML 的 mod 目录，再加载 sidecar ----
    import bcml.util as bcml_util
    bcml_util.get_modpack_dir = lambda: type(bcml_util.get_modpack_dir()) if False else __import__("pathlib").Path(sandbox)
    bcml_util.get_safe_pathname = lambda n: "".join(ch for ch in n.strip().replace(" ", "") if ch.isalnum() or ch in "-_.")

    spec = importlib.util.spec_from_file_location("rpc_server_under_test", SERVER)
    srv = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(srv)

    def listing():
        out = []
        for d in sorted(os.listdir(sandbox)):
            info = os.path.join(sandbox, d, "info.json")
            if os.path.isdir(os.path.join(sandbox, d)) and os.path.exists(info):
                meta = json.load(open(info, encoding="utf-8"))
                out.append((d, meta["priority"], meta["id"]))
        return out

    print("初始:")
    for x in listing():
        print("   ", x)

    # ---- 3) 反转顺序 ----
    before = [d for d, _, _ in listing()]
    reversed_order = list(reversed(before))
    print("\n调用 reorderMods(反转顺序)…")
    res = srv.reorder_mods({"order": reversed_order, "start": 100})
    print("   返回:", res)

    after = listing()
    print("结果:")
    for x in after:
        print("   ", x)

    check(res["moved"] == 4, "4 个目录都被处理")
    check(len(after) == 4, "没有目录丢失")
    check([d.split("_", 1)[1] for d, _, _ in after] == [d.split("_", 1)[1] for d in reversed_order], "模组顺序 = 请求顺序")
    check([p for _, p, _ in after] == [100, 101, 102, 103], "优先级连续 100..103")
    check([i for _, _, i in after] == [f"{100+j:04d}_{d.split('_',1)[1]}" for j, d in enumerate(reversed_order)],
          "id 已同步重写")
    check(not any(d.startswith("__tmp_") for d, _, _ in after), "没有残留 __tmp_ 目录")

    # ---- 4) 空顺序 / 不存在的目录：不应报错也不应改动 ----
    r2 = srv.reorder_mods({"order": []})
    check(r2["moved"] == 0, "空顺序返回 moved=0")
    r3 = srv.reorder_mods({"order": ["不存在的目录"], "start": 100})
    check(r3["moved"] == 0, "不存在的目录被跳过")

    # ---- 5) 幂等：再来一次同样的顺序 ----
    srv.reorder_mods({"order": reversed_order, "start": 100})
    check(listing() == after, "重复执行同一顺序结果不变（幂等）")

finally:
    shutil.rmtree(sandbox, ignore_errors=True)

print()
if failures:
    print(f"存在 {len(failures)} 项失败: {failures}")
    sys.exit(1)
print("全部通过")
