using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BCML.WinUI3.App.Services;
using BCML.WinUI3.Core.Models;
using BCML.WinUI3.Core.Services;
using BCML.WinUI3.Core.Sidecar;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using System.Globalization;
using Microsoft.UI.Xaml.Media.Animation;

namespace BCML.WinUI3.App.Pages;

/// <summary>列表里的一行。</summary>
public sealed partial class ModRow : ObservableObject
{
    public ModInfo Model { get; private set; }

    public ModRow(ModInfo model)
    {
        Model = model;
        PriorityText = model.Priority.ToString("D4");
        Name = model.Name;
        Enabled = !model.Disabled;
    }


    private static readonly char[] PathSeparators = { '\\', '/' };

    /// <summary>该模组会改动哪些合并器（get_mod_info 的 changes）。</summary>
    public List<string> Changes { get; set; } = new();

    /// <summary>来源页（info.json 的 url）。</summary>
    public string? Url { get; set; }

    /// <summary>info.json 的修改时间（后端格式化好的字符串）。</summary>
    public string? Date { get; set; }

    public bool Processed { get; set; }

    [ObservableProperty] private string _priorityText;
    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private BitmapImage? _thumbnail;

    /// <summary>上次解码的封面字节数 —— 一样就不重新解码（避免封面闪）。</summary>
    public int ThumbnailBytes { get; set; }
    [ObservableProperty] private string? _description;

    /// <summary>拖拽手柄是否显示 —— 由页面上的「排序手柄」开关统一下发。</summary>
    [ObservableProperty] private Visibility _handleVisibility = Visibility.Collapsed;

    /// <summary>
    /// 卡片上的上移/下移按钮是否显示 —— 与抓手同源，只有排序模式打开时才出现。
    /// 单独一个属性而不是复用 HandleVisibility：模板里两个控件各自 x:Bind，
    /// 语义分开，将来若要单独控制也不用改绑定。
    /// </summary>
    [ObservableProperty] private Visibility _moveButtonsVisibility = Visibility.Collapsed;

    public bool NotBusy => !IsBusy;

    public Visibility DisabledBadgeVisibility => Enabled ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>列表里的「已禁用」小徽章。在 DataTemplate 里，只能靠 x:Bind 取。</summary>
    public string DisabledBadge => Loc.T("mods.disabledBadge");

    public Visibility ThumbnailVisibility => Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PlaceholderVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(NotBusy));

    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(DisabledBadgeVisibility));

    partial void OnThumbnailChanged(BitmapImage? value)
    {
        OnPropertyChanged(nameof(ThumbnailVisibility));
        OnPropertyChanged(nameof(PlaceholderVisibility));
    }

    public void RefreshFromModel()
    {
        PriorityText = Model.Priority.ToString("D4");
        Enabled = !Model.Disabled;
    }

    /// <summary>
    /// 后端把目录改名之后，把新路径套回这一行 —— 不再重新读整个列表。
    ///
    /// BCML 的优先级就编码在目录名里（0100_xxx），所以排序必然要改目录名；
    /// 但"改了名字"不等于"要重读一遍模组列表"，把新名字接上就够了。
    /// </summary>
    public void RenameTo(string newPath)
    {
        Model.Path = newPath;
        var dirName = Path.GetFileName(newPath.TrimEnd(PathSeparators));
        Model.Id = dirName;

        var head = dirName.Split('_')[0];
        if (head.Length == 4 && int.TryParse(head, out var pr)) Model.Priority = pr;

        RefreshFromModel();
        OnPropertyChanged(nameof(DirectoryName));
    }

    /// <summary>
    /// 换一份后端数据（路径不变），**保留**封面、忙碌状态、抓手可见性等 UI 状态。
    /// 列表重读走这里，而不是 new 一个新的 ModRow —— 后者是整表闪烁的主因。
    /// </summary>
    public void AdoptModel(ModInfo model)
    {
        Model = model;
        RefreshFromModel();
        OnPropertyChanged(nameof(DirectoryName));
    }

    public string DirectoryName => Path.GetFileName(Model.Path.TrimEnd(PathSeparators));
}

public sealed partial class ModsPage : Page
{
    private readonly AppServices _services;
    private readonly MainWindow _window;
    private bool _loading;

    /// <summary>完整列表（含被过滤掉的）—— 排序要按它下发，否则隐藏的 mod 会占着优先级不放。</summary>
    private List<ModRow> _allRows = new();

    /// <summary>
    /// 详情页数据缓存（键=模组目录）。改动清单要走一遍模组文件，比较重，
    /// 所以在列表里来回点不会重复请求；列表刷新时整体清空。
    /// </summary>
    private readonly Dictionary<string, ModDetailsResult> _detailsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>正在加载详情的那个模组路径 —— 避免同一行重复发起。</summary>
    private string? _detailsLoadingPath;

    /// <summary>重新合并的兜底超时。串行池下实测只要几秒，给 10 分钟已经很宽松。</summary>
    private static readonly TimeSpan RemergeTimeout = TimeSpan.FromMinutes(10);

    /// <summary>上一次渲染的模组路径，用来判断「选中项真的换了」。</summary>
    private string? _lastDetailPath;

    public ObservableCollection<ModRow> Rows { get; } = new();

    /// <summary>详情批量加载是否正在跑（防止重读列表时又叠一批请求）。</summary>
    private bool _detailsLoading;

    /// <summary>抓手开关上次下发的状态 —— 没变就不重复刷 UI。</summary>
    private bool _handlesApplied;
    private bool _handlesOn;

    private ModRow? CurrentRow => ModList.SelectedItem as ModRow;

    public ModsPage(AppServices services, MainWindow window)
    {
        _services = services;
        _window = window;
        InitializeComponent();

        // 列表重排时 WinUI 默认会给**每一个移动的行**跑一遍 reposition 动画。
        // 整表倒序 = 十几个动画串起来挨个播，看起来就像"卡了半天"——
        // 而后端实际只花了百毫秒。清空容器过渡后移动是瞬时的，
        // 拖动时的拖拽视觉提示不受影响（那个不是容器过渡）。
        ModList.ItemContainerTransitions = new TransitionCollection();

        ApplyLanguage();
        Loc.LanguageChanged += (_, _) => ApplyLanguage();
        // 载入时机统一由 MainWindow 负责（后端就绪 / 后端重启各触发一次），
        // 这里不自行订阅 ReadyChanged，否则会和 MainWindow 的调用重复拉一遍列表。
    }

    /// <summary>界面文案本地化（工具栏 + 详情区）。词条在 <see cref="Loc"/> 里维护。</summary>
    private void ApplyLanguage()
    {
        PageTitle.Text = Loc.T("nav.mods");
        // AppBarButton/AppBarToggleButton 显示的是 Label（不是 Content），
        // 而且这个 Label 在收起时会自动当 ToolTip 用，所以不用再单独设 Content。
        ShowDisabledToggle.Label = Loc.T("mods.showDisabled");
        ToolTipService.SetToolTip(ShowDisabledToggle, Loc.T("mods.showDisabledHint"));
        ShowHandlesToggle.Label = Loc.T("mods.showHandles");
        ToolTipService.SetToolTip(ShowHandlesToggle, Loc.T("mods.showHandlesHint"));

        ReverseOrderButton.Label = Loc.T("mods.reverseOrder");
        ToolTipService.SetToolTip(ReverseOrderButton, Loc.T("mods.reverseOrderHint"));
        AutomationProperties.SetName(ReverseOrderButton, Loc.T("mods.reverseOrder"));

        RefreshButton.Label = Loc.T("common.refresh");
        InstallButton.Label = Loc.T("mods.install");
        RemergeButton.Label = Loc.T("common.remerge");
        BackupButton.Label = Loc.T("backup.centerTitle");
        UninstallAllItem.Label = Loc.T("mods.uninstallAll");

        NoSelectionHint.Text = Loc.T("mods.noSelection");
        InfoSectionTitle.Text = Loc.T("mods.infoTitle");
        DependsSectionTitle.Text = Loc.T("mods.dependsTitle");
        DisabledMergersSectionTitle.Text = Loc.T("mods.disabledMergersTitle");
        // 「内容描述」「可用操作」两处标题已从 XAML 去掉（描述正文紧跟在标题之后、
        // 按钮自带文字，都无需再顶一行标题），所以这里不再赋值。

        // 「可用操作」的按钮都带文字，所以这里同时写 TextBlock.Text（可见文字）和
        // A11y 名 + ToolTip（悬浮与读屏）。两处都取自同一份 Loc，切换语言时一起变。
        ExploreButtonText.Text = Loc.T("mods.explore");
        UrlButtonText.Text = Loc.T("mods.source");
        UpdateButtonText.Text = Loc.T("mods.update");
        ReprocessButtonText.Text = Loc.T("mods.reprocess");
        OptionsButtonText.Text = Loc.T("mods.optionsShort");
        UninstallButtonText.Text = Loc.T("mods.uninstall");

        LabelTextButton(ExploreButton, ExploreButtonText, Loc.T("mods.exploreHint"));
        LabelTextButton(UrlButton, UrlButtonText, null);   // 见 UpdateDetails：按有无 URL 动态换提示
        LabelTextButton(UpdateButton, UpdateButtonText, Loc.T("mods.updateHint"));
        LabelTextButton(ReprocessButton, ReprocessButtonText, null);
        LabelTextButton(OptionsButton, OptionsButtonText, Loc.T("modOptions.tip"));
        LabelTextButton(UninstallButton, UninstallButtonText, Loc.T("mods.uninstallHint"));

        UpdateDetails();   // 徽标 / 按钮可用状态在语言切换后也要跟着刷新
        RefreshImportResultCards();   // 导入结果卡片里的文字同理
    }

