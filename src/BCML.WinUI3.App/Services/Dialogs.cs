using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BCML.WinUI3.App.Services;

/// <summary>
/// ContentDialog 的公共构造帮助方法。
///
/// 为什么要有：项目里原先有 12 处各写各的 <c>new ContentDialog { ... }</c>，其中 8 处的形态
/// 完全一样（标题 + 内容 + 主按钮 + 取消/关闭），只是文案不同。重复模板带来的问题是
/// <see cref="ContentDialog.XamlRoot"/> 这种**必须设置**的属性很容易在新增弹窗时漏掉
/// （漏了就会在非首个窗口上抛"XamlRoot is not set"）。集中到一处后，调用方只传文案，
/// 结构由这里保证。
///
/// 注意：这里只统一**结构**，不统一文案 —— 每个弹窗的中文/英文仍由调用方用 <c>Loc.T</c> 传入，
/// 所以不会损失各弹窗自己的语义。
/// </summary>
internal static class Dialogs
{
    /// <summary>
    /// 滚动条要让开的宽度。WinUI 的 <c>ScrollBarSize</c> 是 16px
    /// （<c>ScrollViewerScrollBarMargin</c> 再加 1），留 20 有富余。
    /// </summary>
    private const double ScrollBarGutter = 20;

    /// <summary>
    /// 给 ContentDialog 的内容套一层带高度上限的滚动容器，并为右侧滚动条留出位置。
    ///
    /// **为什么要留位置。** ScrollViewer 的默认模板里，<c>ScrollContentPresenter</c>
    /// 带 <c>Grid.ColumnSpan="2"</c> —— 内容横跨「含滚动条那一列」的整个宽度，
    /// 于是滚动条是**叠在内容右边缘上**的。调 ScrollViewer 自己的 Padding 解决不了
    /// （它只把内容整体缩一圈，右边缘照样顶在滚动条底下）。要让开，只能真正缩窄
    /// 内容的可视宽度，也就是给内容加右侧 Margin。
    ///
    /// 外面再包一层 Grid 而不是直接改调用方传进来的元素：那样会**修改调用方的对象**，
    /// 弹出的对话框若被复用或调用方另有引用，会莫名其妙多出一个 Margin。
    /// </summary>
    private static ScrollViewer ScrollHost(object content, double maxHeight)
    {
        var shell = new Grid { Margin = new Thickness(0, 0, ScrollBarGutter, 0) };
        shell.Children.Add(content as UIElement ?? new TextBlock { Text = content?.ToString() ?? "" });

        return new ScrollViewer
        {
            Content = shell,
            MaxHeight = maxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>
    /// 标准确认弹窗：返回用户是否点了主按钮（Primary）。
    ///
    /// <paramref name="primaryIsDefault"/> 为 false 时把默认焦点放在「取消」上，
    /// 用于让危险操作（卸载等）不会因误回车而直接执行。
    ///
    /// <paramref name="maxContentHeight"/> 大于 0 时，把内容包进一个带高度上限的
    /// ScrollViewer —— 内容可能是长长的选项列表（模组选项）或好几段说明。
    /// </summary>
    public static async Task<bool> ConfirmAsync(
        XamlRoot xamlRoot,
        string title,
        object content,
        string primaryText,
        string cancelText,
        bool primaryIsDefault = true,
        string? secondaryText = null,
        double maxContentHeight = 0)
    {
        object finalContent = maxContentHeight > 0
            ? ScrollHost(content, maxContentHeight)
            : content;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = finalContent,
            PrimaryButtonText = primaryText,
            CloseButtonText = cancelText,
            DefaultButton = primaryIsDefault ? ContentDialogButton.Primary : ContentDialogButton.Close,
        };
        if (!string.IsNullOrEmpty(secondaryText))
            dialog.SecondaryButtonText = secondaryText;

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 只读展示弹窗：内容放进一个带高度上限的 <see cref="ScrollViewer"/>，
    /// 底部只有一个「知道了 / 关闭」按钮。用于帮助文本、备份列表、原文本预览等。
    /// </summary>
    public static async Task ShowScrollableAsync(
        XamlRoot xamlRoot,
        string title,
        UIElement content,
        double maxHeight,
        string closeText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = ScrollHost(content, maxHeight),
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }
}
