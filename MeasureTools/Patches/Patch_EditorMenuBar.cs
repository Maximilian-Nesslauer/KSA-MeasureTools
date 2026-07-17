using Brutal.ImGuiApi;
using HarmonyLib;
using KSA;
using MeasureTools.Features.Measure;

namespace MeasureTools.Patches;

// Adds a top-level "Measure" menu in the vehicle editor. The editor has no stock
// "View" menu (Program.DrawMenuBar replaces Universe/View with a single "Editor"
// menu while Program.Editor != null), so the flight hook in Patch_MenuBar never
// runs there. Program.DrawProgramMenusHook is an empty hook the game calls inside
// the main menu bar in both flight and editor; the guard keeps the tab editor-only
// so it does not duplicate the flight View entry.
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class Patch_EditorMenuBar
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (Program.Editor == null)
            return;

        if (ImGui.BeginMenu("Measure"u8))
        {
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
}
