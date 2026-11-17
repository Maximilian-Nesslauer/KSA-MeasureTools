using HarmonyLib;
using KSA;

namespace MeasureTools.Patches;

// Program.OnDrawUiThreadSafe calls Universe.OnDrawUi, which sets the body-marker
// eligibility that the measurement preview needs from the current frame.
[HarmonyPatch(typeof(Program), "OnDrawUiThreadSafe", new[] { typeof(double) })]
internal static class Patch_DrawUi
{
    [HarmonyPostfix]
    private static void Postfix() => Mod.Draw();
}
