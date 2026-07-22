using Brutal.ImGuiApi;
using HarmonyLib;
using KSA;
using MeasureTools.Features.Measure;

namespace MeasureTools.Patches;

// Adds a top-level "Measure" menu to the main menu bar in both flight and the
// vehicle editor. Program.DrawProgramMenusHook is an empty hook the game calls in
// the menu bar (after its own menus) in both contexts, which keeps this entry
// independent of the stock View and HUD menus and their layout. Accessing
// MeasureWindow.Instance lazily creates the window inside an active ImGui frame,
// which the ImGuiWindow base constructor requires.
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class Patch_MenuBar
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (!ImGui.BeginMenu("Measure"u8))
            return;

        bool shown = MeasureWindow.IsOpen;
        if (ImGui.MenuItem("Show Window"u8, default(ImString), shown))
        {
            if (shown)
                MeasureWindow.Instance.Close();
            else
                MeasureWindow.Instance.Open();
        }
        ImGui.EndMenu();
    }
}
