# -*- coding: utf-8 -*-
"""验证产出的独立补丁 BNP。

三件事：
  A. 结构检查：解包后可被 BCML 识别（info.json / 平台 / 内容路径）
  B. 内容等价：包内 TitleBG.pack 与「源包里的 zUnL3Patch_LITE 选项」**逐字节相同**
     —— 只要相同，装上它的效果就与在少女动作包里勾选该选项完全一致
  C. 真实安装：走 BCML 的 install_mod 装一次（不触发合并），确认能装、
     条目落位正确，然后 uninstall（也跳过合并）复原
"""
from __future__ import annotations

import hashlib
import io
import shutil
import sys
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

ROOT = Path(r"C:\Users\z2637\WorkBuddy\2026-10-02-14-24-10\BCML-WinUI3")
sys.path.insert(0, str(ROOT / "publish" / "win-x64" / "runtime" / "python39" / "Lib" / "site-packages"))
sys.path.insert(0, str(ROOT / "sidecar"))

import rpc_server  # noqa: E402,F401  —— 装串行池，避免 spawn 进程风暴

import oead  # noqa: E402
from bcml import install, util as U  # noqa: E402

BNP = (Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "Mod文件"
       / "纯净版" / "林可儿手臂修正" / "林可儿手臂修正（非官方3.0精简补丁）.bnp")
SRC_BNP = (Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "Mod文件"
           / "纯净版" / "少女动作"
           / "girly_animation_pack_10_6_footstepsound_fixed_CN.bnp")
OPTION = "zUnL3Patch_LITE"
PACKREL = "01007EF00011E000/romfs/Pack/TitleBG.pack"
EXPECT = [
    "Model/Link.sbfres",
    "Model/Armor_Default.sbfres",
    "Actor/Pack/GameROMPlayer.sbactorpack",
]


def sha(p: Path):
    h = hashlib.sha1()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def ents(p: Path):
    d = p.read_bytes()
    if d[:4] == b"Yaz0":
        d = oead.yaz0.decompress(d)
    return {str(f.name): hashlib.sha1(bytes(f.data)).hexdigest()
            for f in oead.Sarc(d).get_files()}


def main() -> int:
    print("=" * 76)
    print("A. 结构检查")
    print("=" * 76)
    if not BNP.is_file():
        print(f"✗ 找不到 {BNP}")
        return 2
    print(f"  文件：{BNP.name}  {BNP.stat().st_size/1048576:.2f} MB")
    meta = install.extract_mod_meta(BNP)
    print(f"  BCML 能读出 info.json：{bool(meta)}")
    for k in ("name", "version", "platform", "desc"):
        v = meta.get(k)
        print(f"    {k} = {str(v)[:60]!r}")
    print(f"    有 options 键：{'options' in meta}（应为 False）")

    mod_dir = install.open_mod(BNP)
    print(f"  open_mod 解包成功 → {mod_dir}")
    files = sorted(str(f.relative_to(mod_dir)).replace("\\", "/")
                   for f in mod_dir.rglob("*") if f.is_file())
    print(f"  包内文件 {len(files)} 个：")
    for f in files:
        print(f"     {f}")
    if not (mod_dir / U.get_content_path()).is_dir():
        print(f"  ✗ 缺少内容路径 {U.get_content_path()}")
        return 1
    print(f"  内容路径存在：{U.get_content_path()} ✓")

    print()
    print("=" * 76)
    print("B. 内容等价：与源包里的同名选项逐字节比对")
    print("=" * 76)
    src_dir = install.open_mod(SRC_BNP)
    h_new = sha(mod_dir / PACKREL)
    h_src = sha(src_dir / "options" / OPTION / PACKREL)
    print(f"  新 bnp 的 TitleBG.pack : {h_new}")
    print(f"  源选项的 TitleBG.pack : {h_src}")
    print(f"  {'✓ 逐字节相同 → 效果与勾选该选项完全一致' if h_new == h_src else '✗ 不同！'}")

    e_new = ents(mod_dir / PACKREL)
    e_src = ents(src_dir / "options" / OPTION / PACKREL)
    print(f"\n  包内条目 {len(e_new)} 个（源选项 {len(e_src)} 个）：")
    for k in sorted(e_new):
        same = e_new[k] == e_src.get(k)
        print(f"     {k:<46} {e_new[k][:12]}  {'== 源' if same else '≠ 源'}")
    if set(e_new) != set(EXPECT):
        print(f"  ✗ 条目集合与预期不符，预期 {EXPECT}")
    else:
        print("  ✓ 条目集合正是预期的 3 项（玩家模型 / 默认防具模型 / 玩家 actor 包）")

    print()
    print("=" * 76)
    print("C. 真实安装（不触发合并）")
    print("=" * 76)
    before = sorted(p.name for p in U.get_modpack_dir().iterdir() if p.is_dir())
    print(f"  装前模组数：{len(before)}")
    prio = install.get_next_priority()
    print(f"  分配优先级：{prio}")
    install.install_mod(BNP, options={"disable": [], "options": {}},
                        insert_priority=prio, merge_now=False)

    after = sorted(p.name for p in U.get_modpack_dir().iterdir() if p.is_dir())
    new_dirs = [d for d in after if d not in before]
    print(f"  装后模组数：{len(after)}   新增：{new_dirs}")
    if len(new_dirs) != 1:
        print("  ✗ 新增模组数不为 1")
        return 1
    nd = U.get_modpack_dir() / new_dirs[0]
    body = nd / PACKREL
    e_body = ents(body) if body.is_file() else {}
    print(f"  新模组本体 TitleBG.pack 条目 {len(e_body)} 个：")
    ok_body = True
    for k in EXPECT:
        hit = k in e_body
        ok_body &= hit
        print(f"     {k:<46} {'✓' if hit else '✗ 缺失'}")
    print(f"  {'✓ 三项齐全' if ok_body else '✗ 条目不全'}")

    # 复原：uninstall 且不触发合并
    install.uninstall_mod(U.BcmlMod(nd), wait_merge=True)
    final = sorted(p.name for p in U.get_modpack_dir().iterdir() if p.is_dir())
    gone = new_dirs[0] not in final
    print(f"\n  卸载后模组数：{len(final)}   {'✓ 已复原' if gone and len(final) == len(before) else '✗ 未复原'}")

    shutil.rmtree(mod_dir, ignore_errors=True)
    shutil.rmtree(src_dir, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