    // ------------------------------------------------------------------ 载入
    // 注意：后台线程里不要读 MainWindow.DispatcherQueue —— 那是 WinRT 属性，
    // 跨线程访问会抛 COMException（消息为空，极难排查）。
    // AppServices.Ui 是构造期在 UI 线程抓好的 DispatcherQueue 引用，读字段 + TryEnqueue 都安全。

    public async Task TryLoadAsync()
    {
        if (!_services.IsReady || _loading) return;
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (_services.Bcml is null) return;
        // 重入守卫放在这里而不是只放在 TryLoadAsync：
        // ReadyChanged（后端就绪/重启）与「刷新」按钮都会直接调 LoadAsync，
        // 两次并发进来会各自 Clear 列表、各自建一遍行、各自触发一次详情加载。
        if (_loading) return;
        _loading = true;
        var keep = CurrentRow?.Name;
        try
        {
            var mods = await _services.Bcml.GetModsAsync(includeDisabled: true);

            // 复用已有行对象：ModRow 里挂着封面 BitmapImage、忙碌状态、抓手可见性，
            // 每次重读都 new 一遍会让 ListView 整表重刷、封面重新解码 ——
            // 那正是「选中 / 刷新时闪烁、详情区闪空」的来源。
            // 两趟匹配：先按路径，再按模组名兜底。
            // 为什么需要第二趟：排序会把目录**改名**（0100_xxx → 0116_xxx），
            // 只认路径的话整表都算"新行"，封面又要重新解码一遍 —— 排序就闪。
            var byPath = new Dictionary<string, ModRow>(StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, ModRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _allRows)
            {
                byPath.TryAdd(r.Model.Path, r);
                byName.TryAdd(r.Name, r);           // 名字可能重，先到先得
            }

            var used = new HashSet<ModRow>();
            var reused = 0;
            // 重读期间后端返回的启用状态会写回模型（AdoptModel → RefreshFromModel），
            // 那不是用户操作，抑制掉；否则会反过来又把状态推回后端。
            _suppressEnabledAction = true;
            try
            {
                _allRows = mods.Select(m =>
                {
                    ModRow? old = null;
                    if (byPath.TryGetValue(m.Path, out var byP) && used.Add(byP)) old = byP;
                    else if (byName.TryGetValue(m.Name, out var byN) && used.Add(byN)) old = byN;

                    if (old is not null)
                    {
                        old.AdoptModel(m);
                        reused++;
                        HookRow(old);
                        return old;
                    }
                    var fresh = new ModRow(m);
                    HookRow(fresh);
                    return fresh;
                }).ToList();
            }
            finally
            {
                _suppressEnabledAction = false;
            }
            // 列表重读过，之前缓存的详情（版本/元数据）可能已经过期
            _detailsCache.Clear();
            _lastDetailPath = null;
            _detailsLoadingPath = null;
            SyncVisible();

            var enabled = _allRows.Count(r => r.Enabled);

            // 把上一次选中的行重新选上；首次进入没有历史选择时默认选第一行，
            // 免得右侧详情区一进来是空白。
            if (keep is not null)
            {
                var again = _allRows.FirstOrDefault(r => r.Name == keep);
                if (again is not null && Rows.Contains(again)) ModList.SelectedItem = again;
            }
            if (ModList.SelectedItem is null && Rows.Count > 0) ModList.SelectedItem = Rows[0];
            ApplyHandles();
            UpdateDetails();

            AppServices.Diag($"列表已重读：复用 {reused} 行 / 显示 {Rows.Count} 行 / 共 {_allRows.Count}（启用 {enabled}）");
            _ = LoadDetailsAsync();
        }
        catch (Exception ex)
        {
            await ShowError(Loc.T("mods.errListFailed"), ex);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 把 <see cref="Rows"/>（ListView 绑定的可见集合）同步成当前该显示的内容。
    ///
    /// **不要**用 Clear() + 重新 Add()：那样 ListView 会把整表当成全新数据重置 ——
    /// 选中项被清空（详情区闪一下白）、滚动位置回到顶部、每行容器全部重建。
    /// 这里改成先删多余、再按目标顺序 Insert/Move，只有真的变了才动集合。
    /// </summary>
    private void SyncVisible()
    {
        var showDisabled = ShowDisabledToggle.IsChecked != false;
        var want = _allRows.Where(r => showDisabled || r.Enabled).ToList();

        if (want.Count == Rows.Count)
        {
            var same = true;
            for (var i = 0; i < want.Count; i++)
            {
                if (ReferenceEquals(want[i], Rows[i])) continue;
                same = false;
                break;
            }
            if (same) return;               // 一模一样就别动，省掉一次无谓的重排
        }

        // 1) 先移除不该显示的
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!want.Contains(Rows[i])) Rows.RemoveAt(i);
        }

        // 2) 再按目标顺序补位 / 移动（Move 走 ListView 的重排动画，不会闪）
        for (var i = 0; i < want.Count; i++)
        {
            var row = want[i];
            var at = Rows.IndexOf(row);
            if (at < 0) Rows.Insert(i, row);
            else if (at != i) Rows.Move(at, i);
        }
    }

    /// <summary>封面与描述是一张一张问后端要的（base64），限并发 4 路。</summary>
    private async Task LoadDetailsAsync()
    {
        if (_services.Bcml is null) return;

        // 重入守卫：列表可能在短时间内被重读好几次（后端就绪、刷新、装完模组…），
        // 每次放一批并发请求出去，就会出现日志里那种「5 分钟 6 次封面加载完成」，
        // 同一行的封面也被反复解码 —— 也是闪烁来源之一。
        if (_detailsLoading) return;
        _detailsLoading = true;
        try
        {
        using var gate = new SemaphoreSlim(4);
        var rows = _allRows.ToList();
        var failed = 0;

        await Task.WhenAll(rows.Select(async row =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var detail = await _services.Bcml.GetModInfoAsync(row.Model).ConfigureAwait(false);

                // base64 解码是纯 CPU 活，留在后台线程
                var bytes = string.IsNullOrEmpty(detail.ImageBase64)
                    ? null
                    : Convert.FromBase64String(detail.ImageBase64);

                // 但 BitmapImage 必须在 UI 线程创建 —— 后台线程上 new BitmapImage() 会抛
                // COMException 0x8001010E(RPC_E_WRONG_THREAD)。所以整段回到 UI 线程做，
                // 用 TaskCompletionSource 把结果/异常带回来，避免 async void 丢异常。
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var queued = _services.Ui?.TryEnqueue(async () =>
                {
                    try
                    {
                        row.Description = detail.Description;
                        row.Changes = detail.Changes ?? new List<string>();
                        row.Url = detail.Url;
                        row.Date = detail.Date;
                        row.Processed = detail.Processed;

                        // 已经解码过同一张图就别再解一次：给同一行换一个新的 BitmapImage
                        // 会让封面重新加载、闪一下。字节数一样就认为图没变。
                        if (bytes is not null && (row.Thumbnail is null || row.ThumbnailBytes != bytes.Length))
                        {
                            var img = new BitmapImage();
                            using var ms = new MemoryStream(bytes).AsRandomAccessStream();
                            await img.SetSourceAsync(ms);
                            row.Thumbnail = img;
                            row.ThumbnailBytes = bytes.Length;
                        }
                        if (ReferenceEquals(row, CurrentRow)) UpdateDetails();
                        done.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        done.SetException(ex);
                    }
                }) == true;

                if (queued) await done.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                AppServices.Diag($"  封面/描述失败 [{row.Name}] {ex}");
            }
            finally
            {
                gate.Release();
            }
        }));

