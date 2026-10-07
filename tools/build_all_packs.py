# -*- coding: utf-8 -*-
"""批量打包 24 个整合包：逐轮切配置 → 生效 → 合并 → 归档产物。

━━━ 关键：必须用同进程调用，不要走 RPC 子进程 ━━━

第一版实现是「启动 sidecar 子进程 + stdin/stdout 帧通信」，结果单轮合并
**20 分钟都跑不完**，而 App 里只要 **10 秒**。根因：

    合并过程中 BCML 会产生海量进度输出，sidecar 会为每条输出发一个
    `notify.log` 帧。我的同步读循环虽然只认 `id` 匹配的响应、把通知全丢弃，
    但**每一条都得先从管道里读出来再解码**；合并的日志量大到把 stdout
    管道缓冲区塞满，于是 sidecar 写阻塞、我也在等响应 —— 双向死锁。

    App 用的是异步读循环（持续消费通知），所以不会堵。

    正确姿势就是 `tools/time_remerge.py` 的做法：**`import rpc_server` 后
    直接调 `rpc_server._call_api(method, params)`**，全程同一个进程，没有管道。
    实测 10.68 秒一轮。

━━━ 每轮的四步 ━━━

    ① applyProfile    —— 设排序 / 启用禁用 / 写 options.json
    ② applyModOptions —— 对有选项的模组重选，真正让选项生效
                         （重建 options/ + 撤销旧注入 + 重算 logs/）
    ③ remerge         —— 生成 merged_nx 产物
    ④ 拷贝 merged_nx 到配置对应的叶目录

━━━ 为什么必须靠快照 ━━━

`remerge` 之后 BCML 会删掉未选中的 `options/*` 目录。下一轮开始时那些变体
已经不在磁盘上，直接 applyModOptions 会报「无法从快照恢复」。
本项目快照（`pristine/options/`）保存全量变体，正好补上这一环：
实测少女动作包 75 个、林可儿 3 个，足够反复切。

用法：
    python tools/build_all_packs.py --dry-run        # 只列计划
    python tools/build_all_packs.py --only 1.9.0     # 只跑某版本（8 轮）
    python tools/build_all_packs.py                  # 全跑 24 轮
    python tools/build_all_packs.py --no-archive     # 只合并不拷产物（测速）
"""
from __future__ import annotations

import json
import os
import shutil
import sys
import time
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent

GAME_VERSIONS = ["1.6.0", "1.8.2", "1.9.0"]

# 三个游戏本体版本的解包目录（BCML 的 game_dir_nx / dlc_dir_nx 指向 romfs）
BCML_DATA = Path(os.environ["LOCALAPPDATA"]) / "bcml"
GAME_DIRS = {
    "1.6.0": {
        "game_dir_nx": Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "解包"
        / "1.6.0" / "1.6.0" / "01007EF00011E000" / "romfs",
        "dlc_dir_nx": Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "解包"
        / "1.6.0" / "1.6.0" / "01007EF00011F001" / "romfs",
    },
    "1.8.2": {
        "game_dir_nx": Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "解包"
        / "1.8.2" / "The Legend of Zelda Breath of the Wild 1.8.2"
        / "APP+UPD" / "romfs",
        "dlc_dir_nx": Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "解包"
        / "1.8.2" / "The Legend of Zelda Breath of the Wild 1.8.2"
        / "DLC" / "romfs",
    },
    "1.9.0": {
        "game_dir_nx": Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "解包"
        / "1.9.0" / "1.9.0" / "01007EF00011E000" / "romfs",
        "dlc_dir_nx": Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "解包"
        / "1.9.0" / "1.9.0" / "01007EF00011F001" / "romfs",
    },
}


