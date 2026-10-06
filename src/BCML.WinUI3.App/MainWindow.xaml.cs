using System;
using System.Threading.Tasks;
using BCML.WinUI3.App.Pages;
using BCML.WinUI3.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using BCML.WinUI3.Core.Services;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;

namespace BCML.WinUI3.App;

public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;
    private ModsPage? _modsPage;
    private SettingsPage? _settingsPage;
    private ToolsPage? _toolsPage;
    private LogPage? _logPage;

    public AppServices Services => _services;

    public MainWindow(AppServices services, string[]? startupArgs = null)
    {
        _services = services;
        InitializeComponent();

        _services.StatusChanged += (_, s) => StatusText.Text = s;
        ApplyLanguage();
        _services.LogAppended += OnLogAppended;
        // 后端就绪（含从「设置」页重启后端后再次就绪）时刷新模组列表
        _services.ReadyChanged += (_, _) => _ = _modsPage?.LoadAsync()!;

        // 选中项变化会触发 Nav_SelectionChanged → Navigate，所以这里不要再显式调一次，
        // 否则页面会被导航两次（表现为列表重复加载）。
        var startPage = ParsePageArg(startupArgs) ?? "mods";
        if (!SelectNavItem(startPage)) Navigate(startPage);

        TryResize();
        SetUpBackdrop();   // 必须在最前面：它决定整窗底色
        SetUpTitleBar();
        ApplyTheme(_services.Theme);
    }

    // ---------------------------------------------------------------- 首次运行引导
    private SetupWizardPage? _wizardPage;

    /// <summary>
    /// 首次运行（或未完成过向导）时，弹出引导向导。
    /// 有 <c>--page</c> 参数时（调试 / 冒烟测试）不弹，免得干扰自动化验证。
    /// </summary>
    public void ShowWizardIfNeeded(string[]? startupArgs)
    {
        // --wizard <n>：调试用，直接跳到第 n 页（1..4），不受「已完成」标记影响。
        var forced = ParseValueArg(startupArgs, "--wizard");
        if (forced is not null)
        {
            _wizardPage = new SetupWizardPage(_services, this, ParseWizardPage(forced));
            WizardHost.Content = _wizardPage;
            WizardHost.Visibility = Visibility.Visible;
            AppServices.Diag($"首次运行引导已弹出（--wizard {forced}）");
            return;
        }

        if (_services.WizardDone) return;
        if (startupArgs is { Length: > 0 }) return;
        if (_wizardPage is not null) return;

        _wizardPage = new SetupWizardPage(_services, this);
        WizardHost.Content = _wizardPage;
        WizardHost.Visibility = Visibility.Visible;
        AppServices.Diag("首次运行引导已弹出");
    }

    private static int ParseWizardPage(string raw) =>
        int.TryParse(raw, out var n) ? Math.Clamp(n - 1, 0, 3) : 0;

    private static string? ParseValueArg(string[]? args, string name)
    {
        if (args is null) return null;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    /// <summary>关闭引导（完成或跳过）。</summary>
    public void CloseWizard()
    {
        WizardHost.Visibility = Visibility.Collapsed;
        WizardHost.Content = null;
        // 先退订再丢引用：Loc.LanguageChanged / ReadyChanged 是长生命周期事件源，
        // 不退订的话旧向导页会一直留在事件表上（回调改的是已经脱离可视化树的控件）。
        _wizardPage?.Unload();
        _wizardPage = null;
        AppServices.Diag("首次运行引导已关闭");
    }

    /// <summary>
    /// 窗口尺寸 / 最小尺寸。
    ///
    /// 注意：<c>AppWindow.Resize</c> 与 <c>PreferredMinimum*</c> 用的都是**物理像素**，
    /// 而 XAML 布局用的是 DIP。在 3840×2160 @175% 这类高分屏上，直接写 1280×860
    /// 实际只有 731×491 DIP —— 窗口小得离谱、页头工具栏被迫折成 4 行。
    /// 所以这里先把目标 DIP 尺寸按当前显示器缩放比换算成物理像素，再夹到工作区内。
    /// </summary>
    private void TryResize()
    {
        try
        {
            var scale = GetScaleFactor();
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            var work = area.WorkArea;

            const int wantW = 1280, wantH = 860;      // DIP
            var w = Math.Min((int)Math.Round(wantW * scale), (int)(work.Width * 0.96));
            var h = Math.Min((int)Math.Round(wantH * scale), (int)(work.Height * 0.96));

            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                // 允许拖到很窄：窄尺寸下由 VSM 把侧栏收成图标条、详情栏同步缩窄
                p.PreferredMinimumWidth = (int)Math.Round(680 * scale);
                p.PreferredMinimumHeight = (int)Math.Round(520 * scale);
                p.IsResizable = true;
            }

            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            StartupLog.Step($"窗口尺寸 {w}×{h} 物理像素（缩放 {scale:P0}，工作区 {work.Width}×{work.Height}）");
        }
        catch (Exception ex)
        {
            StartupLog.Warn("调整窗口尺寸失败: " + ex.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>当前显示器缩放比（1.0 = 100%）。取不到就按 1.0 算。</summary>
    private double GetScaleFactor()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dpi = GetDpiForWindow(hwnd);
            if (dpi >= 96) return dpi / 96.0;
        }
        catch { /* 忽略，回落 1.0 */ }
        return 1.0;
    }

    /// <summary>展开/收起侧栏。NavigationView 自带的展开按钮已隐藏，
    /// 改由各页头里的汉堡按钮调用这里 —— 这样按钮就和页头处在同一行。</summary>
    public void TogglePane() => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    /// <summary>
    /// 自绘顶栏：隐藏系统标题栏，把「拖拽区」限定在 ☰ 右侧那一段，
    /// 这样 ☰ 在非客户端区域之外、仍然能收到点击。
    /// 标题栏按钮的前景色按主题给（ExtendsContentIntoTitleBar 后系统不再自动管）。
    /// </summary>
    private void SetUpTitleBar()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDragRegion);

            var tb = AppWindow.TitleBar;
            var dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
            var fg = dark
                ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                : Windows.UI.Color.FromArgb(255, 0, 0, 0);
            var clear = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            tb.ButtonBackgroundColor = clear;
            tb.ButtonInactiveBackgroundColor = clear;
            tb.ButtonForegroundColor = fg;
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(140, fg.R, fg.G, fg.B);
            tb.ButtonHoverBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(32, 255, 255, 255)
                : Windows.UI.Color.FromArgb(24, 0, 0, 0);
            tb.ButtonPressedBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(56, 255, 255, 255)
                : Windows.UI.Color.FromArgb(48, 0, 0, 0);
            tb.ButtonHoverForegroundColor = fg;
            tb.ButtonPressedForegroundColor = fg;
            StartupLog.Step("自绘顶栏已启用");
        }
        catch (Exception ex)
        {
            // 某些环境（如远程桌面）可能不支持 —— 回退成系统顶栏即可，不致命
            StartupLog.Step("自绘顶栏失败，回退系统顶栏: " + ex.Message);
        }
    }

    /// <summary>
    /// 设置窗口背景材质（Mica）。
    ///
    /// 这是侧栏「一片纯黑」的根因：以前从来没设过 <c>SystemBackdrop</c>。
    /// WinUI 的 NavigationView 侧栏用的是**半透明**的层画刷（`NavigationViewDefaultPaneBackground`），
    /// 设计上就是要叠在 Mica 上 —— 没有材质时它只能叠在裸窗口背景上，于是整块发黑，
    /// 既不像浅色也不像深色，切换主题也不跟着变。
    ///
    /// 按官方推荐逐级回退：Mica → 亚克力 → 明确的不透明底色（老系统 / 远程桌面）。
    /// 最后那档不能省：不回退的话没材质的机器上又变成黑块。
    /// </summary>
    private void SetUpBackdrop()
    {
        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                StartupLog.Step("背景材质：Mica");
                return;
            }
            if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
                StartupLog.Step("背景材质：亚克力（本机不支持 Mica）");
                return;
            }
        }
        catch (Exception ex)
        {
            StartupLog.Step("设置背景材质失败，回退纯色: " + ex.Message);
        }

        // 没有任何材质可用：给根元素一个跟主题走的底色，至少不会是一块死黑
        RootGrid.Background =
            (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        StartupLog.Step("背景材质：不可用，已回退为纯色");
    }

    private void PaneToggle_Click(object sender, RoutedEventArgs e) => TogglePane();

    public void BringToFront()
    {
        try
        {
            Activate();
            if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized)
            {
                p.Restore();
            }
        }
        catch { /* 忽略 */ }
    }

    private void OnLogAppended(object? sender, string line) => _logPage?.Append(line);

    /// <summary>把侧栏与顶栏文案按当前界面语言刷新一遍。</summary>
    public void ApplyLanguage()
    {
        Title = Loc.T("app.title");
        AppTitleText.Text = Loc.T("app.title");

        // 必须按 Tag 映射，不能按 index —— 侧栏顺序与词条数组一旦不一致，
        // 「设置」和「工具」的文案就会互换（这个 bug 踩过一次）。
        foreach (var obj in Nav.MenuItems)
        {
            if (obj is NavigationViewItem item && item.Tag is string tag)
            {
                item.Content = Loc.T("nav." + tag);
            }
        }
        StatusText.Text = Loc.T("settings.backendHint");
        RetryButton.Content = Loc.T("app.retryBackend");

        // 侧栏开关是纯图标按钮：读屏不会自动取名，只会念一个空的「按钮」。
        // 提示与无障碍名都跟着语言走（快捷键提示拼在 ToolTip 上，读屏名只给用途）。
        var paneText = Loc.T("nav.togglePane");
        ToolTipService.SetToolTip(PaneToggleButton, $"{paneText}（Ctrl+B）");
        AutomationProperties.SetName(PaneToggleButton, paneText);
    }

    /// <summary>运行时切换主题：改根元素的 RequestedTheme，整个子树立即跟随。</summary>
    public void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        SetUpTitleBar();   // 标题栏按钮颜色是系统画的，得按新主题重设
    }

    /// <summary>
    /// 侧栏随窗口宽度自动收起。
    ///
    /// 之前用 VisualStateManager + AdaptiveTrigger 设 <c>Nav.PaneDisplayMode</c> 没生效
    /// （VSM 的 Setter 对 NavigationView 这个属性不生效），改成 SizeChanged 里直接设，确定可靠。
    /// 只改「呈现形态」与展开宽度，不碰 IsPaneOpen，免得顶掉用户的手动开合。
    /// </summary>
    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        var wantMode = w >= 900
            ? NavigationViewPaneDisplayMode.Left
            : NavigationViewPaneDisplayMode.LeftCompact;

        if (Nav.PaneDisplayMode != wantMode) Nav.PaneDisplayMode = wantMode;

        if (wantMode == NavigationViewPaneDisplayMode.Left)
        {
            var wantLen = w >= 1150 ? 190 : 150;
            if (Math.Abs(Nav.OpenPaneLength - wantLen) > 0.5) Nav.OpenPaneLength = wantLen;
        }
    }

    /// <summary>从命令行取 --page / -p 的值。</summary>
    private static string? ParsePageArg(string[]? args)
    {
        if (args is null) return null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--page" or "-p") return args[i + 1];
        }
        return null;
    }

    /// <summary>按 Tag 选中侧栏项；找不到返回 false。</summary>
    private bool SelectNavItem(string tag)
    {
        foreach (var item in Nav.MenuItems)
        {
            if (item is NavigationViewItem nvi && nvi.Tag as string == tag)
            {
                Nav.SelectedItem = nvi;
                return true;
            }
        }
        return false;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag) Navigate(tag);
    }

    private void Navigate(string tag)
    {
        switch (tag)
        {
            case "mods":
                _modsPage ??= new ModsPage(_services, this);
                ContentFrame.Content = _modsPage;
                _ = _modsPage.TryLoadAsync();
                break;
            case "settings":
                _settingsPage ??= new SettingsPage(_services, this);
                ContentFrame.Content = _settingsPage;
                _ = _settingsPage.RefreshAsync();
                break;
            case "tools":
                _toolsPage ??= new ToolsPage(_services, this);
                ContentFrame.Content = _toolsPage;
                _ = _toolsPage.RefreshAsync();
                break;

            case "logs":
                _logPage ??= new LogPage(_services, this);
                ContentFrame.Content = _logPage;
                break;
        }
    }

    // ---------------------------------------------------------------- 后端启动
    public async Task InitializeBackendAsync()
    {
        StatusRing.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;

        var ok = await _services.EnsureStartedAsync();
        // 注意：不要加 ConfigureAwait(false) —— 后面要直接改 XAML。
        // SynchronizationContext 已在 Program.Main 里设为 DispatcherQueueSynchronizationContext。

        StatusRing.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;

        if (ok)
        {
            // 列表载入由 ReadyChanged 统一触发，这里不重复调用
        }
        else
        {
            _ = ShowErrorAsync(Loc.T("app.backendNotReady"),
                _services.LastError ?? Loc.T("app.unknownError"),
                Loc.T("app.sidecarHint"));
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await InitializeBackendAsync();
    }

    // ---------------------------------------------------------------- 忙碌 / 错误
    private DispatcherQueueTimer? _busyTimer;

    /// <summary>
    /// 显示/隐藏忙碌遮罩。
    ///
    /// 与原版一致的俏皮标语：遮罩打开时洗牌，之后**每 5 秒**换一条（见 <see cref="BusyMessages"/>）。
    /// 后端并不上报真实进度，所以这里不伪造百分比 —— 只转圈 + 标语 + 一行说明。
    /// </summary>
    public void SetBusy(bool busy, string? title = null, string? detail = null)
    {
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            if (title is not null) BusyText.Text = title;
            BusyDetail.Text = detail ?? "";

            BusyMessages.Reshuffle();
            BusySlogan.Text = BusyMessages.Next();
            StartBusyTimer();
        }
        else
        {
            StopBusyTimer();
        }
    }

    private void StartBusyTimer()
    {
        _busyTimer ??= DispatcherQueue.CreateTimer();
        _busyTimer.Interval = TimeSpan.FromSeconds(5);
        _busyTimer.Tick -= BusyTimer_Tick;
        _busyTimer.Tick += BusyTimer_Tick;
        if (!_busyTimer.IsRunning) _busyTimer.Start();
    }

    private void StopBusyTimer()
    {
        if (_busyTimer is { IsRunning: true }) _busyTimer.Stop();
    }

    private void BusyTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (BusyOverlay.Visibility == Visibility.Visible)
            BusySlogan.Text = BusyMessages.Next();
    }

    public async Task ShowErrorAsync(string title, string message, string? hint = null)
    {
        try
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(hint))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = hint,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
            }
            await Dialogs.ShowScrollableAsync(
                Content.XamlRoot, title, panel, DialogSizing.Help, Loc.T("app.confirmButton"));
        }
        catch { /* 对话框失败不应再抛 */ }
    }
}
