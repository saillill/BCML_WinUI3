using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BCML.WinUI3.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using BCML.WinUI3.Core.Services;

namespace BCML.WinUI3.App.Pages;

/// <summary>
/// 首次运行的引导向导（对应原版 <c>FirstRun.jsx</c>）。
///
/// 原版是 4~5 页的轮播：欢迎 → 导入旧设置 → 配置基本设置 →（若有旧模组）迁移 → 完成。
/// 这里保留同样的分页与语义，但用 WinUI 的 <c>Stepper</c> 式布局重做：
/// 顶部步骤条、中间内容、底部上一页 / 下一页 / 完成。
///
/// 与设置页共用同一套<b>路径校验</b>（<c>Api.dir_exists</c>）与<b>保存接口</b>
/// （<c>save_settings</c>），所以这里不重复实现校验规则，只是把交互做成向导。
/// </summary>
public sealed partial class SetupWizardPage : Page
{
    private sealed record PathSpec(
        string Key, string Type, string LabelKey, string HintKey,
        bool WiiUOnly, bool SwitchOnly, bool Required);

    private static readonly PathSpec[] Paths =
    {
        new("game_dir_nx",  "game_dir_nx",  "settings.baseGame", "settings.baseGameHint", false, true,  true),
        new("dlc_dir_nx",   "dlc_dir_nx",   "settings.dlc",      "settings.dlcHint",      false, true,  false),
        // Wii U
        new("game_dir",     "game_dir",     "settings.baseGame", "settings.baseGameHint", true,  false, true),
        new("update_dir",   "update_dir",   "settings.update",   "settings.updateHint",   true,  false, true),
        new("dlc_dir",      "dlc_dir",      "settings.dlc",      "settings.dlcHint",      true,  false, false),
        new("cemu_dir",     "cemu_dir",     "settings.cemu",     "settings.cemuHint",     true,  false, false),
    };

    private enum StatusKind { Empty, Busy, Valid, Invalid, Warn }

    private sealed class PathRow
    {
        public required PathSpec Spec { get; init; }
        public required TextBox Box { get; init; }
        public required Border Card { get; init; }
        public required FontIcon Icon { get; init; }
        public bool Valid { get; set; }
    }

    private readonly AppServices _services;
    private readonly MainWindow _window;

    private Dictionary<string, JsonNode?> _settings = new();
    private readonly Dictionary<string, PathRow> _pathRows = new();

    private int _page;                 // 0..3
    private bool _settingsLoaded;
    private bool _willRead;
    private bool _completing;
    private bool _loading;

    public SetupWizardPage(AppServices services, MainWindow window, int startPage = 0)
    {
        _services = services;
        _window = window;
        _page = startPage;
        InitializeComponent();
        ApplyLanguage();
        Loc.LanguageChanged += OnLanguageChanged;
        _services.ReadyChanged += OnReadyChanged;
        _ = LoadSettingsAsync();
        Render();
    }

    // 用具名方法而不是 lambda 订阅：lambda 没法退订，而 Loc.LanguageChanged 与
    // AppServices.ReadyChanged 都是长生命周期的事件源，向导页每次打开都会新建一份，
    // 不退订就会一直挂在事件表上（页面已经被从可视化树里摘掉了，回调还在跑）。
    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyLanguage();

    private async void OnReadyChanged(object? sender, EventArgs e) => await LoadSettingsAsync();

