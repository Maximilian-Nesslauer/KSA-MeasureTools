using System;
using MeasureTools.Core;
using MeasureTools.Features.Measure;
using MeasureTools.Patches;
using Brutal.Logging;
using HarmonyLib;
using KSA;
using StarMap.API;

namespace MeasureTools;

[StarMapMod]
public sealed class Mod
{
    private static Harmony? _harmony;

    private const string TestedGameVersion = "v2026.6.6.4601";

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
        // Apply each patch on its own so a future game change to one target does not
        // stop the other from being patched.
        ApplyPatch(typeof(Patch_MenuBar), "View menu toggle");
        ApplyPatch(typeof(Patch_MouseButton), "mouse intercept");

        DefaultCategory.Log.Info("[MeasureTools] Loaded.");
    }

    private static void ApplyPatch(Type patchClass, string description)
    {
        try
        {
            _harmony!.CreateClassProcessor(patchClass).Patch();
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] Applied {description} patch ({patchClass.Name}).");
        }
        catch (Exception ex)
        {
            // A missing hook should not unload the mod or block the other patch.
            LogHelper.ErrorOnce("patch-" + patchClass.Name, $"[MeasureTools] Failed to apply {description} patch: {ex}");
        }
    }

    // Runs every frame after KSA's own ImGui, while the frame is still active:
    // prune stale state first so neither the window nor the overlay ever resolves
    // an anchor whose body was removed, then the tool window, then the map overlay.
    [StarMapAfterGui]
    public void Draw(double dt)
    {
        try
        {
            MeasureState.Prune();
            Viewport viewport = Program.MainViewport;
            if (viewport == null)
                return;
            MeasureWindow.DrawActive(viewport);
            MeasureOverlay.Draw(viewport);
        }
        catch (Exception ex)
        {
            // Key on the exception type so a second, different failure mode is not
            // silenced by the first.
            LogHelper.ErrorOnce("aftergui-" + ex.GetType().Name, $"[MeasureTools] Per-frame draw failed: {ex}");
        }
    }

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;

        MeasureState.Reset();
        MeasureWindow.ResetStatic();
        MeasureOverlay.Reset();
        Patch_MouseButton.Reset();
        LogHelper.Reset();
#if DEBUG
        PerfTracker.Reset();
#endif
        DefaultCategory.Log.Info("[MeasureTools] Unloaded.");
    }
}
