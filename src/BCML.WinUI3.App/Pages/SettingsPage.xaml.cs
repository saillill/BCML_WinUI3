using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BCML.WinUI3.App.Services;
using BCML.WinUI3.Core.Sidecar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using BCML.WinUI3.Core.Services;

namespace BCML.WinUI3.App.Pages;

public sealed partial class SettingsPage : Page
{
    /// <summary>一条路径设置项。WiiUOnly / SwitchOnly 决定它在哪种模式下显示。</summary>
    private sealed record PathSpec(
        string Key, string Type, string LabelKey, string HintKey,
        bool WiiUOnly, bool SwitchOnly, bool Required);

    /// <summary>一个布尔选项。</summary>
    private sealed record OptionSpec(string Key, string LabelKey, string HintKey, bool SwitchForcedOn);

    private static readonly PathSpec[] Paths =
    {
        // Switch（默认）
        new("game_dir_nx",  "game_dir_nx",  "settings.baseGame", "settings.baseGameHint", false, true,  true),
        new("dlc_dir_nx",   "dlc_dir_nx",   "settings.dlc",      "settings.dlcHint",      false, true,  false),
        new("export_dir_nx","export_dir",   "settings.export",   "settings.exportHint",   false, true,  false),
        // Wii U
        new("game_dir",     "game_dir",     "settings.baseGame", "settings.baseGameHint", true,  false, true),
        new("update_dir",   "update_dir",   "settings.update",   "settings.updateHint",   true,  false, true),
        new("dlc_dir",      "dlc_dir",      "settings.dlc",      "settings.dlcHint",      true,  false, false),
        new("cemu_dir",     "cemu_dir",     "settings.cemu",     "settings.cemuHint",     true,  false, false),
        new("export_dir",   "export_dir",   "settings.export",   "settings.exportHint",   true,  false, false),
    };

    private static readonly OptionSpec[] Options =
    {
        new("no_cemu",        "settings.noCemu",        "settings.noCemuHint",        true),
        new("strip_gfx",      "settings.stripGfx",      "settings.stripGfxHint",      false),
        new("no_guess",       "settings.noGuess",       "settings.noGuessHint",       false),
        new("no_hardlinks",   "settings.noHardlinks",   "settings.noHardlinksHint",   false),
        new("force_7z",       "settings.force7z",       "settings.force7zHint",       false),
        new("suppress_update","settings.suppressUpdate","settings.suppressUpdateHint",false),
        new("changelog",      "settings.changelog",     "settings.changelogHint",     false),
    };

    /// <summary>
    /// 游戏文本语言包 —— 取值即 settings.lang，取自 dump 里实际存在的
    /// <c>Pack/Bootup_&lt;code&gt;.pack</c>（共 16 种，含简体 CNzh 与繁体 TWzh，
    /// 比 BCML 自己那份 LANGUAGE_MAP 多出 USpt 与 THth）。
    /// 中文变体按使用习惯排在最前。
    /// </summary>
    private static readonly (string Code, string Label)[] GameLanguages =
    {
        ("CNzh", "简体中文"), ("TWzh", "繁體中文"),
        ("USen", "US English"), ("EUen", "EU English"),
        ("JPja", "日本語"), ("KRko", "한국어"),
        ("USfr", "US French (Français)"), ("USes", "US Spanish (Español)"),
        ("USpt", "US Portuguese (Português)"),
        ("EUde", "EU German (Deutsch)"), ("EUes", "EU Spanish (Español)"),
        ("EUfr", "EU French (Français)"), ("EUit", "EU Italian (Italiano)"),
        ("EUnl", "EU Dutch (Nederlands)"), ("EUru", "EU Russian (Русский)"),
        ("THth", "ไทย (Thai)"),
    };

    /// <summary>校验结果卡片要画的几种状态。</summary>
    private enum StatusKind { Empty, Busy, Valid, Invalid, Warn }

    /// <summary>代码生成的一行路径设置项 + 它的校验状态卡片。</summary>
    private sealed class PathRow
    {
        public required PathSpec Spec { get; init; }
        public required TextBox Box { get; init; }
        public required Border Card { get; init; }
        public required FontIcon Icon { get; init; }
    }

