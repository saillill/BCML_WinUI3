using System;
using System.Collections.Generic;
using BCML.WinUI3.Core.Services;

namespace BCML.WinUI3.App.Services;

/// <summary>
/// 忙碌遮罩上的俏皮标语。
///
/// 原版 BCML 的做法（<c>assets/src/js/Progress.jsx</c>）：一组固定文案，弹窗出现时**洗牌**，
/// 每 5 秒换一条，循环播放。这里保留同一套文案与节奏，并给出完整中文对照。
///
/// 原版说明里那句「Believe it or not, real progress updates are not an option」
/// 是有意为之 —— BCML 后端并**不上报真实进度**，所以本应用同样只做转圈 + 标语，
/// 不去伪造一个百分比。
///
/// 关于语言：<see cref="Original"/> 是**英文原文**（与上游逐条一致，作为唯一权威表）。
/// 界面语言为中文时走 <see cref="ZhMap"/> 取中文；非中文一律回落到英文原文 ——
/// 所以英文界面显示英文，中文界面显示中文，两边都不会串。
///
/// 关于那几条带 &lt;a&gt; 链接的文案：上游用 React 的 dangerouslySetInnerHTML 把它渲染成真超链接，
/// 而本应用是 WinUI 的 TextBlock（纯文本）。直接照抄会在界面上显示一堆
/// <c>&lt;a href='...'&gt;</c> 标签 —— 所以这里**去掉标签、只留可读文本与网址**。
/// </summary>
public static class BusyMessages
{
    /// <summary>原版 100 条文案（与 Progress.jsx 逐条对应，顺序也一致）。</summary>
    private static readonly string[] Original =
    {
        "Finding Koroks",
        "Polishing Master Sword",
        "Dancing with Bokoblins",
        "Stealing bananas from Yigas",
        "Eating raw meat like a chad",
        "Thinking about Zelda's warm embrace",
        "Slaying Lynels by the dozen",
        "Spamming Urbosa's Fury",
        "Running away from Guardians",
        "Detonating remote bombs",
        "Avoiding Beedle",
        "Exacting revenge on Magda",
        "Debating between Hylia and the Golden Goddesses",
        "Oh, look, more opal",
        "Attempting to climb a mountain in the rain",
        "Thinking about Mipha's slimy embrace",
        "Finding yet more Koroks",
        "Disturbing the Monk's Sleep",
        "Slashing Cuccos since 1991",
        "Becoming a Pot Lid Hero",
        "Ragdolling like a Goron",
        "Not running at 60FPS",
        "That mountain over there, I can't reach it",
        "\"O Epona, Epona, wherefore art thou Epona?\"",
        "The batteries are about to run out again",
        "Oh look, yet another Korok",
        "Cooking only hearty foods",
        "Friend-zoning Paya",
        "Hiding secrets from everybody",
        "Riding a shrine elevator",
        "Bow-spinning mods",
        "Eating a Royal Claymore",
        "Ignoring the old man",
        "Selling the Sheikah Slate",
        "Annoying the monks",
        "Pushing Master Kohga",
        "Deleting the Great Plateau",
        "Moisturizing Ganon",
        "Going back to bed",
        "100 more years never hurt anyone",
        "Changing name and joining a construction company",
        "Trapping fairies in cooking pot",
        "Flushing Hestu's gift",
        "Bullet time? What's a bullet?",
        "Pretending to remember Zelda",
        "I can't go any farther",
        "\"Linkle, you're going the wrong way!\"",
        "Believe it or not, real progress updates are not an option",
        "BCML tip: When in doubt, remerge",
        "BCML tip: The in-app help has a lot of information",
        "BCML tip: Read the in-app help",
        "BCML tip: Questions or problems? Try the in-app help",
        "BCML tip: To reorder your mods, turn on Sort Mode",
        "BCML tip: Ctrl-Click to select multiple mods",
        "BCML tip: Higher number priority overrides lower number priority",
        "BCML tip: When using a set of related mods, put the base mod beneath the addons",
        "BCML tip: Back up your mods when making substantial changes",
        "BCML tip: The backup/restore feature can be used for mod \"profiles\"",
        "Shameless plug: I blog at calebdixonsmith.top — Theology Without Warranty",
        "Support BCML on Patreon (patreon.com/nicenenerd) so my wife will let me keep doing this",
        "While Link fights Ganon, I fight bad theology on TikTok (@nicenenerd)",
        "Downloading RAM so Link goes fast",
        "Feeding Koroks",
        "Leaving Ganon in peace",
        "Selling apples to Koroks",
        "Becoming a traveling merchant",
        "Stalking Beedle",
        "Creating a paper currency",
        "Planting Korok seeds",
        "Fleeing Daruk's crushing embrace",
        "Turning the Shrine of Resurrection into a tourist attraction",
        "Climbing Death Mountain in the buff",
        "Accidentally stoning a Korok",
        "Dying to Dark Beast Ganon",
        "Building a Master Rocket Zero to the moon",
        "Taking bets on Sand Seal races",
        "Upgrading to the Master Cycle One",
        "Ascending to High Hrothgar—oh wait, wrong game",
        "Collecting monk dust for elixirs",
        "Chasing a Hinox eyeball",
        "Abusing ghost rabbits for money",
        "Making TikToks on Sheikah Slate",
        "Old enough to save Hyrule, too young for The Noble Canteen",
        "\"Hold on Zelda, only 842 more Koroks!\"",
        "\u201cWhat we call Man's power over Nature turns out to be a power exercised by some men over other men with Nature as its instrument.\u201d",
        "\u201cNot even Spider-Man climbs like me!\u201d",
        "Vandalizing private homes to find money",
        "Enjoying some rock-hard food",
        "Riding Dark Beast Ganon",
        "Letting it all hang out on NakedIsland",
        "Hiding feelings in English, expressing feelings in Japanese",
        "\u201cThose bananas sure are expensive...\u201d",
        "Wrecking Guardians with pot-lid parries",
        "Racing Sidon and Teba",
        "Thinking about Paya's awkward embrace",
        "Getting lost in a labyrinth",
        "Hiding dubious food from Gordon Ramsay",
        "Singing karaoke with Kass",
        "Stanning King Rhoam Bosphoramus Hyrule",
        "Getting kicked out of Lurelin for bomb fishing",
    };

