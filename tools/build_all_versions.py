# -*- coding: utf-8 -*-
"""按游戏本体版本逐个打包 24 个整合包 —— **每个版本一个独立进程**。

━━━ 为什么必须分进程 ━━━

BCML 的 `util.get_settings` 把 settings.json 读进函数属性缓存
（`if not hasattr(get_settings, "settings")`），**一个进程内只读一次**。
所以「换游戏版本」只能靠换进程 —— 同一个进程里改配置是不生效的。

这也是之前最大的一个错误：BCML 一直指向 1.9.0，于是三个版本的产物
**字节完全相同**（合并了一次复制三份）。而用户早就说过三个版本
「不能通用」—— 在 1.6.0 / 1.8.2 的游戏上跑 1.9.0 的内容，
基础文件对不上，表现就是模型/动作错乱。

━━━ 流程 ━━━

每个版本：
  ① build_all_packs.py 启动时先把 settings.json 指到该版本的
     game_dir_nx / dlc_dir_nx（**在 import bcml 之前**），
     并删掉 mods_nx/9999_BCML 强制走一遍完整重建；
  ② 该进程跑完 8 个配置的合并与归档。

全部跑完后还原 settings.json（备份在 settings.json.bak-before-pack）。

用法：
    python tools/build_all_versions.py            # 三个版本全跑
    python tools/build_all_versions.py --only 1.6.0
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
PY = ROOT / "publish" / "win-x64" / "runtime" / "python39" / "python.exe"
BCML_DATA = Path(os.environ["LOCALAPPDATA"]) / "bcml"
SETTINGS = BCML_DATA / "settings.json"
BACKUP = BCML_DATA / "settings.json.bak-before-pack"

VERSIONS = ["1.6.0", "1.8.2", "1.9.0"]

only = None
if "--only" in sys.argv:
    only = sys.argv[sys.argv.index("--only") + 1]
targets = [v for v in VERSIONS if v == only] if only else list(VERSIONS)

if not PY.is_file():
    raise SystemExit(f"找不到内嵌运行时：{PY}")

# 先把当前 settings.json 备份好，收尾要还原（用户 BCML 原本指向 1.9.0）
original = SETTINGS.read_text(encoding="utf-8") if SETTINGS.is_file() else "{}"
(BCML_DATA / "settings.json.bak-user").write_text(original, encoding="utf-8")
print(f"已备份原设置 → {BCML_DATA / 'settings.json.bak-user'}")
print(f"原 game_dir_nx = {json.loads(original or '{}').get('game_dir_nx')}\n")

logs = BCML_DATA / "pack-logs"
logs.mkdir(parents=True, exist_ok=True)

# 可只打某个包型（透传给 build_all_packs.py）
pkg_only = sys.argv[sys.argv.index("--pkg") + 1] if "--pkg" in sys.argv else None

t_all = time.perf_counter()
results = []
try:
    for ver in targets:
        print("=" * 74)
        print(f"开始打包游戏版本 {ver}（独立进程）"
              + (f"  仅 {pkg_only}" if pkg_only else ""))
        print("=" * 74, flush=True)
        log = logs / f"build-{ver}.log"
        t0 = time.perf_counter()
        cmd = [str(PY), str(HERE / "build_all_packs.py"), "--game", ver, "--only", ver]
        if pkg_only:
            cmd += ["--pkg", pkg_only]
        with log.open("w", encoding="utf-8", errors="replace") as fh:
            # 重定向到**文件**（不是管道）—— 管道不排空会死锁，文件不会
            proc = subprocess.run(cmd, stdout=fh, stderr=subprocess.STDOUT, cwd=str(ROOT))
        dt = time.perf_counter() - t0
        ok = proc.returncode == 0
        results.append((ver, ok, dt, log))
        print(f"  {'✓' if ok else '✗'} {ver}  {dt/60:.1f} 分钟  日志 {log.name}\n",
              flush=True)
finally:
    # 无论成败都还原用户设置
    SETTINGS.write_text(original, encoding="utf-8")
    print(f"已还原原设置（game_dir_nx = "
          f"{json.loads(original or '{}').get('game_dir_nx')}）")

print("=" * 74)
print(f"总计 {(time.perf_counter()-t_all)/60:.1f} 分钟")
for ver, ok, dt, log in results:
    print(f"  {'✓' if ok else '✗'} {ver}  {dt/60:.1f} 分钟")