def _switch_game_version(
    ver: str, game_override: str | None = None, dlc_override: str | None = None
) -> None:
    """把 BCML 的游戏目录切到指定版本 —— **必须在 import bcml 之前调用**。

    为什么必须在 import 前：BCML 的 `util.get_settings` 把 settings.json
    读进函数属性缓存（`if not hasattr(get_settings, "settings")`），
    一个进程内**只读一次**。所以想换版本只能换进程，
    本脚本因此也只在启动最初改写一次配置。

    `game_override` / `dlc_override` 用于**指向任意游戏目录**（命令行
    `--game-dir` / `--dlc-dir`）。当整合包要装到的那份游戏不是
    `GAME_DIRS` 里记录的那几次 dump 时，就用它 —— 不同来源/区域的 dump
    即使版本号相同，`Bootup*.pack` 与资源表也可能不同，
    用错就会卡加载或直接报错。

    BCML 自己判断「换了版本」的方式是重跑合并 —— `install.refresh_merges()`
    会先 `rmtree` 掉 `mods_nx/9999_BCML`（那个主模组就是游戏本体的映射），
    再用当前 `game_dir_nx` 重新生成一遍。
    """
    import json  # 局部导入：本函数在模块顶部被调用，避免污染全局命名空间

    spec = dict(GAME_DIRS[ver])
    if game_override:
        spec["game_dir_nx"] = Path(game_override)
    if dlc_override:
        spec["dlc_dir_nx"] = Path(dlc_override)
    for key, path in spec.items():
        if not path.is_dir():
            raise SystemExit(
                f"找不到 {ver} 的解包目录（{key}）：\n    {path}\n"
                "请确认该版本已解包，或用 --game-dir / --dlc-dir 指定。"
            )

    BCML_DATA.mkdir(parents=True, exist_ok=True)
    settings_path = BCML_DATA / "settings.json"
    original = settings_path.read_text(encoding="utf-8") if settings_path.is_file() else "{}"
    # 备份一份，供 build_all_versions.py 收尾时还原
    (BCML_DATA / "settings.json.bak-before-pack").write_text(original, encoding="utf-8")
    data = json.loads(original or "{}")
    data.update({k: str(v).replace("\\", "/") for k, v in spec.items()})
    settings_path.write_text(
        json.dumps(data, ensure_ascii=False, indent=4), encoding="utf-8"
    )
    # 清掉 9999_BCML（主模组 = 游戏本体的映射）与 merged_nx（合并输出），
    # 强制走一遍完整重建 —— 对齐 BCML 换游戏版本时的行为。
    #
    # 两个都要清：`refresh_merges()` 只 rmtree 9999_BCML，而合并输出
    # （`%LOCALAPPDATA%/bcml/merged_nx`，由 Rust 扩展写入）不会被它清。
    # 换版本时若留着上一版的输出，就可能把上一版的启动包/资源表
    # 混进这一版的产物里 —— 那类文件版本不对，游戏直接起不来。
    master = BCML_DATA / "mods_nx" / "9999_BCML"
    if master.is_dir():
        shutil.rmtree(master, ignore_errors=True)
    merged = BCML_DATA / "merged_nx"
    if merged.is_dir():
        shutil.rmtree(merged, ignore_errors=True)


def _arg(flag: str):
    return sys.argv[sys.argv.index(flag) + 1] if flag in sys.argv else None


_game_arg = _arg("--game")
_game_dir_arg = _arg("--game-dir")
_dlc_dir_arg = _arg("--dlc-dir")

if _game_arg is None and "--only" in sys.argv:
    _maybe = _arg("--only")
    if _maybe in GAME_DIRS:
        _game_arg = _maybe
if _game_arg is None and (_game_dir_arg or _dlc_dir_arg):
    # 只给了目录没给版本号：默认按 1.9.0 的槽位切，反正路径会被覆盖
    _game_arg = "1.9.0"

if _game_arg:
    _switch_game_version(_game_arg, _game_dir_arg, _dlc_dir_arg)
    if _game_dir_arg:
        print(f"[game] 使用自定义游戏目录：{_game_dir_arg}")

try:
    import bcml.util  # noqa: F401
except ImportError:
    sys.stderr.write(
        "需要随包内嵌运行时：\n"
        f'    "{ROOT / "publish" / "win-x64" / "runtime" / "python39" / "python.exe"}" '
        f"tools/{Path(__file__).name}\n"
    )
    raise SystemExit(2)

sys.path.insert(0, str(ROOT / "sidecar"))
import rpc_server  # noqa: E402

PACK_ROOT = Path.home() / "Downloads" / "塞尔达传说 旷野之息" / "MOD整合包"
MERGED = Path(os.environ["LOCALAPPDATA"]) / "bcml" / "merged_nx"
MODS = Path(os.environ["LOCALAPPDATA"]) / "bcml" / "mods_nx"

# 出错时把完整堆栈写到这里 —— 管道日志会被 notify.log 帧穿插，堆栈容易丢
_TRACE_LOG = Path(sys.argv[0]).resolve().parent.parent / "build_all_packs.error.txt"

# 有选项、每轮需要用 applyModOptions 生效的模组
OPTION_MODS = {
    "0100_林可儿Mod3.0TheLinkleMod",
    "0117_少女动作包脚步声修正版GirlyAnimationPack10.6Fixed",
}