    /// <summary>
    /// 中文对照 —— 覆盖全部 100 条（原先只覆盖 77 条，剩下的 23 条在中文界面会漏成英文）。
    /// 键必须与 <see cref="Original"/> 中的字符串**一字不差**；见 SelfTest 的完整性校验。
    /// </summary>
    private static readonly Dictionary<string, string> ZhMap = new()
    {
        ["Finding Koroks"] = "正在寻找克洛格",
        ["Polishing Master Sword"] = "正在擦拭大师之剑",
        ["Dancing with Bokoblins"] = "正在和波克布林跳舞",
        ["Stealing bananas from Yigas"] = "正在偷依盖队的香蕉",
        ["Eating raw meat like a chad"] = "正在生啃兽肉",
        ["Thinking about Zelda's warm embrace"] = "正在回味塞尔达的怀抱",
        ["Slaying Lynels by the dozen"] = "正在一头接一头地屠杀人马",
        ["Spamming Urbosa's Fury"] = "正在狂按乌尔波扎的愤怒",
        ["Running away from Guardians"] = "正在逃离守护者",
        ["Detonating remote bombs"] = "正在引爆遥控炸弹",
        ["Avoiding Beedle"] = "正在躲开特里",
        ["Exacting revenge on Magda"] = "正在找玛格达算账",
        ["Debating between Hylia and the Golden Goddesses"] = "正在纠结信仰海利亚还是三女神",
        ["Oh, look, more opal"] = "哦，又是蛋白石",
        ["Attempting to climb a mountain in the rain"] = "正在雨中爬山",
        ["Thinking about Mipha's slimy embrace"] = "正在回味米法的拥抱",
        ["Finding yet more Koroks"] = "正在寻找更多克洛格",
        ["Disturbing the Monk's Sleep"] = "正在打扰僧侣长眠",
        ["Slashing Cuccos since 1991"] = "自 1991 年起就在砍鸡",
        ["Becoming a Pot Lid Hero"] = "正在成为锅盖英雄",
        ["Ragdolling like a Goron"] = "正在像鼓隆族那样翻滚",
        ["Not running at 60FPS"] = "帧数没到 60",
        ["That mountain over there, I can't reach it"] = "对面那座山，我够不着",
        ["\"O Epona, Epona, wherefore art thou Epona?\""] = "「伊波娜啊伊波娜，你为何不在我身边？」",
        ["The batteries are about to run out again"] = "电池又快没电了",
        ["Oh look, yet another Korok"] = "哦，又是一个克洛格",
        ["Cooking only hearty foods"] = "只做精力系料理",
        ["Friend-zoning Paya"] = "正在把帕雅发好人卡",
        ["Hiding secrets from everybody"] = "正在对所有人隐瞒秘密",
        ["Riding a shrine elevator"] = "正在乘坐神庙电梯",
        ["Bow-spinning mods"] = "正在给模组转弓",
        ["Eating a Royal Claymore"] = "正在啃王族双手剑",
        ["Ignoring the old man"] = "正在无视那位老人",
        ["Selling the Sheikah Slate"] = "正在变卖希卡石板",
        ["Annoying the monks"] = "正在惹僧侣生气",
        ["Pushing Master Kohga"] = "正在推可盖大人",
        ["Deleting the Great Plateau"] = "正在删除初始台地",
        ["Moisturizing Ganon"] = "正在给盖侬做保湿",
        ["Going back to bed"] = "正在回去补觉",
        ["100 more years never hurt anyone"] = "再睡一百年也无妨",
        ["Changing name and joining a construction company"] = "正在改名并入职建筑公司",
        ["Trapping fairies in cooking pot"] = "正在把小精灵关进锅里",
        ["Flushing Hestu's gift"] = "正在把伯库林的礼物冲走",
        ["Bullet time? What's a bullet?"] = "子弹时间？什么叫子弹？",
        ["Pretending to remember Zelda"] = "正在假装还记得塞尔达",
        ["I can't go any farther"] = "我走不动了",
        ["\"Linkle, you're going the wrong way!\""] = "「林可儿，你走反了！」",
        ["Believe it or not, real progress updates are not an option"] = "信不信由你，BCML 后端并不上报真实进度",
        ["BCML tip: When in doubt, remerge"] = "BCML 提示：拿不准的时候，就重新合并一次",
        ["BCML tip: The in-app help has a lot of information"] = "BCML 提示：应用内帮助里有很多信息",
        ["BCML tip: Read the in-app help"] = "BCML 提示：记得读一读应用内帮助",
        ["BCML tip: Questions or problems? Try the in-app help"] = "BCML 提示：有疑问或遇到问题？先看应用内帮助",
        ["BCML tip: To reorder your mods, turn on Sort Mode"] = "BCML 提示：要调整模组顺序，先打开「排序模式」",
        ["BCML tip: Ctrl-Click to select multiple mods"] = "BCML 提示：按住 Ctrl 点击可多选模组",
        ["BCML tip: Higher number priority overrides lower number priority"] = "BCML 提示：编号大的优先级高于编号小的",
        ["BCML tip: When using a set of related mods, put the base mod beneath the addons"] = "BCML 提示：用一组相关模组时，把本体放在附加内容下面",
        ["BCML tip: Back up your mods when making substantial changes"] = "BCML 提示：做较大改动前先备份模组",
        ["BCML tip: The backup/restore feature can be used for mod \"profiles\""] = "BCML 提示：备份/还原功能可以当成模组「配置档案」来用",
        ["Shameless plug: I blog at calebdixonsmith.top — Theology Without Warranty"] = "无耻安利：我的博客在 calebdixonsmith.top —— Theology Without Warranty",
        ["Support BCML on Patreon (patreon.com/nicenenerd) so my wife will let me keep doing this"] = "在 Patreon（patreon.com/nicenenerd）支持 BCML，我老婆才肯让我继续做下去",
        ["While Link fights Ganon, I fight bad theology on TikTok (@nicenenerd)"] = "林克打盖侬的时候，我在 TikTok（@nicenenerd）上怼糟糕的神学",
        ["Downloading RAM so Link goes fast"] = "正在下载内存好让林克跑快点",
        ["Feeding Koroks"] = "正在喂克洛格",
        ["Leaving Ganon in peace"] = "正在让盖侬安息",
        ["Selling apples to Koroks"] = "正在向克洛格卖苹果",
        ["Becoming a traveling merchant"] = "正在成为行商人",
        ["Stalking Beedle"] = "正在跟踪特里",
        ["Creating a paper currency"] = "正在发行纸币",
        ["Planting Korok seeds"] = "正在种植克洛格种子",
        ["Fleeing Daruk's crushing embrace"] = "正在逃离达鲁克的拥抱",
        ["Turning the Shrine of Resurrection into a tourist attraction"] = "正在把复苏神庙改成景点",
        ["Climbing Death Mountain in the buff"] = "正在裸爬死亡之山",
        ["Accidentally stoning a Korok"] = "不小心用石头砸了克洛格",
        ["Dying to Dark Beast Ganon"] = "正在被灾厄盖侬打死",
        ["Building a Master Rocket Zero to the moon"] = "正在造火箭飞向月球",
        ["Taking bets on Sand Seal races"] = "正在为沙海象赛跑下注",
        ["Upgrading to the Master Cycle One"] = "正在升级大师摩托",
        ["Ascending to High Hrothgar—oh wait, wrong game"] = "正在前往高吼峰——哦，等等，串游戏了",
        ["Collecting monk dust for elixirs"] = "正在收集僧侣的灰尘做药",
        ["Chasing a Hinox eyeball"] = "正在追西诺克斯的眼球",
        ["Abusing ghost rabbits for money"] = "正在靠幽灵兔刷钱",
        ["Making TikToks on Sheikah Slate"] = "正在用希卡石板拍短视频",
        ["Old enough to save Hyrule, too young for The Noble Canteen"] = "够年纪拯救海拉鲁，却还进不了贵族酒馆",
        ["\"Hold on Zelda, only 842 more Koroks!\""] = "「塞尔达等等，还差 842 个克洛格！」",
        ["\u201cWhat we call Man's power over Nature turns out to be a power exercised by some men over other men with Nature as its instrument.\u201d"] = "“我们所谓人对自然的力量，不过是一些人借自然为工具、对另一些人所行使的力量。”",
        ["\u201cNot even Spider-Man climbs like me!\u201d"] = "“连蜘蛛侠都没我这么能爬！”",
        ["Vandalizing private homes to find money"] = "正在私闯民宅找钱",
        ["Enjoying some rock-hard food"] = "正在享用硬得像石头的美食",
        ["Riding Dark Beast Ganon"] = "正在骑灾厄盖侬",
        ["Letting it all hang out on NakedIsland"] = "正在无人岛上放飞自我",
        ["Hiding feelings in English, expressing feelings in Japanese"] = "用英语藏心事，用日语说出口",
        ["\u201cThose bananas sure are expensive...\u201d"] = "“那些香蕉可真贵啊……”",
        ["Wrecking Guardians with pot-lid parries"] = "正在用锅盖弹反打爆守护者",
        ["Racing Sidon and Teba"] = "正在和希多、特巴比赛",
        ["Thinking about Paya's awkward embrace"] = "正在回味帕雅尴尬的拥抱",
        ["Getting lost in a labyrinth"] = "正在迷宫里迷路",
        ["Hiding dubious food from Gordon Ramsay"] = "正在把可疑料理藏起来不让戈登看",
        ["Singing karaoke with Kass"] = "正在和卡西瓦唱卡拉OK",
        ["Stanning King Rhoam Bosphoramus Hyrule"] = "正在狂热粉国王罗姆·博斯福莱姆斯·海拉鲁",
        ["Getting kicked out of Lurelin for bomb fishing"] = "正在因为用炸弹炸鱼被赶出利特村",
    };

