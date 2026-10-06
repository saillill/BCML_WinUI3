namespace BCML.WinUI3.App.Services;

/// <summary>
/// 各类 ContentDialog 内容区的高度上限（单位：像素）。
///
/// 为什么要集中：这三个数字原先散落在 MainWindow.xaml.cs 与 ModsPage.xaml.cs 里各写各的，
/// 用途相同的两个弹窗（模组选项 / 备份中心）分别写成 520 和 560，视觉上高度不一且无明显理由。
/// 抽成常量后，同类弹窗只有一个取值来源，改一处即全局生效。
///
/// 取值说明：
///   · <see cref="Help"/>   —— 纯文本帮助（短、可滚动），不需要太高；
///   · <see cref="Option"/> —— 表单/选项类（模组安装选项等）：<b>同类弹窗统一用这一个</b>；
///   · <see cref="List"/>   —— 备份中心这类长列表；
///   · <see cref="Code"/>   —— 日志/原文本代码块预览。
/// </summary>
internal static class DialogSizing
{
    /// <summary>帮助文本类弹窗。</summary>
    public const double Help = 420;

    /// <summary>表单/选项类弹窗（模组安装选项、模组选项编辑器、备份中心等，统一高度）。</summary>
    public const double Option = 540;

    /// <summary>长列表类弹窗（备份中心）。</summary>
    public const double List = 540;

    /// <summary>代码/日志原文预览（等宽字体）。</summary>
    public const double Code = 220;
}
