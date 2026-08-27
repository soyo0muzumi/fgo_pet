namespace FgoPet.App.Tests.Panels;

internal static class PanelFixtures
{
    public static string LongChinese() =>
        "玛修·基列莱特是迦勒底的人造从者，她总是以认真的态度守护着御主，这份长长的中文台词只是为了验证超长文本不会让面板无限增高。";

    public static string UnbrokenEnglish() =>
        "Supercalifragilisticexpialidocious-pneumonoultramicroscopicsilicovolcanoconiosis-Antidisestablishmentarianism";

    public static IEnumerable<string> LongChineseDialogue(int count) =>
        Enumerable.Repeat(LongChinese(), count);

    public static IEnumerable<string> EnglishDialogue(int count) =>
        Enumerable.Repeat(UnbrokenEnglish(), count);
}