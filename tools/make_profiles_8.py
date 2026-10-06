# -*- coding: utf-8 -*-
"""生成整合包配置档案（原版伞/伞形伞 × 有/无少女动作 × 纯净版/增强版）。

按游戏本体版本分目录输出：1.6.0 / 1.8.2 / 1.9.0，每版 8 个，共 24 个。
三个版本的游戏本体不同，但 mod 集合与选项**完全相同**（用户已确认），
所以各版本的文件内容一致，只是分目录归档。

━━━ 规则（与用户逐条确认）━━━

【少女动作】维度 —— 指少女动作包这个 mod 启用还是禁用
    有少女动作 = 少女动作包 启用
    无少女动作 = 少女动作包 禁用

【伞】维度 —— 由林可儿模组的 Umbrella Glider 选项控制
    原版伞 = 保持游戏原本的滑翔帆模型  → Umbrella Glider 不勾
    伞形伞 = 把滑翔帆换成洋伞          → Umbrella Glider 勾选

【纯净版 / 增强版】
    纯净版 = 只启用林可儿核心 7 个（0100–0106）+ 少女动作包（若有少女动作）
             → 有少女动作 8 个 / 无少女动作 7 个
    增强版 = 0107–0116 的 10 个套装/NPC 模组也启用
             → 有少女动作 18 个 / 无少女动作 17 个

━━━ 必须知道的：导入 ≠ 生效 ━━━

`apply_profile`（导入）**只写 `options.json` 的记录**，不重建 `options/` 目录、
不撤销已注入模组本体的内容、也不重算 `logs/`。而 BCML 合并读的正是
模组根目录文件 + `logs/`。因此：

    · 导入「伞形伞」配置 → 仍是原版伞（除非本来就装着洋伞）
    · 导入「原版伞」配置 → 仍是洋伞（Umbrella Glider 的 3 个条目
      在导入该 mod 时就因 `default: true` 被注入本体 TitleBG.pack）

**两者都需要再对林可儿点一次「重选选项」才真正生效**——那一步才会
重建 `options/`、撤销未选项的注入、重算 `logs/`（详见 `apply_mod_options`）。
所以原版伞配置会带上这条提示，见 `_note`。

用法：
    python tools/make_profiles_8.py [输出目录]
    默认输出到 ~/Downloads（会自动建各版本子目录）
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
    """生成单个组合的清单文档。

    ⚠ 两个模组的 selects 必须**完全由常量推导**，绝不可沿用扫描到的现值。

    早期写法是 `sel = list(e["options"]["selects"])` 作默认、只在
    `d == ANIM_DIR and anim` 时覆盖。后果：
      · `anim=False`（无少女动作）时代码根本不进那个分支，sel 保持
        "导入时的全量 18 项"，于是 `Paraglider_L3`（洋伞动作）留了下来 ——
        无少女动作的包里仍然带洋伞滑翔动作。
      · 更迷惑的是「原版伞」失效：林可儿那边确实去掉了 Umbrella Glider，
        但少女动作包这边又把 Paraglider_L3 加了回去，两个模组各改一处，
        最终产物看起来毫无区别。

    所以这里改成：先算默认（不选任何可选选项），再按维度显式赋值。
    """
    enabled = set(CORE)
    if anim:
        enabled.add(ANIM_DIR)
    if enhanced:
        enabled |= {d for d in installed if d not in (ANIM_DIR,)}

    mods = []
    for d, e in sorted(installed.items(), key=lambda kv: kv[1]["priority"]):
        disabled = d not in enabled

        # 默认：不选任何选项（这两个模组之外的可选项一律留空）
        sel: list = []

        if d == LINKLE_DIR:
            # 林可儿：基础两项；「伞形伞」才加 Umbrella Glider。
            sel = list(LINKLE_BASE)
            if umbrella:
                sel.append(UMBRELLA)
        elif d == ANIM_DIR and anim:
            # 少女动作包：17 项基础 + 滑翔伞动作二选一。
            # 注意这里**不再依赖 umbrella**（伞型对动作包的影响只体现在
            # Paraglider_* 上），但这仍是「伞型」维度的一部分：
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
            "原版伞：导入后还需对「林可儿 Mod 3.0」点一次「重选选项」"
            "（取消勾选 Umbrella Glider），否则洋伞仍在。"
            if not umbrella else ""
        ),
    }


# 三个游戏本体版本的解包目录（用户提供；内容不同但 mod 集合一致）
GAME_VERSIONS = ["1.6.0", "1.8.2", "1.9.0"]
GAME_ROOT = (Path.home() / "Downloads" / "塞尔达传说 旷野之息"
             / "MOD整合包")
# 各版本解包目录（仅作存在性校验记录）
GAME_UNPACK = {
    "1.6.0": Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "解包" / "1.6.0" / "1.6.0",
    "1.8.2": (Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "解包" / "1.8.2"
              / "The Legend of Zelda Breath of the Wild 1.8.2" / "APP+UPD"),
    "1.9.0": Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "解包" / "1.9.0" / "1.9.0",
}

# 已有的目录骨架：MOD整合包/<版本>/<包型>/<伞型>/<动作>/
# 用户的 8 个维度正好落进这棵树：2 包型 × 2 伞型 × 2 动作 = 8 叶 × 3 版本 = 24
PKG_ENHANCED = "增强包"
PKG_PURE = "纯净包"
UMB_YES = "伞形滑翔翼"
UMB_NO = "默认滑翔翼"
ANIM_YES = "有少女动作"
ANIM_NO = "无少女动作"


def _dest_dir(root: Path, game_ver: str, enhanced: bool,
              umbrella: bool, anim: bool) -> Path:
    """把 8 个维度映射到已有的目录骨架。"""
    return (root / game_ver
            / (PKG_ENHANCED if enhanced else PKG_PURE)
            / (UMB_YES if umbrella else UMB_NO)
            / (ANIM_YES if anim else ANIM_NO))


def main() -> int:
    # 输出根目录：默认 MOD整合包\（沿用用户已有的目录骨架）
    outroot = Path(sys.argv[1]) if len(sys.argv) > 1 else GAME_ROOT
    dry = "--dry-run" in sys.argv
    installed = _scan()

    total = 0
    for game_ver in GAME_VERSIONS:
        unpack = GAME_UNPACK.get(game_ver)
        unpack_ok = "✓" if unpack and unpack.is_dir() else "?"
        print(f"─── 游戏版本 {game_ver}  (解包 {unpack_ok}) ───")

        made = []
        for enhanced in (False, True):      # 纯净版 → 增强版
            for umbrella in (False, True):  # 原版伞 → 伞形伞
                for anim in (True, False):  # 有 → 无
                    doc = _build_one(installed, umbrella, anim, enhanced)
                    label = doc.pop("_label")
                    note = doc.pop("_note")
                    dest = _dest_dir(outroot, game_ver, enhanced, umbrella, anim)
                    made.append((label, doc, note, dest, umbrella, anim, enhanced))
                    total += 1

        for label, doc, note, dest, umbrella, anim, enhanced in made:
            on = [m for m in doc["mods"] if not m["disabled"]]
            anim_m = next(m for m in doc["mods"] if m["dir"] == ANIM_DIR)
            linkle = next(m for m in doc["mods"] if m["dir"] == LINKLE_DIR)
            has_u = UMBRELLA in (linkle["options"]["selects"] or [])
            pg = [s for s in (anim_m["options"]["selects"] or [])
                  if s.startswith("Paraglider_")]
            rel = dest.relative_to(outroot)
            print(f"  ✓ {label}")
            print(f"      → {rel}")
            print(f"      启用 {len(on)}/{doc['modCount']}"
                  f"  动作包{'启用' if not anim_m['disabled'] else '禁用'}"
                  f"  第15组={pg[0] if pg else '—'}"
                  f"  Umbrella={'开' if has_u else '关'}")
            if note:
                print(f"      ⚠ {note}")
            if not dry:
                dest.mkdir(parents=True, exist_ok=True)
                path = dest / "配置.json"
                path.write_text(
                    json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8"
                )
        print()

    print(f"共 {total} 个配置" + ("（dry-run，未写盘）" if dry else ""))
    print(f"输出根目录：{outroot}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
