using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BCML.WinUI3.App.Services;
using BCML.WinUI3.Core.Models;
using BCML.WinUI3.Core.Sidecar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using BCML.WinUI3.Core.Services;

namespace BCML.WinUI3.App.Pages;

/// <summary>
/// 工具页 —— 对应原版 DevTools 的全部工具：
/// BNP Creator / Convert BNP Platform / BNP to Standalone / Upgrade Old BNP /
/// Generate RSTB / Compare Mods / View Merged Files。
///
/// 设计要点：原版这些方法内部都用 <c>self.window.create_file_dialog</c> 选路径，
/// 在无界面 sidecar 里不可用。所以统一采用「**界面选路径 → 后端只干活**」的模式，
/// 每个工具都对应 sidecar 里的一个 sys.* 无界面接口。
/// </summary>
public sealed partial class ToolsPage : Page
{
    private readonly AppServices _services;
    private readonly MainWindow _window;

    private string? _sourceBnp;
    private string? _creatorFolder;
    private string? _upgradeBnp;
    private string? _standaloneBnp;
    private readonly ObservableCollection<BnpOptionDraft> _options = new();
    private List<ModInfo> _mods = new();

    public ToolsPage(AppServices services, MainWindow window)
    {
        _services = services;
        _window = window;
        InitializeComponent();
        OptionList.ItemsSource = _options;
        Loc.LanguageChanged += (_, _) => ApplyLanguage();
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        TitleText.Text = Loc.T("nav.tools");

        // 平台转换
        SecConvert.Text = Loc.T("tools.convert");
        ConvertHint.Text = Loc.T("tools.convertHint");
        PickSourceButton.Content = Loc.T("tools.pickBnp");
        ConvertButton.Content = Loc.T("tools.startConvert");
        WarnSkip.Content = Loc.T("tools.warnSkip");
        WarnFail.Content = Loc.T("tools.warnFail");
        SourceBox.PlaceholderText = Loc.T("tools.noFilePicked");

        // BNP Creator
        SecCreator.Text = Loc.T("tools.creator");
        CreatorHint.Text = Loc.T("tools.creatorHint");
        CreatorFolderLabel.Text = Loc.T("tools.creatorFolder");
        CreatorPickFolderButton.Content = Loc.T("tools.pickFolder");
        CreatorFolderBox.PlaceholderText = Loc.T("tools.noFolderPicked");
        MetaLabel.Text = Loc.T("tools.meta");
        NameLabel.Text = Loc.T("tools.name");
        VersionLabel.Text = Loc.T("tools.version");
        AuthorLabel.Text = Loc.T("tools.author");
        AuthorNote.Text = Loc.T("tools.authorNote");
        IconLabel.Text = Loc.T("tools.icon");
        CreatorPickIconButton.Content = Loc.T("tools.pickIcon");
        DependsLabel.Text = Loc.T("tools.depends");
        OptionsSectionLabel.Text = Loc.T("tools.optionsSection");
        OptionsHint.Text = Loc.T("tools.optionsHint");
        AddOptionButton.Content = Loc.T("tools.addOption");
        CreateButton.Content = Loc.T("tools.create");

        // 升级旧 BNP
        SecUpgrade.Text = Loc.T("tools.upgrade");
        UpgradeHint.Text = Loc.T("tools.upgradeHint");
        UpgradePickButton.Content = Loc.T("tools.pickBnp");
        UpgradeBox.PlaceholderText = Loc.T("tools.noFilePicked");
        UpgradeButton.Content = Loc.T("tools.doUpgrade");

        // 独立模组
        SecStandalone.Text = Loc.T("tools.standalone");
        StandaloneHint.Text = Loc.T("tools.standaloneHint");
        StandalonePickButton.Content = Loc.T("tools.pickBnp");
        StandaloneBox.PlaceholderText = Loc.T("tools.noFilePicked");
        StandaloneButton.Content = Loc.T("tools.doStandalone");

        // RSTB
        SecRstb.Text = Loc.T("tools.rstb");
        RstbHint.Text = Loc.T("tools.rstbHint");
        RstbModLabel.Text = Loc.T("tools.rstbMod");
        RstbButton.Content = Loc.T("tools.doRstb");

        SecRepair.Text = Loc.T("tools.repair");
        RepairHint.Text = Loc.T("tools.repairHint");
        RepairButton.Content = Loc.T("tools.doRepair");

        // 比对
        SecCompare.Text = Loc.T("tools.compare");
        CompareHint.Text = Loc.T("tools.compareHint");
        CompareLeftLabel.Text = Loc.T("tools.compareLeft");
        CompareRightLabel.Text = Loc.T("tools.compareRight");
        CompareButton.Content = Loc.T("tools.doCompare");

        // 目录
        SecOutput.Text = Loc.T("tools.output");
        OpenMasterButton.Content = Loc.T("tools.openMaster");
        OpenStoreButton.Content = Loc.T("tools.openStore");
        RemergeButton.Content = Loc.T("common.remerge");

        SecPending.Text = Loc.T("tools.pending");
        PendingText.Text = Loc.T("tools.pendingList");

        // 无障碍名：下拉框不会自动从旁边的标签取名，不设的话读屏只会念「组合框」。
        AutomationProperties.SetName(RstbModBox, Loc.T("tools.rstb"));
        AutomationProperties.SetName(CompareLeftBox, Loc.T("tools.compareLeft"));
        AutomationProperties.SetName(CompareRightBox, Loc.T("tools.compareRight"));

        RefreshModPickers();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var master = await _services.Bcml?.GetMasterModpackAsync()!;
            MasterPath.Text = master ?? "";
        }
        catch { /* 非关键信息 */ }

