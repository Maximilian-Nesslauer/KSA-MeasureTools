using System;
using HarmonyLib;
using KSA;
using MeasureTools.Core;
using MeasureTools.Features.Measure;

namespace MeasureTools.Patches;

// Universe.DeserializeSave keeps the same CelestialSystem, so Prune's system-change
// compare never fires on a save load. Vehicle anchors drop out by themselves; body
// and free points would survive at offsets that mean nothing in the loaded save.
[HarmonyPatch(typeof(Universe), nameof(Universe.DeserializeSave))]
internal static class Patch_LoadSave
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        // A postfix throws into the loader.
        try
        {
            MeasureState.ClearAll();
            MeasureState.SetReferenceOverride(null);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("loadsave-" + ex.GetType().Name,
                $"[MeasureTools] Clearing measurements on save load failed: {ex}");
        }
    }
}