    private static readonly Random Rng = new();

    private static int _index;
    private static string[]? _shuffled;

    /// <summary>取一条随机标语（内部按洗牌后的顺序轮播，与原版一致）。</summary>
    public static string Next()
    {
        _shuffled ??= Shuffle((string[])Original.Clone());
        var raw = _shuffled[_index % _shuffled.Length];
        _index++;
        return Translate(raw);
    }

    /// <summary>重置轮播顺序（每次打开忙碌遮罩时调用，对应原版的 shuffle）。</summary>
    public static void Reshuffle()
    {
        _shuffled = Shuffle((string[])Original.Clone());
        _index = 0;
    }

    private static string Translate(string raw)
    {
        if (!Loc.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return raw;
        return ZhMap.TryGetValue(raw, out var zh) ? zh : raw;
    }

    private static string[] Shuffle(string[] a)
    {
        for (var i = a.Length - 1; i > 0; i--)
        {
            var j = Rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
        return a;
    }

    /// <summary>原版文案条数，供自测使用。</summary>
    public static int Count => Original.Length;

    /// <summary>
    /// 自检：确认中文对照表与原文表一一对应（无多、无少、键与原文完全一致）。
    /// 返回错误列表，为空表示一致。供 <c>tools</c> 下的自测脚本调用。
    /// </summary>
    public static IReadOnlyList<string> SelfTest()
    {
        var errors = new List<string>();
        var originals = new HashSet<string>(Original, StringComparer.Ordinal);
        if (originals.Count != Original.Length)
            errors.Add($"Original 存在重复条目：{Original.Length} 条里只有 {originals.Count} 条唯一");

        foreach (var o in Original)
            if (!ZhMap.ContainsKey(o))
                errors.Add($"ZhMap 缺少译文的原文：{o}");

        foreach (var k in ZhMap.Keys)
            if (!originals.Contains(k))
                errors.Add($"ZhMap 有多余的键（原文表里没有）：{k}");

        return errors;
    }
}