        try
        {
            if (_services.Bcml is not null)
            {
                _mods = (await _services.Bcml.GetModsAsync(true)).ToList();
                RefreshModPickers();
            }
        }
        catch { /* 后端未就绪时留空 */ }
    }

    /// <summary>把已安装模组灌进 RSTB / 比对三处下拉框（保持当前选中项）。</summary>
    private void RefreshModPickers()
    {
        Fill(RstbModBox);
        Fill(CompareLeftBox);
        Fill(CompareRightBox);

        void Fill(ComboBox box)
        {
            var keep = (box.SelectedItem as ModItem)?.Path;
            var items = _mods.Select(m => new ModItem(m.Name, m.Path)).ToList();
            box.ItemsSource = items;
            box.DisplayMemberPath = nameof(ModItem.Label);
            box.SelectedItem = items.FirstOrDefault(i => i.Path == keep) ?? items.FirstOrDefault();
        }
    }

    private sealed record ModItem(string Name, string Path)
    {
        public string Label => Name;
    }

    // ================================================================== 平台转换
    private async void PickSource_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickBnpAsync();
        if (file is null) return;
        _sourceBnp = file;
        SourceBox.Text = file;
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null) return;
        if (string.IsNullOrWhiteSpace(_sourceBnp))
        {
            Notify(InfoBarSeverity.Warning, Loc.T("tools.convert"), Loc.T("tools.needSource"));
            return;
        }

        var toWiiU = TargetWiiU.IsChecked == true;
        var warnOnly = WarnSkip.IsChecked == true;
        var outFile = await PickSaveAsync(
            Loc.T("tools.convert"), System.IO.Path.GetFileNameWithoutExtension(_sourceBnp)
                                 + (toWiiU ? "_wiiu" : "_switch"), ".bnp");
        if (outFile is null) return;

        _window.SetBusy(true, Loc.T("tools.converting"), Loc.T("tools.convertingHint"));
        try
        {
            var result = await _services.Bcml.ConvertBnpAsync(_sourceBnp, outFile, toWiiU, warnOnly);
            var message = $"{result.Output}\n{result.Size / 1024.0 / 1024.0:0.0} MB";
            if (result.Warnings.Count > 0)
            {
                message += "\n\n" + Loc.T("tools.warnCount", result.Warnings.Count) + "\n"
                         + string.Join("\n", result.Warnings.Take(8));
            }
            Notify(InfoBarSeverity.Success, Loc.T("tools.convertDone"), message);
        }
        catch (Exception ex)
        {
            Notify(InfoBarSeverity.Error, Loc.T("tools.convertFailed"), Detail(ex));
        }
        finally { _window.SetBusy(false); }
    }

    // ================================================================== BNP Creator
    private async void CreatorPickFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_window));
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _creatorFolder = folder.Path;
        CreatorFolderBox.Text = folder.Path;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameBox.Text = System.IO.Path.GetFileName(folder.Path.TrimEnd('\\', '/'));
        }
    }

    private async void CreatorPickIcon_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_window));
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" })
            picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        IconBox.Text = file.Path;
    }

    private void AddOption_Click(object sender, RoutedEventArgs e) =>
        _options.Add(new BnpOptionDraft { Multi = true, Raw = "" });

    private void RemoveOption_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BnpOptionDraft draft) _options.Remove(draft);
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null) return;
        if (string.IsNullOrWhiteSpace(_creatorFolder))
        {
            Notify(InfoBarSeverity.Warning, Loc.T("tools.creator"), Loc.T("tools.needFolder"));
            return;
        }
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            Notify(InfoBarSeverity.Warning, Loc.T("tools.creator"), Loc.T("tools.needName"));
            return;
        }

        var outFile = await PickSaveAsync(Loc.T("tools.creator"), name, ".bnp");
        if (outFile is null) return;

        var meta = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["version"] = VersionBox.Text.Trim() is { Length: > 0 } v ? v : "1.0.0",
            ["desc"] = "",
            ["url"] = AuthorBox.Text.Trim(),
            ["image"] = IconBox.Text.Trim(),
            ["depends"] = DependsBox.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList(),
        };

        // 选项定义 → BCML 的 options.groups + selects（默认勾选第一个候选值）
        var groups = new Dictionary<string, object?>();
        var selects = new Dictionary<string, object?>();
        foreach (var draft in _options)
        {
            var (group, values) = draft.Parse();
            if (group.Length == 0 || values.Count == 0) continue;
            groups[group] = new { options = values.ToArray(), multi = draft.Multi };
            selects[group] = draft.Multi
                ? new List<string>()
                : new List<string> { values[0] };
        }

        _window.SetBusy(true, Loc.T("tools.creating"), Loc.T("tools.creatingHint"));
        try
        {
            var result = await _services.Bcml.CreateBnpAsync(
                _creatorFolder, outFile, meta, selects,
                groups.Count > 0
                    ? new Dictionary<string, object?> { ["options"] = new { groups }, ["disable"] = Array.Empty<string>() }
                    : null);
            var path = result.TryGetProperty("output", out var o) ? o.GetString() : outFile;
            var size = result.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
            Notify(InfoBarSeverity.Success, Loc.T("tools.createDone"),
                $"{path}\n{size / 1024.0 / 1024.0:0.0} MB");
        }
        catch (Exception ex)
        {
            Notify(InfoBarSeverity.Error, Loc.T("tools.createFailed"), Detail(ex));
        }
        finally { _window.SetBusy(false); }
    }

    // ================================================================== 升级旧 BNP
    private async void UpgradePick_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickBnpAsync();
        if (file is null) return;
        _upgradeBnp = file;
        UpgradeBox.Text = file;
    }

    private async void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null) return;
        if (string.IsNullOrWhiteSpace(_upgradeBnp))
        {
            Notify(InfoBarSeverity.Warning, Loc.T("tools.upgrade"), Loc.T("tools.needSource"));
            return;
        }

        var outFile = await PickSaveAsync(
            Loc.T("tools.upgrade"),
            System.IO.Path.GetFileNameWithoutExtension(_upgradeBnp) + "_upgraded", ".bnp");
        if (outFile is null) return;

        _window.SetBusy(true, Loc.T("tools.upgrading"), Loc.T("tools.creatingHint"));
        try
        {
            var result = await _services.Bcml.UpgradeBnpAsync(_upgradeBnp, outFile);
            var path = result.TryGetProperty("output", out var o) ? o.GetString() : outFile;
            var size = result.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
            var upgraded = result.TryGetProperty("upgraded", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.True;
            Notify(InfoBarSeverity.Success, Loc.T("tools.upgradeDone"),
                $"{path}\n{size / 1024.0 / 1024.0:0.0} MB\n"
                + (upgraded ? Loc.T("tools.upgradedYes") : Loc.T("tools.upgradedNo")));
        }
        catch (Exception ex)
        {
            Notify(InfoBarSeverity.Error, Loc.T("tools.upgrade"), Detail(ex));
        }
        finally { _window.SetBusy(false); }
    }

    // ================================================================== BNP → 独立模组
    private async void StandalonePick_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickBnpAsync();
        if (file is null) return;
        _standaloneBnp = file;
        StandaloneBox.Text = file;
    }

    private async void Standalone_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null) return;
        if (string.IsNullOrWhiteSpace(_standaloneBnp))
        {
            Notify(InfoBarSeverity.Warning, Loc.T("tools.standalone"), Loc.T("tools.needBnp"));
            return;
        }

        var outFile = await PickSaveAsync(
            Loc.T("tools.standalone"),
            System.IO.Path.GetFileNameWithoutExtension(_standaloneBnp), ".zip");
        if (outFile is null) return;

        _window.SetBusy(true, Loc.T("tools.standaloneRunning"), Loc.T("tools.standaloneRunningHint"));
        try
        {
            var result = await _services.Bcml.BnpToStandaloneAsync(_standaloneBnp, outFile);
            var path = result.TryGetProperty("output", out var o) ? o.GetString() : outFile;
            var size = result.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
            Notify(InfoBarSeverity.Success, Loc.T("tools.standaloneDone"),
                $"{path}\n{size / 1024.0 / 1024.0:0.0} MB");
        }
        catch (Exception ex)
        {
            Notify(InfoBarSeverity.Error, Loc.T("tools.standaloneFailed"), Detail(ex));
        }
        finally { _window.SetBusy(false); }
    }

    // ================================================================== 生成 RSTB
    /// <summary>
    /// 修复模组编号：把 mods_nx 里所有模组的优先级重新连续编号（0100 起）。
    ///
    /// 什么时候需要：如果两个目录共用了同一个编号（例如同时出现两个 0100_）、
    /// 或有号段消失，BCML 解析优先级就会错乱，界面也可能拿不到某个模组。
    /// 没坏的时候点它是幂等的（按现有编号稳定排序后重排）。
    /// </summary>
    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null) return;

        if (!await Dialogs.ConfirmAsync(
                XamlRoot, Loc.T("tools.repair"), Loc.T("tools.repairConfirm"),
                Loc.T("tools.doRepair"), Loc.T("common.cancel"))) return;

        _window.SetBusy(true, Loc.T("tools.repairRunning"), Loc.T("tools.repairRunningHint"));
        try
        {
            var renamed = await _services.Bcml.RepairPrioritiesAsync();
            Notify(InfoBarSeverity.Success, Loc.T("tools.repairDone"),
                   Loc.T("tools.repairDoneHint", renamed));
        }
        catch (Exception ex)
        {
            Notify(InfoBarSeverity.Error, Loc.T("tools.repair"), Detail(ex));
        }
        finally { _window.SetBusy(false); }
    }

    private async void Rstb_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null) return;
        if (RstbModBox.SelectedItem is not ModItem item)
        {
            Notify(InfoBarSeverity.Warning, Loc.T("tools.rstb"), Loc.T("tools.needMod"));
            return;
        }

        _window.SetBusy(true, Loc.T("tools.rstbRunning"), Loc.T("tools.rstbRunningHint"));
        try
        {
            var result = await _services.Bcml.GenRstbAsync(item.Path);
            var path = result.TryGetProperty("rstb", out var p) ? p.GetString() : "";
            Notify(InfoBarSeverity.Success, Loc.T("tools.rstbDone"), path ?? "");
        }
        catch (Exception ex)
        {
            Notify(InfoBarSeverity.Error, Loc.T("tools.rstb"), Detail(ex));
        }
        finally { _window.SetBusy(false); }
    }

    // ================================================================== 模组比对
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null) return;
        if (CompareLeftBox.SelectedItem is not ModItem left ||
            CompareRightBox.SelectedItem is not ModItem right)
        {
            Notify(InfoBarSeverity.Warning, Loc.T("tools.compare"), Loc.T("tools.needBothMods"));
            return;
        }

        _window.SetBusy(true, Loc.T("tools.comparing"), null);
        try
        {
            var result = await _services.Bcml.CompareModsAsync(left.Path, right.Path);
            var diff = result.Groups.Where(g => g.HasDiff).ToList();
            CompareSummary.Text = Loc.T("tools.compareSummary", result.TotalLeft, result.TotalRight)
                + (diff.Count == 0 ? "\n" + Loc.T("tools.noDiff") : "");
            CompareResultList.ItemsSource = diff;
            CompareResultList.Visibility = diff.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Notify(InfoBarSeverity.Success, Loc.T("tools.compareDone"), "");
        }
        catch (Exception ex)
        {
            Notify(InfoBarSeverity.Error, Loc.T("tools.compare"), Detail(ex));
        }
        finally { _window.SetBusy(false); }
    }

    // ================================================================== 输出与目录
    private async void OpenMaster_Click(object sender, RoutedEventArgs e)
    {
        var path = await _services.Bcml?.GetMasterModpackAsync()!;
        OpenInExplorer(path);
    }

    private void OpenStore_Click(object sender, RoutedEventArgs e) =>
        OpenInExplorer(_services.Info?.StoreDir);

    private async void Remerge_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null) return;
        if (!await Dialogs.ConfirmAsync(
                XamlRoot, Loc.T("common.remerge"), Loc.T("mods.remergeConfirm"),
                Loc.T("mods.remergeStart"), Loc.T("common.cancel"))) return;

        _window.SetBusy(true, Loc.T("mods.remerging"), null);
        try
        {
            await _services.Bcml.RemergeAsync("all");
            Notify(InfoBarSeverity.Success, Loc.T("common.remerge"), Loc.T("mods.remergeDone"));
        }
        catch (Exception ex)
        {
            Notify(InfoBarSeverity.Error, Loc.T("common.remerge"), ex.Message);
        }
        finally { _window.SetBusy(false); }
    }

    // ================================================================== 辅助
    private async Task<string?> PickBnpAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_window));
        picker.FileTypeFilter.Add(".bnp");
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async Task<string?> PickSaveAsync(string title, string suggested, string ext)
    {
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_window));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = suggested;
        picker.FileTypeChoices.Add(title, new List<string> { ext });
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private static string Detail(Exception ex) =>
        ex is SidecarException se && se.Detail is { Length: > 0 } ? se.Detail : ex.Message;

    private static void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch { /* 忽略 */ }
    }

    private void Notify(InfoBarSeverity severity, string title, string message)
    {
        Notice.IsOpen = true;
        Notice.Severity = severity;
        Notice.Title = title;
        Notice.Message = message;
    }
}
