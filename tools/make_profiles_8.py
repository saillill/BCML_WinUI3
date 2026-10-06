# -*- coding: utf-8 -*-
"""生成 8 个整合包配置档案（原版伞/伞形伞 × 有/无少女动作 × 纯净版/增强版）。

━━━ 规则（与用户逐条确认）━━━

【少女动作】维度 —— 指少女动作包这个 mod 启用还是禁用
    有少女动作 = 少女动作包 启用
    无少女动作 = 少女动作包 禁用

【伞】维度 —— 由林可儿模组的 Umbrella Glider 选项控制
    原版伞 = 保持游戏原本的滑翔帆模型  → Umbrella Glider 不勾
    伞形伞 = 把滑翔帆换成洋伞          → Umbrella Glider 勾选

【纯净版 / 增强版】
    纯净版 = 只启用林可儿核心 7 个（0100–0106）+ 少女动作包（若有少女动作）
    增强版 = 0107–0116 的 10 个套装/NPC 模组也启用

━━━ 一个必须知道的事实 ━━━

林可儿模组**本体**的 `01007EF00011E000/romfs/Pack/TitleBG.pack` 里，
已经固化了 Umbrella Glider 的 3 个条目（安装时注入并合并进本体，哈希与选项目录
完全一致）：

    Model/Item_Parastole2.sbfres        42 KB   洋伞模型
    Model/Item_Parastole2.Tex.sbfres    58 KB   洋伞贴图
    Model/Player_Animation.sbfres      17.7 MB  洋伞专属滑翔动作

**不勾选 Umbrella Glider 并不能删掉它们** —— 选项只影响 `options/` 目录，
而这三个文件已经长在本体 pack 里了。所以「原版伞」需要额外清理本体 pack，
见 `tools/strip_umbrella.py`。

本脚本只负责生成清单；清理动作需要用户显式执行（改模组本体，不可逆）。

用法：
    python tools/make_profiles_8.py [输出目录]      # 默认 ~/Downloads
"""
from __future__ import annotations

import datetime
import json
import os
import sys
from pathlib import Path

# --------------------------------------------------------------------------
# 模组名单
# --------------------------------------------------------------------------
CORE = [
    "0100_林可儿Mod3.0TheLinkleMod",
    "0101_林可儿中文对话修正",
    "0102_林可儿3.0防具图标Switch移植版",
    "0103_林可儿珠宝饰品补充LinkleJewelry",
    "0104_林可儿Switch标题画面自然版",
    "0105_林可儿防具展示架修复Switch移植版",
    "0106_林可儿盔甲改造修复LinkleArmourRefits",
]
ANIM_DIR = "0117_少女动作包脚步声修正版GirlyAnimationPack10.6Fixed"
LINKLE_DIR = "0100_林可儿Mod3.0TheLinkleMod"

MODS_ROOT = Path(os.environ["LOCALAPPDATA"]) / "bcml" / "mods_nx"

# 少女动作包的 18 项推荐选择（第 15 组单独处理）
ANIM_BASE = [
    "Battle_Girly",
    "EQ_PLAYNIM_EDIT",
    "IDLE_GAP",
    "MainEtc_Sit",
    "Misc_Edited",
    "PosesW_Genshin",
    "Poses_Cute",
    "RandomGirly",
    "RunSp_Slow",
    "Run_Girly",
    "Shallow_Girly",
    "Size100",
    "Smile75_BattleSER",
    "Talk_Shy",
    "WalkEd_Long",
    "Walk_Girly",
    "zUnL3Patch_LITE",
]
# 第 15 组：滑翔伞动作
PARAGLIDER_UMBRELLA = "Paraglider_L3"     # 洋伞（与林可儿洋伞配套）
PARAGLIDER_VANILLA = "Paraglider_Default"  # 原版滑翔帆

# 林可儿模组的选项
LINKLE_BASE = ["Mannequin Fix", "Royal Weapons"]
UMBRELLA = "Umbrella Glider"


