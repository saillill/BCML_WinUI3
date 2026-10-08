# BCML-WinUI3

A native **WinUI 3** rewrite of the [BCML](https://github.com/NiceneNerd/BCML)
mod-merging tool for *The Legend of Zelda: Breath of the Wild*.
The backend continues to reuse BCML's own Python implementation.

## Usage

1. Download `BCML-WinUI3-<version>-win-x64.zip` and extract it.
2. Run `BCML-WinUI3.exe`. The first launch opens a setup wizard that asks for the
   game's `romfs` directory.
3. Python and BCML do not need to be installed separately.

## Requirements

- Windows 10 19041 or later / Windows 11, x64.

## Building from source

```bash
tools/build_release.py        # dotnet publish + bundle the embedded runtime
tools/test_sidecar.py         # backend RPC tests
tools/check_loc.py            # localization entry symmetry check
```

The artifact is written to `artifacts/BCML-WinUI3-<version>-win-x64.zip`.

## License

GPL-3.0-or-later — see [LICENSE](LICENSE).

---

# BCML-WinUI3（简体中文）

将 [BCML](https://github.com/NiceneNerd/BCML)（《塞尔达传说：旷野之息》模组合并工具）的界面**重写为原生 WinUI 3**，
后端继续复用 BCML 自己的 Python 实现。

## 使用

1. 下载 `BCML-WinUI3-<版本>-win-x64.zip` 并解压。
2. 运行 `BCML-WinUI3.exe`。首次启动会走设置向导，需要指定游戏 romfs 目录。
3. 无需另装 Python 或 BCML。

## 系统要求

- Windows 10 19041 及以上 / Windows 11，x64。

## 从源码构建

```bash
tools/build_release.py        # dotnet publish + 打包内嵌运行时
tools/test_sidecar.py         # 后端 RPC 测试
tools/check_loc.py            # 本地化词条对称性校验
```

产物在 `artifacts/BCML-WinUI3-<版本>-win-x64.zip`。

## 许可证

GPL-3.0-or-later —— 详见 [LICENSE](LICENSE)。
