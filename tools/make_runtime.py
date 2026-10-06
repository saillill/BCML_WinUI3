#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""组装「内嵌 Python 3.9 运行时」，让发布包免装 BCML 也能跑（完全绿色版）。

产物（默认写进 publish/win-x64/runtime/python39/）：

    python.exe / python39.dll / python39.zip   ← 官方 embeddable 发行版（3.9.13）
    python39._pth                              ← 路径配置：加入 Lib\\site-packages
    Lib/site-packages/                         ← 从本机 Python 3.9 里挑出的 BCML 依赖子集
    runtime-manifest.json                      ← 每个文件的 SHA-256，供应用启动时校验完整性

为什么要这么挑依赖：
    BCML 完整安装里有 410MB，但其中 cefpython3（162MB）只被 bcml/__main__.py 的
    桌面 GUI 入口用到，assets/node_modules（71MB）是 CEF 前端资源 —— 无界面 sidecar
    一个都不需要。真正的依赖闭包只有 ~60MB（见 REQUIRED_PACKAGES）。

    依赖清单是这么定出来的（两个方向互相印证，不是拍脑袋列）：
      1. 构建期：单独用 AST 扫过 bcml/botw/rstb/webview 全部 .py 的 import（含延迟导入），
         把标准库剔除，得到候选集 —— 这个扫描是一次性分析，动作在本脚本之外，
         结论就是下面的 REQUIRED_PACKAGES；
      2. 运行期：--verify 用组装好的运行时真跑一次 sidecar，真调用 sys.info / api.get_mods /
         sys.modDetails —— 缺哪个包会立刻炸出来。
    所以升级 BCML 之后要重跑 --verify，光看清单不够。

用法：
    python tools/make_runtime.py                     # 组装到 publish/win-x64
    python tools/make_runtime.py --verify            # 组装后再实跑一次自检
    python tools/make_runtime.py --site-packages <p> # 手动指定源 site-packages
    python tools/make_runtime.py --clean             # 强制重建（默认按源包 mtime 判断可否复用）
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import shutil
import subprocess
import time
import sys
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent

PY_VERSION = "3.9.13"
EMBED_NAME = f"python-{PY_VERSION}-embed-amd64.zip"
EMBED_URL = f"https://www.python.org/ftp/python/{PY_VERSION}/{EMBED_NAME}"

DEFAULT_TARGET = ROOT / "publish" / "win-x64"
CACHE_DIR = HERE / "cache"
RUNTIME_SUBPATH = Path("runtime") / "python39"

MANIFEST_NAME = "runtime-manifest.json"

# ---------------------------------------------------------------------------
# 依赖清单：site-packages 下需要整目录拷贝的顶层包
# ---------------------------------------------------------------------------
REQUIRED_PACKAGES = [
    # BCML 本体与它直接依赖的三方库
    "bcml",              # 主包（含 helpers/7z.exe，打包 BNP 要用，不能删）
    "botw",              # bcml/mergers/rstable.py 顶层 import botw.rstb
    "rstb",              # 5 个 merger 在模块级 import rstb
    "xxhash",            # 哈希缓存
    # oead 是单个 .pyd（不是目录），由下面的 REQUIRED_FILES 带入
    # BCML 在模块级 import webview（bcml/util.py、bcml/_api.py），即使不开窗也要能导入
    "webview",
    "proxy_tools",       # webview 的探测依赖
    "pythonnet",         # webview 的 winforms 后端需要 clr
    "clr_loader",
    # requests 及其依赖
    "requests",
    "urllib3",
    "certifi",
    "idna",
    "charset_normalizer",
    # 其它
    "packaging",
    "cffi",
    "pycparser",
    "pkg_resources",
    "setuptools",
]

# site-packages 根目录下的散装文件（顶层扩展模块）
REQUIRED_FILES = [
    "oead.cp39-win_amd64.pyd",
    "_cffi_backend.cp39-win_amd64.pyd",
    "clr.py",
]