def _scan() -> dict:
    """扫描已安装模组，按 dir 索引。"""
    out = {}
    for d in sorted(MODS_ROOT.iterdir()):
        if not d.is_dir():
            continue
        info = d / "info.json"
        if not info.is_file():
            continue
        j = json.loads(info.read_text(encoding="utf-8"))
        try:
            opts = json.loads((d / "options.json").read_text(encoding="utf-8"))
        except Exception:
            opts = {}
        out[d.name] = {
            "name": j.get("name") or d.name,
            "dir": d.name,
            "priority": int(j.get("priority") or 0),
            "options": opts,
        }
    return out


def _build_one(installed: dict, umbrella: bool, anim: bool, enhanced: bool) -> dict:
    """生成单个组合的清单文档。"""
    enabled = set(CORE)
    if anim:
        enabled.add(ANIM_DIR)
    if enhanced:
        enabled |= {d for d in installed if d not in (ANIM_DIR,)}

    mods = []
    for d, e in sorted(installed.items(), key=lambda kv: kv[1]["priority"]):
        disabled = d not in enabled
        sel = list(e["options"].get("selects") or [])
        base = list(e["options"].get("selects") or [])

        if d == LINKLE_DIR:
            sel = list(LINKLE_BASE)
            if umbrella:
                sel.append(UMBRELLA)
        elif d == ANIM_DIR and anim:
            sel = list(ANIM_BASE) + [
                PARAGLIDER_UMBRELLA if umbrella else PARAGLIDER_VANILLA
            ]

        mods.append(
            {
                "name": e["name"],
                "dir": d,
                "priority": e["priority"],
                "disabled": disabled,
                "options": {
                    "disable": list(e["options"].get("disable") or []),
                    "options": dict(e["options"].get("options") or {}),
                    "selects": sorted(sel),
                },
                "optionFolders": sorted(sel),
            }
        )

    label = (f"{'伞形伞' if umbrella else '原版伞'} "
             f"{'有' if anim else '无'}少女动作 "
             f"{'增强版' if enhanced else '纯净版'}")
    return {
        "format": "bcml-winui3-modlist",
        "version": 1,
        "exportedAt": datetime.datetime.now().isoformat(timespec="seconds"),
        "modCount": len(mods),
        "mods": mods,
        "_label": label,
        "_note": (
            "原版伞：需先执行 tools/strip_umbrella.py 清理林可儿本体 pack "
            "里已固化的洋伞条目，否则伞仍会是洋伞。"
            if not umbrella else ""
        ),
    }


def main() -> int:
    outdir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.home() / "Downloads"
    outdir.mkdir(parents=True, exist_ok=True)
    installed = _scan()

    made = []
    for umbrella in (False, True):          # 原版伞 → 伞形伞
        for anim in (True, False):          # 有 → 无
            for enhanced in (False, True):  # 纯净版 → 增强版
                doc = _build_one(installed, umbrella, anim, enhanced)
                label = doc.pop("_label")
                note = doc.pop("_note")
                path = outdir / f"{label}.json"
                path.write_text(
                    json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8"
                )
                made.append((label, doc, note, path))

    for label, doc, note, path in made:
        on = [m for m in doc["mods"] if not m["disabled"]]
        linkle = next(m for m in doc["mods"] if m["dir"] == LINKLE_DIR)
        anim_m = next(m for m in doc["mods"] if m["dir"] == ANIM_DIR)
        has_u = UMBRELLA in (linkle["options"]["selects"] or [])
        pg = [s for s in (anim_m["options"]["selects"] or [])
              if s.startswith("Paraglider_")]
        print(f"✓ {label}")
        print(f"    启用 {len(on)}/{doc['modCount']} 模组")
        print(f"    少女动作包 {'启用' if not anim_m['disabled'] else '禁用'}"
              f"   第15组={pg[0] if pg else '—'}")
        print(f"    Umbrella Glider {'开' if has_u else '关'}")
        if note:
            print(f"    ⚠ {note}")
    print()
    print(f"输出目录：{outdir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
