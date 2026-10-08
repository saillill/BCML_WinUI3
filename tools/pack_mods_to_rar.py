# -*- coding: utf-8 -*-
"""把 MOD整合包 下每个叶目录的 mod 数据打成 rar，并把配置档案单独抽出来。

流程（每个叶目录独立完成，先校验后删除）：
  ① 对 01007EF00011E000 / 01007EF00011F001 各打一个同名 rar（标准压缩 -m3）
  ② 用 `rar t` 做完整性校验（CRC），并核对 rar 内的原始总大小 == 目录总大小
  ③ 只有 ② 通过才删除原目录
  ④ 把 配置.json 移到 C:\\Users\\z2637\\Downloads\\塞尔达传说 旷野之息\\ 下

用法：
    python pack_mods_to_rar.py            # 正式执行
    python pack_mods_to_rar.py --dry-run  # 只列计划
"""
from __future__ import annotations

import io
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

RAR = Path(r"C:\Program Files\WinRAR\Rar.exe")

MOD_ROOT = Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "MOD整合包"
CONFIG_OUT = Path.home() / "Downloads" / "塞尔达传说 旷野之息"

APPIDS = ("01007EF00011E000", "01007EF00011F001")
VERS = ("1.6.0", "1.8.2", "1.9.0")
PKGS = ("纯净包", "增强包")
UMBS = ("默认滑翔翼", "伞形滑翔翼")
ANIMS = ("有少女动作", "无少女动作")

DRY = "--dry-run" in sys.argv


def dir_stats(d: Path) -> tuple[int, int]:
    """源目录的 (文件数, 总字节)。"""
    files = [f for f in d.rglob("*") if f.is_file()]
    return len(files), sum(f.stat().st_size for f in files)


def rar_stats(r: Path) -> tuple[int, int]:
    """rar 内的 (文件数, 未压缩总字节)。

    取 `Rar.exe lt` 的逐条目清单解析。
    注意两点：
      · WinRAR 控制台输出用系统代码页（本机 GBK），须按 gbk 解码；
      · `lt` **没有合计行**，只能逐条目累加；「大小」行不能与
        「压缩后大小」混淆，故用 `^大小:` 锚定行首。
    另外不要给它加 `-idq` —— 那会把清单本身也静默掉，什么都读不到。
    """
    out = subprocess.run([str(RAR), "lt", str(r)], capture_output=True)
    text = out.stdout.decode("gbk", errors="replace")
    n = 0
    total = 0
    is_file = False
    for line in text.splitlines():
        s = line.strip()
        m = re.match(r"^类型:\s*(\S+)", s)
        if m:
            is_file = m.group(1) == "文件"
            continue
        m = re.match(r"^大小:\s*(\d+)", s)
        if m and is_file:
            total += int(m.group(1))
            n += 1
    return n, total


def main() -> int:
    if not RAR.is_file():
        print(f"✗ 找不到 Rar.exe：{RAR}")
        return 2

    leaves = [(v, p, u, a) for v in VERS for p in PKGS for u in UMBS for a in ANIMS]
    print(f"叶目录 {len(leaves)} 个    压缩：-m3（标准）")
    print(f"配置档案输出目录：{CONFIG_OUT}")
    if DRY:
        for v, p, u, a in leaves:
            d = MOD_ROOT / v / p / u / a
            print(f"  [{v}] {p}/{u}/{a}")
        print("\ndry-run 结束。")
        return 0

    CONFIG_OUT.mkdir(parents=True, exist_ok=True)
    manifest: list[str] = []
    t0 = time.perf_counter()
    n_rar = 0
    n_cfg = 0
    failed: list[str] = []

    for v, p, u, a in leaves:
        leaf = MOD_ROOT / v / p / u / a
        tag = f"{v} {p}/{u}/{a}"
        print(f"\n── {tag}")
        if not leaf.is_dir():
            print("   ✗ 叶目录不存在，跳过")
            failed.append(tag)
            continue

        for appid in APPIDS:
            src = leaf / appid
            if not src.is_dir():
                continue
            rar_path = leaf / f"{appid}.rar"
            if rar_path.exists():
                rar_path.unlink()

            before_n, before = dir_stats(src)
            proc = subprocess.run(
                [str(RAR), "a", "-m3", "-r", "-idq", "-y", str(rar_path), appid],
                cwd=str(leaf), capture_output=True, text=True,
                encoding="utf-8", errors="replace",
            )
            if proc.returncode != 0 or not rar_path.is_file():
                print(f"   ✗ {appid}.rar 创建失败（code={proc.returncode}）")
                print(f"     {(proc.stderr or proc.stdout)[:300]}")
                failed.append(f"{tag} / {appid}")
                continue

            # ② 校验：CRC 测试 + 文件数与未压缩总字节都与源目录一致
            test = subprocess.run(
                [str(RAR), "t", "-idq", str(rar_path)],
                capture_output=True, text=True, encoding="utf-8", errors="replace",
            )
            inside_n, inside = rar_stats(rar_path)
            ok = (test.returncode == 0
                  and inside_n == before_n and inside == before)
            if not ok:
                print(f"   ✗ {appid}.rar 校验未通过"
                      f"（rar_t={test.returncode}；"
                      f"源 {before_n} 个/{before} 字节 vs "
                      f"包内 {inside_n} 个/{inside} 字节）")
                failed.append(f"{tag} / {appid}")
                continue

            # ③ 校验通过才删除原目录
            shutil.rmtree(src)
            n_rar += 1
            ratio = rar_path.stat().st_size / before * 100 if before else 0
            print(f"   ✓ {appid}.rar  {before_n} 文件  {before/2**20:.1f} MB → "
                  f"{rar_path.stat().st_size/2**20:.1f} MB（{ratio:.0f}%），原目录已删除")
            manifest.append(
                f"{tag}\t{appid}.rar\t{before}\t{rar_path.stat().st_size}"
            )

        # ④ 抽出配置档案
        cfg = leaf / "配置.json"
        if cfg.is_file():
            out = CONFIG_OUT / f"{v}_{p}_{u}_{a}.json"
            shutil.move(str(cfg), str(out))
            n_cfg += 1
            print(f"   ✓ 配置 → {out.name}")

    dt = time.perf_counter() - t0
    print("\n" + "=" * 74)
    print(f"生成 rar {n_rar} 个，抽出配置 {n_cfg} 个，耗时 {dt/60:.1f} 分钟")
    if failed:
        print(f"失败 {len(failed)} 项：")
        for f in failed:
            print(f"   {f}")
    mf = CONFIG_OUT / "_打包清单.txt"
    mf.write_text(
        "叶目录\trar 文件\t原始字节\t压缩后字节\n" + "\n".join(manifest) + "\n",
        encoding="utf-8",
    )
    print(f"清单已写出：{mf}")
    return 0 if not failed else 1


if __name__ == "__main__":
    raise SystemExit(main())