# 明确不带的（体积大且只服务桌面 GUI）
EXCLUDED_TOP = ["cefpython3", "PyQt5", "pip"]

# 拷贝时逐层跳过的目录名
SKIP_DIR_NAMES = {"__pycache__", "node_modules"}
# 拷贝时跳过的文件后缀
SKIP_SUFFIXES = {".pyc", ".pyo"}
# 拷贝时跳过的文件名（BCML 仓库里的开发残留）
SKIP_FILE_NAMES = {"logo-smaller.png"}


def find_source_site_packages(explicit: str | None) -> Path:
    """定位源 site-packages：优先命令行，其次自动探测本机的 Python 3.9。"""
    if explicit:
        p = Path(explicit)
        if not p.is_dir():
            raise SystemExit(f"--site-packages 不是目录：{p}")
        return p

    local = Path(os.environ.get("LOCALAPPDATA", ""))
    candidates = [
        local / "Programs" / "Python" / "Python39" / "Lib" / "site-packages",
        local / "Programs" / "Python" / "Python39-32" / "Lib" / "site-packages",
        Path(r"C:\Python39\Lib\site-packages"),
        Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Python39" / "Lib" / "site-packages",
    ]
    for c in candidates:
        if (c / "bcml").is_dir():
            return c
    raise SystemExit(
        "找不到源 site-packages（需要一份已装 bcml 的 Python 3.9）。\n"
        "请用 --site-packages 手动指定。"
    )


def ensure_embeddable(cache: Path) -> Path:
    """下载并缓存官方 embeddable 发行版。"""
    cache.mkdir(parents=True, exist_ok=True)
    zip_path = cache / EMBED_NAME
    if zip_path.exists() and zip_path.stat().st_size > 1_000_000:
        print(f"  使用缓存：{zip_path}")
        return zip_path

    print(f"  下载 {EMBED_URL}")
    tmp = zip_path.with_suffix(".part")
    with urllib.request.urlopen(EMBED_URL, timeout=120) as resp, open(tmp, "wb") as out:
        total = int(resp.headers.get("Content-Length") or 0)
        got = 0
        while True:
            chunk = resp.read(256 * 1024)
            if not chunk:
                break
            out.write(chunk)
            got += len(chunk)
            if total:
                pct = got * 100 // total
                print(f"\r    {got/1048576:5.1f} / {total/1048576:.1f} MB  ({pct}%)", end="")
    print()
    tmp.replace(zip_path)
    return zip_path


def _rmtree_robust(path: Path) -> None:
    """删目录，但能扛住 Windows 上两种常见的失败。

      1. 文件被占用（杀毒软件正在扫描刚写进去的 .pyd / .exe）—— 等一下重试；
      2. 文件带只读属性 —— `shutil.rmtree` 在 Windows 上删不掉，得先清属性。

    用 `rmtree(ignore_errors=True)` 的话会**静默失败**：目录看起来清掉了，
    其实旧文件还在，后面的拷贝再撞上只读文件报 PermissionError，
    错误信息完全指不到真正的根因。
    """
    if not path.exists():
        return

    def _on_error(_func, file_path, _exc):
        try:
            os.chmod(file_path, 0o666)
        except OSError:
            pass
        try:
            os.remove(file_path)
        except OSError:
            pass

    last = None
    for attempt in range(4):
        try:
            shutil.rmtree(path, onerror=_on_error)
            if not path.exists():
                return
        except OSError as exc:
            last = exc
        time.sleep(0.4 * (attempt + 1))

    if last is not None:
        print(f"    !! 删除 {path} 失败：{last}（旧文件若残留，拷贝可能会失败）")


