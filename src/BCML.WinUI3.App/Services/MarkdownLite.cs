using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using BCML.WinUI3.Core.Services;

namespace BCML.WinUI3.App.Services;

/// <summary>
/// 把模组描述渲染进 <see cref="RichTextBlock"/>。
///
/// 为什么不用现成的 Markdown 控件：描述文本来自 bnp 的 info.json，格式很杂 ——
/// 既有 Markdown（**粗体**、- 列表、# 标题），也混着 HTML（&lt;br&gt;、&amp;nbsp;、&lt;a&gt;）。
/// 拉一个完整 Markdown 实现（还带 WebView 依赖）不划算，这里只覆盖实际会出现的语法。
/// </summary>
public static class MarkdownLite
{
    private static readonly Regex InlineRe = new(
        @"(\*\*(?<b>[^*]+)\*\*)" +          // **粗体**
        @"|(\*(?<i>[^*]+)\*)" +              // *斜体*
        @"|(`(?<c>[^`]+)`)" +                // `代码`
        @"|(\[(?<lt>[^\]]+)\]\((?<lu>[^)]+)\))" +  // [文字](链接)
        @"|(?<u>https?://[^\s<>()]+)",       // 裸链接
        RegexOptions.Compiled);

    private static readonly Regex HtmlBrRe = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlATagRe = new(@"<a[^>]*href=""(?<u>[^""]+)""[^>]*>(?<t>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlTagRe = new(@"<[^>]+>", RegexOptions.Compiled);
    // <li> 必须忽略大小写：描述里 <LI> / <Li> 都出现过，只替小写会漏掉。
    // 顺带把 <li ...> 带属性的写法也吃掉。
    private static readonly Regex HtmlLiOpenRe = new(@"<li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlLiCloseRe = new(@"</li\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadingRe = new(@"^(?<h>#{1,6})\s+(?<t>.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletRe = new(@"^\s*[-*+]\s+(?<t>.*)$", RegexOptions.Compiled);
    private static readonly Regex QuoteRe = new(@"^\s*>\s?(?<t>.*)$", RegexOptions.Compiled);
    private static readonly Regex HrRe = new(@"^\s*(-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled);

    public static void Render(RichTextBlock target, string? text)
    {
        target.Blocks.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            target.Blocks.Add(new Paragraph
            {
                Inlines = { new Run { Text = Loc.T("mods.noDescription"), FontStyle = FontStyle.Italic } },
            });
            return;
        }

        foreach (var paragraph in ToParagraphs(Normalize(text)))
        {
            target.Blocks.Add(paragraph);
        }
    }

    /// <summary>把 HTML 片段压成纯文本骨架，保留换行。</summary>
    private static string Normalize(string s)
    {
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        s = HtmlATagRe.Replace(s, m => $"[{m.Groups["t"].Value.Trim()}]({m.Groups["u"].Value})");
        s = HtmlBrRe.Replace(s, "\n");
        s = HtmlLiOpenRe.Replace(s, "\n- ");
        s = HtmlLiCloseRe.Replace(s, "");

        // 实体解码顺序有讲究：`&amp;` 必须放**最后**。
        // 放前面的话 `&amp;lt;` 会先变成 `&lt;` 再被解成 `<` —— 而按 HTML 规范
        // `&amp;lt;` 表示的应该是字面量 `&lt;`，多解一层就把用户写的文本吃掉了。
        s = s.Replace("&nbsp;", " ").Replace("&lt;", "<").Replace("&gt;", ">")
             .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
             .Replace("&amp;", "&");

        s = HtmlTagRe.Replace(s, "");
        return s;
    }

    private static IEnumerable<Paragraph> ToParagraphs(string text)
    {
        var lines = text.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                // 连续空行只在块之间留一个间隙，不产出一堆空段
                if (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]))
                {
                    yield return new Paragraph { LineHeight = 4 };
                }
                i++;
                continue;
            }

            if (HrRe.IsMatch(line))
            {
                yield return new Paragraph { LineHeight = 2 };
                i++;
                continue;
            }

            var h = HeadingRe.Match(line);
            if (h.Success)
            {
                var level = h.Groups["h"].Value.Length;
                var p = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = level switch { 1 => 20, 2 => 17, _ => 15 } };
                AddInlines(p, h.Groups["t"].Value);
                yield return p;
                i++;
                continue;
            }

            var b = BulletRe.Match(line);
            if (b.Success)
            {
                var p = new Paragraph { Margin = new Thickness(12, 0, 0, 0) };
                p.Inlines.Add(new Run { Text = "• " });
                AddInlines(p, b.Groups["t"].Value);
                yield return p;
                i++;
                continue;
            }

            var q = QuoteRe.Match(line);
            if (q.Success)
            {
                var p = new Paragraph
                {
                    Margin = new Thickness(12, 0, 0, 0),
                    FontStyle = FontStyle.Italic,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                };
                AddInlines(p, q.Groups["t"].Value);
                yield return p;
                i++;
                continue;
            }

            var para = new Paragraph();
            AddInlines(para, line);
            yield return para;
            i++;
        }
    }

    private static void AddInlines(Paragraph paragraph, string line)
    {
        var last = 0;
        foreach (Match m in InlineRe.Matches(line))
        {
            if (m.Index > last) paragraph.Inlines.Add(new Run { Text = line[last..m.Index] });
            last = m.Index + m.Length;

            if (m.Groups["b"].Success)
            {
                paragraph.Inlines.Add(new Bold { Inlines = { new Run { Text = m.Groups["b"].Value } } });
            }
            else if (m.Groups["i"].Success)
            {
                paragraph.Inlines.Add(new Italic { Inlines = { new Run { Text = m.Groups["i"].Value } } });
            }
            else if (m.Groups["c"].Success)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = m.Groups["c"].Value,
                    FontFamily = new FontFamily("Consolas"),
                });
            }
            else if (m.Groups["lt"].Success)
            {
                TryAddLink(paragraph, m.Groups["lt"].Value, m.Groups["lu"].Value);
            }
            else if (m.Groups["u"].Success)
            {
                TryAddLink(paragraph, m.Groups["u"].Value, m.Groups["u"].Value);
            }
        }
        if (last < line.Length) paragraph.Inlines.Add(new Run { Text = line[last..] });
    }

    private static void TryAddLink(Paragraph paragraph, string label, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            paragraph.Inlines.Add(new Run { Text = label });
            return;
        }
        var link = new Hyperlink { NavigateUri = uri };
        link.Inlines.Add(new Run { Text = label });
        paragraph.Inlines.Add(link);
    }
}
