using System;
using System.Text;
using BCML.WinUI3.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using BCML.WinUI3.Core.Services;

namespace BCML.WinUI3.App.Pages;

/// <summary>
/// 后端日志页。
///
/// 渲染方式刻意做成「原始控制台」：**一个只读 TextBox 承载全部文本**，等宽、不换行、
/// 可整段选中复制。不用 ListBox 逐行放 TextBlock —— 那样既无法跨行选中，
/// 每行还要各自创建控件，合并时几千行下来开销很明显。
///
/// 刷新按 200ms 节流；**只要用户正在选中文本就暂停刷新**，避免复制到一半被冲掉。
/// </summary>
public sealed class LogPage : Page
{
    private const int MaxChars = 2_000_000;
    private const int MaxLines = 8000;

    private readonly AppServices _services;
    private readonly StringBuilder _buffer = new();
    private readonly TextBox _view = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(12, 8, 12, 8),
        IsSpellCheckEnabled = false,
    };
    private readonly TextBlock _status = new();
    private readonly TextBlock _title = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly ToggleButton _autoScroll = new()
    {
        Content = Loc.T("logs.autoScroll"),
        IsChecked = true,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Button _copyAll = new() { Content = Loc.T("logs.copyAll") };
    private readonly Button _clear = new() { Content = Loc.T("logs.clear") };

    private bool _dirty;
    private bool _autoScrollOn = true;
    private bool _paused;

    public LogPage(AppServices services, MainWindow window)
    {
        _services = services;
        _ = window;

        // TextBox 自身没有滚动条属性，要用 ScrollViewer 的附加属性设置
        ScrollViewer.SetHorizontalScrollBarVisibility(_view, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_view, ScrollBarVisibility.Auto);

        var header = new Grid { ColumnSpacing = 8, Padding = new Thickness(20, 16, 20, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 4; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _title.Text = Loc.T("logs.title");
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Style = (Style)Application.Current.Resources["TitleTextBlockStyle"];
        Grid.SetColumn(_title, 0);

        _status.Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"];
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(_status, 1);

        _autoScroll.Checked += (_, _) => { _autoScrollOn = true; ScrollToEnd(); };
        _autoScroll.Unchecked += (_, _) => _autoScrollOn = false;
        Grid.SetColumn(_autoScroll, 2);

        _copyAll.Click += (_, _) =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(_view.Text ?? "");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            _status.Text = Loc.T("logs.copied");
        };
        Grid.SetColumn(_copyAll, 3);

        _clear.Click += (_, _) =>
        {
            _buffer.Clear();
            _view.Text = "";
            _dirty = false;
            UpdateStatus();
        };
        Grid.SetColumn(_clear, 4);

        header.Children.Add(_title);
        header.Children.Add(_status);
        header.Children.Add(_autoScroll);
        header.Children.Add(_copyAll);
        header.Children.Add(_clear);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(_view, 1);
        root.Children.Add(header);
        root.Children.Add(_view);
        Content = root;

        // 节流：日志是逐行推来的，合并时每秒可能上百行
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) => Flush();
        _timer.Start();

        Loaded += (_, _) =>
        {
            _buffer.Clear();
            foreach (var line in _services.Snapshot()) AppendLine(line);
            _dirty = true;
            Flush();
            AppServices.Diag($"日志页已加载，缓冲 {_buffer.Length} 字符");
        };
        _services.LogAppended += (_, line) => Append(line);

        // 界面语言切换时把标题/按钮文案刷一遍。
        // 用具名方法订阅读取方便退订 —— Loc.LanguageChanged 是静态事件，
        // 挂上去就永远活着，页面被换掉后回调还会打在旧实例上。
        Loc.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(ApplyLanguage);
    }

    private void ApplyLanguage()
    {
        _title.Text = Loc.T("logs.title");
        _autoScroll.Content = Loc.T("logs.autoScroll");
        _copyAll.Content = Loc.T("logs.copyAll");
        _clear.Content = Loc.T("logs.clear");
        _status.Text = Loc.T("logs.lines", CountLines());
    }

    private int CountLines()
    {
        var n = 0;
        foreach (var c in _buffer.ToString()) if (c == '\n') n++;
        return n;
    }

    /// <summary>可从任意线程调用。</summary>
    public void Append(string line)
    {
        var queue = DispatcherQueue;
        if (queue is null) return;
        queue.TryEnqueue(() =>
        {
            AppendLine(line);
            _dirty = true;
        });
    }

    private void AppendLine(string line)
    {
        _buffer.Append(line).Append('\n');
        TrimIfNeeded();
    }

    private void TrimIfNeeded()
    {
        if (_buffer.Length <= MaxChars) return;
        var text = _buffer.ToString();
        var cut = text.IndexOf('\n', text.Length - MaxChars / 2);
        if (cut < 0) cut = text.Length - MaxChars / 2;
        _buffer.Clear();
        _buffer.Append(text, cut + 1, text.Length - cut - 1);
    }

    private void Flush()
    {
        if (!_dirty) return;

        // 用户正在选中文本 → 暂停刷新，免得复制到一半被冲掉
        var selecting = (_view.SelectionLength > 0) || (_view.SelectionStart > 0 && _view.FocusState != FocusState.Unfocused);
        if (selecting)
        {
            if (!_paused)
            {
                _paused = true;
                UpdateStatus();
            }
            return;
        }
        if (_paused)
        {
            _paused = false;
        }

        // 文本太长时只保留尾部，避免 TextBox 卡顿
        var text = _buffer.ToString();
        var lines = 0;
        for (var i = text.Length - 1; i >= 0 && lines < MaxLines; i--)
        {
            if (text[i] == '\n') lines++;
        }
        if (lines >= MaxLines)
        {
            var cut = text.IndexOf('\n', text.Length - text.Length / 2);
            if (cut > 0) text = Loc.T("logs.trimmed") + "\n" + text[(cut + 1)..];
        }

        _view.Text = text;
        _dirty = false;
        UpdateStatus();
        if (_autoScrollOn) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        try { _view.Select(_view.Text.Length, 0); }
        catch { /* 忽略 */ }
    }

    private void UpdateStatus()
    {
        var nl = 0;
        var span = _buffer.ToString().AsSpan();
        foreach (var c in span) if (c == '\n') nl++;
        _status.Text = _paused
            ? Loc.T("logs.linesPaused", nl)
            : Loc.T("logs.lines", nl);
    }
}