def _already_identical(src: Path, dst: Path) -> bool:
    """目标文件已经和源一致（大小 + 修改时间都在容差内）就可以跳过拷贝。

    这条不只是提速：Windows 上重复构建时，`.pyd` 这类被加载过的文件经常
    **既删不掉也覆盖不了**（WinError 32），内容又确实没变 —— 硬要重写只会让
    整个构建失败。先判一次“已经一样”，这种卡死就绕过去了。
    """
    try:
        a, b = src.stat(), dst.stat()
    except OSError:
        return False
    if a.st_size != b.st_size:
        return False
    # 文件系统 / zip 的时间精度只有 1~2 秒，给 2 秒容差
    return abs(a.st_mtime - b.st_mtime) <= 2


def _copy2_robust(src: Path, dst: Path) -> None:
    """copy2 的加固版：先确保目标可写且不残留，再拷，失败重试。

    直接在 Windows 上重复构建时会撞上：
      · 目标文件带只读属性（copy2 会把源的 mode 一起复制过来）→ 覆盖失败；
      · 目标刚被写入、杀软正在扫 → 短暂占用；
      · 目标被某个进程映射着（WinError 32）→ 只能靠“内容本来就一样”绕过去。
    """
    if dst.exists() and _already_identical(src, dst):
        return

    last = None
    for attempt in range(4):
        try:
            if dst.exists():
                try:
                    os.chmod(dst, 0o666)
                except OSError:
                    pass
                dst.unlink()
            shutil.copy2(src, dst)
            return
        except (PermissionError, OSError) as exc:
            last = exc
            time.sleep(0.4 * (attempt + 1))
    raise RuntimeError(f"拷贝失败：{src} -> {dst}（{last}）")


def copy_tree(src: Path, dst: Path) -> tuple[int, int]:
    """按跳过规则拷贝目录，返回 (文件数, 字节数)。"""
    n_files = 0
    n_bytes = 0
    for dp, dirnames, filenames in os.walk(src):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIR_NAMES]
        rel = Path(dp).relative_to(src)
        (dst / rel).mkdir(parents=True, exist_ok=True)
        for f in filenames:
            if Path(f).suffix in SKIP_SUFFIXES:
                continue
            if f in SKIP_FILE_NAMES:
                continue
            if ".bak-" in f:          # bcml/util.py.bak-before-encoding-fix 之类的残留
                continue
            s = Path(dp) / f
            try:
                size = s.stat().st_size
            except OSError:
                continue
            _copy2_robust(s, dst / rel / f)
            n_files += 1
            n_bytes += size
    return n_files, n_bytes


def dir_size(path: Path) -> int:
    return sum(p.stat().st_size for p in path.rglob("*") if p.is_file())


MARKER_NAME = ".bcml-runtime.json"


def _marker_payload(site_packages: Path) -> dict:
    """把「这次是用什么装的」记下来，用来判断能不能直接复用。

    这里取的是**包目录内所有文件的最大 mtime**，不是目录本身的 mtime：
    修包里的某个 .py（例如给 bcml/util.py 打编码修复）不会改变父目录的 mtime，
    只看目录的话会误判成「没变过」，于是复用到一份陈旧的运行时。
    """
    def stamp(p: Path) -> dict:
        if not p.exists():
            return {}
        newest = 0.0
        newest_name = ""

        def consider(f: Path, rel: str) -> None:
            nonlocal newest, newest_name
            try:
                m = f.stat().st_mtime
            except OSError:
                return
            if m > newest:
                newest = m
                newest_name = rel

        if p.is_file():
            consider(p, p.name)
        else:
            # 用 os.walk 并剪掉 node_modules/__pycache__：这两棵树在 bcml/assets 下
            # 有上万个小文件，全量 rglob 会白白拖慢每次构建判断。
            for dp, dirnames, filenames in os.walk(p):
                dirnames[:] = [d for d in dirnames if d not in SKIP_DIR_NAMES]
                for f in filenames:
                    if Path(f).suffix in SKIP_SUFFIXES:
                        continue
                    full = Path(dp) / f
                    consider(full, str(full.relative_to(p)))
        return {"newest": int(newest), "file": newest_name}

    return {
        "pythonVersion": PY_VERSION,
        "packages": REQUIRED_PACKAGES,
        "files": REQUIRED_FILES,
        "sources": {
            name: stamp(site_packages / name)
            for name in REQUIRED_PACKAGES + REQUIRED_FILES
        },
    }


