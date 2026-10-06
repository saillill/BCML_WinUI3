using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BCML.WinUI3.Core.Models;
using BCML.WinUI3.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BCML.WinUI3.App.Services;

/// <summary>
/// 「重新选择模组选项」对话框。
///
/// 有些 BNP（例如「林可儿 3.0」「少女动作包」）带好几组可选组件，安装时选过一次之后
/// BCML 就没有入口再改了。这里把 mod 目录里 options/ 下的变体重新列出来让用户改选。
///
/// 与后端的分工：
///   · 本对话框只负责收集用户勾了哪些 folder；
///   · 真正的"重建 options/ 目录"由 <c>sys.applyModOptions</c> 做（它会先删干净再从
///     原始快照重建，所以不会残留旧选择）；
///   · 让改动生效还需要一次重新合并 —— 由调用方在返回 true 后触发。
/// </summary>
public static class ModOptionsDialog
{
    /// <summary>弹出对话框。返回 true 表示用户确认并且选项已经写到磁盘（尚未合并）。</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, AppServices services, ModOptionsInfo info)
    {
        // 记录每个可变体对应的勾选控件；确认时统一收集
        var multiChecks = new List<(string Folder, CheckBox Box)>();
        var singleRadios = new List<(string Folder, RadioButton Box)>();

        var panel = new StackPanel { Spacing = 14, MinWidth = 460 };

        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("modOptions.intro"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"],
        });

        // ---- 单选组：每组一个 RadioButtons
        foreach (var group in info.Single)
        {
            // 不再按 Exists 过滤！BCML 安装时会把没选中的变体从磁盘删掉，
            // 只看 Exists 的话用户就只看得到"当前已经选过的那几个"，根本换不了。
            // 后端现在按 info.json 的定义返回全量清单，并标好哪些是可恢复的。
            var usable = group.Options.Where(o => o.Exists).ToList();
            var broken = group.Options.Where(o => !o.Exists).ToList();
            if (usable.Count == 0 && broken.Count == 0) continue;

            var section = new StackPanel { Spacing = 6 };
            section.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(group.Label) ? Loc.T("modOptions.group") : group.Label,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            });
            if (!string.IsNullOrWhiteSpace(group.Description) &&
                !string.Equals(group.Description, group.Label, StringComparison.Ordinal))
            {
                section.Children.Add(new TextBlock
                {
                    Text = group.Description,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["CaptionSecondaryTextStyle"],
                });
            }

            var radios = new RadioButtons();
            foreach (var opt in usable)
            {
                var radio = new RadioButton
                {
                    Content = BuildOptionContent(opt),
                    Tag = opt.Folder,
                };
                if (opt.Selected) radio.IsChecked = true;
                radios.Items.Add(radio);
                singleRadios.Add((opt.Folder, radio));
            }
            // 拿不回来的变体也列出来，但禁用 —— 让用户知道"这里本来有这一项"
            foreach (var opt in broken) radios.Items.Add(BuildUnavailableRow(opt));
            section.Children.Add(radios);
            panel.Children.Add(section);
        }

        // ---- 多选项：一堆复选框
        var multiUsable = info.Multi.Where(o => o.Exists).ToList();
        var multiBroken = info.Multi.Where(o => !o.Exists).ToList();
        if (multiUsable.Count > 0 || multiBroken.Count > 0)
        {
            var section = new StackPanel { Spacing = 6 };
            section.Children.Add(new TextBlock
            {
                Text = Loc.T("modOptions.optional"),
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            });
            foreach (var opt in multiUsable)
            {
                var box = new CheckBox
                {
                    Content = BuildOptionContent(opt),
                    IsChecked = opt.Selected,
                    Tag = opt.Folder,
                };
                section.Children.Add(box);
                multiChecks.Add((opt.Folder, box));
            }
            foreach (var opt in multiBroken) section.Children.Add(BuildUnavailableRow(opt));
            panel.Children.Add(section);
        }

        if (singleRadios.Count == 0 && multiChecks.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("modOptions.none"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (!await Dialogs.ConfirmAsync(
                root, Loc.T("modOptions.title", info.Name), panel,
                Loc.T("modOptions.apply"), Loc.T("common.cancel"),
                maxContentHeight: DialogSizing.Option)) return false;

        // ---- 收集选择
        var selects = new List<string>();
        foreach (var (folder, box) in multiChecks)
        {
            if (box.IsChecked == true) selects.Add(folder);
        }
        foreach (var (folder, radio) in singleRadios)
        {
            if (radio.IsChecked == true) selects.Add(folder);
        }

        // 没有任何变化就不用打扰后端
        var before = info.Selected.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var after = selects.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (before.SequenceEqual(after, StringComparer.Ordinal))
        {
            AppServices.Diag("选项重选：选择没有变化，跳过");
            return false;
        }

        await services.Bcml!.ApplyModOptionsAsync(info.Mod, selects);
        return true;
    }

    /// <summary>选项的展示内容：名字 +（第二行）说明。</summary>
    private static object BuildOptionContent(ModOptionItem opt)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(opt.Name) ? opt.Folder : opt.Name,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(opt.Description))
        {
            stack.Children.Add(new TextBlock
            {
                Text = opt.Description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
        }
        return stack;
    }

    /// <summary>
    /// 「已不可用」的选项行：显示成灰色、不可勾选，并注明原因。
    ///
    /// 为什么要显示而不是直接藏起来：这一项在 info.json 里确实存在，
    /// 只是原始 bnp 找不到了、解不出来。藏起来的话用户会以为"这 mod 本来就没这选项"，
    /// 而不是"我这边丢文件了、需要重装"—— 前者会让人一直等一个永远不会出现的按钮。
    /// </summary>
    private static UIElement BuildUnavailableRow(ModOptionItem opt)
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 2) };
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(opt.Name) ? opt.Folder : opt.Name,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorDisabledBrush"],
        });
        stack.Children.Add(new TextBlock
        {
            Text = Loc.T("modOptions.unavailable"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorDisabledBrush"],
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });

        // 用 IsHitTestVisible=false 而不是 IsEnabled=false：后者在某些主题下
        // 会给整行加一层不透明底色，看起来像"选中了"。这里只要不能点就行。
        return new Border
        {
            Child = stack,
            Padding = new Thickness(8, 2, 8, 2),
            IsHitTestVisible = false,
            Opacity = 0.6,
        };
    }
}
