using System;
using MeasureTools.Core;
using MeasureTools.Features.Measure;
using MeasureTools.Patches;
using Brutal.Logging;
using HarmonyLib;
using KSA;
using StarMap.API;
using StarMap.API.Services;

namespace MeasureTools;

[StarMapMod]
public sealed class Mod
{
    private static Harmony? _harmony;

    private const string TestedGameVersion = "v2026.11.10.5801";

    private readonly IModMenuService _modMenu;
    private IDisposable? _modMenuItem;

    public Mod(IModMenuService modMenu)
    {
        _modMenu = modMenu;
    }

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        string gameVersion = VersionInfo.Current.VersionString;
        DefaultCategory.Log.Info($"[MeasureTools] Game version: {gameVersion}");
        if (gameVersion != TestedGameVersion)
            DefaultCategory.Log.Warning(
                $"[MeasureTools] Tested against {TestedGameVersion}, current is {gameVersion}. " +
                "Some features may not work correctly.");

        GameReflection.LogDriftIfUnavailable();
        MeasureColors.Load();

        _harmony = new Harmony("com.maxi.measuretools");
        // Apply each patch on its own so a future game change to one target does not
        // stop the other from being patched.
        ApplyPatch(typeof(Patch_MouseButton), "mouse intercept");
        ApplyPatch(typeof(Patch_LoadSave), "save load reset");

        // StarMap throws when another mod already uses the label, and the tool has
        // no menu entry then.
        try
        {
            _modMenuItem = _modMenu.AddModMenuItem("Measure", MeasureWindow.ToggleWindow);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("mods-menu", $"[MeasureTools] Failed to add the Mods menu item: {ex}");
        }

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

    // Prune first, so neither the window nor the overlay resolves an anchor whose
    // body is gone.
    [StarMapAfterGui]
    public void Draw(double dt)
    {
        try
        {
#if DEBUG
            using var perfScope = new PerfTracker.Scope("Mod.Draw");
#endif
            MeasureState.Prune();
            if (!MeasureViewport.TryGetActive(out IGameViewport viewport))
                return;
            MeasureWindow.DrawActive(viewport);
            MeasureOverlay.Draw(viewport);
        }
        catch (Exception ex)
        {
            // Per-frame path: first throw of each type logs a stack, then quiet.
            LogHelper.ErrorOnce("aftergui-" + ex.GetType().Name, $"[MeasureTools] Per-frame draw failed: {ex}");
        }
    }

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        _modMenuItem?.Dispose();
        _modMenuItem = null;

        MeasureColors.SaveIfDirty();
        MeasureColors.Reset();
        MeasureState.Reset();
        MeasureWindow.ResetStatic();
        MeasureOverlay.Reset();
        MeshFeatureCache.Reset();
        Patch_MouseButton.Reset();
        DebugConfig.Reset();
        LogHelper.Reset();
#if DEBUG
        PerfTracker.Reset();
#endif
        DefaultCategory.Log.Info("[MeasureTools] Unloaded.");
    }
}