def is_up_to_date(runtime: Path, site_packages: Path) -> bool:
    """运行时已存在、且源包没变过 —— 就可以跳过重建（重新发布时省几分钟）。"""
    marker = runtime / MARKER_NAME
    if not marker.exists():
        return False
    # 清单也要在。少了它说明是「引入清单之前」建的运行时，必须重建，
    # 否则复用下去永远补不上完整性清单，校验会一直走降级分支。
    if not (runtime / MANIFEST_NAME).exists():
        return False
    try:
        old = json.loads(marker.read_text("utf-8"))
    except Exception:
        return False
    return old == _marker_payload(site_packages)


def write_manifest(runtime: Path) -> dict:
    """给运行时里每个文件算 SHA-256，落一份 runtime-manifest.json。

    应用启动时会拿它对内嵌运行时做完整性校验 —— 没有这层的话，一个被截断 / 被替换的
    `oead.cp39-win_amd64.pyd` 只会表现成「后端起不来」，用户完全无从判断是安装损坏
    还是环境不对。

    刻意**排除** `__pycache__` 与 `.pyc/.pyo`：这些是解释器跑起来之后自己生成的，
    构建期（--verify）会写、打包前又会被清掉，纳入清单会让校验结果随「跑没跑 verify」而变。
    校验侧是「按清单逐项查」，多出来的文件不影响结论。
    """
    entries: dict[str, dict] = {}
    for dp, dirnames, filenames in os.walk(runtime):
        dirnames[:] = [d for d in dirnames if d != "__pycache__"]
        for f in filenames:
            if Path(f).suffix in SKIP_SUFFIXES:
                continue
            full = Path(dp) / f
            rel = full.relative_to(runtime).as_posix()
            if rel == MANIFEST_NAME or rel == MARKER_NAME:
                continue
            digest = hashlib.sha256()
            try:
                with open(full, "rb") as fh:
                    for chunk in iter(lambda: fh.read(1 << 20), b""):
                        digest.update(chunk)
                size = full.stat().st_size
            except OSError as exc:
                print(f"    !! 计算摘要失败 {rel}: {exc}")
                continue
            entries[rel] = {"size": size, "sha256": digest.hexdigest()}

    payload = {
        "pythonVersion": PY_VERSION,
        "generatedAt": datetime.datetime.now().isoformat(timespec="seconds"),
        "fileCount": len(entries),
        "totalBytes": sum(v["size"] for v in entries.values()),
        "files": entries,
    }
    out = runtime / MANIFEST_NAME
    out.write_text(json.dumps(payload, ensure_ascii=False, indent=1, sort_keys=True),
                   encoding="utf-8")
    print(f"  完整性清单：{len(entries)} 个文件 / "
          f"{payload['totalBytes'] / 1048576:.1f} MB → {MANIFEST_NAME}")
    return payload