    private readonly AppServices _services;
    private readonly MainWindow _window;

    /// <summary>后端的完整 settings 字典（保存时原样回传，只改我们动过的键）。</summary>
    private Dictionary<string, JsonNode?> _settings = new();

    /// <summary>代码生成的路径行。</summary>
    private readonly Dictionary<string, PathRow> _pathRows = new();

    /// <summary>代码生成的选项行。</summary>
    private readonly Dictionary<string, ToggleSwitch> _optionSwitches = new();

    private bool _loading;

    /// <summary>settings.lang 的原始值（可能是列表外的取值，保存时要原样写回）。</summary>
    private string _gameLangRaw = "";

    public SettingsPage(AppServices services, MainWindow window)
    {
        _services = services;
        _window = window;
        InitializeComponent();
        PythonBox.Header = Loc.T("settings.pythonLabel");
        PythonBox.PlaceholderText = Loc.T("settings.pythonHint");
        PythonBox.LostFocus += async (_, _) => await ValidatePythonAsync();
        SetStatus((PythonStatusCard, PythonStatusIcon), StatusKind.Empty, Loc.T("settings.pythonHint"));
        _services.ReadyChanged += async (_, _) => await RefreshAsync();
        Loc.LanguageChanged += (_, _) => ApplyLanguage();
    }

    // ================================================================== 本地化
    private void ApplyLanguage()
    {
        TitleText.Text = Loc.T("settings.title");
        ReloadButton.Content = Loc.T("common.reload");
        SaveButton.Content = Loc.T("common.save");

        SecGame.Text = Loc.T("settings.gameFolders");
        WiiuHint.Text = Loc.T("settings.wiiuModeHint");
        SecOptions.Text = Loc.T("settings.options");
        SecGameLang.Text = Loc.T("settings.gameLang");
        GameLangHint.Text = Loc.T("settings.gameLangHint");
        GameLangNote.Text = Loc.T("settings.gameLangNote");
        SecAppearance.Text = Loc.T("settings.appearance");
        UiLangLabel.Text = Loc.T("settings.uiLanguage");
        UiLangHint.Text = Loc.T("settings.uiLanguageHint");
        ThemeLabel.Text = Loc.T("settings.theme");
        ThemeHint.Text = Loc.T("settings.themeHint");
        SecData.Text = Loc.T("settings.dataDir");
        DataDirHint.Text = Loc.T("settings.dataDirHint");
        SecShortcuts.Text = Loc.T("settings.shortcuts");
        ShortcutAppLabel.Text = Loc.T("settings.shortcutsApp");
        ShortcutAppHint.Text = Loc.T("settings.shortcutsAppHint");
        ShortcutAppDesktopButton.Content = Loc.T("settings.shortcutAppDesktop");
        ShortcutAppStartButton.Content = Loc.T("settings.shortcutStartMenu");
        ShortcutCliLabel.Text = Loc.T("settings.shortcutsCli");
        ShortcutHint.Text = Loc.T("settings.shortcutsHint");
        ShortcutDesktopButton.Content = Loc.T("settings.shortcutDesktop");
        ShortcutStartButton.Content = Loc.T("settings.shortcutStartMenu");
        SecBackend.Text = Loc.T("settings.backend");
        BackendHint.Text = Loc.T("settings.backendHint");
        BrowsePythonButton.Content = Loc.T("common.browse");
        RestartBackendButton.Content = Loc.T("settings.restartBackend");
        PythonBox.Header = Loc.T("settings.pythonLabel");
        PythonBox.PlaceholderText = Loc.T("settings.pythonHint");

        foreach (var (_, row) in _pathRows)
        {
            row.Box.Header = Loc.T(row.Spec.LabelKey) + (row.Spec.Required ? $"（{Loc.T("settings.required")}）" : "");
            row.Box.PlaceholderText = Loc.T(row.Spec.HintKey);
        }

        // 无障碍名：ComboBox 不会像 TextBox 那样从旁边的标签自动取名字，
        // 不显式设置的话屏幕阅读器只会读一个孤零零的「组合框」。
        AutomationProperties.SetName(GameLangBox, Loc.T("settings.gameLang"));
        AutomationProperties.SetName(UiLangBox, Loc.T("settings.uiLanguage"));
        AutomationProperties.SetName(ThemeBox, Loc.T("settings.theme"));
    }

