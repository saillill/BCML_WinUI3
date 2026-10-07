# -*- coding: utf-8 -*-
"""核对一个游戏 romfs 是否与 解包/<版本> 是同一份 dump。

为什么需要这个：整合包只是「被模组改过的那批文件」（约 1200 个 / 500 MB），
必须覆盖到**同版本同来源**的游戏上。如果目标游戏是另一次 dump
（不同区域/来源/更新），启动包 `Bootup*.pack` 与资源表
`ResourceSizeTable.product.srsizetable` 会对不上，
表现就是**卡加载或直接报错**。

用法：
    <内嵌运行时>python.exe tools/check_game_dump.py <游戏 romfs 路径> [参考版本]
例如：
    ... tools/check_game_dump.py "D:/games/BotW/01007EF00011E000/romfs" 1.6.0
"""
from __future__ import annotations

import hashlib
import os
import sys
from pathlib import Path

VER_DIRS = {
    "1.6.0": Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "解包"
    / "1.6.0" / "1.6.0" / "01007EF00011E000" / "romfs",
    "1.8.2": Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "解包"
    / "1.8.2" / "The Legend of Zelda Breath of the Wild 1.8.2"
    / "APP+UPD" / "romfs",
    "1.9.0": Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "解包"
    / "1.9.0" / "1.9.0" / "01007EF00011E000" / "romfs",
}

# 这几个文件的版本指纹最强，且与「能不能启动」直接相关
PROBES = [
    "Pack/Bootup.pack",
    "Pack/Bootup_CNzh.pack",
    "Pack/TitleBG.pack",
    "System/Resource/ResourceSizeTable.product.srsizetable",
    "Actor/ActorInfo.product.sbyml",
    "Font/Font_TH.sbfarc",          # 只有 1.9.0 有
    "System/font/nvn_font/nvn_font.ntx",  # 只有 1.6.0 / 1.8.2 有
]


def sha1_of(p: Path):
    if not p.is_file():
        return None, None
    h = hashlib.sha1()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return p.stat().st_size, h.hexdigest()[:16]


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    target = Path(sys.argv[1])
    if not target.is_dir():
        print(f"目录不存在：{target}")
        return 2

    print(f"待核对：{target}\n")
    base = {}
    for ver, d in VER_DIRS.items():
        base[ver] = {rel: sha1_of(d / rel) for rel in PROBES}

    print(f"{'文件':<50}" + "".join(f"{v:<12}" for v in VER_DIRS))
    print("-" * 90)
    mine = {}
    for rel in PROBES:
        mine[rel] = sha1_of(target / rel)
        cells = "".join(f"{str(base[v][rel][0]):<12}" for v in VER_DIRS)
        print(f"{rel:<50}{cells}")

    print("\n目标游戏的实际大小：")
    for rel in PROBES:
        s, h = mine[rel]
        print(f"  {rel:<50} {str(s):<12} {h or '—'}")

    print("\n与各参考版本比对：")
    for ver in VER_DIRS:
        same = diff = missing = 0
        for rel in PROBES:
            ms, mh = mine[rel]
            bs, bh = base[ver][rel]
            if ms is None:
                missing += 1
            elif bs is None:
                diff += 1
            elif mh == bh:
                same += 1
            else:
                diff += 1
        verdict = "**吻合**" if diff == 0 and same > 0 else ""
        print(f"  vs {ver}: 相同 {same}，不同 {diff}，目标缺失 {missing}  {verdict}")

    print("\n提示：若没有任何一个版本「不同 0」，说明这份 dump 与现有三个参考都不一致，"
          "\n     需要把 BCML 的游戏目录指到它、重新合并一次，才能得到匹配的整合包。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
