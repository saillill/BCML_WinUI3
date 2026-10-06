# -*- coding: utf-8 -*-
"""把林可儿模组的 Umbrella Glider 选项**取消勾选**（实现「原版伞」）。

━━━ 为什么需要这一步 ━━━

林可儿 `info.json` 里 Umbrella Glider 的 `default` 是 `true`，所以导入这个
模组时 BCML 会自动勾选它，于是 3 条洋伞文件被注入模组本体并合并进
`01007EF00011E000/romfs/Pack/TitleBG.pack`：

    Model/Item_Parastole2.sbfres        42 KB   洋伞模型
    Model/Item_Parastole2.Tex.sbfres    58 KB   洋伞贴图
    Model/Player_Animation.sbfres      17.7 MB  洋伞专属滑翔动作

本脚本**不再自己操作 pack**，改为直接调用 `rpc_server.apply_mod_options()`
—— 也就是 App 里「重选选项」走的同一条路径。这样只有一份实现，不会出现
「工具这么做、App 那么做」两边不一致的问题。

该路径现在会：
  1) 用快照 pristine 判定「所有变体提供过哪些文件」
  2) 把这次没选中的变体当初注入本体的内容**撤掉**（SARC 只摘条目，不整份删）
  3) 按新选择重新注入 + 重算 logs/

所以「取消勾选」这次才真的能撤掉洋伞，而不是"只增不减"。

用法：
    python tools/strip_umbrella.py            # 只看会怎么变（dry-run）
    python tools/strip_umbrella.py --apply    # 真正执行，并重算日志
"""
from __future__ import annotations

import json
import os
import shutil
import sys
import time
from pathlib import Path

MODS_ROOT = Path(os.environ["LOCALAPPDATA"]) / "bcml" / "mods_nx"
LINKLE_DIR = "0100_林可儿Mod3.0TheLinkleMod"
UMBRELLA = "Umbrella Glider"


def _load_rpc():
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "sidecar"))
    import rpc_server  # noqa: PLC0415

    return rpc_server


def main() -> int:
    apply = "--apply" in sys.argv
    mod = MODS_ROOT / LINKLE_DIR
    if not mod.is_dir():
        print(f"找不到模组目录：{mod}")
        return 1

    rpc = _load_rpc()
    options_json = mod / "options.json"
    data = json.loads(options_json.read_text(encoding="utf-8"))
    current = list(data.get("selects") or [])

    if UMBRELLA not in current:
        print(f"「{UMBRELLA}」当前已不在已选列表中：{current}")
        print("无需处理。")
        return 0

    after = [s for s in current if s != UMBRELLA]
    print(f"模组： {mod.name}")
    print(f"当前已选： {current}")
    print(f"调整后：  {after}")
    print()

    if not apply:
        print("这是 dry-run。将执行 apply_mod_options()：")
        print(f"  · 从本体撤销「{UMBRELLA}」注入的条目")
        print("  · 重建 options/（不含该变体）")
        print("  · 重算 logs/")
        print()
        print("确认无误后加 --apply 真正执行。")
        return 0

    # --- 备份本体 pack（这是唯一不可逆的部分，先留一手）---
    rel = Path("01007EF00011E000/romfs/Pack/TitleBG.pack")
    pack = mod / rel
    if pack.is_file():
        stamp = time.strftime("%Y%m%d-%H%M%S")
        bak = pack.with_suffix(pack.suffix + f".bak-{stamp}")
        shutil.copy2(pack, bak)
        print(f"已备份本体 pack -> {bak.name}")

    print("正在调用 apply_mod_options()（与 App 内「重选选项」同一条路径）…")
    try:
        result = rpc.apply_mod_options({"mod": str(mod), "selects": after})
    except Exception as err:  # noqa: BLE001
        print(f"✗ 失败：{err}")
        print("  备份仍在，可手动恢复本体 pack。")
        return 1

    print(f"✓ 已应用，当前选项：{result.get('applied')}")
    print()
    print("完成。请在应用里执行一次「重新合并」让改动生效。")
    print("（若之后想恢复洋伞，再重选勾上 Umbrella Glider 即可，随时可逆。）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