    // ================================================================== 载入
    public async Task RefreshAsync()
    {
        if (_loading) return;

        // Python 路径直接放进输入框（用户要求）：优先用用户手动指定的，
        // 没有就填后端正跑着的解释器，右侧卡片同步给出「能否 import bcml」的结论。
        PythonBox.Text = !string.IsNullOrWhiteSpace(_services.PythonPathOverride)
            ? _services.PythonPathOverride!
            : (_services.Info?.Executable ?? "");

        if (!_services.IsReady)
        {
            BackendStatus.Text = _services.IsStarting
                ? Loc.T("settings.startingBackend")
                : Loc.T("settings.backendNotReadyShort") + (_services.LastError ?? "");
            _ = ValidatePythonAsync();
            return;
        }

        _loading = true;
        try
        {
            var info = _services.Info!;
            // pid / 版本这类是数据，不翻译；括号用半角，避免和中文标点混在一起
            var status = $"BCML {info.BcmlVersion}  ·  Python {info.Python} (pid {info.Pid})\n{info.Executable}";
            if (_services.RuntimeIntegrityNote is { } note)
            {
                // 把随包运行时的完整性结论摆出来 —— 出问题时这是第一眼该看到的信息
                status += $"\n{Loc.T("settings.runtimeIntegrity")}: {note}";
            }
            BackendStatus.Text = status;

            var raw = await _services.Bcml!.GetSettingsAsync();
            _settings = raw.ToDictionary(kv => kv.Key, kv => JsonNode.Parse(kv.Value.GetRawText()));

            WiiuToggle.IsOn = GetBool("wiiu");
            BuildPathRows();
            BuildOptionRows();
            BuildDataDirRows();
            BuildCombos();
            ApplyLanguage();
            _ = ValidatePythonAsync();
        }
        catch (Exception ex)
        {
            NotifyError(Loc.T("settings.saveFailed"), ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private string GetString(string key) =>
        _settings.TryGetValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s)
            ? s ?? "" : "";

    private bool GetBool(string key) =>
        _settings.TryGetValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;

    // ================================================================== 校验状态卡片
    /// <summary>
    /// 造一个 34×34 的圆角卡片，里面放一枚图标 —— 路径有效性的统一视觉语言。
    /// 之前是一串「✓ 有效」文字，太丑也不好对齐。
    /// </summary>
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

    /// <summary>把校验结果画到卡片上（勾 / 叉 / 转圈 / 横杠）+ 更新悬浮提示。</summary>
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

    // ================================================================== 路径行
    private void BuildPathRows()
    {
        // 重建之前先把当前输入收进 _settings，否则切模式会丢掉用户还没保存的改动
        CommitPathEditsToSettings();

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

        // 初次进来给每个已填的路径跑一次校验。
        // 空路径也要走一遍 —— 校验函数会把「必填 / 可选」的状态画到右侧小卡片上。
        foreach (var key in _pathRows.Keys.ToList())
        {
            _ = ValidatePathAsync(key);
        }
    }

    /// <summary>
    /// 把用户**还没保存**的路径输入回写进 <c>_settings</c>。
    ///
    /// <see cref="BuildPathRows"/> 会按 <c>_settings</c> 里的值重建整组输入框，所以切
    /// Wii U / Switch 模式时如果不先回写，用户刚敲进去还没点保存的路径就会被清掉 ——
    /// 表现是"切一下模式，填的东西没了"。
    /// </summary>
    private void CommitPathEditsToSettings()
    {
        foreach (var (key, row) in _pathRows)
        {
            var text = row.Box.Text?.Trim() ?? "";
            _settings[key] = JsonValue.Create(text);
        }
    }

    private async Task ValidatePathAsync(string key)
    {
        if (!_pathRows.TryGetValue(key, out var row)) return;
        var folder = row.Box.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(folder))
        {
            SetStatus((row.Card, row.Icon),
                row.Spec.Required ? StatusKind.Warn : StatusKind.Empty,
                (row.Spec.Required ? Loc.T("path.required") : Loc.T("common.optional"))
                    + "　" + Loc.T(row.Spec.HintKey));
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
            AppServices.Diag($"路径校验 {row.Spec.Key} = {(ok ? "有效" : "无效")}：{folder}");
            SetStatus((row.Card, row.Icon),
                ok ? StatusKind.Valid : StatusKind.Invalid,
                (ok ? Loc.T("path.valid") : Loc.T("path.invalid")) + "：" + folder);
        }
        catch (Exception ex)
        {
            AppServices.Diag($"路径校验 {row.Spec.Key} 异常：{ex.Message}");
            SetStatus((row.Card, row.Icon), StatusKind.Invalid, Loc.T("path.invalid") + "：" + ex.Message);
        }
    }

    /// <summary>校验 Python 解释器（能否 import bcml / oead），结果画在输入框右侧的卡片上。</summary>
    private async Task ValidatePythonAsync()
    {
        var path = PythonBox.Text?.Trim() ?? "";
        var target = (PythonStatusCard, PythonStatusIcon);

        if (string.IsNullOrEmpty(path))
        {
            SetStatus(target, StatusKind.Warn, Loc.T("settings.pythonHint"));
            return;
        }
        if (_services.Bcml is null)
        {
            SetStatus(target, StatusKind.Warn, Loc.T("path.checking"));
            return;
        }

        SetStatus(target, StatusKind.Busy, Loc.T("path.checking"));
        try
        {
            var (ok, reason) = await _services.Bcml.ProbePythonAsync(path);
            AppServices.Diag($"Python 校验 = {(ok ? "有效" : "无效")}：{path}{(ok ? "" : " —— " + reason)}");
            SetStatus(target, ok ? StatusKind.Valid : StatusKind.Invalid,
                (ok ? Loc.T("path.valid") : Loc.T("path.invalid")) + "：" + (ok ? path : reason));
        }
        catch (Exception ex)
        {
            AppServices.Diag($"Python 校验异常：{ex.Message}");
            SetStatus(target, StatusKind.Invalid, Loc.T("path.invalid") + "：" + ex.Message);
        }
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
        catch (Exception ex)
        {
            // 注意这里是 Loc.T(row.Spec.LabelKey) 而不是直接塞 LabelKey ——
            // 塞键名的话用户会看到「路径无效：settings.updateDir」这种半成品文案。
            NotifyError(Loc.T("settings.notValid", Loc.T(row.Spec.LabelKey)), ex.Message);
        }
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

    // ================================================================== 选项行
    private void BuildOptionRows()
    {
        OptionList.Children.Clear();
        _optionSwitches.Clear();

        foreach (var spec in Options)
        {
            var toggle = new ToggleSwitch
            {
                IsOn = GetBool(spec.Key),
                OffContent = "",
                OnContent = "",
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Top,
            };
            if (spec.SwitchForcedOn && !WiiuToggle.IsOn)
            {
                toggle.IsOn = true;
                toggle.IsEnabled = false;
            }

            var label = new TextBlock
            {
                Text = Loc.T(spec.LabelKey),
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
            };
            var hint = new TextBlock
            {
                Text = Loc.T(spec.HintKey),
                Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"],
                TextWrapping = TextWrapping.Wrap,
            };

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(label);
            text.Children.Add(hint);

            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(toggle, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(toggle);
            row.Children.Add(text);

            OptionList.Children.Add(row);
            _optionSwitches[spec.Key] = toggle;
        }
    }

    // ================================================================== 数据目录
    private void BuildDataDirRows()
    {
        DataDirList.Children.Clear();
        var info = _services.Info;
        if (info is null) return;

        void Add(string label, string? value)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var l = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"],
            };
            Grid.SetColumn(l, 0);

            var t = new TextBlock
            {
                Text = value ?? "—",
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            Grid.SetColumn(t, 1);

            var open = new Button { Content = Loc.T("common.open") };
            Grid.SetColumn(open, 2);
            open.IsEnabled = !string.IsNullOrWhiteSpace(value);
            open.Click += (_, _) => OpenInExplorer(value);

            row.Children.Add(l);
            row.Children.Add(t);
            row.Children.Add(open);
            DataDirList.Children.Add(row);
        }

        Add(Loc.T("settings.dataDir"), info.StoreDir);
        Add(Loc.T("nav.mods"), info.ModsDir);
        Add(Loc.T("settings.export"), info.MergedDir);
        Add("Profiles", info.ProfilesDir);
    }

    private static void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch { /* 忽略 */ }
    }

    // ================================================================== 下拉框
    private void BuildCombos()
    {
        GameLangBox.Items.Clear();
        foreach (var (_, label) in GameLanguages) GameLangBox.Items.Add(label);
        var lang = GetString("lang");
        var idx = Array.FindIndex(GameLanguages, g => g.Code == lang);
        if (idx < 0 && !string.IsNullOrWhiteSpace(lang))
        {
            // 设置里是列表外的取值（老配置或游戏本就没有的语言包）——原样保留一项，
            // 免得用户一进来看到中文却保存成另一个语言。
            GameLangBox.Items.Add($"{lang}（当前值，不在游戏语言包列表内）");
            idx = GameLangBox.Items.Count - 1;
        }
        GameLangBox.SelectedIndex = idx >= 0 ? idx : 0;
        _gameLangRaw = lang;

        UiLangBox.Items.Clear();
        foreach (var (_, label) in Loc.Available) UiLangBox.Items.Add(label);
        UiLangBox.SelectedIndex = Array.FindIndex(Loc.Available, l => l.Code == Loc.Language) is var i && i >= 0 ? i : 0;

        ThemeBox.Items.Clear();
        ThemeBox.Items.Add(Loc.T("theme.system"));
        ThemeBox.Items.Add(Loc.T("theme.light"));
        ThemeBox.Items.Add(Loc.T("theme.dark"));
        ThemeBox.SelectedIndex = _services.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
    }

    /// <summary>取当前下拉框对应的游戏语言代码（含「列表外原值」那一条）。</summary>
    private string SelectedGameLanguage()
    {
        var i = GameLangBox.SelectedIndex;
        if (i < 0) return _gameLangRaw;
        return i < GameLanguages.Length ? GameLanguages[i].Code : _gameLangRaw;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedIndex < 0) return;
        var theme = ThemeBox.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        _services.SetTheme(theme);
        _window.ApplyTheme(theme);
    }

