# BCML-WinUI3

把 [BCML](https://github.com/NiceneNerd/BCML)（《塞尔达传说：旷野之息》模组合并工具）的界面**重写为原生 WinUI 3**，
后端继续复用 BCML 自己的 Python 实现。

> 状态：**架构验证完成，界面正在逐页迁移**。目前「模组」页（列表 / 启停 / 排序 / 安装 / 卸载 / 重新处理 / 配置档案）
> 与「设置」页已经能用真实数据跑通；其余页面见下方路线图。

---

## 为什么是「原生界面 + Python 后端」，而不是整个重写成 C#

| | 规模 | 结论 |
|---|---|---|
| BCML Python 后端 | 30 个文件 / 约 1 万行，其中 `mergers/` 20 个模块 5,300 行 | 重写等于重做整个二进制格式层 |
| 二进制格式核心 | 依赖 **`oead`**（Rust + PyO3）：SARC / BYML / AAMP / MSBT / BFRES / Yaz0 | C# 没有绑定，得自己接 Rust FFI |
| 前端耦合面 | 仅 **37 个 API 方法**（`pywebview.api.*`） | 缝隙很干净，换壳成本低 |

所以：**UI 换成 WinUI 3，合并逻辑一行不改**。mod 兼容性 100% 保留，也永远跟得上 BCML 上游。

---

## 架构

```
┌──────────────────────────────┐        ┌─────────────────────────────┐
│  BCML-WinUI3.exe (WinUI 3)   │        │  python rpc_server.py       │
│                              │        │  （随包内嵌的 Python 3.9）   │
│  Pages/ModsPage              │        │                             │
│  Pages/SettingsPage          │  stdio │  bcml._api.Api              │
│  Pages/LogPage               │◄──────►│   ├─ install / mergers      │
│                              │ JSON-  │   ├─ oead（Rust 原生扩展）   │
│  Core/Sidecar/SidecarClient  │  RPC   │   └─ multiprocessing 池      │
│  Core/Services/BcmlService   │        │                             │
└──────────────────────────────┘        └─────────────────────────────┘
```

- **传输**：stdin/stdout 上的 **定长帧 JSON-RPC 2.0**（4 字节小端长度 + UTF-8 正文，与 LSP 同款）。
  不走 HTTP / 端口：没有防火墙弹窗、没有端口冲突、进程退出即断开。
- **进度**：BCML 内部所有 `print()` 都被 sidecar 接住，转成 `notify.log` 通知推给界面，
  同时写 stderr 与环形缓冲（「日志」页）。
- **错误**：`-32000` 后端异常 / `-32010` 该操作需要界面（由 C# 侧用原生对话框实现）/ `-32601` 方法不存在。

### 内嵌 Python 3.9（免装 BCML 的绿色版）

发布包自带一份**精简过的 Python 3.9.13 运行时**（`runtime/python39/`，约 74 MB），
解压即用，目标机器**不需要装 BCML**。由 `tools/make_runtime.py` 组装：

| 组成 | 来源 | 大小 |
|---|---|---|
| 解释器 + 标准库 | 官方 `python-3.9.13-embed-amd64.zip` | ~16 MB |
| `bcml`（含 `helpers/7z.exe`） | 本机已装 BCML 的 site-packages | 35 MB |
| `botw` / `rstb` / `oead` / `xxhash` | 同上 | 16 MB |
| `webview` / `pythonnet` / `requests` 等导入期依赖 | 同上 | 7 MB |
| **合计** | | **~74 MB** |

依赖清单不是拍脑袋列的，而是「**AST 扫描 `bcml`/`botw`/`rstb`/`webview` 全部 import
（含延迟导入）+ 实跑自检**」两边夹出来的。刻意**不带**的两大块：

- `cefpython3`（162 MB）—— 只被 `bcml/__main__.py` 的桌面 GUI 入口用到；
- `bcml/assets/node_modules`（71 MB）—— CEF 前端资源。

启动时的解释器优先级：**设置页手动指定 → 随包内嵌运行时 → 本机已装的 BCML Python 3.9**。
为了让「内嵌运行时自带依赖」这个前提真的成立，启动 sidecar 时会显式设 `PYTHONNOUSERSITE=1`
并清掉 `PYTHONPATH` —— 免得本机 `%APPDATA%\Python\...` 里的包悄悄混进来，
造成「我这能跑、别人那不行」。

```bash
python tools/make_runtime.py --verify     # 组装 + 实跑一次 sidecar 真实调用
python tools/make_runtime.py --clean      # 强制重建（默认按源包 mtime 判断可否复用）
```

### 运行时完整性校验

构建时给运行时每个文件算 SHA-256，落进 `runtime-manifest.json`；启动时逐项比对。
分档处理，避免「要么全放行、要么一刀切拒绝」：

| 情况 | 行为 |
|---|---|
| 逐项一致 | 正常启动，结论写进日志并在「设置」页显示 |
| 没有内嵌运行时（开发期 `bin/`） | 跳过校验，回退到系统 Python |
| 运行时在但没带清单（旧包） | 只提示一下，仍然启动 |
| **文件被改 / 被截断 / 缺失** | **拒绝启动后端**，并直接说出是哪个文件不对 |

有了这层，一个被替换或被截断的 `oead.pyd` 就不会只表现成含糊的「后端起不来」。
清单刻意不含 `__pycache__` / `.pyc` —— 那是解释器跑起来才生成的，纳入会让校验结果
随「有没有跑过」而变。

### 两个必须知道的坑（都已处理）

1. **stdout 会被 BCML 的 print 冲垮。**
   sidecar 启动时先 `os.dup(1)` 独占一份原始 stdout 专供 RPC，再把 `sys.stdout` 换成转发器，
   最后 `os.dup2(2, 1)` 把**原生 fd 1** 也指向 stderr —— 这样 `multiprocessing` 的 spawn 子进程里
   任何输出也不会污染 RPC 帧流。

2. **`BitmapImage` 必须在 UI 线程创建。**
   后台线程上 `new BitmapImage()` 会抛 `COMException 0x8001010E(RPC_E_WRONG_THREAD)`，
   而且**消息是空的**、只有堆栈能看出凶手。正确做法：base64 解码留在后台（纯 CPU），
   建图 + 赋值的整段回到 `DispatcherQueue`，并用 `TaskCompletionSource` 把异常带回，
   避免 `TryEnqueue(async ...)` 变成 `async void` 把异常吞掉。
   同理，**不要在后台线程读 `Window.DispatcherQueue`** —— 那是 WinRT 属性，也会抛这个异常。

3. **不要试图把已有 `UIElement` 搬到别的地方**（例如把页头塞进 `NavigationView.Header`）。
   即使先从原父级 `Remove`，`UIElementCollection.Add` 仍会抛
   `COMException 0x800F1000「没有检测到已安装的组件」`。
   现在的做法是：隐藏 `NavigationView` 自带的展开按钮（`IsPaneToggleButtonVisible="False"`），
   由各页头里的汉堡按钮调 `MainWindow.TogglePane()` —— 不动元素，效果一样。
   另外 `ContentPresenter` 是模板部件，不要在应用代码里当容器用（`set_Content` 会直接拒）。

4. **`Frame` 的 `Horizontal/VerticalContentAlignment` 默认是 `Center`。**
   后果：当页面内容的期望尺寸超过可用空间时，整页会被**居中并溢出** ——
   表现为「窗口一小，页面就飘到中间、工具栏被右边缘切掉」。
   必须显式写 `HorizontalContentAlignment="Stretch" VerticalContentAlignment="Stretch"`。
   顺带：两栏布局别用固定宽 + MinWidth（`340 + 420` 会直接超宽），
   改成按比例分（`3*` / `2*`）并给上限（`MaxWidth="460"`）。

5. **`dotnet publish` 不会复制应用自己的 `.pri`**，而它是编译后 XAML（XBF）的载体。
   缺了它，`InitializeComponent()` 会在运行时抛 `XamlParseException`（表现为「窗口打不开」）。
   `BCML.WinUI3.App.csproj` 里的 `CopyAppPriToPublish` target 就是补这个。

6. **sidecar 里 spawn 子进程必须显式给 `stdin=DEVNULL`**（见 `_child_kwargs()`）。
   本进程的 stdin 就是 RPC 管道，而 Windows 上 `subprocess` 默认只关 fd≥3，**fd 0 会被继承**。
   实测：

   | 子进程 | 结果 |
   |---|---|
   | `cmd /c echo hi` | 正常退出 |
   | `python -V` | 正常退出 |
   | `python -c "print(1)"`（继承 stdin） | **永久挂起** |
   | 同上 + `stdin=DEVNULL` / `PIPE` | 正常退出 |

   而 sidecar 是单 worker 串行的，一个挂住的子进程会把**整个后端**拖死 ——
   界面表现是「点了没反应，之后所有操作都没反应」。受影响的包括 `sys.probePython`
   （Python 路径校验）与全部走 7z 的工具。顺带还能避免子进程把 RPC 请求帧偷走。

---

## 目录结构

```
BCML-WinUI3/
├─ BCML.WinUI3.slnx
├─ Directory.Build.props              # AppVersion 单一来源 + git revision 注入
├─ sidecar/
│  └─ rpc_server.py                   # JSON-RPC 服务，包装 bcml._api.Api
├─ src/
│  ├─ BCML.WinUI3.Core/               # 纯逻辑库（无 WinUI 依赖）
│  │  ├─ Sidecar/SidecarClient.cs     # 进程管理 + 帧收发 + 请求配对 + 超时
│  │  ├─ Services/BcmlService.cs      # 强类型 API 封装
│  │  └─ Models/Models.cs
│  └─ BCML.WinUI3.App/                # WinUI 3 应用
│     ├─ Program.cs                   # 显式 Main：单实例 + 启动期崩溃落盘
│     ├─ App.xaml(.cs)
│     ├─ MainWindow.xaml(.cs)         # NavigationView + 状态栏 + 忙碌遮罩 + 引导遮罩
│     ├─ Pages/ModsPage.xaml(.cs)     # 模组列表（核心页）
│     ├─ Pages/SettingsPage.xaml(.cs)
│     ├─ Pages/SetupWizardPage.xaml(.cs)  # 首次运行引导（原版 FirstRun.jsx）
│     ├─ Pages/LogPage.cs             # 后端日志（纯代码构建）
│     ├─ Services/AppServices.cs      # sidecar 生命周期 + 本地偏好 + 日志缓冲
│     └─ Services/BusyMessages.cs     # 忙碌遮罩的俏皮标语（原版 95 条）
└─ tools/
   ├─ make_runtime.py                 # 组装内嵌 Python 3.9 运行时（绿色版）
   ├─ build_release.py                # 一键出发布包（publish + 运行时 + zip）
   ├─ sidecar_selftest.py             # 不开界面单独验证 RPC 链路
   ├─ test_sidecar.py                 # 自动化测试（协议 / 请求分流 / 输入校验 / 重新合并 / 模组选项）
   ├─ test_path_guard.py              # 回归：路径穿越守卫
   ├─ shot_py.py                      # 截图（ctypes，不需要 .NET）
   ├─ crop_bmp.py                     # 裁剪并放大截图（内含最小 PNG 编码器）
   ├─ click.py                        # 按窗口坐标点击 / 滚轮（自动换算屏幕坐标）
   ├─ key.py                          # 给窗口发一个按键（ESC/ENTER）
   ├─ mods_select.ps1                 # UI 自动化：选中第 N 个模组（核对详情面板用）
   ├─ list_buttons.ps1                # UI 自动化：列出按钮（核对新控件是否渲染）
   ├─ wizard_next.ps1                 # UI 自动化：驱动首次运行向导翻页
   ├─ shot_app.ps1 / shot_max.ps1     # 截主窗口（自动化核对界面用）
   └─ cache/                          # 下载缓存（python embeddable zip）
```

---

## 构建与运行

**前置**：.NET SDK；以及**一份已装 `bcml` 的 Python 3.9**（只用于「取材」依赖打进包里，
运行时不再依赖它 —— 组装脚本会从它的 `site-packages` 挑出需要的子集）。

```bash
cd BCML-WinUI3

# 只验证后端链路（不需要界面，最快的回归手段）
python tools/sidecar_selftest.py

# 编译
"C:/Program Files/dotnet/dotnet.exe" build BCML.WinUI3.slnx -c Debug

# 运行
src/BCML.WinUI3.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/BCML-WinUI3.exe

# 出发布包（自包含 .NET + Windows App SDK + 内嵌 Python 3.9）
python tools/build_release.py                 # publish + 组装运行时 + 打 zip
python tools/build_release.py --verify        # 顺带实跑一次内嵌运行时自检
python tools/build_release.py --no-runtime    # 出「瘦包」，需要目标机器已装 BCML
```

### 排查用

- **`--page <mods|settings|tools|backup|logs>`**：直接启动到指定页，省得手点。
  例如 `BCML-WinUI3.exe --page logs`。
- **`--wizard <1|2|3|4>`**：直接打开首次运行引导的第 N 页（不受「已完成」标记影响），
  便于截图或冒烟测试。
- `%LOCALAPPDATA%\BCML-WinUI3\startup.log` —— 启动链路 + **所有未处理异常**。
  非打包的 WinUI 3 应用里，事件处理器抛异常会**静默退进程**（没有对话框、没有控制台输出），
  用户看到的就是「点一下就闪退」。所以 App 里挂了三处兜底：
  `Application.UnhandledException`（落盘 + 弹框 + `Handled = true` 尽量保住进程）、
  `AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException`。
  **排查闪退先看这个文件。**
- `%LOCALAPPDATA%\BCML-WinUI3\backend.log` —— sidecar 启动、模组/封面加载等业务日志。

---

## 已实现

**自绘顶栏**（`ExtendsContentIntoTitleBar`）
- 系统顶栏不显示，改为自己画一条 44px 的：
  `[☰ 展开/收起侧栏]  BCML-WinUI3  …（右侧留给系统的最小化/最大化/关闭）`
- ⚠ `SetTitleBar` 会把整个元素变成拖拽区，里面的按钮收不到点击。
  所以 ☰ 放在拖拽区**左边的独立列**里（`TitleBarDragRegion` 只覆盖右边的列）。

**模组页**（主从布局：左列表 / 右详情）
- 页头只留 标题 + 工具栏（副标题已按需求去掉）
- 真实读取 BCML 的模组列表（名称、优先级、启用状态、封面缩略图）
- 卡片**只显示封面 + 名称**（+ 禁用徽章），元信息与描述统一放在右侧详情面板
- **拖动排序**：`CanReorderItems` + `ReorderMode=Enabled`（行上有拖拽手柄）。
  落盘走 sidecar 的 `sys.reorderMods` —— **只重写 `priority` 与目录名，不触发合并**，
  所以拖完可以继续调，最后再手动「重新合并」。两阶段改名避免优先级互换时撞车；
  被「显示已禁用」过滤掉的 mod 会保持原位、不被抢走优先级。
- 右侧详情面板：**大封面**（+ 禁用遮罩）、标题 + **徽标行**（启用/禁用、版本、平台、处理状态）、
  **模组信息表**（优先级 / 文件夹 / 平台 / 版本 / 更新时间 / 模组 ID，只列有值的字段）、
  **前置依赖**、**已启用选项**、**已关闭的合并器**、
  **Markdown 描述**（自带轻量渲染器，兼容描述里混的 `<br>`/`**粗体**`/`- 列表`）、
  **涉及改动**（该模组涉及的合并器）、
  **改动明细**（按合并器分组的文件清单，逐组可折叠、等宽字体、超 300 项截断提示）、
  以及 文件夹 / 来源页 / 启用停用 / 更新 / 上移 / 下移 / 重新处理 / 卸载
  - 元数据与改动清单走 sidecar 的 **`sys.modDetails`**，一次 RPC 同时拿全，界面只有一个加载态；
    改动清单要遍历模组文件、比较重，所以**选中某个模组时才去拉**，并按模组目录缓存，
    在列表里来回点不会重复请求（列表刷新时整体失效）
- 启用 / 禁用（写 `<mod>/.disabled`，与 Python 版 BCML 完全互通）
- 卸载、重新处理（重算该模组的合并日志）
- 安装 `.bnp`：原生文件选择器 → **选项对话框**（复选 + 单选组，单选组默认选作者标 `*` 的推荐项）→ 安装 → 自动重新合并
- 重新合并（完整 `refresh_merges`）
- **导入 / 导出…（首页工具栏弹窗，不单独占侧栏入口）**
  - **导出当前排序到文件…** → 一份几 KB 的 JSON：每个模组的排序（priority）、
    启用 / 禁用、`options.json`（合并器选项）、已启用的选项目录。**不含 mod 文件**
  - **从文件导入排序…** → 按清单重写优先级与目录名、恢复启用状态与合并器选项，
    并给出差异报告（本机没装的 mod、选项目录不一致需要重装的 mod）
  - 弹窗下半部分保留 BCML 自带的重型配置档案（保存当前 / 加载 / 删除）
- 配置档案：列出 / 保存当前 / 加载 / 删除
- 忙碌遮罩 + 错误对话框（把后端 traceback 收进可展开的详情）

**设置页**
- 后端状态（BCML 版本 / Python / pid / 解释器）、Python 路径覆盖 + 重启后端
- BCML 目录一览（数据目录 / 模组 / 合并输出 / 配置档案）并可直接在资源管理器中打开
- 关键设置编辑（`game_dir_nx` / `export_dir_nx` / WiiU 模式）并保存

**备份与恢复页**
- 列出 `<store>/backups/*.7z`：名称、包含的模组数、文件大小、修改时间、完整路径
- 新建备份（可命名，留空用 `BCML_Backup_年-月-日`）
- 恢复（二次确认，红色警示「当前模组会被全部删除」）、删除、在资源管理器中定位
- 打开备份目录

**工具页**（原版 DevTools 的工具已**全部接入**）
- **创建 BNP（BNP Creator）**：选文件夹 + 元数据表单（名称 / 版本 / 作者 / 图标 / 依赖）+
  选项组定义（复选 · 单选），打包成 `.bnp`
- **BNP 平台转换**（Switch ↔ Wii U）：可选择「跳过并列出警告」或「中止」两种策略
- **升级旧版 BNP**：解包时自动把旧 `rules.txt` 转成 `info.json` 再重新打包
- **BNP 转为独立模组**：独立合并后导出可直接放入 Cemu graphicPacks / Atmosphere 的 zip
- **为模组生成 RSTB**：只开 rstb 合并器跑一次合并，把 ResourceSizeTable 写回模组
- **模组比对（Compare Mods）**：按合并器分组做左右差集，并排渲染（绿=仅左 / 红=仅右）
- 打开合并输出 / 打开数据目录 / 重新合并

这些工具在原版里都靠 `self.window.create_file_dialog` 选路径，无界面 sidecar 里不可用；
本应用统一采用「**界面选路径 → 后端只干活**」的模式，每个工具对应一个 `sys.*` 无界面接口
（`sys.createBnp` / `convertBnp` / `upgradeBnp` / `bnpToStandalone` / `genRstb` / `compareMods`）。

**日志页**：**原始控制台渲染** —— 一个只读 TextBox 承载全部文本（等宽、不换行、
可整段选中复制 / 复制全部 / 清空显示）。刷新按 200ms 节流，**用户选中文本时自动暂停刷新**，
避免复制到一半被冲掉。

**设置页**：对齐原版全部设置项（本体 / 更新 / DLC / Cemu / 导出 / 数据目录、
8 个合并开关、游戏语言 12 种、创建快捷方式），并新增**主题**与**界面语言**。

**创建快捷方式**分两组，别再搞混：

| 组 | 建的是什么 | 落点 |
|---|---|---|
| **BCML-WinUI3（本应用）** | 指向 `BCML-WinUI3.exe`，双击直接进这个界面 | 桌面 / 开始菜单 |
| 命令行版 BCML | 原版 `api.make_shortcut`，指向 `pythonw.exe` + `bcml` 包 | 桌面 / 开始菜单 |

原版只有后者 —— 对用这个 WinUI 界面的人来说基本没用，所以补了前者（`sys.makeAppShortcut`）。
`.lnk` 的写入复用 BCML 自带编译模块里的 `manager.create_shortcut`，不在 C# 侧再引一套
COM（`IShellLink`）互操作。桌面/开始菜单路径走注册表的「已知文件夹」，
避开 OneDrive 重定向导致的「建了但看不见」。

**应用图标**：`Assets/BCML.ico`（BCML 官方图标，6 个尺寸 16→256px）通过 csproj 的
`<ApplicationIcon>` 编译进 exe 资源 —— 非打包应用的任务栏 / 资源管理器 / 快捷方式图标
都取这里，不设就会是 .NET 的默认空白图标。
每条路径都有 ✓/✗ **合规性校验**与**自动定位**（选错层级时可自动修正）。

详见 [`docs/原版功能对照.md`](docs/原版功能对照.md)。

**响应式布局**（`AdaptiveTrigger`，按窗口宽度自动切换）
| 窗口宽度 | 侧栏 | 右侧详情栏 | 封面高 |
|---|---|---|---|
| ≥ 1150 | 展开 190px | 470px | 210px |
| ≥ 900 | 展开 150px | 350px | 168px |
| < 900 | 收成 48px 图标条 | 260px | 126px |

窗口最小 680×520 DIP，可自由拖拽缩放。

---

## 路线图

| 页面 | 状态 |
|---|---|
| 模组列表 / 启停 / 排序 / 安装 / 卸载 / 重新合并 / 配置档案 | ✅ 已实现 |
| 设置 / 后端日志 | ✅ 已实现 |
| 备份与恢复（创建 / 恢复 / 删除 BCML 备份） | ✅ 已实现 |
| 首次运行引导向导（4 页）+ 忙碌遮罩俏皮标语（原版 95 条） | ✅ 已实现 |
| 模组详情：合并器改动列表 / 来源链接 / 封面 / 描述 | ✅ 已实现（右侧详情面板） |
| bnp 工具箱（创建 / 转换 / 升级 bnp、生成 rstb、导出独立包、模组比对） | ✅ 已实现 |
| **内嵌 Python 3.9（免装 BCML，做成完全绿色版）** | ✅ 已实现（`runtime/python39`，74 MB） |
| 版本比对（本地版本 vs GameBanana 最新） | ⬜ 待做 |
| 旧版 BCML 模组迁移（`get_old_mods` / `convert_old_mods`，仅 Wii U 模式有意义） | ⬜ 待做 |
| 界面语言词条铺满（模组 / 备份 / 日志页与各弹窗，现存 134 处硬编码中文） | ⬜ 待做 |
| 真实进度条（把 `Messager` 的行解析成百分比） | ⬜ 待做 |
| Inno Setup 安装包（复用 ETU 那套 ISCC 流程） | ⬜ 待做 |

7. **不要在 sidecar 里用 BCML 的 `multiprocessing.Pool`。**
   `bcml/util.py::start_pool()` 是 `Pool(processes=min(63, cpu_count()))`。在 sidecar 里
   它 spawn 出来的 worker 会**以 `rpc_server.py` 作为 `__mp_main__` 重新导入本模块**，
   几十个进程并发去加载 20MB 的 `oead.pyd`，结果一个都起不来：

   - worker 全部停在 ~9MB（oead 还没载入）；
   - 主进程 CPU 只有几个百分点；
   - `refresh_merges()` 十几分钟不返回 → 界面「重新合并」无限转圈。

   把池调小到 2/4 一样卡，所以不是规模问题，是 spawn 这条路在本进程里走不通。
   现在 `_install_serial_pool()` 会把 `start_pool` 换成 `_SerialPool`（同接口、在本进程里
   顺序执行），**实测 6.2 秒完成**。想恢复并行可设 `BCML_SIDECAR_PARALLEL=1`。

---

## 顺手修掉的一个 BCML 本体 bug

`bcml/util.py::parse_profile_file` 固定用 UTF-8 读 `.profile`，但那文件是**用系统默认编码
（中文 Windows 上是 GBK）** 写的 —— 于是配置名一旦是中文（例如「有少女动作」），
`get_profiles()` 直接抛 `UnicodeDecodeError`，BCML 自己的「配置文件」功能整个不可用。

已加编码回退（`utf-8 → gbk → cp936 → latin-1`）并把写入侧统一成 UTF-8。
改的是 site-packages 里的 `util.py`，备份在 `util.py.bak-before-encoding-fix`。
**这个修复值得提到 BCML_ZH 上游。**

## 与 BCML_ZH 的关系

本项目的 Python 部分就是你现有的 BCML_ZH 后端（`bcml` 包）——**没有 fork、没有改动**，
sidecar 只是它的一个无界面宿主。所以：

- 两边的模组目录、配置档案、备份**完全共用**，可以随时来回切；
- 上游 BCML 升级后，直接换 Python 环境即可，界面不用动。

---

## 备份中心（一个入口、两类备份）

「导入 / 导出」与侧栏的「备份」页原本是两套东西，现已合并到模组页工具条上的 **备份…**：

| 类别 | 内容 | 存哪 |
|---|---|---|
| **完整备份** | 整个 `mods_nx` 的 `.7z`（全部 mod 文件 + 配置），几百 MB | BCML 的备份目录 |
| **导出的备份文件** | 几 KB 的 JSON，只记录排序 / 启用 / 合并器选项 | 用户挑的任意目录 |

两类都会登记进 `%LOCALAPPDATA%\BCML-WinUI3\backup-index.json`（`BackupIndex`），
所以**换目录、换机器也能在列表里找到并直接还原** —— 原本 BCML 只暴露「扫目录扫到的 .7z」，
导出的 JSON 散落各处就再也找不回来了。

列表每行给两个动作：还原 /（删除文件，文件已不在时退化成「从列表移除」）。
BCML 自带的「配置档案」也保留在同一对话框里，没有丢功能。

## Mod 选项重选

像「林可儿 3.0」「少女动作包」这类 BNP 装完之后会在 mod 目录留下
`options/<变体名>/` 一棵目录树，每个子目录是同一组件的不同版本。BCML 只在**安装时**
让你选一次，之后没有入口再改。

- 详情面板出现 **选项** 按钮（只对带可选组件的 mod 显示，图标是带勾的清单）；
- 打开后按 `info.json` 的 `options` 定义渲染：`multi` 是复选框，`single` 是若干单选组；
- 应用后**自动重新合并**，不需要用户再点一次。

**「不残留旧配置」是怎么保证的**：`find_modded_files()` 会把整个 mod 目录（含 `options/`）
都扫进合并，所以只要让 `mod/options/` 下**只留下被选中的变体**即可。而为了能反复改选，
第一次重选时会先在 `%LOCALAPPDATA%\BCML-WinUI3\mod-options\<mod>\pristine\` 留一份
`options/` 的**硬链快照**（几乎不占空间）；之后每次重选都是
「整棵删掉 → 从快照按新选择重建」——旧选择的文件不可能残留。
快照会在 mod 被重装/更新（出现新变体目录）时自动重建。

---

## 界面层：原生 WinUI 化与列表稳定性

### 侧栏不再是「一片纯黑」

`MainWindow` 从来没设过 `SystemBackdrop`。NavigationView 的侧栏用的是**半透明**的
层画刷（`NavigationViewDefaultPaneBackground`），设计上要叠在 Mica 上 —— 没有材质时
它只能叠在裸窗口背景上，于是整块发黑，切换主题也不跟着变。

现在按官方推荐逐级回退：**Mica → 亚克力 → 明确的不透明底色**（老系统 / 远程桌面）。
最后那档不能省，否则没材质的机器上又变成黑块。

### 顶部工具条改成原生 CommandBar

原来是自绘 `WrapPanel` 里混排 10 个控件（2 个无名 ToggleButton、2 个纯图标排序按钮、
4 个带文字按钮、1 个手写「更多」Flyout），换行位置随内容漂移，主次也看不出来。

现在用 `CommandBar` + `AppBarSeparator` 分组：

    [安装模组…][重新合并][备份]  |  [刷新][显示已禁用的模组]  |  [优先级从低到高][优先级从高到低]  |  ⋯

- 主动作 / 视图开关 / 排序 三组一眼分清；
- 破坏性动作（卸载全部）进 `SecondaryCommands`，溢出菜单由控件自己生成，删掉了手写 Flyout；
- 窗口变窄时自动收进 ⋯，比 WrapPanel 的"随机换行"确定得多。

> 踩坑：`ClosedDisplayMode` 的枚举（`AppBarClosedDisplayMode`）只有
> `Hidden/Minimal/Compact`，**没有 `Full`**；标签显不显示由 `DefaultLabelPosition` 决定。
> 另外 XML 注释不能写在标签的属性列表中间。

顺手给详情区那排纯图标按钮补了 `AutomationProperties.Name` —— 原来只设了 ToolTip，
读屏软件和 UI Automation 拿到的按钮名是空的（一排"无名按钮"）。

### 修掉选择 / 刷新时的闪烁

四个叠加原因，逐个消掉：

| 原因 | 修法 |
|---|---|
| `LoadAsync` 每次 `new` 全部 `ModRow`，封面 BitmapImage 被反复解码 | **复用行对象**（先按路径、再按名字两趟匹配），只刷新数据 |
| `RebuildVisible` 用 `Clear()` + 重新 `Add()`，ListView 整表重置、选中项被清空、详情区闪白 | 换成**增量同步** `SyncVisible`（Remove/Insert/Move），集合没变就不动 |
| `LoadDetailsAsync` 没有重入守卫（日志里 5 分钟出现过 6 次「封面/描述加载完成」） | 加 `_detailsLoading` 守卫 |
| `ApplyHandles` 每次加载都把每一行的 `HandleVisibility` 重设一遍 | 只有开关状态真的变了才下发 |

第二趟"按名字匹配"很关键：排序会把目录**改名**（0100_xxx → 0116_xxx），只认路径的话
整表都算新行、封面重新解码 —— 排序就闪。实测排序后 `复用 17 行`。

### 拖动与排序

- 排序 / 拖动落盘不再弹整屏忙碌遮罩（只是重命名几个目录，不值得盖住界面）；
- **上移/下移原来存的是旧顺序**：`Rows.Move` 只动了可见集合，而落盘读的是 `_allRows`，
  所以按完刷新又弹回原位。现在统一用 `ApplyVisibleOrderToAllRows()` 把界面顺序映射回完整顺序。

## 模组编号撞号：一次真实的数据损坏

现象：点「禁用模组」报

    FileNotFoundError: ...\mods_nx\0100_林可儿Mod3.0TheLinkleMod\info.json

排查发现磁盘上**有两个 `0100_`**，同时 `0108_` 消失 —— 两个模组共用了同一个优先级编号。

### 根因

`_resequence` 的完整性校验只比**完整目录名**是否重复，没比 `NNNN_` 前缀这个真正的
优先级数字。于是一次只覆盖部分模组的排序请求，让没被列到的目录原地不动、
被列到的重新编号，两者撞到同一个前缀：

    0100_少女动作包…   ← 不在本批次里，原地不动
    0100_琳克丝NPC…    ← 本批次里排第 0 位，被编成 0100
    0108_              ← 消失了

编号一撞，BCML 解析优先级就错乱，界面拿着的路径也随之失效。

### 三层修法

1. **拒绝部分排序**：`order` 必须覆盖全部模组目录，否则直接报错且不碰磁盘
   （部分重编号正是产生"孤儿"的原因）；
2. **编号唯一性校验 + 收尾自检**：计划阶段就查 `NNNN_` 前缀是否全局唯一，
   改完之后再按磁盘实扫一遍，一旦发现重复立即整体回滚；
3. **动作不再信任过期路径**：新增 `sys.modAction`，界面传的 path 若已不存在，
   就按「去掉编号前缀后的名字」重新解析（要求唯一命中，命中多个宁可报错也不猜），
   真找不到时给可读提示「请点右上角"刷新"后重试」，而不是抛 traceback。

另外提供了 `sys.repairPriorities`（工具页「修复模组编号」）：把全部模组重新连续编号，
对没坏的情况幂等。**你机器上那份撞号的目录已经用它修好了**（0100–0116 连续且唯一）。

回归测试见 `ReorderGuardTests` / `StalePathTests`（共 5 条），
其中一条专门用「真实路径改掉编号前缀」来复现过期路径场景。

---

## 排序为什么"转半天"，以及详情栏瘦身

### 排序：一次磁盘改名，不该演变成一次全量重读

BCML 的优先级就编码在目录名里（`0100_xxx`），所以「优先级从低到高」必然要改目录名 ——
这一步躲不掉，但它实测只有**百毫秒级**。慢的是它后面干的事：

    SortAsync → PersistOrderAsync → ReorderLocalAsync（改 17 个目录名，~160ms）
                                  → 然后 LoadAsync()  ← 问题在这
                                       · 重新问后端要全部模组
                                       · 清详情缓存、重新拉封面/描述
                                       · 整表重排
                                  → 再飘一条「顺序已保存」横幅

`sys.reorderMods` 本来就会返回**按新顺序排列的目录名**，所以现在直接把新名字套回内存里的行
（`ModRow.RenameTo`：更新 path / id / priority，顺带刷新绑定），**不再重读列表**。
横幅也去掉了 —— 排序是高频小操作，飘一条提示纯属噪音。

结果：点击到落盘完成约 **160ms**，UI 不闪、不灰、不弹窗。

### 详情栏：高级信息默认折叠

「模组信息」「已启用选项」「涉及改动」「改动明细」用原生 `Expander` 包起来，默认折叠。
这些是"想看才看"的内容，摊开要占掉大半个详情栏，把真正要点的「可用操作」挤到很下面。

- 折叠状态不牺牲信息量：`Expander.Header` 里保留了摘要（如「改动明细」显示 `共 N 个文件`）；
- 数据的来源没变 —— 这些字段来自同一次 `sys.modDetails`，折叠**不会**省掉后端调用，
  纯粹是不占版面；
- `Expander` 自带展开箭头，是平台原生的可折叠区块，不需要自绘。

### 工具条：有空间就都显示出来

之前 `显示排序手柄` 和 `卸载全部模组` 写在 `SecondaryCommands` 里 ——
那是"永远收进 ⋯"，跟窗口宽窄无关。用户反馈「明明有空间却在更多里」。

现在**全部**放 PrimaryCommands：窗口够宽时直接铺开，不够宽时由 CommandBar 自己判断溢出。
另外给「显示排序手柄」补了图标（与列表每行的抓手同一个 glyph `E76F`，一眼能对上关系）。

为了让 9 个命令在常见窗口宽度下都排得下，顺带缩短了几个标签：

| 原 | 现 |
|---|---|
| 显示已禁用的模组 | 显示已禁用 |
| 安装模组… | 安装模组 |
| 优先级从低到高 / 从高到低 | 优先级 低→高 / 优先级 高→低 |

> 又一个坑：`DefaultLabelPosition="Bottom"` 在 `Compact` 显示模式下**不渲染标签**（只剩图标），
> `Right` 才会把标签带出来。

---

## 排序：从「两个方向」改成一个「反转」，以及一条隐藏的破坏性 bug

### 为什么「优先级从低到高」点了没反应

不是按钮坏了，是它**在语义上必然是空操作**。

BCML 的优先级就是目录名的编号，而后端 `_resequence` 是这样给号的：

    "final": f"{start + len(plan):04d}_{...}"     # plan 的顺序 = 我们下发的显示顺序

也就是说，**每次落盘都按当前显示顺序从 0100 连续编号** —— 列表永远是"从上到下编号递增"。
于是"按优先级升序"永远成立，点了当然没变化。真正有意义的只有"反过来"。

所以现在只留一个 **反转顺序** 按钮：每点一次必然改变顺序，不可能出现"点了没用"。
（两个方向的按钮已经被删掉，`mods.sortAsc` / `mods.sortDesc` 词条也一并清了。）

### 实测耗时

| 阶段 | 耗时 |
|---|---|
| 后端（17 个目录改名 + 重写 info.json） | 53–150 ms |
| **全程（含 UI 重排）** | **74–167 ms** |

### 真正"转半天"的原因：详情缓存全失效

一开始量到的是 98 ms，但日志里紧跟着出现：

    23:37:15.962  顺序已保存（后端 83 ms）
    23:37:15.963  反转完成：全程 98 ms
    23:37:18.077  详情已加载 [林可儿 Mod 3.0]：改动 639 个文件   ← 2.1 秒后

排序会**改目录名**，而详情缓存、失败集合、「有没有选项目录」的探测结果都是**按路径做键**的。
改完名全部变成"未命中" → 选中的模组重新走一次 `sys.modDetails` → 遍历它全部文件
（「林可儿 Mod 3.0」639 个文件，2 秒以上）。

修法：`RenameRow()` 在改名时把这几份缓存**一起搬到新键**上。
模组内容没变，只是目录换了个名字，没有理由重新遍历。修完后同样场景全程 167 ms，
且日志里不再出现那次「详情已加载」。

## ⚠ 一个会静默改坏模组启用状态的 bug

**现象**：排序 / 反转 / 拖动之后，会有一两个**不相干**的模组被启用或禁用
（真的写 `.disabled` 文件）。行内开关的 `Toggled` 处理函数是唯一会调用
`mod_action(disable/enable)` 的地方，而它被误触发了。

**根因**：`ToggleSwitch.Toggled` 对**程序性**的 `IsOn` 变化也会触发。
列表重排时 ListView 回收并复用行容器，`IsOn="{x:Bind Enabled, Mode=OneWay}"` 把新行的值
推进 `IsOn`，于是又触发一次 `Toggled` —— 而那一刻 `DataContext` **可能还停在上一行**。
原来的守卫是：

    if (ts.IsOn == row.Enabled) return;   // 看着能挡住程序性刷新

在"DataContext 已换、IsOn 还没换"的时序下 `ts.IsOn != row.Enabled` 成立，
于是被当成用户操作，**对上一行执行了启用/禁用**。

**第一次修法是错的（值得记下来）**：想用 `PointerPressed` / `KeyDown` 来判定"用户真的碰了它"，
只有紧跟其后的 `Toggled` 才算数。结果 `ToggleSwitch` **不保证把 `PointerPressed` 冒泡到控件本身**
（内部元素会把它吃掉），于是 `_pendingToggleRow` 一直是 null ——
**用户真正的点击全被当成"程序性回填"丢掉了**，表现就是"禁用按钮又没用了"。

**最终修法：不去猜事件，改成监听模型。**

    IsOn="{x:Bind Enabled, Mode=TwoWay}"     <!-- 不挂 Toggled -->

`ToggleSwitch.Toggled` 对程序性变化也会触发，但 **TwoWay 绑定只有用户拨动控件时才写回模型**；
容器回收时的程序性回填只走"模型 → 控件"这一个方向，不产生任何通知。
所以监听模型属性变化（`OnRowPropertyChanged` → `ApplyEnabledAsync`）收到的每一次
`Enabled` 变化都必然来自用户操作，天然不受容器回收影响。

配套：自己写回状态时（后端确认 / 失败回滚 / `LoadAsync` 重读）统一走
`SetEnabledSilently()` 或 `_suppressEnabledAction` 包一层，避免把同一次操作再发一遍给后端。

**验证**：行内开关与详情区「禁用/启用」按钮都实测过一轮
（禁用 → 计数 5→6，启用 → 6→5，作用在正确的模组上），连续反转两次时启用集合零变化。

`tools/toggle_switch.ps1`：按 UIA 的 TogglePattern 驱动这些开关。
ToggleSwitch 不是 Button、也不实现 InvokePattern，用点按钮那套脚本找不到它。

**代价**：这个 bug 在排查期间把用户的启用/禁用组合搅乱过一次，
已按 23:24 核对过的快照还原（禁用：琳克丝NPC / 个性化物品说明 / 旷野之勇者 /
士兵套装 / 希利亚风米拉）。修完验证：连续反转两次，禁用集合与模组顺序都零变化。

## 顺带修掉的测试 bug

`ReorderGuardTests.tearDownClass` 原来是拿 `setUpClass` 时记下的**目录名**去还原顺序。
可那些名字这时早就不存在了（`0100_xxx` 已经变成 `0116_xxx`），
后端以"请求没有覆盖全部模组"拒绝执行，而 `except: pass` 把它吞掉了 ——
**每跑一次测试就把用户的模组顺序搅乱一次**。

改成按「去掉编号前缀后的名字」把当前目录名映射回原始顺序；用例内部也改用
**当前**目录名而不是快照名。连跑两次测试，顺序已验证不再变动。

> 另外记一条操作纪律：**测试套件会真的改磁盘顺序，跑测试时应用必须关掉** ——
> 否则应用内存里的顺序会过期，之后它按旧顺序落盘，结果又是一个意外的排列。
