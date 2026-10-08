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
    纯净版 = 只启用林可儿核心 8 个（林可儿本体 + 手臂修正补丁 + 其余 6 个）
             + 少女动作包（若有少女动作）
             → 有少女动作 9 个 / 无少女动作 8 个
    增强版 = 其余套装/NPC 模组也启用 → 全部启用

★★ 模组一律按**名字**认，不按编号 ★★

    模组目录名是 `<4位优先级>_<安全名>`，而优先级会随用户拖动重排
    （实测：加进「手臂修正补丁」后，原本 0100–0117 全体顺移一位，
    少女动作包从 0117 变成 0118）。早先这里写死的是**带编号的目录名**，
    重排后 `0101_林可儿中文对话修正` 已经指向别的模组 —— 会静默生成
    错误的启用清单。所以现在统一用 `_key()` 去掉编号前缀来匹配。

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
import re
import sys
from pathlib import Path

# --------------------------------------------------------------------------
# 模组名单
# --------------------------------------------------------------------------
# 一律用**去掉编号前缀**的名字匹配（见模块 docstring 里说明的原因）。
# 这些名字来自 BCML 的 `get_safe_pathname(info.json 的 name)`，重排号也不变。
CORE = [
    "林可儿Mod3.0TheLinkleMod",
    "非官方林可儿3.0补丁精简版手臂修正",   # 从少女动作包抽出的手臂修正补丁
    "林可儿中文对话修正",
    "林可儿3.0防具图标Switch移植版",
    "林可儿珠宝饰品补充LinkleJewelry",
    "林可儿Switch标题画面自然版",
    "林可儿防具展示架修复Switch移植版",
    "林可儿盔甲改造修复LinkleArmourRefits",
]
ANIM_KEY = "少女动作包脚步声修正版GirlyAnimationPack10.6Fixed"
LINKLE_KEY = "林可儿Mod3.0TheLinkleMod"

MODS_ROOT = Path(os.environ["LOCALAPPDATA"]) / "bcml" / "mods_nx"


