using Brutal.ImGuiApi;
using HarmonyLib;
using KSA;
using MeasureTools.Features.Measure;

namespace MeasureTools.Patches;

// Adds a "Measure" toggle to the stock View menu. GaugeCanvas.OnDrawMenuBar is a
// trivial static method the game calls inside the View menu; a postfix appends our
// item there (the same hook DeltaVMap and KSASM use). Accessing MeasureWindow.Instance
// here lazily creates the window inside an active ImGui frame, which the ImGuiWindow
// base constructor requires.
[HarmonyPatch(typeof(GaugeCanvas), nameof(GaugeCanvas.OnDrawMenuBar))]
internal static class Patch_MenuBar
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        bool shown = MeasureWindow.IsOpen;
        if (ImGui.MenuItem("Measure"u8, default(ImString), shown))
        {
            if (shown)
                MeasureWindow.Instance.Close();
            else
                MeasureWindow.Instance.Open();
        }
    }
}