def call(method: str, params=None):
    """同进程调用（与 time_remerge.py 一致，避免管道死锁）。

    ⚠ 必须先查 `_SIMPLE`，再回落到 `_call_api`。

    这两者是**两个不同的命名空间**：
      · `_SIMPLE`    —— 本项目自定义方法的注册表（applyProfile / applyModOptions / …）
      · `_call_api`  —— 只认 BCML `Api` 上的原生方法（remerge / import_mod / …）

    `_call_api` 找不到方法时抛 `NoSuchMethodError(method)`，而这个异常的
    str() **就是方法名本身**。所以早先版本写错（直接 `_call_api`）时，
    24 轮全部报 `✗ 0.0s  applyProfile` —— 看着像 applyProfile 内部出错，
    实际是「压根没找到这个方法」。排查时务必留意这种「异常消息 == 方法名」
    的形状，它是 NoSuchMethodError 的典型特征。
    """
    params = params or {}
    if method in rpc_server._SIMPLE:
        fn = rpc_server._SIMPLE[method]
        return fn(params) if fn else None

    result = rpc_server._call_api(method, params)
    if isinstance(result, dict):
        if result.get("error"):
            raise RuntimeError(f"{method}: {result['error']}")
        if "success" in result:
            return result.get("data")
    return result


def iter_targets(only=None):
    """遍历目标：(版本, 包型, 伞型, 动作, 叶目录, 配置文件)。"""
    for ver in GAME_VERSIONS:
        if only and ver != only:
            continue
        for pkg in ("纯净包", "增强包"):
            for umb in ("默认滑翔翼", "伞形滑翔翼"):
                for anim in ("有少女动作", "无少女动作"):
                    dest = PACK_ROOT / ver / pkg / umb / anim
                    cfg = dest / "配置.json"
                    if cfg.is_file():
                        yield ver, pkg, umb, anim, dest, cfg


def apply_config(cfg_path: Path) -> None:
    """① 导入配置 → ② 对有选项的模组重选，让选项真正生效。"""
    raw = cfg_path.read_text(encoding="utf-8")
    call("applyProfile", {"profileJson": raw})

    doc = json.loads(raw)
    for m in doc["mods"]:
        d = m.get("dir")
        if d not in OPTION_MODS:
            continue
        mod_path = MODS / d
        if not mod_path.is_dir():
            continue
        selects = (m.get("options") or {}).get("selects") or []
        call("applyModOptions", {"mod": str(mod_path), "selects": list(selects)})


def archive_output(dest: Path) -> list:
    """把 merged_nx 的 romfs 内容拷到叶目录（直接放目录，不压缩）。"""
    written = []
    for appid in ("01007EF00011E000", "01007EF00011F001"):
        src = MERGED / appid
        if not src.is_dir():
            continue
        target = dest / appid
        if target.exists():
            shutil.rmtree(target, ignore_errors=True)
        _copytree(src, target)
        written.append(appid)
    return written


def _copytree(src: Path, dst: Path) -> None:
    """尽量硬链接（省空间、快），跨卷退回复制。"""
    dst.mkdir(parents=True, exist_ok=True)
    for root, _dirs, files in os.walk(src):
        rel = Path(root).relative_to(src)
        (dst / rel).mkdir(parents=True, exist_ok=True)
        for f in files:
            s = Path(root) / f
            t = dst / rel / f
            if t.exists():
                t.unlink()
            try:
                os.link(s, t)
            except OSError:
                shutil.copy2(s, t)


def main() -> int:
    dry = "--dry-run" in sys.argv
    no_archive = "--no-archive" in sys.argv
    only = None
    if "--only" in sys.argv:
        only = sys.argv[sys.argv.index("--only") + 1]

    targets = list(iter_targets(only))
    print(f"目标 {len(targets)} 个\n")

    if dry:
        for ver, pkg, umb, anim, dest, _cfg in targets:
            print(f"  [{ver}] {pkg}/{umb}/{anim}")
        print("\ndry-run 结束，未执行合并。")
        return 0

    ok = 0
    fail = []
    t_all = time.perf_counter()
    for i, (ver, pkg, umb, anim, dest, cfg) in enumerate(targets, 1):
        tag = f"[{i}/{len(targets)}] {ver} {pkg}/{umb}/{anim}"
        print(f"{tag} …", flush=True)
        t0 = time.perf_counter()
        try:
            apply_config(cfg)
            call("remerge", {"name": "all"})
            got = [] if no_archive else archive_output(dest)
            dt = time.perf_counter() - t0
            print(f"    ✓ {dt:.1f}s" + (f"  产物 {got}" if got else ""))
            ok += 1
        except Exception as e:  # noqa: BLE001
            dt = time.perf_counter() - t0
            detail = traceback.format_exc()
            print(f"    ✗ {dt:.1f}s  {e}")
            _TRACE_LOG.write_text(
                f"{tag}\n{'-' * 60}\n{detail}\n", encoding="utf-8"
            )
            fail.append((tag, f"{e}  （详细堆栈见 {_TRACE_LOG.name}）"))
            break

    print()
    print(f"完成 {ok}/{len(targets)}   总耗时 {(time.perf_counter()-t_all)/60:.1f} 分钟")
    for tag, err in fail:
        print(f"  失败 {tag}: {err}")
    return 0 if not fail else 1


if __name__ == "__main__":
    raise SystemExit(main())
