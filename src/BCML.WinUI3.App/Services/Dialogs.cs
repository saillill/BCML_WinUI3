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
            ? new ScrollViewer { Content = content, MaxHeight = maxContentHeight }
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
            Content = new ScrollViewer { Content = content, MaxHeight = maxHeight },
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }
}