def _key(dirname: str) -> str:
    """去掉目录名的 `<4位优先级>_` 前缀。

    这是**稳定标识**：模组被重排号后前缀会变，但后面的安全名不变。
    """
    return re.sub(r"^\d+_", "", dirname)

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
    """扫描已安装模组，**按 `_key()` 索引**（编号前缀不进 key）。"""
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
        out[_key(d.name)] = {
            "name": j.get("name") or d.name,
            "dir": d.name,              # 真实目录名（带编号）—— 清单里要用它
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
        enabled.add(ANIM_KEY)
    if enhanced:
        enabled |= {k for k in installed if k != ANIM_KEY}

    mods = []
    for k, e in sorted(installed.items(), key=lambda kv: kv[1]["priority"]):
        disabled = k not in enabled

        # 默认：不选任何选项（这两个模组之外的可选项一律留空）
        sel: list = []

        if k == LINKLE_KEY:
            # ★ Umbrella Glider **只在动画包禁用时**才由林可儿提供。
            #
            #   为什么必须互斥：林可儿的 `Umbrella Glider` 与动画包的
            #   `Paraglider_L3` 是**两套不同的洋伞实现**，各自都提供
            #   `Model/Item_Parastole2.sbfres` 与 `.Tex.sbfres`；
            #   而且林可儿那份还额外提供 `Model/Player_Animation.sbfres`，
            #   动画包则由 `EQ_PLAYNIM_EDIT` 提供同名但内容不同的那份。
            #
            #   同时启用 → 同名条目在合并时按模组优先级二选一 →
            #   出现「洋伞模型 + 原版滑翔动作」这类错配 → 进游戏走路头部乱转。
            #   实测：原版伞配置里混进了 Paraglider_L3 的 Item_Parastole2。
            #
            #   动画包启用时洋伞交给动画包的 `Paraglider_L3`
            #   （名称即「林可儿 3.0 洋伞（含专属动作）」，模型/贴图/动作自成一套）。
            sel = list(LINKLE_BASE)
            if umbrella and not anim:
                sel.append(UMBRELLA)
        elif k == ANIM_KEY and anim:
            # 少女动作包：17 项基础 + 滑翔伞动作二选一（伞型维度在这里体现）。
            sel = list(ANIM_BASE) + [
                PARAGLIDER_UMBRELLA if umbrella else PARAGLIDER_VANILLA
            ]

        mods.append(
            {
                "name": e["name"],
                "dir": e["dir"],        # 真实目录名（带当前编号）
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
        "_note": _note_for(umbrella, anim),
    }


def _note_for(umbrella: bool, anim: bool) -> str:
    """给配置写一句「怎么才真正生效」的提示。

    本清单的 `options.selects` 已经是最终选择，但 `apply_profile`（导入清单）
    **只写记录、不重建 `options/`、不撤销已注入内容**，所以手工导入后仍需
    对相关模组点一次「重选选项」。批量打包脚本 build_all_packs.py 会自动补这一步。

    伞型与动画包的互斥关系（见 _build_one 注释）会让"该改哪个模组"随组合而变，
    所以提示要按组合给出，不能一律写同一句。
    """
    parts = []
    if anim:
        # 动画包启用：洋伞/滑翔帆由动画包的 Paraglider_* 决定，
        # 林可儿的 Umbrella Glider 必须保持关闭（已在 selects 里体现）。
        if umbrella:
            parts.append("洋伞由「少女动作包」的 Paraglider_L3 提供")
        else:
            parts.append("滑翔帆由「少女动作包」的 Paraglider_Default 提供")
    elif umbrella:
        # 动画包禁用：洋伞由林可儿自己提供（默认 Umbrella Glider 即为开）
        parts.append("洋伞由「林可儿 Mod 3.0」的 Umbrella Glider 提供")
    else:
        parts.append("洋伞已关闭，滑翔帆为游戏原版")

    parts.append(
        "导入后如需手工生效，请对"
        + ("「少女动作包」" if anim else "「林可儿 Mod 3.0」")
        + "点一次「重选选项」"
    )
    return "；".join(parts) + "。"


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
    #
    #   ★ 只取「非选项参数」当输出目录 ★
    #   之前直接写 `sys.argv[1]`，于是传 `--pure-only` 时它被当成目录名 ——
    #   配置全写进了仓库根下一个叫 `--pure-only` 的文件夹里（已踩过）。
    positionals = [a for a in sys.argv[1:] if not a.startswith("-")]
    outroot = Path(positionals[0]) if positionals else GAME_ROOT
    dry = "--dry-run" in sys.argv
    pure_only = "--pure-only" in sys.argv     # 只写「纯净包」的配置，别动增强包
    installed = _scan()

    # 缺模组要**直接报错**，不能默默生成一份错的启用清单
    missing = [k for k in CORE + [ANIM_KEY, LINKLE_KEY] if k not in installed]
    if missing:
        print("✗ 以下模组尚未安装，无法生成配置：")
        for k in missing:
            print(f"    {k}")
        print("\n  当前已安装：")
        for k, e in sorted(installed.items(), key=lambda kv: kv[1]["priority"]):
            print(f"    [{e['priority']:>4}] {e['dir']}")
        return 2
    print(f"已识别模组 {len(installed)} 个（按名字匹配，与编号无关）")

    total = 0
    for game_ver in GAME_VERSIONS:
        unpack = GAME_UNPACK.get(game_ver)
        unpack_ok = "✓" if unpack and unpack.is_dir() else "?"
        print(f"─── 游戏版本 {game_ver}  (解包 {unpack_ok}) ───")

        made = []
        plans = [(False,)] if pure_only else [(False,), (True,)]
        for (enhanced,) in plans:           # 纯净版 → 增强版
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
            anim_m = next(m for m in doc["mods"] if _key(m["dir"]) == ANIM_KEY)
            linkle = next(m for m in doc["mods"] if _key(m["dir"]) == LINKLE_KEY)
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
