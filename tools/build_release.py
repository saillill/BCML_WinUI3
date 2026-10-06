#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建 BCML-WinUI3 的发布包（.NET + Windows App SDK + 内嵌 Python 3.9 全部自包含）。

产物：
  publish/win-x64/                          发布目录（BCML-WinUI3.exe + sidecar/ + runtime/python39/）
  artifacts/BCML-WinUI3-<版本>-win-x64.zip   便携包（解压即用，免装 BCML）

前置：
  · .NET SDK
  · 一份已装 bcml 的 Python 3.9（只用于「取材」依赖，运行时不再依赖它）——
    组装脚本会从它的 site-packages 里挑出 BCML 需要的子集打进包里。

用法：
  python tools/build_release.py              # 构建 + 组装运行时 + 打包
  python tools/build_release.py --no-zip     # 只 publish + 组装运行时
  python tools/build_release.py --no-runtime # 跳过内嵌运行时（只出需要本机 BCML 的瘦包）
  python tools/build_release.py --verify     # 额外实跑一次内嵌运行时自检
"""
import os
import re
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
APP_NAME = "BCML-WinUI3"
CSPROJ = os.path.join(ROOT, "src", "BCML.WinUI3.App", "BCML.WinUI3.App.csproj")
PUBLISH_DIR = os.path.join(ROOT, "publish", "win-x64")
ARTIFACTS = os.path.join(ROOT, "artifacts")
MAKE_RUNTIME = os.path.join(HERE, "make_runtime.py")
RUNTIME_SUBPATH = os.path.join("runtime", "python39")


def dotnet() -> str:
    for p in (r"C:\Program Files\dotnet\dotnet.exe", "dotnet"):
        if p == "dotnet" or os.path.exists(p):
            return p
    raise SystemExit("找不到 dotnet")


def python() -> str:
    """用当前解释器跑组装脚本（Python 3.7+ 即可，和 BCML 的 3.9 无关）。"""
    return sys.executable or "python"


def read_version() -> str:
    props = os.path.join(ROOT, "Directory.Build.props")
    m = re.search(r"<AppVersion>([^<]+)</AppVersion>", open(props, encoding="utf-8").read())
    return m.group(1).strip() if m else "0.0.0"


def step(msg):
    print(f"\n=== {msg} ===", flush=True)


def run(cmd, **kw):
    print("  $ " + " ".join(cmd), flush=True)
    r = subprocess.run(cmd, **kw)
    if r.returncode != 0:
        raise SystemExit(f"命令失败（返回码 {r.returncode}）")
    return r


def dir_stats(path):
    n = 0
    total = 0
    for dp, _, fs in os.walk(path):
        for f in fs:
            n += 1
            try:
                total += os.path.getsize(os.path.join(dp, f))
            except OSError:
                pass
    return n, total


def main():
    version = read_version()
    print(f"{APP_NAME} 构建  ·  版本 {version}")

    want_runtime = "--no-runtime" not in sys.argv

    # ---- 1) 先确保没有实例占着输出目录（否则 MSB3027） ----
    step("关闭正在运行的实例")
    subprocess.run(["taskkill", "/F", "/IM", f"{APP_NAME}.exe"],
                   capture_output=True, check=False)
    print("  （若没有在运行会返回非 0，忽略）")

    # ---- 2) publish ----
    step("dotnet publish（自包含 .NET + Windows App SDK）")
    if os.path.isdir(PUBLISH_DIR):
        shutil.rmtree(PUBLISH_DIR, ignore_errors=True)
    run([
        dotnet(), "publish", CSPROJ,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:WindowsAppSDKSelfContained=true",
        # 不清除调试符号：csproj 里 Release 用的是 portable PDB，
        # 打包时会按扩展名排除（见 skip_ext），所以 zip 里不会有，
        # 但发布目录留着 —— 用户那边崩了能拿行号定位。
        "-o", PUBLISH_DIR,
        "-v", "q", "-nologo",
    ], cwd=ROOT)

    # ---- 3) 组装内嵌 Python 3.9 运行时 ----
    # 必须在 publish 之后做：publish 会清空整个输出目录。
    if want_runtime:
        step("组装内嵌 Python 3.9 运行时")
        cmd = [python(), MAKE_RUNTIME, "--target", PUBLISH_DIR]
        if "--verify" in sys.argv:
            cmd.append("--verify")
        if "--runtime-clean" in sys.argv:
            cmd.append("--clean")
        run(cmd, cwd=ROOT)
    else:
        step("跳过内嵌运行时（--no-runtime）")
        print("  注意：这个包需要目标机器已装 BCML（Python 3.9 + oead）。")

    # ---- 4) 清掉运行时里的字节码缓存 ----
    # 上一步的 --verify 会真的把解释器跑起来，Python 顺手在每个 import 过的包下面
    # 写了 __pycache__/*.pyc。不清掉的话它们会被打进 zip（约 100 个文件 / 0.7 MB），
    # 而且**是否出现取决于跑没跑 --verify** —— 构建产物就不确定了。
    if want_runtime:
        step("清理运行时字节码缓存")
        removed = 0
        for dp, dirnames, filenames in os.walk(os.path.join(PUBLISH_DIR, RUNTIME_SUBPATH)):
            for d in list(dirnames):
                if d == "__pycache__":
                    shutil.rmtree(os.path.join(dp, d), ignore_errors=True)
                    dirnames.remove(d)
                    removed += 1
            for f in filenames:
                if f.endswith((".pyc", ".pyo")):
                    try:
                        os.remove(os.path.join(dp, f))
                    except OSError:
                        pass
        print(f"  删除 {removed} 个 __pycache__ 目录")

    # ---- 5) 校验关键文件 ----
    step("校验发布目录")
    required = [
        f"{APP_NAME}.exe",
        f"{APP_NAME}.pri",                 # 编译后 XAML 的载体，缺了窗口打不开
        "BCML.WinUI3.Core.dll",
        os.path.join("sidecar", "rpc_server.py"),
        "Microsoft.UI.Xaml.dll",
    ]
    if want_runtime:
        required += [
            os.path.join(RUNTIME_SUBPATH, "python.exe"),
            # 解释器本体：少了它 python.exe 起不来，但文件存在性检查看不出来
            os.path.join(RUNTIME_SUBPATH, "python39.dll"),
            # 标准库压缩包：少了它 import 任何模块都失败
            os.path.join(RUNTIME_SUBPATH, "python39.zip"),
            os.path.join(RUNTIME_SUBPATH, "python39._pth"),
            os.path.join(RUNTIME_SUBPATH, "Lib", "site-packages", "bcml", "install.py"),
            os.path.join(RUNTIME_SUBPATH, "Lib", "site-packages", "oead.cp39-win_amd64.pyd"),
            # 打包 BNP 用的 7z，藏在 bcml/helpers 里，漏了就整条工具链坏掉
            os.path.join(RUNTIME_SUBPATH, "Lib", "site-packages", "bcml", "helpers", "7z.exe"),
            # 完整性清单：应用启动时会拿它校验运行时，缺了就只能降级成「不做校验」
            os.path.join(RUNTIME_SUBPATH, "runtime-manifest.json"),
        ]
    missing = [r for r in required if not os.path.exists(os.path.join(PUBLISH_DIR, r))]
    for r in required:
        print(("  OK   " if os.path.exists(os.path.join(PUBLISH_DIR, r)) else "  缺失 ") + r)
    if missing:
        raise SystemExit("发布目录不完整：" + ", ".join(missing))

    exe = os.path.join(PUBLISH_DIR, f"{APP_NAME}.exe")
    n_files, total = dir_stats(PUBLISH_DIR)
    print(f"\n  发布目录：{n_files} 个文件，共 {total / 1024 / 1024:.1f} MB")
    print(f"  可执行文件：{exe}")
    if want_runtime:
        rn, rb = dir_stats(os.path.join(PUBLISH_DIR, RUNTIME_SUBPATH))
        print(f"  其中内嵌运行时：{rn} 个文件，{rb / 1024 / 1024:.1f} MB")

    if "--no-zip" in sys.argv:
        return

    # ---- 6) 打便携包 ----
    step("打 zip")
    os.makedirs(ARTIFACTS, exist_ok=True)
    zip_path = os.path.join(ARTIFACTS, f"{APP_NAME}-{version}-win-x64.zip")
    if os.path.exists(zip_path):
        os.remove(zip_path)

    # 只带应用自己的文件，不把 pdb / 编译中间产物 / 缓存塞进去
    skip_ext = (".pdb",)
    skip_names = {".bcml-runtime.json"}
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for dp, _, files in os.walk(PUBLISH_DIR):
            # 这里再挡一道 __pycache__：上面虽然清过了，但打包前万一又跑过一次
            # 解释器（例如手工 --verify），不该让它悄悄进包
            if "__pycache__" in dp.split(os.sep):
                continue
            for f in files:
                if f.endswith(skip_ext) or f in skip_names:
                    continue
                full = os.path.join(dp, f)
                z.write(full, os.path.relpath(full, PUBLISH_DIR))

    print(f"  {zip_path}")
    print(f"  压缩包大小：{os.path.getsize(zip_path) / 1024 / 1024:.1f} MB")
    print(f"\n完成于 {datetime.now():%Y-%m-%d %H:%M:%S}")
    if want_runtime:
        print("解压后直接运行 BCML-WinUI3.exe —— 内嵌 Python 3.9 已随包发布，无需另装 BCML。")
    else:
        print("解压后直接运行 BCML-WinUI3.exe；后端会自动探测本机已安装的 BCML（Python 3.9）。")


if __name__ == "__main__":
    main()
