using MeasureTools.Core;
using Brutal.Logging;
using HarmonyLib;
using KSA;
using StarMap.API;

namespace MeasureTools;

[StarMapMod]
public sealed class Mod
{
    private static Harmony? _harmony;

    private const string TestedGameVersion = "v2026.6.3.4568";

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        string gameVersion = VersionInfo.Current.VersionString;
        DefaultCategory.Log.Info($"[MeasureTools] Game version: {gameVersion}");
        if (gameVersion != TestedGameVersion)
            DefaultCategory.Log.Warning(
                $"[MeasureTools] Tested against {TestedGameVersion}, current is {gameVersion}. " +
                "Some features may not work correctly.");

        _harmony = new Harmony("com.maxi.measuretools");

        // Validate reflection targets and apply patches per feature:
        //
        // if (GameReflection.ValidateMyFeature())
        //     MyFeature.ApplyPatches(_harmony);
        // else
        //     DefaultCategory.Log.Warning("[MeasureTools] MyFeature disabled - reflection targets not found.");

        DefaultCategory.Log.Info("[MeasureTools] Loaded and patched.");
    }

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;

        // Reset all feature state here, e.g.:
        // MyFeature.Reset();

        LogHelper.Reset();
#if DEBUG
        PerfTracker.Reset();
#endif
        DefaultCategory.Log.Info("[MeasureTools] Unloaded.");
    }
}