    private void UiLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || UiLangBox.SelectedIndex < 0) return;
        var code = Loc.Available[UiLangBox.SelectedIndex].Code;
        if (code == Loc.Language) return;
        _services.SetUiLanguage(code);

        // 语言切换需要重启才彻底生效（XAML 里的静态文案不是绑定）
        _ = _window.ShowErrorAsync(Loc.T("settings.uiLanguage"), Loc.T("settings.restartNeeded"));
    }

    // ================================================================== WiiU 切换
    private void WiiuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        BuildPathRows();
        BuildOptionRows();
    }

    // ================================================================== 保存
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null) return;

        var payload = new Dictionary<string, object?>();
        foreach (var kv in _settings) payload[kv.Key] = kv.Value?.DeepClone();

        payload["wiiu"] = WiiuToggle.IsOn;
        payload["lang"] = SelectedGameLanguage();

        foreach (var (key, row) in _pathRows) payload[key] = row.Box.Text?.Trim() ?? "";
        foreach (var (key, toggle) in _optionSwitches) payload[key] = toggle.IsOn;

        // Switch 模式下 no_cemu 必须为 true，否则 BCML 会去找 Cemu
        if (!WiiuToggle.IsOn) payload["no_cemu"] = true;

        _window.SetBusy(true, Loc.T("common.save"), null);
        try
        {
            await _services.Bcml.SaveSettingsAsync(payload);
            NotifyOk(Loc.T("settings.saved"), Loc.T("settings.savedHint"));
        }
        catch (Exception ex)
        {
            NotifyError(Loc.T("settings.saveFailed"), ex.Message);
        }
        finally
        {
            _window.SetBusy(false);
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ================================================================== 快捷方式
    private async void ShortcutDesktop_Click(object sender, RoutedEventArgs e) => await MakeShortcutAsync(true);

    private async void ShortcutStart_Click(object sender, RoutedEventArgs e) => await MakeShortcutAsync(false);

    private async void ShortcutAppDesktop_Click(object sender, RoutedEventArgs e) => await MakeAppShortcutAsync(true);

    private async void ShortcutAppStart_Click(object sender, RoutedEventArgs e) => await MakeAppShortcutAsync(false);

    /// <summary>原版接口：给「命令行版 BCML」建快捷方式。</summary>
    private async Task MakeShortcutAsync(bool desktop)
    {
        if (_services.Bcml is null)
        {
            NotifyError(Loc.T("settings.shortcuts"), Loc.T("settings.backendNotReady"));
            return;
        }
        try
        {
            await _services.Bcml.MakeShortcutAsync(desktop);
            NotifyOk(Loc.T("settings.shortcuts"),
                (desktop ? Loc.T("settings.shortcutDesktop") : Loc.T("settings.shortcutStartMenu")) + " ✓");
        }
        catch (Exception ex)
        {
            NotifyError(Loc.T("settings.shortcuts"), ex.Message);
        }
    }

    /// <summary>给**本应用**（BCML-WinUI3.exe）建快捷方式。</summary>
    private async Task MakeAppShortcutAsync(bool desktop)
    {
        if (_services.Bcml is null)
        {
            NotifyError(Loc.T("settings.shortcuts"), Loc.T("settings.backendNotReady"));
            return;
        }

        // Environment.ProcessPath = 当前进程的 exe 路径，比拼 AppContext.BaseDirectory 稳
        // （后者给的是目录，还得自己补 AssemblyName）。
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            NotifyError(Loc.T("settings.shortcuts"), Loc.T("settings.shortcutNoExe"));
            return;
        }

        try
        {
            var (path, ok) = await _services.Bcml.MakeAppShortcutAsync(exe, desktop);
            var where = desktop ? Loc.T("settings.shortcutDesktop") : Loc.T("settings.shortcutStartMenu");
            if (ok)
            {
                NotifyOk(Loc.T("settings.shortcuts"), where + " ✓");
                AppServices.Diag($"已创建本应用快捷方式：{path}");
            }
            else
            {
                NotifyError(Loc.T("settings.shortcuts"), Loc.T("settings.shortcutFailed") + "：" + path);
            }
        }
        catch (Exception ex)
        {
            NotifyError(Loc.T("settings.shortcuts"), ex.Message);
        }
    }

    // ================================================================== 后端
    private async void BrowsePython_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_window));
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        PythonBox.Text = file.Path;
        await ValidatePythonAsync();
    }

    private async void RestartBackend_Click(object sender, RoutedEventArgs e)
    {
        _services.SavePythonPath(PythonBox.Text?.Trim());
        _window.SetBusy(true, Loc.T("settings.restartBackend"), null);
        try
        {
            await _services.RestartAsync();
        }
        finally
        {
            _window.SetBusy(false);
        }
        await RefreshAsync();
    }

    // ================================================================== 提示
    private void NotifyOk(string title, string message)
    {
        Notice.IsOpen = true;
        Notice.Severity = InfoBarSeverity.Success;
        Notice.Title = title;
        Notice.Message = message;
    }

    private void NotifyError(string title, string message)
    {
        Notice.IsOpen = true;
        Notice.Severity = InfoBarSeverity.Error;
        Notice.Title = title;
        Notice.Message = message;
    }
}
