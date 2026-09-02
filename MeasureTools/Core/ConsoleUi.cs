using System;
using Brutal.ImGuiApi;
using KSA;

namespace MeasureTools.Core;

/// <summary>
/// Label-and-control rows on the game's ConsoleWidgets, so the window lays out like
/// the stock consoles rather than raw ImGui. It does not set UseConsoleChrome, so
/// nothing pushes the console widget style around DrawContent; the widgets style
/// themselves through ConsoleWidgets.BeginRowCore.
/// </summary>
internal static class ConsoleUi
{
    public static void Muted(ReadOnlySpan<char> text)
    {
        ConsoleStyle.PushValueFont();
        ImGui.TextColored(in ConsoleStyle.TextMuted, text);
        ConsoleStyle.PopFont();
    }

    public static bool CheckboxRow(ReadOnlySpan<char> label, ReadOnlySpan<char> id, ref bool value,
        ReadOnlySpan<char> tooltip = default)
    {
        ConsoleWidgets.BeginRow(label);
        bool changed = ConsoleWidgets.Checkbox(id, ref value, pending: false);
        if (tooltip.Length > 0 && ConsoleWidgets.RowHovered)
            ConsoleWidgets.Tooltip(tooltip);
        ConsoleWidgets.EndRow();
        return changed;
    }
}
