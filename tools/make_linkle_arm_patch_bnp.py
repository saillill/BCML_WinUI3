# -*- coding: utf-8 -*-
"""把少女动作包里的「非官方 林可儿 3.0 补丁・精简版」单独抽出来做成 BNP。

背景（用户需求）：原版林可儿的手臂偏长，少女动作包**最后一组**
（`Extra 2) OPTIONAL PATCHES`）的 **`zUnL3Patch_LITE`** 修复了这个问题。
该补丁提供 `TitleBG.pack` 的 3 个条目：
    Model/Link.sbfres                ← 玩家模型（手臂比例在这里）
    Model/Armor_Default.sbfres
    Actor/Pack/GameROMPlayer.sbactorpack
用户想把它做成独立 BNP，在**不用少女动作包**（原版林可儿）时装上。

做法严格走 BCML 自己的流程（不手工拼压缩包）：
  ① 用 BCML 的 `install.open_mod` 打开源 bnp，取出该选项目录；
  ② 把它的文件按原相对路径摆进一个独立模组目录；
  ③ 调 BCML 的 `dev.create_bnp_mod` 生成 BNP
     （它会写 info.json、打包 SARC、生成 logs、再用 7z 封包）。

⚠ 与少女动作包**互斥**：两者提供同一组条目，不要同时启用。

★ 两个必须注意的坑（都踩过） ★

1) **必须装上 sidecar 的串行池**（import rpc_server 即生效）。
   否则 BCML 的 `util.start_pool()` 会真的 `multiprocessing.Pool(63)`，
   而 Windows 下 worker 会以本脚本为 `__mp_main__` 重新导入 —— 直接
   掀出一场「进程风暴」（实测 40+ 个 python.exe 卡住）。

2) **必须加 `if __name__ == "__main__":` 守卫**。同理：Windows 用 spawn
   启动子进程时会重新导入主模块，没有守卫就会递归执行整个脚本。

用法：
    <内嵌运行时>python.exe tools/make_linkle_arm_patch_bnp.py [输出目录]
"""
from __future__ import annotations

import io
import shutil
import sys
import tempfile
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "publish" / "win-x64" / "runtime" / "python39" / "Lib" / "site-packages"))
sys.path.insert(0, str(ROOT / "sidecar"))

# 先装串行池，再 import bcml 的其余部分 —— 顺序无所谓，但必须在用到池之前
import rpc_server  # noqa: E402,F401  —— 作用是 _install_serial_pool()

from bcml import dev, install, util as U  # noqa: E402

SRC_BNP = (Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "Mod文件"
           / "纯净版" / "少女动作"
           / "girly_animation_pack_10_6_footstepsound_fixed_CN.bnp")
OPTION = "zUnL3Patch_LITE"

META = {
    "name": "非官方 林可儿 3.0 补丁・精简版（手臂修正）",
    "version": "1.0.0",
    "desc": (
        "从「少女动作包・脚步声修正版」的最后一组「可选补丁」中单独提取。\n\n"
        "修正原版林可儿手臂偏长的问题，包含玩家模型（Model/Link.sbfres）、"
        "默认防具模型（Model/Armor_Default.sbfres）与玩家 actor 包"
        "（Actor/Pack/GameROMPlayer.sbactorpack）三项。\n\n"
        "用途：在**不启用少女动作包**（原版林可儿）时单独安装。\n"
        "注意：与少女动作包内含的同名补丁互斥，两者不要同时启用。"
    ),
    "url": "https://gamebanana.com/mods/505453",
    "showCompare": False,
    "showConvert": False,
    "depends": [],
}

DEFAULT_OUT_DIR = (Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "Mod文件"
                   / "纯净版" / "林可儿手臂修正")
OUT_BNP_NAME = "林可儿手臂修正（非官方3.0精简补丁）.bnp"


def main() -> int:
    out_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_OUT_DIR
    out_bnp = out_dir / OUT_BNP_NAME

    print("=" * 74)
    print("① 打开源包，取出补丁选项")
    print("=" * 74)
    if not SRC_BNP.is_file():
        print(f"✗ 找不到源包：{SRC_BNP}")
        return 2
    print(f"  源包：{SRC_BNP.name}（{SRC_BNP.stat().st_size/1048576:.0f} MB）")

    tmp_src = install.open_mod(SRC_BNP)
    opt_dir = tmp_src / "options" / OPTION
    if not opt_dir.is_dir():
        cands = [p for p in tmp_src.rglob(OPTION) if p.is_dir()]
        if not cands:
            print(f"✗ 源包里找不到选项目录 {OPTION}")
            return 2
        opt_dir = cands[0]
    print(f"  选项目录：{opt_dir.relative_to(tmp_src)}")
    files = [f for f in opt_dir.rglob("*") if f.is_file()]
    print(f"  文件 {len(files)} 个：")
    for f in files:
        print(f"     {f.relative_to(opt_dir)}  ({f.stat().st_size/1048576:.2f} MB)")

    print()
    print("=" * 74)
    print("② 组装独立模组目录")
    print("=" * 74)
    work = Path(tempfile.mkdtemp(prefix="lkle-arm-"))
    copied = 0
    for f in files:
        rel = f.relative_to(opt_dir)
        parts = rel.parts
        if parts and parts[0] == "logs":
            # BCML 的合并日志：create_bnp_mod 会重新生成，别抄旧的
            continue
        if "~~" in parts[0]:
            # 「选项选择标记」文件（那串 "N) XXX ~~ Y) ZZZ"），独立模组不需要
            continue
        out = work / rel
        out.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(f, out)
        copied += 1
        print(f"  + {rel}")
    print(f"  共放入 {copied} 个文件")

    if not (work / U.get_content_path()).is_dir():
        print(f"✗ 组装后的目录缺少 {U.get_content_path()}，无法打包")
        shutil.rmtree(work, ignore_errors=True)
        return 2

    print()
    print("=" * 74)
    print("③ 调用 BCML 的 create_bnp_mod 生成 BNP")
    print("=" * 74)
    out_dir.mkdir(parents=True, exist_ok=True)
    dev.create_bnp_mod(mod=work, output=out_bnp, meta=dict(META))

    print()
    print("=" * 74)
    print("④ 校验产物")
    print("=" * 74)
    if not out_bnp.is_file():
        print("  ✗ 未生成")
        shutil.rmtree(work, ignore_errors=True)
        shutil.rmtree(tmp_src, ignore_errors=True)
        return 1
    print(f"  输出：{out_bnp}")
    print(f"  大小：{out_bnp.stat().st_size/1048576:.2f} MB")
    meta = install.extract_mod_meta(out_bnp)
    for k in ("name", "version", "id", "platform"):
        print(f"    {k} = {meta.get(k)!r}")
    print(f"    含 options 键 = {'options' in meta}（独立补丁应为 False）")

    shutil.rmtree(work, ignore_errors=True)
    shutil.rmtree(tmp_src, ignore_errors=True)
    print("\n完成。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
