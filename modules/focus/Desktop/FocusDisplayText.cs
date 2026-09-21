using FgoPet.Core.Focus;

namespace FgoPet.App.Panels;

/// <summary>
/// Compact 计时面的只读展示投影（任务卡 C 步骤 4）。
///
/// 「相位 / 轮次 / 剩余时间」这三个串原先是 <c>AttachedPanelViewModel</c> 里的三个
/// <c>[ObservableProperty]</c> 派生字段。它们只是 <see cref="FocusSession"/> 的排版结果，
/// 把中文字面量留在 shell VM 里既污染 VM，也容易让 shell 侧慢慢长出第二份 focus 语义。
///
/// 现在统一由这个 View 层类型格式化：
/// <list type="bullet">
///   <item><c>AttachedPanelViewModel</c> 只负责把权威快照递进来（<c>FocusDisplay</c> 属性）；</item>
///   <item>中文字面量不进 <see cref="FocusSession"/> —— 它是领域模型，只认状态和秒数。</item>
/// </list>
/// </summary>
public sealed record FocusDisplayText(string Phase, string Cycle, string Remaining)
{
    public static FocusDisplayText From(FocusSession session) => new(
        Phase: FormatPhase(session),
        Cycle: FormatCycle(session),
        Remaining: FormatClock(session.RemainingSeconds));

    private static string FormatPhase(FocusSession session) =>
        session.Phase == FocusPhase.Break
            ? "休息"
            : session.Status is FocusStatus.PausedFocus or FocusStatus.PausedBreak
                ? "已暂停"
                : "专注中";

    private static string FormatCycle(FocusSession session) =>
        session.TotalCycles > 0 ? $"第 {session.CurrentCycle} / {session.TotalCycles} 轮" : string.Empty;

    private static string FormatClock(int totalSeconds) =>
        $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
}