        AppServices.Diag($"封面/描述加载完成，失败 {failed} 个");
        }
        finally
        {
            _detailsLoading = false;
        }
    }

    /// <summary>
    /// 图标按钮统一走这里：同时设 ToolTip 和 AutomationProperties.Name。
    /// 只设 ToolTip 的话，读屏软件和 UI Automation 拿到的按钮名是**空的** ——
    /// 界面上就是一排「无名按钮」，辅助功能和自动化测试都没法用。
    /// </summary>
    private static void LabelIconButton(Button button, string name, string? hint = null)
    {
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, hint is null ? name : name + " —— " + hint);
    }

    /// <summary>
    /// 「图标 + 文字」按钮：文字已经在 <paramref name="text"/> 里显示出来了，
    /// 这里只需补 A11y 名和可选的 ToolTip（悬浮时给一句更长的说明）。
    /// 名字取文字本身 —— 保证读屏念出来的和眼睛看到的一致。
    /// </summary>
    private static void LabelTextButton(Button button, TextBlock text, string? hint)
    {
        AutomationProperties.SetName(button, text.Text);
        ToolTipService.SetToolTip(button, string.IsNullOrEmpty(hint) ? text.Text : hint);
    }

    private void SetBusyUi(bool busy)
    {
        // 工具条是一整条 CommandBar，直接整条禁用。
        ToolBar.IsEnabled = !busy;

        // 详情区操作条是一个 Grid（继承自 Panel，而 Panel 没有 IsEnabled ——
        // IsEnabled 定义在 Control 上），所以逐个按钮设。
        // 这里遍历子元素而不是写死一堆名字：以后往条上加按钮不用回来补代码。
        foreach (var child in ActionsSection.Children)
        {
            if (child is Control c) c.IsEnabled = !busy;
        }

        ModList.IsEnabled = !busy;
    }

    // ------------------------------------------------------------------ 详情面板
    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();

    private void UpdateDetails()
    {
        var row = CurrentRow;
        var has = row is not null;
        var hasDesc = has && !string.IsNullOrWhiteSpace(row!.Description);
        var hasUrl = has && !string.IsNullOrWhiteSpace(row!.Url);

        ShowCover(row);

        DetailTitle.Text = has ? row!.Name : Loc.T("mods.noSelectionTitle");
        DetailDisabledOverlayText.Text = Loc.T("mods.disabledBadge");
        DetailDisabledOverlay.Visibility = has && !row!.Enabled ? Visibility.Visible : Visibility.Collapsed;

        // 没有选中模组时，下面整块内容区都不出现，只留一句引导。
        NoSelectionHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        DetailDesc.Visibility = hasDesc ? Visibility.Visible : Visibility.Collapsed;
        ActionsSection.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        DetailNotice.IsOpen = false;

        if (row is null)
        {
            MarkdownLite.Render(DetailDesc, null);
        }
        else
        {
            // 路径不单独占一行，改挂在「打开文件夹」按钮的提示里
            ToolTipService.SetToolTip(ExploreButton, row.Model.Path);

            MarkdownLite.Render(DetailDesc, row.Description);
        }

        // ---- 详情区（徽标 / 信息表 / 依赖 / 选项 / 改动明细）
        var path = row?.Model.Path;
        var changed = !string.Equals(path, _lastDetailPath, StringComparison.OrdinalIgnoreCase);
        _lastDetailPath = path;
        RenderDetailSections(row);

        // 选中项换了、而且还没缓存过 → 去后端拉一次完整详情
        if (row is not null && changed && !_detailsCache.ContainsKey(row.Model.Path))
        {
            _ = LoadRowDetailsAsync(row);
        }

        // ---- 操作按钮状态（补齐原版的 更新 / 浏览 / 卸载 等）
        ExploreButton.IsEnabled = has;

        // 「来源」始终占位、只是没 URL 时置灰，**不再隐藏**。
        //
        // 原来 hasUrl 为假就 Collapsed，第 1 行会变成「浏览 + 空 + 更新」——
        // 中间空一格；若改成重排又会把「更新」挪来挪去，按钮位置随模组跳动。
        // 置灰既让位置固定（三个格子永远对得上），也不留空档。
        // 提示语也据此切换：有链接时说点开链接，没链接时说明该模组未提供来源。
        UrlButton.IsEnabled = has && hasUrl;
        LabelTextButton(UrlButton, UrlButtonText, Loc.T(hasUrl ? "mods.sourceHint" : "mods.sourceNone"));

        // ---- 可用操作的可见性
        //
        // 第 2 行有 3 个格子：重新处理 / 重选选项 / 卸载。其中「重新处理」只在已处理过的
        // 模组上有意义，「重选选项」只在带可选组件的模组上有意义 —— 两者都可能隐藏，
        // 于是这一行会出现 1~3 个按钮。
        //
        // 关键：必须把**三个按钮的列号一起重排**，不能只挪卸载。早先的写法反过来试过两次：
        // 一次只按「重选选项」摆卸载，于是「重新处理」隐藏时 col0/col1 空两格；
        // 一次只挪卸载、把「重选选项」留在 XAML 写死的 col1，于是「重新处理」隐藏时
        // col0 又空一格 —— 也就是用户截图里看到的样子。
        //
        // 现在的做法：把可见的按钮从左往右**依次紧凑排列**（依次给 0、1、2...），
        // 再让最后一个跨列补满剩余格子。这样 1 个按钮占满整行、2 个各占一半、
        // 3 个各占一格，四种组合都不留空档。
        var reprocessVisible = has && row!.Processed;
        var hasOptions = _hasOptionsCache.TryGetValue(row?.Model.Path ?? "", out var known) && known;

        ReprocessButton.Visibility = reprocessVisible ? Visibility.Visible : Visibility.Collapsed;
        OptionsButton.Visibility = hasOptions ? Visibility.Visible : Visibility.Collapsed;

        // 按可见顺序依次占格：重新处理 → 重选选项 → 卸载（卸载始终可见）。
        // 列号与跨列都显式设置，不依赖 XAML 里的初值，避免残留上一次的状态。
        var col = 0;
        if (reprocessVisible) ReprocessButton.SetValue(Grid.ColumnProperty, col++);
        if (hasOptions) OptionsButton.SetValue(Grid.ColumnProperty, col++);

        UninstallButton.SetValue(Grid.ColumnProperty, col);
        UninstallButton.SetValue(Grid.ColumnSpanProperty, 3 - col);

        if (has && !hasOptions && !_optionsProbePending.Contains(row!.Model.Path))
        {
            _ = ShowOptionsButtonIfAvailableAsync(row.Model.Path);
        }

        UpdateButton.IsEnabled = has && !row!.IsBusy;
        ReprocessButton.IsEnabled = has && row!.Processed && !row.IsBusy;
        UninstallButton.IsEnabled = has && !row!.IsBusy;
    }

    /// <summary>
    /// 拉取选中模组的完整详情（元数据 + 选项目录 + 合并器改动清单）。
    /// 界面线程外跑 RPC，回到 UI 线程渲染。
    /// </summary>
    private async Task LoadRowDetailsAsync(ModRow row)
    {
        if (_services.Bcml is null) return;
        var path = row.Model.Path;
        if (_detailsLoadingPath == path) return;      // 已经在拉了
        _detailsLoadingPath = path;
        RenderDetailSections(CurrentRow);             // 先把加载态显示出来

        try
        {
            var details = await _services.Bcml.GetModDetailsAsync(path).ConfigureAwait(false);
            _detailsCache[path] = details;
            AppServices.Diag($"详情已加载 [{row.Name}]：元数据={details.Meta is not null} " +
                             $"改动 {details.Total} 个文件 / {details.Edits.Count} 个合并器");
        }
        catch (Exception ex)
        {
            // 详情拉不到不算致命：封面/描述还在，只是信息表里的版本/依赖会缺
            AppServices.Diag($"详情加载失败 [{row.Name}] {ex}");
            // 不缓存失败：下次切回这一行会重新问一次后端
        }
        finally
        {
            _detailsLoadingPath = null;
        }

        _services.Ui?.TryEnqueue(() =>
        {
            if (string.Equals(CurrentRow?.Model.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                RenderDetailSections(CurrentRow);
            }
        });
    }

    /// <summary>渲染详情区的徽标、信息表与依赖（纯读缓存，不做 IO）。</summary>
    private void RenderDetailSections(ModRow? row)
    {
        DetailBadges.Children.Clear();
        InfoGrid.Children.Clear();
        InfoGrid.RowDefinitions.Clear();
        DependsWrap.Children.Clear();
        DisabledMergersWrap.Children.Clear();

        if (row is null)
        {
            DetailBadges.Visibility = Visibility.Collapsed;
            InfoSection.Visibility = Visibility.Collapsed;
            DependsSection.Visibility = Visibility.Collapsed;
            DisabledMergersSection.Visibility = Visibility.Collapsed;
            return;
        }

        _detailsCache.TryGetValue(row.Model.Path, out var details);
        var meta = details?.Meta;

        // ---- 徽标：版本 / 平台 / 启用状态 / 处理状态（不适用的一律不显示）
        AddBadge(DetailBadges, row.Enabled, row.Enabled ? Loc.T("mods.statusEnabled") : Loc.T("mods.statusDisabled"));
        if (!string.IsNullOrWhiteSpace(meta?.Version)) AddBadge(DetailBadges, false, "v" + meta!.Version);
        var platform = PlatformLabel(meta?.Platform);
        if (platform.Length > 0) AddBadge(DetailBadges, false, platform);
        AddBadge(DetailBadges, false, row.Processed ? Loc.T("mods.processed") : Loc.T("mods.unprocessed"));
        DetailBadges.Visibility = Visibility.Visible;

        // ---- 信息表（只列有值的字段）
        var info = new List<(string Label, string Value)>
        {
            (Loc.T("mods.infoPriority"), row.Model.Priority.ToString("D4")),
            (Loc.T("mods.infoFolder"), row.DirectoryName),
        };
        if (platform.Length > 0) info.Add((Loc.T("mods.infoPlatform"), platform));
        if (!string.IsNullOrWhiteSpace(meta?.Version)) info.Add((Loc.T("mods.infoVersion"), meta!.Version));
        if (!string.IsNullOrWhiteSpace(row.Date)) info.Add((Loc.T("mods.infoUpdated"), row.Date!));
        if (!string.IsNullOrWhiteSpace(meta?.Id) && meta!.Id != row.DirectoryName)
        {
            info.Add((Loc.T("mods.infoId"), meta.Id));
        }
        foreach (var (label, value) in info) AddInfoRow(label, value);
        InfoSection.Visibility = info.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // ---- 前置依赖
        var depends = meta?.Depends ?? new List<string>();
        foreach (var d in depends) DependsWrap.Children.Add(MakeChip(d));
        DependsSection.Visibility = depends.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // ---- 已关闭的合并器
        var offMergers = meta?.DisabledMergers ?? new List<string>();
        foreach (var m in offMergers) DisabledMergersWrap.Children.Add(MakeChip(m, danger: true));
        DisabledMergersSection.Visibility = offMergers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>信息表的一行：左标签（定宽）+ 右值（可换行、可选中）。</summary>
    private void AddInfoRow(string label, string value)
    {
        var r = InfoGrid.RowDefinitions.Count;
        InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock
        {
            Text = label,
            MinWidth = 68,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var val = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Grid.SetRow(name, r);
        Grid.SetColumn(name, 0);
        Grid.SetRow(val, r);
        Grid.SetColumn(val, 1);
        InfoGrid.Children.Add(name);
        InfoGrid.Children.Add(val);
    }

    /// <summary>小徽标（版本 / 平台 / 状态）。启用态用强调色，禁用态用警示色。</summary>
    private static void AddBadge(Panel host, bool accent, string text)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            BorderThickness = new Thickness(1),
        };

        if (accent)
        {
            border.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            border.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        }
        else
        {
            border.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            border.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        }

        border.Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = accent
                ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        host.Children.Add(border);
    }

    /// <summary>改动清单/选项用的普通小标签。</summary>
    private static Border MakeChip(string text, bool danger = false)
    {
        var brush = danger
            ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            BorderThickness = new Thickness(1),
            BorderBrush = brush,
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = danger
                    ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
                    : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            },
        };
    }

    /// <summary>把 info.json 的 platform 字段翻成人看的名字；未知就返回空串（不显示）。</summary>
    private static string PlatformLabel(string? platform) => platform?.ToLowerInvariant() switch
    {
        "switch" or "nx" => Loc.T("mods.platformSwitch"),
        "wiiu" or "wii u" or "cemu" => Loc.T("mods.platformWiiu"),
        _ => "",
    };

    private void ShowCover(ModRow? row)
    {
        if (row?.Thumbnail is null)
        {
            DetailCover.Source = null;
            DetailCover.Visibility = Visibility.Collapsed;
            DetailCoverPlaceholder.Visibility = Visibility.Visible;
            return;
        }
        DetailCover.Source = row.Thumbnail;
        DetailCover.Visibility = Visibility.Visible;
        DetailCoverPlaceholder.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ 拖动排序
    private async void ModList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // WinUI 的 ListView 在 CanReorderItems=true 时已经把 ItemsSource(ObservableCollection)
        // 里的元素移动过了，这里只需要把新顺序落盘。
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move) return;

        // 复用同一套「可见顺序 → 完整顺序」的映射（上移/下移也要用）
        ApplyVisibleOrderToAllRows();

        await PersistOrderAsync();
    }

    /// <summary>
    /// 把某一行的路径换成后端改名后的新路径，并**同步搬迁所有按路径缓存的条目**。
    ///
    /// 为什么必须搬：BCML 的优先级编码在目录名里，排序/拖动会改名 → 行的 Path 变了。
    /// 而详情缓存、详情失败集合、「有没有选项目录」的探测结果都是**按路径做键**的。
    /// 不搬的话，改完名立刻变成"全部缓存未命中"：
    ///   · 选中的那个模组会重新走一次 sys.modDetails —— 那要遍历它的全部文件
    ///     （「林可儿 Mod 3.0」639 个文件，内嵌 3.9 实测约 130 ms；见 tools/time_moddetails.py）；
    ///   · 每行的「选项」按钮探测也会重来一遍。
    /// 这就是用户看到的「只是排个序，为什么要转半天」。
    /// </summary>
    private void RenameRow(ModRow row, string newPath)
    {
        var old = row.Model.Path;
        if (string.Equals(old, newPath, StringComparison.OrdinalIgnoreCase)) return;

        row.RenameTo(newPath);

        if (_detailsCache.Remove(old, out var cached)) _detailsCache[newPath] = cached;
        if (_hasOptionsCache.Remove(old, out var hasOptions)) _hasOptionsCache[newPath] = hasOptions;
        if (string.Equals(_lastDetailPath, old, StringComparison.OrdinalIgnoreCase)) _lastDetailPath = newPath;
    }

    /// <summary>把当前顺序落盘（重命名目录以体现优先级）。
    ///
    /// 刻意做成**完全静默**：排序/拖动只是重命名十几个目录（实测百毫秒级），
    /// 弹整屏遮罩、禁用整条工具条、或者飘一条「顺序已保存」横幅，都比操作本身还重。
    /// 而且这里**不再重新读一遍列表** —— 后端返回的新目录名直接套回内存里的行即可；
    /// 重读（17 个模组 + 封面 + 详情）才是之前"排个序要转半天"的真正原因。
    /// </summary>
    private async Task PersistOrderAsync()
    {
        if (_services.Bcml is null || _allRows.Count == 0) return;
        var order = _allRows.Select(r => r.DirectoryName).ToList();

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var paths = await _services.Bcml.ReorderLocalAsync(order);
            var backendMs = sw.ElapsedMilliseconds;

            if (paths.Count == _allRows.Count)
            {
                var modsDir = System.IO.Path.GetDirectoryName(_allRows[0].Model.Path) ?? "";
                for (var i = 0; i < _allRows.Count; i++)
                {
                    RenameRow(_allRows[i], System.IO.Path.Combine(modsDir, paths[i]));
                }
                AppServices.Diag($"顺序已保存：{paths.Count} 个目录已重命名" +
                                 $"（后端 {backendMs} ms，就地更新未重读列表）");

                // 详情栏里「文件夹」那一行显示的是目录名，改名后要重画一次。
                // 缓存已经跟着搬过去了，所以这次只走内存、不会再去后端。
                UpdateDetails();
            }
            else
            {
                // 数量对不上说明后端有意外情况，这时才退回重读
                AppServices.Diag($"顺序已保存，但返回 {paths.Count} 个名字 / 期望 {_allRows.Count} 个，改为重读列表");
                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            await ShowError("保存顺序失败", ex);
        }
    }

    /// <summary>
    /// 把某一行在可见列表里上/下移一格并落盘。
    ///
    /// 按**行**而不是按「当前选中行」—— 卡片上的上移/下移直接把自己那一行传进来，
    /// 不用先把列表选中项切过去（那样会顺带重画详情栏、还容易误操作别的行）。
    /// </summary>
    private async Task MoveRowAsync(ModRow row, int delta)
    {
        var index = Rows.IndexOf(row);
        if (index < 0) return;
        var target = index + delta;
        if (target < 0 || target >= Rows.Count) return;      // 到顶/到底就不动

        Rows.Move(index, target);

        // 光移动 Rows 是不够的：落盘时读的是 _allRows 的顺序，
        // 不把它也同步过来，保存的就是**移动之前**的顺序 ——
        // 表现就是按了上移/下移，列表刷新后又弹回原位。
        ApplyVisibleOrderToAllRows();

        await PersistOrderAsync();

        // 移动后行容器很可能被 ListView 回收复用，把这一行重新选上，
        // 免得详情栏停在别的模组上（用户点的是卡片，注意力还在这一行）。
        if (Rows.Contains(row)) ModList.SelectedItem = row;
    }

    /// <summary>
    /// 把可见列表（<see cref="Rows"/>）的当前顺序填回 <see cref="_allRows"/> 中
    /// 「本来属于可见项」的那些槽位 —— 被过滤掉的行保持原位，也不会抢走别人的优先级。
    /// 拖动与上移/下移都靠它把界面顺序变成落盘顺序。
    /// </summary>
    private void ApplyVisibleOrderToAllRows()
    {
        var visible = Rows.ToList();
        var visibleSet = new HashSet<ModRow>(visible);
        using var it = visible.GetEnumerator();
        for (var i = 0; i < _allRows.Count; i++)
        {
            if (!visibleSet.Contains(_allRows[i])) continue;
            if (!it.MoveNext()) break;
            _allRows[i] = it.Current;
        }
    }

    // ------------------------------------------------------------------ 详情操作
    /// <summary>卡片上的上移（Tag 挂的是 ModRow 本身）。</summary>
    private async void RowUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ModRow row) await MoveRowAsync(row, -1);
    }

    /// <summary>卡片上的下移（Tag 挂的是 ModRow 本身）。</summary>
    private async void RowDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ModRow row) await MoveRowAsync(row, +1);
    }

    private void Explore_Click(object sender, RoutedEventArgs e)
    {
        var row = CurrentRow;
        if (row is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{row.Model.Path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _ = ShowError(Loc.T("mods.errOpenFolder"), ex);
        }
    }

    private void Url_Click(object sender, RoutedEventArgs e)
    {
        var url = CurrentRow?.Url;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _ = ShowError(Loc.T("mods.errOpenUrl"), ex);
        }
    }

    private async void Reprocess_Click(object sender, RoutedEventArgs e)
    {
        var row = CurrentRow;
        if (row is null) return;
        row.IsBusy = true;
        UpdateDetails();
        try
        {
            var ok = await RunAsync(Loc.T("mods.reprocessing"), row.Name + "\n" + Loc.T("mods.reprocessDetail"),
                () => _services.Bcml!.ModActionAsync(row.Model, "reprocess"), fullBusy: false);
            if (!ok) return;                 // 失败时 RunAsync 已经弹过错误，别再报一次"成功"
            Notice.IsOpen = true;
            Notice.Severity = InfoBarSeverity.Success;
            Notice.Title = Loc.T("mods.reprocessed");
            Notice.Message = Loc.T("mods.reprocessPending");
        }
        finally
        {
            row.IsBusy = false;
            UpdateDetails();
        }
    }

    /// <summary>「这个 mod 有没有可选组件」的缓存（按 mod 路径）。</summary>
    private readonly Dictionary<string, bool> _hasOptionsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 正在探测中的 mod 路径。UpdateDetails 会被行状态变化频繁调用，没有这道闸
    /// 就会对同一个 mod 并发发好几次 RPC。
    /// </summary>
    private readonly HashSet<string> _optionsProbePending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>探测并显示「选项」按钮。查询失败就当没有选项，不要因此打断详情显示。</summary>
    private async Task ShowOptionsButtonIfAvailableAsync(string modPath)
    {
        if (_services.Bcml is null) return;
        if (!_optionsProbePending.Add(modPath)) return;   // 已有一次在飞

        if (!_hasOptionsCache.TryGetValue(modPath, out var hasOptions))
        {
            try
            {
                var info = await _services.Bcml.GetModOptionsAsync(modPath);
                hasOptions = info?.HasOptions == true;
                _hasOptionsCache[modPath] = hasOptions;
            }
            catch (Exception ex)
            {
                AppServices.Diag($"探测模组选项失败 [{modPath}] {ex.Message}");
                return;
            }
            finally
            {
                _optionsProbePending.Remove(modPath);
            }
        }

        // 异步回来时用户可能已经切到别的模组了，别错把按钮显示在别人身上
        _services.Ui?.TryEnqueue(() =>
        {
            if (!string.Equals(CurrentRow?.Model.Path, modPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            OptionsButton.Visibility = hasOptions ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    /// <summary>
    ///
    /// 流程：读选项定义（带快照，这样上一轮取消掉的变体也能选回来）
    ///   → 弹对话框收集选择 → 后端重建 options/ → 重新合并让它生效。
    /// 「不残留旧配置」由后端保证：它先整棵删掉 options/，再从原始快照按新选择重建。
    /// </summary>
    private async void Options_Click(object sender, RoutedEventArgs e)
    {
        var row = CurrentRow;
        if (row is null || _services.Bcml is null) return;

        ModOptionsInfo? info;
        try
        {
            info = await _services.Bcml.GetModOptionsAsync(row.Model.Path, snapshot: true);
        }
        catch (Exception ex)
        {
            await ShowError(Loc.T("common.failed", Loc.T("modOptions.title", row.Name)), ex);
            return;
        }

        if (info is null || !info.HasOptions)
        {
            Notice.Severity = InfoBarSeverity.Informational;
            Notice.Title = Loc.T("modOptions.noOptions");
            Notice.Message = "";
            Notice.IsOpen = true;
            return;
        }

        var changed = await ModOptionsDialog.ShowAsync(XamlRoot, _services, info);
        if (!changed) return;

        // 选项文件已经写到磁盘，但必须再合并一次才会进入游戏用的合并结果
        var ok = await RunAsync(Loc.T("modOptions.applied"), Loc.T("mods.remergingHint"),
            () => _services.Bcml!.RemergeAsync("all"),
            timeout: RemergeTimeout);

        if (ok)
        {
            Notice.Severity = InfoBarSeverity.Success;
            Notice.Title = Loc.T("modOptions.done");
            Notice.Message = Loc.T("mods.remergeDoneHint");
            Notice.IsOpen = true;
        }

        await LoadAsync();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var row = CurrentRow;
        if (row is null) return;

        if (!await Dialogs.ConfirmAsync(
                XamlRoot, Loc.T("mods.uninstallConfirmTitle"), row.Name,
                Loc.T("mods.uninstallAction"), Loc.T("common.cancel"),
                primaryIsDefault: false)) return;

        row.IsBusy = true;
        UpdateDetails();
        try
        {
            await RunAsync(Loc.T("mods.uninstalling"), row.Name,
                () => _services.Bcml!.ModActionAsync(row.Model, "uninstall"), fullBusy: false);
        }
        finally
        {
            row.IsBusy = false;
        }
        await LoadAsync();
    }

    /// <summary>
    /// 列表行上的启用 / 禁用开关。
    ///
    /// ToggleSwitch 在用户拨动的**那一刻**就把 IsOn 改掉了，所以失败时必须显式拨回去 ——
    /// 否则界面显示「已禁用」而磁盘上没有 `.disabled`，下次重进列表又变回来，
    /// 用户会以为开关坏了。
    /// </summary>
    /// <summary>
    /// 我们自己正在写回 Enabled（后端确认后同步状态），此时不要再当成用户操作。
    /// </summary>
    private bool _suppressEnabledAction;

    /// <summary>
    /// 订阅某一行的属性变化 —— 把「用户拨了启用开关」变成一次后端调用。
    ///
    /// **为什么不直接用 ToggleSwitch.Toggled**：它对**程序性**的 IsOn 变化也会触发。
    /// 列表重排（排序 / 反转 / 拖动）时 ListView 回收并复用行容器，绑定把新行的值推进 IsOn
    /// 就又触发一次 Toggled，而那一刻 DataContext 可能还停在**上一行** ——
    /// 结果就是把另一个模组给启用/禁用了（真的写 .disabled 文件）。
    ///
    /// 换成监听模型：绑定是 TwoWay，但**只有用户拨动控件时才会写回模型**，
    /// 程序性回填只走"模型 → 控件"这一方向、不产生通知。
    /// 所以这里收到的每一次 Enabled 变化都必然来自用户操作。
    /// </summary>
    private void HookRow(ModRow row)
    {
        row.PropertyChanged -= OnRowPropertyChanged;
        row.PropertyChanged += OnRowPropertyChanged;
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModRow.Enabled)) return;
        if (_suppressEnabledAction) return;
        if (sender is not ModRow row) return;
        _ = ApplyEnabledAsync(row);
    }

    /// <summary>用户拨了某一行的启用开关之后，把新状态同步到后端。</summary>
    private async Task ApplyEnabledAsync(ModRow row)
    {
        var target = row.Enabled;      // 用户拨到的状态
        var previous = !target;        // 拨之前的状态

        row.IsBusy = true;
        UpdateDetails();
        var ok = false;
        try
        {
            ok = await RunAsync(target ? Loc.T("mods.enabling") : Loc.T("mods.disabling"), row.Name,
                () => _services.Bcml!.ModActionAsync(row.Model, target ? "enable" : "disable"),
                fullBusy: false);
        }
        finally
        {
            // 成功 → 保持用户选的状态；失败 → 拨回去。两种都带抑制标记：
            // 这次写回是我们自己做的，不该再触发一轮后端调用。
            SetEnabledSilently(row, ok ? target : previous);
            row.IsBusy = false;
            UpdateDetails();
        }

        // 列表过滤掉已禁用项时，禁用后该行应当立刻消失
        if (ok && !target) SyncVisible();
    }

    /// <summary>把状态写回模型与界面，但不触发新一轮后端调用。</summary>
    private void SetEnabledSilently(ModRow row, bool enabled)
    {
        _suppressEnabledAction = true;
        try
        {
            row.Model.Disabled = !enabled;
            row.RefreshFromModel();
        }
        finally
        {
            _suppressEnabledAction = false;
        }
    }

    /// <summary>详情面板里的「更新」：选一个新 bnp 原地替换（原版的 update）。</summary>
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var row = CurrentRow;
        if (row is null || _services.Bcml is null) return;

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_window));
        picker.FileTypeFilter.Add(".bnp");
        picker.SuggestedStartLocation = PickerLocationId.Downloads;

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = row.Name,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                },
                new TextBlock
                {
                    Text = Path.GetFileName(file.Path),
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = Loc.T("mods.updateConfirmHint"),
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"],
                },
            },
        };
        if (!await Dialogs.ConfirmAsync(
                XamlRoot, Loc.T("mods.updateConfirmTitle"), content,
                Loc.T("mods.replaceAction"), Loc.T("common.cancel"))) return;

        row.IsBusy = true;
        UpdateDetails();
        try
        {
            var ok = await _services.Bcml.UpdateModAsync(row.Model.Path, file.Path);
            Notice.IsOpen = true;
            Notice.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            Notice.Title = ok ? Loc.T("mods.updated") : Loc.T("mods.updateIncomplete");
            Notice.Message = ok
                ? Loc.T("mods.updatedHint")
                : Loc.T("mods.updateCheckLogs");
        }
        catch (Exception ex)
        {
            await ShowError(Loc.T("mods.updateFailed"), ex);
        }
        finally
        {
            row.IsBusy = false;
        }
        await LoadAsync();
    }

    // ------------------------------------------------------------------ 排序手柄 / 排序
    private void ShowHandlesToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyHandles();
    }

    /// <summary>
    /// 「排序手柄」开关：关闭时列表完全不可拖（避免误拖），开启后才允许拖动重排，
    /// 同时每行左侧露出抓手图标作为可拖拽的视觉提示。
    /// </summary>
    private void ApplyHandles()
    {
        var on = ShowHandlesToggle.IsChecked == true;

        // 状态没变就别再改一遍：每次都重设这 4 个属性 + 遍历所有行改 HandleVisibility，
        // 会让 ListView 重新评估每个容器（模板里绑着它），白白闪一次。
        if (_handlesApplied && _handlesOn == on) return;
        _handlesApplied = true;
        _handlesOn = on;

        ModList.CanDragItems = on;
        ModList.CanReorderItems = on;
        ModList.AllowDrop = on;
        ModList.ReorderMode = on ? ListViewReorderMode.Enabled : ListViewReorderMode.Disabled;
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        foreach (var r in _allRows)
        {
            r.HandleVisibility = vis;
            r.MoveButtonsVisibility = vis;
        }
    }

    /// <summary>
    /// 反转整个列表的顺序。
    ///
    /// **为什么这里只有一个「反转」，而不是「升序 / 降序」两个方向：**
    /// BCML 的优先级就编码在目录名里（`0100_xxx`），而每次落盘都是**按当前显示顺序**
    /// 从 0100 开始连续编号（后端 `_resequence` 的 `final = f"{start + len(plan):04d}_…"`，
    /// plan 就是我们传过去的显示顺序）。所以列表**永远是从上到下编号递增**，
    /// "按优先级升序"是恒等操作 —— 点了必然没有任何变化，看起来就像按钮坏了。
    /// 真正有意义的只有"反过来"，所以只留这一个按钮，每点一次都必然改变顺序。
    ///
    /// 耗时实测（17 个模组）：后端改名约 80–150 ms，全程约 100–170 ms。
    /// </summary>
    private async void ReverseOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_allRows.Count < 2) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var keep = CurrentRow;

        _allRows.Reverse();
        SyncVisible();
        if (keep is not null && Rows.Contains(keep)) ModList.SelectedItem = keep;

        AppServices.Diag($"反转后将要落盘的顺序（前 3 个）：{string.Join(" | ", _allRows.Take(3).Select(r => r.DirectoryName))}");
        await PersistOrderAsync();
        AppServices.Diag($"反转完成：全程 {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>原版首页的「卸载全部模组」。</summary>
    private async void UninstallAll_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null || _allRows.Count == 0) return;

        if (!await Dialogs.ConfirmAsync(
                XamlRoot,
                Loc.T("mods.uninstallAllConfirmTitle", _allRows.Count),
                Loc.T("mods.uninstallAllHint3") + Loc.T("mods.uninstallAllPost"),
                Loc.T("common.uninstallAll"), Loc.T("common.cancel"),
                primaryIsDefault: false)) return;

        await RunAsync(Loc.T("mods.uninstallingAll"), Loc.T("mods.uninstallingAllHint"),
            () => _services.Bcml!.UninstallAllAsync());
        await LoadAsync();
    }

    // ------------------------------------------------------------------ 通用包装
    /// <summary>
    /// 跑一个后端操作，统一处理忙碌遮罩与错误提示。
    /// 返回 <c>true</c> 表示**真的成功了** —— 调用方必须在成功时才更新本地模型，
    /// 否则界面会显示一个后端其实没接受的状态（典型症状：开关拨过去了、重新进入页面又弹回来）。
    /// </summary>
    private async Task<bool> RunAsync(string title, string detail, Func<Task> action,
                                      bool fullBusy = true, TimeSpan? timeout = null)
    {
        if (_services.Bcml is null)
        {
            Notice.IsOpen = true;
            Notice.Severity = InfoBarSeverity.Warning;
            Notice.Title = Loc.T("common.backendNotReady");
            Notice.Message = _services.LastError ?? Loc.T("mods.backendHint");
            return false;
        }

        if (fullBusy) _window.SetBusy(true, title, detail);
        SetBusyUi(true);
        try
        {
            var work = action();

            // 超时兜底：后端真卡住时，宁可报一条"超时"也不能让遮罩无限转圈。
            // 历史上「重新合并」正是这样卡死的（BCML 的进程池在本进程里起不来），
            // 那次没有任何错误、界面就一直转。
            if (timeout is { } limit)
            {
                var done = await Task.WhenAny(work, Task.Delay(limit)).ConfigureAwait(true);
                if (done != work)
                {
                    // 让仍在跑的任务有人接管，避免它以后抛异常变成"未观测异常"
                    _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                    await ShowError(Loc.T("common.failed", title),
                        new TimeoutException(Loc.T("mods.opTimeout", (int)limit.TotalMinutes)));
                    return false;
                }
            }

            await work.ConfigureAwait(true);   // 把真实的异常抛到下面的 catch
            return true;
        }
        catch (Exception ex)
        {
            await ShowError(Loc.T("common.failed", title), ex);
            return false;
        }
        finally
        {
            if (fullBusy) _window.SetBusy(false);
            SetBusyUi(false);
        }
    }

    private async Task ShowError(string title, Exception ex)
    {
        var hint = ex is SidecarException se ? se.Detail : null;
        await _window.ShowErrorAsync(title, ex.Message, hint);
    }

    // ------------------------------------------------------------------ 工具栏
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void ShowDisabledToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        SyncVisible();
        UpdateDetails();
    }

    /// <summary>
    /// 重新合并全部模组。
    ///
    /// 这里曾经「点了之后一直转圈、永远不结束」，原因不在界面而在后端：
    /// BCML 的 <c>install.refresh_merges()</c> 会开 <c>multiprocessing.Pool</c>，
    /// 而 spawn 出来的 worker 会以 rpc_server.py 作为 __mp_main__ 重新导入本模块、
    /// 并发去加载 20MB 的 oead.pyd，结果谁都起不来 —— 请求永远不返回。
    /// 修法是 sidecar 里把进程池换成串行替身（见 rpc_server.py 的 _SerialPool），
    /// 实测十几秒完成（本机 17 个模组约 12 s，见 tools/time_remerge.py）。
    /// 这里再加一层超时兜底，保证以后再卡也能给出可读的错误。
    /// </summary>
    private async void Remerge_Click(object sender, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync(
                XamlRoot, Loc.T("mods.remergeConfirmTitle"), Loc.T("mods.remergeConfirm"),
                Loc.T("mods.remergeStart"), Loc.T("common.cancel"))) return;

        var ok = await RunAsync(Loc.T("mods.remerging"), Loc.T("mods.remergingHint"),
            () => _services.Bcml!.RemergeAsync("all"),
            timeout: RemergeTimeout);

        if (ok)
        {
            Notice.Severity = InfoBarSeverity.Success;
            Notice.Title = Loc.T("mods.remergeDone");
            Notice.Message = Loc.T("mods.remergeDoneHint");
            Notice.IsOpen = true;
            AppServices.Diag($"重新合并完成（{DateTime.Now:HH:mm:ss}）");
        }

        // 无论成败都重读一次：合并可能改了排序/状态，界面得跟后端对齐
        await LoadAsync();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null) return;
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_window));
        picker.FileTypeFilter.Add(".bnp");
        picker.SuggestedStartLocation = PickerLocationId.Downloads;

        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        var paths = files.Select(f => f.Path).ToArray();
        try
        {
            var selects = await AskOptionsAsync(paths);
            if (selects is null) return;   // 用户取消

            await RunAsync("正在安装模组",
                string.Join("、", paths.Select(Path.GetFileName)) + "\n安装完会自动重新合并。",
                () => _services.Bcml!.Client.CallApiAsync("install_mod", new
                {
                    mods = paths,
                    options = new { disable = Array.Empty<string>(), @options = new { } },
                    selects,
                }, BcmlService.MergeTimeout));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await ShowError(Loc.T("mods.installFailed"), ex);
        }
    }

    /// <summary>
    /// 问后端要每个 bnp 的选项定义，弹出选项对话框。
    /// 返回 { bnp路径: [选中的选项目录名...] }；用户取消时返回 null。
    /// </summary>
    private async Task<Dictionary<string, object?>?> AskOptionsAsync(string[] paths)
    {
        var data = await _services.Bcml!.Client.CallApiAsync("check_mod_options", new { mods = paths });
        var selects = new Dictionary<string, object?>();

        if (data.ValueKind != JsonValueKind.Object) return selects;

        var dialogs = new List<(string Path, JsonElement Meta)>();
        foreach (var p in data.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            if (!p.Value.TryGetProperty("options", out var opt) || opt.ValueKind != JsonValueKind.Object)
                continue;
            if ((opt.TryGetProperty("multi", out var mu) && mu.GetArrayLength() > 0) ||
                (opt.TryGetProperty("single", out var si) && si.GetArrayLength() > 0))
            {
                dialogs.Add((p.Name, p.Value));
            }
        }

        foreach (var (path, meta) in dialogs)
        {
            var picked = await ShowOptionDialogAsync(path, meta);
            if (picked is null) return null;
            selects[path] = picked;
        }
        return selects;
    }

    private async Task<List<string>?> ShowOptionDialogAsync(string bnpPath, JsonElement meta)
    {
        var panel = new StackPanel { Spacing = 14, MinWidth = 460 };
        var multiChecks = new List<(CheckBox Box, string Folder)>();
        var singleGroups = new List<(RadioButtons Group, List<string> Folders)>();

        if (meta.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
        {
            if (options.TryGetProperty("multi", out var multi) && multi.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in multi.EnumerateArray())
                {
                    if (!item.TryGetProperty("folder", out var f) || f.GetString() is not { } folder) continue;
                    var isDefault = item.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.True;
                    var box = new CheckBox
                    {
                        Content = item.TryGetProperty("name", out var n) ? n.GetString() : folder,
                        IsChecked = isDefault,
                        Tag = folder,
                    };
                    if (item.TryGetProperty("desc", out var de) && de.GetString() is { Length: > 0 } desc)
                    {
                        ToolTipService.SetToolTip(box, desc);
                    }
                    multiChecks.Add((box, folder));
                    panel.Children.Add(box);
                }
            }

            if (options.TryGetProperty("single", out var single) && single.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in single.EnumerateArray())
                {
                    var title = group.TryGetProperty("name", out var gn) ? gn.GetString() ?? "" : "";
                    var desc = group.TryGetProperty("desc", out var gd) ? gd.GetString() ?? "" : "";

                    panel.Children.Add(new TextBlock
                    {
                        Text = title,
                        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                        TextWrapping = TextWrapping.Wrap,
                    });
                    if (desc.Length > 0)
                    {
                        panel.Children.Add(new TextBlock
                        {
                            Text = desc,
                            TextWrapping = TextWrapping.Wrap,
                            Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"],
                        });
                    }

                    var entries = new List<(string Label, string Folder)>();
                    if (group.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var o in opts.EnumerateArray())
                        {
                            if (!o.TryGetProperty("folder", out var of) || of.GetString() is not { } ofolder)
                                continue;
                            var label = o.TryGetProperty("name", out var on) ? on.GetString() ?? ofolder : ofolder;
                            entries.Add((label, ofolder));
                        }
                    }
                    if (entries.Count == 0) continue;

                    var radios = new RadioButtons
                    {
                        ItemsSource = entries.Select(e => e.Label).ToList(),
                        // 作者推荐的项在名字里带 *，单选组默认选它（没有就选第一个）
                        SelectedIndex = Math.Max(0, entries.FindIndex(e => e.Label.Contains('*'))),
                    };
                    singleGroups.Add((radios, entries.Select(e => e.Folder).ToList()));
                    panel.Children.Add(radios);
                }
            }
        }

        if (!await Dialogs.ConfirmAsync(
                XamlRoot, Loc.T("mods.chooseOptions"), panel,
                Loc.T("mods.installActionShort"), Loc.T("common.cancel"),
                secondaryText: Path.GetFileName(bnpPath),
                maxContentHeight: DialogSizing.Option)) return null;

        var chosen = new List<string>();
        chosen.AddRange(multiChecks.Where(c => c.Box.IsChecked == true).Select(c => c.Folder));
        foreach (var (group, folders) in singleGroups)
        {
            var idx = group.SelectedIndex;
            if (idx >= 0 && idx < folders.Count) chosen.Add(folders[idx]);
        }
        return chosen;
    }

    // ================================================================== 导入 / 导出清单
    /// <summary>
    /// 首页上的「导入 / 导出」弹窗。刻意做成首页弹窗，**不**单独占一个侧栏入口。
    /// 导出物是一份几 KB 的 JSON：只记录排序、启用/禁用、合并器选项与已启用的选项目录，
    /// 不含 mod 文件本身。下半部分保留 BCML 自带的重型配置档案。
    /// </summary>
    /// <summary>
    /// 「备份」中心。
    ///
    /// 原来这里是「导入 / 导出」——只做 JSON 排序清单；而完整备份（整个 mods_nx 的 .7z）
    /// 挂在侧栏另一个页面里。两件事本质是同一件事（把当前状态存下来、以后能还原），
    /// 所以合并到这一个入口，分成两类：
    ///   · **完整备份**：mod 的全部文件 + 配置，几十 MB 到几百 MB；
    ///   · **导出的备份文件**：几 KB 的 JSON，只记录排序 / 启用 / 合并器选项。
    /// 两类记录都写进应用自己的台账（BackupIndex），因此能集中列出、由用户挑一个来还原。
    /// </summary>
    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Bcml is null) return;

        // 用 Grid（单列 Width="*"）+ 每个子元素 HorizontalAlignment=Stretch，
        // 让所有行**左右都对齐、并填满对话框可用宽度**。
        //
        // 原来用 StackPanel + MinWidth=480 有两个毛病：
        //   · StackPanel 给子元素的宽度是"子元素自己期望的宽度"，所以
        //     HorizontalAlignment=Stretch 的按钮也撑不满，右边缘参差不齐；
        //   · MinWidth 只是"至少 480"，对话框比它宽时内容依然左对齐，
        //     右侧空一大片 —— 用户看到的"卡片没有居中"就是这个。
        // Grid 的星号列会真正把子元素拉到列宽，左右留白由对话框自己对称分配。
        //
        // MinWidth 依然要给（内容太窄时列表行会挤成一团），但它现在只决定
        // **内容块本身的下限**；对话框比它宽时，多出来的宽度由内容撑满、
        // 不再出现"内容贴左、右侧空一片"。
        var panel = new Grid { MinWidth = 460, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 往 panel 末尾追加一行。Grid 没有 StackPanel 那样的"自动依次排下去"，
        // 所以每加一个子元素就补一个 Auto 行并把它放到最后一行 —— 这样
        // HorizontalAlignment=Stretch 的子元素才会真正被列宽拉满。
        void Append(FrameworkElement child, double topMargin = 0)
        {
            var row = panel.RowDefinitions.Count;
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            child.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (topMargin > 0) child.Margin = new Thickness(0, topMargin, 0, 0);
            Grid.SetRow(child, row);
            panel.Children.Add(child);
        }

        // ==================== 一、完整备份 ====================
        Append(new TextBlock
        {
            Text = Loc.T("backup.kindFull"),
            Style = (Style)Application.Current.Resources["SectionHeaderTextStyle"],
        });
        Append(new TextBlock
        {
            Text = Loc.T("backup.fullHint"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"],
        }, topMargin: 4);

        var createFullBtn = new Button
        {
            Content = Loc.T("backup.createFull"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Append(createFullBtn, topMargin: 6);

        var fullListPanel = StackedRows();
        Append(fullListPanel, topMargin: 8);

        Append(Divider(), topMargin: 10);

        // ==================== 二、导出的备份文件（JSON）====================
        Append(new TextBlock
        {
            Text = Loc.T("backup.kindList"),
            Style = (Style)Application.Current.Resources["SectionHeaderTextStyle"],
        }, topMargin: 8);
        Append(new TextBlock
        {
            Text = Loc.T("backup.listHint"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"],
        }, topMargin: 4);

        var listListPanel = StackedRows();
        Append(listListPanel, topMargin: 8);

        // 「导出 / 导入」并排等宽 —— 与上面操作条同一套 2 列节奏，
        // 不再是两个各自撑满整行的长条（那样两行看着像两条横幅，也占了双倍高度）。
        var ioRow = new Grid { ColumnSpacing = 6 };
        ioRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ioRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var exportBtn = new Button
        {
            Content = Loc.T("backup.exportList"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var importBtn = new Button
        {
            Content = Loc.T("backup.importList"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(exportBtn, 0);
        Grid.SetColumn(importBtn, 1);
        ioRow.Children.Add(exportBtn);
        ioRow.Children.Add(importBtn);
        Append(ioRow, topMargin: 8);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("backup.centerTitle"),
            // 走统一的滚动宿主：它会为滚动条留出右侧空档（否则滚动条压在列表行上）。
            Content = Dialogs.ScrollHost(panel, DialogSizing.List),
            CloseButtonText = Loc.T("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        // ---------------- 完整备份列表 ----------------
        // 先让 BCML 扫一遍它的备份目录，再并进台账（这样"不是本应用建的"那些也能列出来）
        var fullBackups = await SafeAsync(() => _services.Bcml.GetBackupsAsync());
        if (fullBackups is not null) BackupIndex.MergeFromBcml(fullBackups);

        void RebuildFull()
        {
            fullListPanel.Children.Clear();
            fullListPanel.RowDefinitions.Clear();
            var records = BackupIndex.OfKind(BackupKind.Full);

            if (records.Count == 0)
            {
                AddRow(fullListPanel, Hint(Loc.T("backup.empty")));
                return;
            }

            foreach (var rec in records)
            {
                AddRow(fullListPanel, RecordRow(
                    rec,
                    meta: Loc.T("backup.modCount", rec.ModCount)
                          + (rec.Exists ? "  ·  " + Fmt(rec.SizeBytes) : "  ·  " + Loc.T("backup.missing")),
                    primaryText: Loc.T("backup.restore"),
                    onPrimary: async () =>
                    {
                        await RunAsync(Loc.T("backup.restoring"), Loc.T("backup.restoringHint"),
                            () => _services.Bcml!.RestoreBackupAsync(rec.Path),
                            timeout: RemergeTimeout);
                        await LoadAsync();
                    },
                    // 「删除」= 删文件；没文件了就只能从列表移除
                    destructiveText: rec.Exists ? Loc.T("common.delete") : Loc.T("backup.forgetRecord"),
                    onDestructive: async () =>
                    {
                        if (rec.Exists)
                        {
                            await RunAsync(Loc.T("backup.deleting"), rec.DisplayName,
                                () => _services.Bcml!.DeleteBackupAsync(rec.Path), fullBusy: false);
                        }
                        BackupIndex.Remove(rec.Path);
                        RebuildFull();
                        RebuildList();
                    }));
            }
        }

        // ---------------- 导出的备份文件列表 ----------------
        void RebuildList()
        {
            listListPanel.Children.Clear();
            listListPanel.RowDefinitions.Clear();
            var records = BackupIndex.OfKind(BackupKind.List);

            if (records.Count == 0)
            {
                AddRow(listListPanel, Hint(Loc.T("backup.empty")));
                return;
            }

            foreach (var rec in records)
            {
                AddRow(listListPanel, RecordRow(
                    rec,
                    meta: rec.Exists ? Fmt(rec.SizeBytes) : Loc.T("backup.missing"),
                    primaryText: Loc.T("backup.restoreFromList"),
                    onPrimary: async () =>
                    {
                        dialog.Hide();
                        await ApplyListFileAsync(rec.Path);
                    },
                    destructiveText: rec.Exists ? Loc.T("common.delete") : Loc.T("backup.forgetRecord"),
                    onDestructive: async () =>
                    {
                        if (rec.Exists)
                        {
                            try { File.Delete(rec.Path); }
                            catch (Exception ex) { await _window.ShowErrorAsync(Loc.T("common.failed", Loc.T("common.delete")), ex.Message, null); return; }
                        }
                        BackupIndex.Remove(rec.Path);
                        RebuildList();
                    }));
            }
        }

        // ---------------- 构建列表 ----------------
        // 注：原先这里还有一块「BCML 自带的配置档案」，和上面的「完整备份」是同一件事
        // （都是整个 mods_nx 的完整拷贝，各约 600 MB），两个入口列两遍同样的东西反而让人
        // 分不清该点哪个，所以整块移除，只保留「完整备份」这一个入口。
        RebuildFull();
        RebuildList();

        // 新建完整备份：问一个名字，然后交给 BCML 打包整个 mods_nx
        createFullBtn.Click += async (_, _) =>
        {
            var nameBox = new TextBox
            {
                PlaceholderText = Loc.T("backup.namePlaceholder"),
                Text = $"BCML_Backup_{DateTime.Now:yyyy-MM-dd}",
            };
            var askContent = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc.T("backup.createHint"),
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"],
                    },
                    nameBox,
                },
            };
            if (!await Dialogs.ConfirmAsync(
                    XamlRoot, Loc.T("backup.create"), askContent,
                    Loc.T("backup.start"), Loc.T("common.cancel"))) return;

            // 先关掉对话框：完整备份要跑一阵，遮罩盖在模态对话框下面会看不见
            dialog.Hide();
            var ok = await RunAsync(Loc.T("backup.creating"), Loc.T("backup.creatingHint"),
                () => _services.Bcml!.CreateBackupAsync(nameBox.Text?.Trim() ?? ""),
                timeout: RemergeTimeout);

            if (ok)
            {
                // 记进台账：BCML 是"扫目录"式的，没有创建时间；补一条带时间的记录，
                // 「备份记录保存在软件内部」这件事就落在 BackupIndex 上
                var fresh = await SafeAsync(() => _services.Bcml!.GetBackupsAsync());
                if (fresh is not null)
                {
                    BackupIndex.MergeFromBcml(fresh);
                    var newest = fresh
                        .OrderByDescending(b => b.ModifiedAt ?? DateTime.MinValue)
                        .FirstOrDefault();
                    if (newest?.Path is { } p)
                    {
                        BackupIndex.Upsert(new BackupRecord
                        {
                            Kind = BackupKind.Full,
                            Name = newest.Name,
                            Path = p,
                            Recorded = DateTimeOffset.Now,
                            ModCount = newest.Num,
                            Source = "app",
                        });
                    }
                }

                Notice.Severity = InfoBarSeverity.Success;
                Notice.Title = Loc.T("backup.created");
                Notice.Message = Loc.T("backup.reopenHint");
                Notice.IsOpen = true;
                AppServices.Diag($"完整备份已创建（{DateTime.Now:HH:mm:ss}）");
            }
        };

        exportBtn.Click += async (_, _) =>
        {
            dialog.Hide();
            await ExportModListToFileAsync();
        };
        importBtn.Click += async (_, _) =>
        {
            dialog.Hide();
            await ImportModListFromFileAsync();
        };

        await dialog.ShowAsync();
    }

    // ------------------------------------------------------------------ 备份中心的小零件
    private static Border Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 6, 0, 2),
        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"],
    };

    /// <summary>
    /// 竖向排列、且子元素**会被拉满宽度**的容器（备份列表用）。
    ///
    /// 不能用 StackPanel：它给子元素的是"子元素期望的宽度"，所以里面的
    /// <see cref="RecordRow"/> 这种带星号列的 Grid 撑不满，右边缘参差不齐 ——
    /// 这正是"卡片没有对齐/居中"的来源。Grid 的星号列才会真正把子元素拉到列宽。
    /// </summary>
    private static Grid StackedRows(double spacing = 6)
    {
        var g = new Grid { RowSpacing = spacing, HorizontalAlignment = HorizontalAlignment.Stretch };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return g;
    }

    /// <summary>按顺序加一行到 <see cref="StackedRows"/> 里。</summary>
    private static void AddRow(Grid host, FrameworkElement child)
    {
        var row = host.RowDefinitions.Count;
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        child.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(child, row);
        host.Children.Add(child);
    }

    /// <summary>字节数转成人看的单位。</summary>
    private static string Fmt(long bytes)
    {
        if (bytes <= 0) return "—";
        var mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024
            ? (mb / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
            : mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>备份列表里的一行：名字 + 说明 +（还原 / 删除）两个按钮。</summary>
    private static Grid RecordRow(BackupRecord rec, string meta,
                                  string primaryText, Func<Task> onPrimary,
                                  string destructiveText, Func<Task> onDestructive)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = rec.DisplayName, TextWrapping = TextWrapping.Wrap });
        left.Children.Add(new TextBlock
        {
            Text = meta,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"],
        });
        Grid.SetColumn(left, 0);

        var primary = new Button { Content = primaryText, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(primary, 1);
        primary.Click += async (_, _) => await onPrimary();

        var destructive = new Button { Content = destructiveText, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(destructive, 2);
        destructive.Click += async (_, _) => await onDestructive();

        grid.Children.Add(left);
        grid.Children.Add(primary);
        grid.Children.Add(destructive);
        return grid;
    }

    /// <summary>把指定路径的 JSON 排序清单应用到当前模组列表。</summary>
    private async Task ApplyListFileAsync(string path)
    {
        if (_services.Bcml is null) return;
        try
        {
            if (!File.Exists(path))
            {
                await _window.ShowErrorAsync(Loc.T("common.failed", Loc.T("backup.restoreFromList")),
                    Loc.T("backup.missing"), null);
                return;
            }
            await ApplyListJsonAsync(await File.ReadAllTextAsync(path), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            await ShowError(Loc.T("common.failed", Loc.T("backup.restoreFromList")), ex);
        }
    }

    /// <summary>把当前清单存成 JSON 文件，并登记到备份台账。</summary>
    private async Task ExportModListToFileAsync()
    {
        if (_services.Bcml is null) return;
        try
        {
            var json = await _services.Bcml.ExportModListAsync();

            var picker = new FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(_window));
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.SuggestedFileName = Loc.T("mods.exportFileName", DateTime.Now.ToString("yyyyMMdd_HHmm"));
            picker.FileTypeChoices.Add(Loc.T("backup.kindList"), new List<string> { ".json" });

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            await File.WriteAllTextAsync(file.Path, json);

            // 登记进台账 —— 导出到哪个目录都无所谓，之后能在「备份」里找到并直接应用
            BackupIndex.Upsert(new BackupRecord
            {
                Kind = BackupKind.List,
                Name = Path.GetFileName(file.Path),
                Path = file.Path,
                Recorded = DateTimeOffset.Now,
                ModCount = _allRows.Count,
                Source = "app",
            });

            Notice.IsOpen = true;
            Notice.Severity = InfoBarSeverity.Success;
            Notice.Title = Loc.T("mods.exported");
            Notice.Message = file.Path;
        }
        catch (Exception ex)
        {
            await ShowError(Loc.T("mods.exportFailed"), ex);
        }
    }

    /// <summary>从 JSON 文件恢复排序 / 启用状态 / 合并器选项。</summary>
    private async Task ImportModListFromFileAsync()
    {
        if (_services.Bcml is null) return;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(_window));
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            await ApplyListJsonAsync(await File.ReadAllTextAsync(file.Path), Path.GetFileName(file.Path));
        }
        catch (Exception ex)
        {
            await ShowError(Loc.T("mods.importBadFormat"), ex);
        }
    }

    /// <summary>
    /// 校验并应用一份排序清单 JSON。
    /// 「从文件导入」和「从备份记录里还原」走同一条路，避免两处各写一遍校验与结果播报。
    /// </summary>
    private async Task ApplyListJsonAsync(string json, string fileName)
    {
        if (_services.Bcml is null) return;

        Core.Models.ModListProfile? doc;
        try
        {
            doc = System.Text.Json.JsonSerializer.Deserialize<Core.Models.ModListProfile>(
                json, Core.Models.Json.Options);
        }
        catch
        {
            doc = null;
        }

        if (doc is null || !doc.LooksValid)
        {
            await _window.ShowErrorAsync(Loc.T("mods.importBadFormat"),
                Loc.T("mods.importBadFormatHint"),
                Loc.T("mods.importBadFormatDetail"));
            return;
        }

        var importContent = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = fileName,
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = Loc.T("mods.importSummary", doc.Mods.Count, _allRows.Count),
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"],
                },
            },
        };
        if (!await Dialogs.ConfirmAsync(
                XamlRoot, Loc.T("mods.importConfirmTitle"), importContent,
                Loc.T("mods.importActionShort"), Loc.T("common.cancel"))) return;

        _window.SetBusy(true, Loc.T("mods.importing"), Loc.T("backup.listHint"));
        SetBusyUi(true);
        ApplyProfileResult result;
        try
        {
            result = await _services.Bcml.ApplyModListAsync(json);
        }
        finally
        {
            _window.SetBusy(false);
            SetBusyUi(false);
        }

        await LoadAsync();

        // ==== 导入结果：成功与警告分成两张卡片 ====
        // 成功卡只放「做成了什么」，警告卡只放「还需要你做什么」——
        // 混在一起时读者分不清哪句是完成、哪句是待办。
        ImportResultHeader.Text = Loc.T("mods.importDone");
        ImportOkCard.Title = Loc.T("mods.importOkCardTitle");
        ImportOkCard.Message = Loc.T("mods.importApplied", result.Applied)
            + (result.Missing.Count > 0 ? "\n" + Loc.T("mods.importMissing", result.Missing.Count) : "");

        var warnings = new List<string>();
        if (result.OptionFolderChanges.Count > 0)
        {
            warnings.Add(Loc.T("mods.importOptionDiff",
                string.Join("、", result.OptionFolderChanges.Take(3).Select(c => c.Name))));
        }
        // 「需执行重新合并后生效」不属于问题，只是提示下一步，但和警告同属
        // 「还需动手」那一类，放同一张卡里。
        warnings.Add(Loc.T("mods.reprocessPending"));

        ImportWarnCard.Title = Loc.T("mods.importWarnCardTitle");
        ImportWarnCard.Message = string.Join("\n", warnings);
        ImportWarnCard.IsOpen = true;

        ImportResultPanel.Visibility = Visibility.Visible;
        ImportResultPanel.IsExpanded = true;

        // 顶部那条通用通知不重复报同一件事，收起来。
        Notice.IsOpen = false;

        // 语言切换时卡片里的文字要跟着变，所以把结果留在字段里，ApplyLanguage 重刷。
        _lastImportResult = result;
    }

    /// <summary>最近一次导入的结果 —— 切换语言时用来重刷卡片文字。</summary>
    private ApplyProfileResult? _lastImportResult;

    /// <summary>按当前语言重刷导入结果卡片（切语言 / 重新进入页面时调用）。</summary>
    private void RefreshImportResultCards()
    {
        var result = _lastImportResult;
        if (result is null) return;

        ImportResultHeader.Text = Loc.T("mods.importDone");
        ImportOkCard.Title = Loc.T("mods.importOkCardTitle");
        ImportOkCard.Message = Loc.T("mods.importApplied", result.Applied)
            + (result.Missing.Count > 0 ? "\n" + Loc.T("mods.importMissing", result.Missing.Count) : "");

        var warnings = new List<string>();
        if (result.OptionFolderChanges.Count > 0)
        {
            warnings.Add(Loc.T("mods.importOptionDiff",
                string.Join("、", result.OptionFolderChanges.Take(3).Select(c => c.Name))));
        }
        warnings.Add(Loc.T("mods.reprocessPending"));

        ImportWarnCard.Title = Loc.T("mods.importWarnCardTitle");
        ImportWarnCard.Message = string.Join("\n", warnings);
        ImportWarnCard.IsOpen = true;
        ImportResultPanel.Visibility = Visibility.Visible;
    }

    /// <summary>调后端方法但把异常吞成 null —— 用于弹窗里「顺便列一下」的非关键数据。</summary>
    private static async Task<T?> SafeAsync<T>(Func<Task<T?>> action) where T : class
    {
        try { return await action(); }
        catch { return null; }
    }
}
