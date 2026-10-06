using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace BCML.WinUI3.App.Controls;

/// <summary>
/// 自动换行的横向排列面板。
///
/// WinUI 3 没有内置 WrapPanel（ItemsRepeater 的 UniformGridLayout 又要求所有项等宽），
/// 而详情面板的操作按钮需要「宽窗口一行、窄窗口自动折成两行」，所以手写一个最小实现。
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(6.0, OnSpacingChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(6.0, OnSpacingChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WrapPanel)d).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = availableSize.Width;
        var lineWidth = 0.0;
        var lineHeight = 0.0;
        var totalWidth = 0.0;
        var totalHeight = 0.0;
        var placed = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // Collapsed 的项既不该占宽度、也不该占间距（详情区里「来源」「重新处理」
            // 会按需折叠，不跳过的话会留下一条空档）。
            if (child.Visibility == Visibility.Collapsed) continue;

            var d = child.DesiredSize;
            placed++;

            if (placed > 1 && !double.IsInfinity(limit) &&
                lineWidth + HorizontalSpacing + d.Width > limit)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + VerticalSpacing;
                lineWidth = d.Width;
                lineHeight = d.Height;
            }
            else
            {
                lineWidth += (placed > 1 ? HorizontalSpacing : 0) + d.Width;
                lineHeight = Math.Max(lineHeight, d.Height);
            }
        }

        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;

        return new Size(
            double.IsInfinity(limit) ? totalWidth : Math.Min(totalWidth, limit),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 一次遍历算出每项的 (行号, 行内 x) 与每行高度，再按 VerticalAlignment
        // 把项对齐到行中线 —— 否则页头里「标题 + 若干按钮」混排时，矮的项会贴顶，很乱。
        var n = Children.Count;
        var xs = new double[n];
        var rows = new int[n];
        var lineHeights = new List<double>();

        var x = 0.0;
        var lineHeight = 0.0;
        var row = 0;
        var placed = 0;
        for (var i = 0; i < n; i++)
        {
            var child = Children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                // 折叠项：不参与排布，行号也置 -1，下面跳过它
                rows[i] = -1;
                continue;
            }

            var d = child.DesiredSize;
            if (placed > 0 && x + HorizontalSpacing + d.Width > finalSize.Width)
            {
                lineHeights.Add(lineHeight);
                row++;
                x = 0;
                lineHeight = 0;
            }
            else if (placed > 0)
            {
                x += HorizontalSpacing;
            }

            xs[i] = x;
            rows[i] = row;
            placed++;
            x += d.Width;
            lineHeight = Math.Max(lineHeight, d.Height);
        }
        lineHeights.Add(lineHeight);

        var tops = new double[lineHeights.Count];
        var acc = 0.0;
        for (var i = 0; i < lineHeights.Count; i++)
        {
            tops[i] = acc;
            acc += lineHeights[i] + VerticalSpacing;
        }

        for (var i = 0; i < n; i++)
        {
            if (rows[i] < 0) continue;
            var child = Children[i];
            var d = child.DesiredSize;
            var lh = lineHeights[rows[i]];
            var va = (child as FrameworkElement)?.VerticalAlignment ?? VerticalAlignment.Top;
            var top = tops[rows[i]] + va switch
            {
                VerticalAlignment.Center => (lh - d.Height) / 2,
                VerticalAlignment.Bottom => lh - d.Height,
                _ => 0,
            };
            child.Arrange(new Rect((float)xs[i], (float)top, (float)d.Width, (float)d.Height));
        }

        return finalSize;
    }
}
