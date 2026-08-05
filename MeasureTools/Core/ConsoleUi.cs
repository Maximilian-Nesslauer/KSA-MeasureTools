using System;
using Brutal.ImGuiApi;
using KSA;

namespace MeasureTools.Core;

/// <summary>
/// Label-and-control rows built on the game's ConsoleWidgets. The tool window
/// derives from the stock ImGuiWindow, which draws the console shell and pushes
/// the console widget style around DrawContent, so the content lays out through
/// the same widgets rather than raw ImGui.
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