def build(target: Path, site_packages: Path, clean: bool, force: bool = False) -> Path:
    runtime = target / RUNTIME_SUBPATH
    if not clean and not force and is_up_to_date(runtime, site_packages):
        print(f"  运行时已是最新，跳过重建：{runtime}  （用 --clean 强制重建）")
        return runtime
    if runtime.exists():
        _rmtree_robust(runtime)
    runtime.mkdir(parents=True, exist_ok=True)

    # ---- 1) 解出 embeddable 基础层 ----
    zip_path = ensure_embeddable(CACHE_DIR)
    print(f"  解包 embeddable → {runtime}")
    with zipfile.ZipFile(zip_path) as z:
        z.extractall(runtime)

    # ---- 2) 写 ._pth：把 Lib\site-packages 接进搜索路径 ----
    pth = runtime / "python39._pth"
    pth.write_text(
        "python39.zip\n"
        ".\n"
        "Lib\\site-packages\n"
        "import site\n",
        encoding="ascii",
    )
    print(f"  写入 {pth.name}")

    # ---- 3) 拷贝依赖子集 ----
    sp_dst = runtime / "Lib" / "site-packages"
    sp_dst.mkdir(parents=True, exist_ok=True)

    total_files = total_bytes = 0
    print("  拷贝依赖：")
    for name in REQUIRED_PACKAGES:
        src = site_packages / name
        if not src.is_dir():
            print(f"    !! 跳过（源里没有）：{name}")
            continue
        if name in EXCLUDED_TOP:
            continue
        n, b = copy_tree(src, sp_dst / name)
        total_files += n
        total_bytes += b
        print(f"    {name:20s} {n:5d} 文件  {b/1048576:7.1f} MB")

    for fname in REQUIRED_FILES:
        src = site_packages / fname
        if not src.exists():
            print(f"    !! 跳过（源里没有）：{fname}")
            continue
        _copy2_robust(src, sp_dst / fname)
        total_files += 1
        total_bytes += src.stat().st_size
        print(f"    {fname:20s} {1:5d} 文件  {src.stat().st_size/1048576:7.1f} MB")

    # dist-info：importlib.metadata 与部分包的版本自检要用；很小，顺手带上
    for d in sorted(site_packages.glob("*.dist-info")):
        stem = d.name.split("-")[0].lower().replace("_", "-")
        if any(stem == p.lower().replace("_", "-") for p in REQUIRED_PACKAGES):
            n, b = copy_tree(d, sp_dst / d.name)
            total_files += n
            total_bytes += b

    print(f"\n  依赖合计：{total_files} 个文件，{total_bytes/1048576:.1f} MB")

    # 先写复用标记（不参与完整性校验），再算清单。
    # 顺序有讲究：清单要把运行时的**全部**文件覆盖到，若把标记也写进去，
    # 下次重建时标记内容一变，清单就跟着变，比对起来全是噪声。
    (runtime / MARKER_NAME).write_text(
        json.dumps(_marker_payload(site_packages), ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    write_manifest(runtime)
    print(f"  运行时总大小：{dir_size(runtime)/1048576:.1f} MB  →  {runtime}")
    return runtime


def verify(runtime: Path) -> bool:
    """实跑自检：导入 bcml/oead + 起一次 sidecar 做真实调用。"""
    python = runtime / "python.exe"
    script = ROOT / "sidecar" / "rpc_server.py"

    # 关键：关掉 user site-packages。否则本机 %APPDATA%\Python\Python39\site-packages
    # 里的包会漏进来，让「干净机器也能跑」的结论失真。
    env = dict(os.environ)
    env["PYTHONNOUSERSITE"] = "1"
    env["PYTHONUNBUFFERED"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"

    print("\n=== 自检 1/2：解释器能否 import bcml / oead ===")
    r = subprocess.run([str(python), "-c", "import bcml,oead,rstb,botw,webview,xxhash;print('ok')"],
                       capture_output=True, text=True, cwd=str(runtime), env=env)
    print(f"  returncode={r.returncode} stdout={r.stdout.strip()!r}")
    if r.returncode != 0:
        print("  stderr:\n" + (r.stderr or "").strip()[-2000:])
        return False

    print("\n=== 自检 2/2：sidecar 真实调用 ===")
    import json
    import struct
    import threading

    proc = subprocess.Popen(
        [str(python), str(script)],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        cwd=str(script.parent), env=env,
    )

    # stderr 必须持续抽干：sidecar 会把 BCML 的全部 print 转发到 stderr，
    # 管道缓冲（约 64KB）写满后 sidecar 会阻塞在 write 上，从此不再应答 ——
    # 表现为客户端读 stdout 永远等不到帧。C# 侧的 SidecarClient 有专门的
    # _stderrLoop 做这件事，这里同样不能省。
    stderr_buf: list[str] = []
    stderr_lock = threading.Lock()

    def drain():
        for raw in iter(proc.stderr.readline, b""):
            line = raw.decode("utf-8", "replace").rstrip()
            if not line:
                continue
            with stderr_lock:
                stderr_buf.append(line)
                del stderr_buf[:-400]

    drainer = threading.Thread(target=drain, daemon=True)
    drainer.start()

    def call(method, params=None):
        body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method,
                           "params": params}).encode("utf-8")
        proc.stdin.write(struct.pack("<I", len(body)) + body)
        proc.stdin.flush()
        while True:
            hdr = proc.stdout.read(4)
            if len(hdr) < 4:
                raise RuntimeError("sidecar 提前退出")
            (n,) = struct.unpack("<I", hdr)
            msg = json.loads(proc.stdout.read(n).decode("utf-8"))
            if msg.get("id") == 1:
                return msg

    try:
        info = call("sys.info")
        res = (info or {}).get("result", {})
        print(f"  BCML 版本 = {res.get('bcmlVersion')}  python = {res.get('python')}")
        print(f"  bcmlOk    = {res.get('bcmlOk')}  bcmlError = {res.get('bcmlError')}")
        print(f"  executable= {res.get('executable')}")
        if not res.get("bcmlOk"):
            print("  !! BCML 导入失败")
            return False
        mods = call("api.get_mods", {"disabled": True})
        data = (mods or {}).get("result", {})
        if isinstance(data, dict) and "data" in data:
            data = data["data"]
        n = len(data) if isinstance(data, list) else "?"
        print(f"  get_mods  = {n} 个模组")

        # 详情页那条新链路也顺手验一下（第一次调用会真走一遍文件遍历）
        first = data[0]["path"] if isinstance(data, list) and data else None
        if first:
            det = call("sys.modDetails", {"mod": first, "edits": False})
            d = (det or {}).get("result") or {}
            meta = d.get("meta") or {}
            print(f"  modDetails= {meta.get('name')!r} v{meta.get('version')} "
                  f"platform={meta.get('platform')} 选项={len(meta.get('optionFolders') or [])}")
        return True
    except Exception as exc:                              # noqa: BLE001
        print(f"  !! 自检失败：{exc}")
        with stderr_lock:
            tail = stderr_buf[-25:]
        if tail:
            print("  sidecar 输出尾部：")
            for ln in tail:
                print("    " + ln)
        return False
    finally:
        try:
            proc.stdin.close()
        except Exception:
            pass
        try:
            proc.wait(timeout=10)
        except Exception:
            proc.kill()


def main() -> int:
    ap = argparse.ArgumentParser(description="组装内嵌 Python 3.9 运行时")
    ap.add_argument("--target", default=str(DEFAULT_TARGET),
                    help="发布目录（运行时写到 <target>/runtime/python39）")
    ap.add_argument("--site-packages", default=None, help="源 site-packages 路径")
    ap.add_argument("--clean", action="store_true", help="先删除旧运行时")
    ap.add_argument("--verify", action="store_true", help="组装后实跑自检")
    args = ap.parse_args()

    target = Path(args.target)
    if not target.exists():
        raise SystemExit(f"目标目录不存在：{target}（先跑 dotnet publish）")

    print(f"内嵌 Python {PY_VERSION} 运行时")
    site_packages = find_source_site_packages(args.site_packages)
    print(f"  源 site-packages：{site_packages}")

    runtime = build(target, site_packages, args.clean)

    if args.verify:
        return 0 if verify(runtime) else 1
    print("\n提示：加 --verify 可实跑自检。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