    /// <summary>从界面移除时调用，退订所有长生命周期事件。</summary>
    public void Unload()
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        _services.ReadyChanged -= OnReadyChanged;
    }

    // ================================================================== 布局
    /// <summary>
    /// 把内容宿主 Grid 的宽度锁定为 ScrollViewer 的视口宽度。
    ///
    /// 为什么需要：<c>ScrollViewer</c> 默认给内容「无限宽度」，内层 Grid 会按
    /// 「最宽的那个子页面」来测量，结果比视口还宽 —— 子页面里的
    /// <c>HorizontalAlignment="Center"</c> 是相对这个超宽画布居中的，
    /// 内容就被推到视口右侧外面去了。
    /// XAML 里绑 <c>ViewportWidth</c> 在 WinUI 3 上不生效（不是 DependencyProperty），
    /// 所以只能在这里手动同步。
    /// </summary>
    private void WizardScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer sv && sv.ViewportWidth > 0)
        {
            PageHost.Width = sv.ViewportWidth;
        }
    }

    // ================================================================== 本地化
    private void ApplyLanguage()
    {
        WizardTitle.Text = Loc.T("wizard.title");

        StepLabel1.Text = Loc.T("wizard.step1");
        StepLabel2.Text = Loc.T("wizard.step2");
        StepLabel3.Text = Loc.T("wizard.step3");
        StepLabel4.Text = Loc.T("wizard.step4");

        Step1Title.Text = Loc.T("wizard.welcomeTitle");
        Step1Body.Text = Loc.T("wizard.welcomeBody");
        Step1Note.Text = Loc.T("wizard.welcomeNote");
        Step2Title.Text = Loc.T("wizard.importTitle");
        Step2Body.Text = Loc.T("wizard.importBody");
        Step2SharedNote.Text = Loc.T("wizard.sharedConfig");
        Step3Title.Text = Loc.T("wizard.configTitle");
        Step3Body.Text = Loc.T("wizard.configBody");
        Step4Title.Text = Loc.T("wizard.doneTitle");
        Step4Body.Text = Loc.T("wizard.doneBody");

        WiiuLabel.Text = Loc.T("settings.wiiuMode");
        WiiuHint.Text = Loc.T("settings.wiiuModeHint");
        HelpButton.Content = Loc.T("wizard.help");
        DonateButton.Content = Loc.T("wizard.donate");
        NewbieTip.Title = Loc.T("tools.newbieTip");
        NewbieTipBody.Text = Loc.T("tools.pendingHint");
        ConsentCheck.Content = Loc.T("wizard.consent");
        SkipButton.Content = Loc.T("wizard.skip");
        BackButton.Content = Loc.T("wizard.back");
        NextButton.Content = Loc.T("wizard.next");
        FinishButton.Content = Loc.T("wizard.finish");
        SaveButton.Content = Loc.T("wizard.saveAndNext");

        foreach (var (_, row) in _pathRows)
        {
            row.Box.Header = Loc.T(row.Spec.LabelKey) + (row.Spec.Required ? $"（{Loc.T("settings.required")}）" : "");
            row.Box.PlaceholderText = Loc.T(row.Spec.HintKey);
        }
        Render();
    }

    // ================================================================== 载入设置
    private async Task LoadSettingsAsync()
    {
        if (_services.Bcml is null || _settingsLoaded || _loading) return;
        _loading = true;
        try
        {
            var raw = await _services.Bcml.GetSettingsAsync();
            _settings = raw.ToDictionary(kv => kv.Key, kv => JsonNode.Parse(kv.Value.GetRawText()));
            _settingsLoaded = true;

            WiiuToggle.IsOn = GetBool("wiiu");
            BuildPathRows();
        }
        catch { /* 后端没起来就先让向导停在第一页 */ }
        finally
        {
            _loading = false;
            Render();
        }
    }

    private string GetString(string key) =>
        _settings.TryGetValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s ?? "" : "";

    private bool GetBool(string key) =>
        _settings.TryGetValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;

    // ================================================================== 路径校验（与设置页同规则）
    private static (Border Card, FontIcon Icon) MakeStatusCard()
    {
        var icon = new FontIcon
        {
            Glyph = "\uE738",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var card = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = icon,
        };
        return (card, icon);
    }

    private static void SetStatus((Border Card, FontIcon Icon) target, StatusKind kind, string tip)
    {
        var (glyph, brushKey) = kind switch
        {
            StatusKind.Valid => ("\uE73E", "SystemFillColorSuccessBrush"),
            StatusKind.Invalid => ("\uE711", "SystemFillColorCriticalBrush"),
            StatusKind.Busy => ("\uE895", "TextFillColorSecondaryBrush"),
            StatusKind.Warn => ("\uE7BA", "SystemFillColorCautionBrush"),
            _ => ("\uE738", "TextFillColorSecondaryBrush"),
        };
        var brush = (Brush)Application.Current.Resources[brushKey];
        target.Icon.Glyph = glyph;
        target.Icon.Foreground = brush;
        target.Card.BorderBrush = kind == StatusKind.Empty
            ? (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
            : brush;
        ToolTipService.SetToolTip(target.Card, tip);
    }

    private void BuildPathRows()
    {
        var wiiu = WiiuToggle.IsOn;
        PathList.Children.Clear();
        _pathRows.Clear();

        foreach (var spec in Paths)
        {
            if (spec.WiiUOnly && !wiiu) continue;
            if (spec.SwitchOnly && wiiu) continue;

            var box = new TextBox
            {
                Text = GetString(spec.Key),
                Header = Loc.T(spec.LabelKey) + (spec.Required ? $"（{Loc.T("settings.required")}）" : ""),
                PlaceholderText = Loc.T(spec.HintKey),
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = false,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            box.LostFocus += async (_, _) => await ValidatePathAsync(spec.Key);

            var browse = new Button { Content = Loc.T("common.browse") };
            browse.Click += async (_, _) =>
            {
                var picked = await PickFolderAsync();
                if (picked is null) return;
                box.Text = picked;
                await ValidatePathAsync(spec.Key);
            };
            var locate = new Button { Content = Loc.T("common.locate"), Margin = new Thickness(6, 0, 0, 0) };
            locate.Click += async (_, _) => await LocatePathAsync(spec.Key);

            var (card, icon) = MakeStatusCard();
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8, 0, 0, 2),
            };
            buttons.Children.Add(browse);
            buttons.Children.Add(locate);

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(box, 0);
            Grid.SetColumn(buttons, 1);
            Grid.SetColumn(card, 2);
            row.Children.Add(box);
            row.Children.Add(buttons);
            row.Children.Add(card);

            PathList.Children.Add(row);
            _pathRows[spec.Key] = new PathRow { Spec = spec, Box = box, Card = card, Icon = icon };
        }

        foreach (var key in _pathRows.Keys.ToList()) _ = ValidatePathAsync(key);
    }

    private async Task ValidatePathAsync(string key)
    {
        if (!_pathRows.TryGetValue(key, out var row)) return;
        var folder = row.Box.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(folder))
        {
            row.Valid = false;
            SetStatus((row.Card, row.Icon),
                row.Spec.Required ? StatusKind.Warn : StatusKind.Empty,
                (row.Spec.Required ? Loc.T("path.required") : Loc.T("common.optional"))
                    + "　" + Loc.T(row.Spec.HintKey));
            Render();
            return;
        }

        if (_services.Bcml is null)
        {
            SetStatus((row.Card, row.Icon), StatusKind.Warn, Loc.T("path.checking"));
            return;
        }

        SetStatus((row.Card, row.Icon), StatusKind.Busy, Loc.T("path.checking"));
        try
        {
            var ok = await _services.Bcml.DirExistsAsync(folder, row.Spec.Type);
            row.Valid = ok;
            AppServices.Diag($"向导路径校验 {row.Spec.Key} = {(ok ? "有效" : "无效")}：{folder}");
            SetStatus((row.Card, row.Icon),
                ok ? StatusKind.Valid : StatusKind.Invalid,
                (ok ? Loc.T("path.valid") : Loc.T("path.invalid")) + "：" + folder);
        }
        catch (Exception ex)
        {
            row.Valid = false;
            SetStatus((row.Card, row.Icon), StatusKind.Invalid, Loc.T("path.invalid") + "：" + ex.Message);
        }
        Render();
    }

    private async Task LocatePathAsync(string key)
    {
        if (!_pathRows.TryGetValue(key, out var row) || _services.Bcml is null) return;
        var folder = row.Box.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(folder)) return;
        try
        {
            var found = await _services.Bcml.DrillDirAsync(folder, row.Spec.Type);
            if (!string.IsNullOrWhiteSpace(found) && found != folder) row.Box.Text = found;
            await ValidatePathAsync(key);
        }
        catch { /* 定位失败不阻塞向导 */ }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_window));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    // ================================================================== 渲染
    /// <summary>必填路径是否全部校验通过 —— 决定「保存并继续」是否可用。</summary>
    private bool RequiredPathsValid()
    {
        var required = _pathRows.Values.Where(r => r.Spec.Required).ToList();
        if (required.Count == 0) return true;
        return required.All(r => r.Valid);
    }

    private void Render()
    {
        // 四页用可见性切换（原版是轮播，这里等价）
        Page1.Visibility = _page == 0 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = _page == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page3.Visibility = _page == 2 ? Visibility.Visible : Visibility.Collapsed;
        Page4.Visibility = _page == 3 ? Visibility.Visible : Visibility.Collapsed;

        for (var i = 0; i < StepBar.Children.Count; i++)
        {
            if (StepBar.Children[i] is not StackPanel sp || sp.Children.Count < 2) continue;
            var dot = sp.Children[0] as Border;
            var label = sp.Children[1] as TextBlock;
            if (dot is null || label is null) continue;
            var active = i == _page;
            var done = i < _page;
            dot.Background = (Brush)Application.Current.Resources[
                active || done ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDisabledBrush"];
            label.Foreground = (Brush)Application.Current.Resources[
                active ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush"];
            label.FontWeight = active
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }

        BackButton.IsEnabled = _page > 0;
        NextButton.Visibility = _page is 0 or 1 ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Visibility = _page == 2 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _page == 3 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = _page == 3 ? Visibility.Collapsed : Visibility.Visible;

        // 第 3 步（配置）要等必填路径都绿了才允许继续 —— 与原版 FirstRun 的门槛一致
        SaveButton.IsEnabled = RequiredPathsValid() && _services.IsReady && !_completing;
        FinishButton.IsEnabled = _page == 3 && _willRead;
        SaveHint.Text = !_services.IsReady
            ? Loc.T("wizard.needBackend")
            : RequiredPathsValid() ? "" : Loc.T("wizard.needPaths");
    }

    // ================================================================== 交互
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 0) _page--;
        Render();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page < 3) _page++;
        Render();
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Complete();

    private void WiiuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || !_settingsLoaded) return;
        BuildPathRows();
        Render();
    }

    private void ConsentCheck_Changed(object sender, RoutedEventArgs e)
    {
        _willRead = ConsentCheck.IsChecked == true;
        Render();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null || _completing) return;

        var payload = new Dictionary<string, object?>();
        foreach (var kv in _settings) payload[kv.Key] = kv.Value?.DeepClone();
        payload["wiiu"] = WiiuToggle.IsOn;
        foreach (var (key, row) in _pathRows) payload[key] = row.Box.Text?.Trim() ?? "";
        if (!WiiuToggle.IsOn) payload["no_cemu"] = true;

        _completing = true;
        _window.SetBusy(true, Loc.T("wizard.saving"), Loc.T("wizard.savingHint"));
        try
        {
            await _services.Bcml.SaveSettingsAsync(payload);
            _page = 3;
        }
        catch (Exception ex)
        {
            await _window.ShowErrorAsync(Loc.T("settings.saveFailed"), ex.Message);
        }
        finally
        {
            _window.SetBusy(false);
            _completing = false;
            Render();
        }
    }

    private void Finish_Click(object sender, RoutedEventArgs e) => Complete();

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/NiceneNerd/BCML");
    }

    private void Donate_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://www.patreon.com/nicenenerd");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch { /* 忽略 */ }
    }

    /// <summary>向导结束：标记已完成，之后不再自动弹出。</summary>
    private void Complete()
    {
        _services.MarkWizardDone();
        _window.CloseWizard();
    }
}
