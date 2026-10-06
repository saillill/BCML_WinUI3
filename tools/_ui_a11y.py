#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""给详情区那排纯图标按钮补 AutomationProperties.Name。一次性脚本。

原来只设了 ToolTip —— 界面上是一排"无名按钮"：
读屏软件念不出来，UI Automation 也只能拿到空名字（自动化和辅助功能都不友好）。
"""
from pathlib import Path

CS = Path(__file__).resolve().parent.parent / "src" / "BCML.WinUI3.App" / "Pages" / "ModsPage.xaml.cs"
NL = chr(10)
cs = CS.read_text(encoding="utf-8")


def sub(old: str, new: str, note: str) -> None:
    global cs
    got = cs.count(old)
    assert got == 1, f"{note}：期望 1 处，实际 {got} 处"
    cs = cs.replace(old, new)
    print("  ✓ " + note)


sub("using Microsoft.UI.Xaml.Automation;", "using Microsoft.UI.Xaml.Automation;", "using 已存在") \
    if "using Microsoft.UI.Xaml.Automation;" in cs else None

if "using Microsoft.UI.Xaml.Automation;" not in cs:
    lines = cs.split(NL)
    last = max(i for i, l in enumerate(lines) if l.startswith("using "))
    lines.insert(last + 1, "using Microsoft.UI.Xaml.Automation;")
    cs = NL.join(lines)
    print("  ✓ 补 using Microsoft.UI.Xaml.Automation")

sub("""        ToolTipService.SetToolTip(ExploreButton, Loc.T("mods.explore") + " —— " + Loc.T("mods.exploreHint"));
        ToolTipService.SetToolTip(UrlButton, Loc.T("mods.source"));
        ToolTipService.SetToolTip(UpdateButton, Loc.T("mods.update") + " —— " + Loc.T("mods.updateHint"));
        ToolTipService.SetToolTip(UpButton, Loc.T("mods.moveUp"));
        ToolTipService.SetToolTip(DownButton, Loc.T("mods.moveDown"));
        ToolTipService.SetToolTip(ReprocessButton, Loc.T("mods.reprocess"));
        ToolTipService.SetToolTip(UninstallButton, Loc.T("mods.uninstall"));""",
    """        LabelIconButton(ExploreButton, Loc.T("mods.explore"), Loc.T("mods.exploreHint"));
        LabelIconButton(UrlButton, Loc.T("mods.source"));
        LabelIconButton(UpdateButton, Loc.T("mods.update"), Loc.T("mods.updateHint"));
        LabelIconButton(UpButton, Loc.T("mods.moveUp"));
        LabelIconButton(DownButton, Loc.T("mods.moveDown"));
        LabelIconButton(ReprocessButton, Loc.T("mods.reprocess"));
        LabelIconButton(OptionsButton, Loc.T("modOptions.tip"));
        LabelIconButton(UninstallButton, Loc.T("mods.uninstall"));""",
    "详情按钮统一走 LabelIconButton")

sub("""            ToolTipService.SetToolTip(ToggleEnabledButton, has && row!.Enabled
                ? Loc.T("mods.disable") + " —— " + Loc.T("mods.disableHint")
                : Loc.T("mods.enable"));""",
    """        LabelIconButton(ToggleEnabledButton,
                has && row!.Enabled ? Loc.T("mods.disable") : Loc.T("mods.enable"),
                has && row!.Enabled ? Loc.T("mods.disableHint") : null);""",
    "启用/禁用按钮的提示与可访问名一起更新")

sub("""            ToolTipService.SetToolTip(OptionsButton, Loc.T("modOptions.tip"));""" + NL,
    "",
    "去掉后面重复设按钮名的那行")

sub("    private void SetBusyUi(bool busy)",
    """    /// <summary>
    /// 图标按钮统一走这里：同时设 ToolTip 和 AutomationProperties.Name。
    /// 只设 ToolTip 的话，读屏软件和 UI Automation 拿到的按钮名是**空的** ——
    /// 界面上就是一排「无名按钮」，辅助功能和自动化测试都没法用。
    /// </summary>
    private static void LabelIconButton(Button button, string name, string? hint = null)
    {
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, hint is null ? name : name + " —— " + hint);
    }

    private void SetBusyUi(bool busy)""",
    "新增 LabelIconButton 辅助方法")

CS.write_text(cs, encoding="utf-8")
print("已写入")
