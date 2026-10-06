# BCML-WinUI3

将 [BCML](https://github.com/NiceneNerd/BCML)（《塞尔达传说：旷野之息》模组合并工具）的界面**重写为原生 WinUI 3**，
后端继续复用 BCML 自己的 Python 实现。

## 特点

- **随包发布、免装 BCML**：内嵌 Python 3.9 运行时（含 `oead` 原生扩展），解压即用。
- **界面语言**：简体中文 / English（当前版本仅此两种）。
- **模组管理**：列表 / 启停 / 排序（排序模式，带上移下移）/ 安装 / 卸载 / 重新处理 / 配置档案。
- **工具**：RSTB 生成、模组对比、BNP 转换、优先级修复。
- **备份**：完整备份 / 备份中心 / 导入导出清单。

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

## 说明

- 界面文案走 `src/BCML.WinUI3.Core/Services/Loc.cs` 的字典表，新增语言 = 往 `Tables` 里加一张表，
  并在 `Available` 里登记；`tools/check_loc.py` 会校验各表词条数量是否一致。
- XAML 资源在非打包（`WindowsPackageType=None`）模式下编进 `BCML-WinUI3.pri`，
  校验界面资源时请查 `.pri` 而不是 `.dll`。
