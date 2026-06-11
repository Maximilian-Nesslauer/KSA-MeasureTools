using System;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Logging;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using MeasureTools.Core;
using MeasureTools.Features.Measure;

namespace MeasureTools.Patches;

// Intercepts the left mouse press while the measure tool is armed in map mode, so a
// placement click does not also focus a body, change the target or create a burn
// (Program.OnMouseButton dispatches those after the controllers). Camera navigation
// is untouched: the map camera pans/rotates with middle/right drag only
// (MapController.OnMouseButton), and modified clicks pass through so shift-click
// target setting keeps working. A short right click (press and release without
// movement) cancels the in-progress placement; a real right drag still rotates.
[HarmonyPatch(typeof(Program), nameof(Program.OnMouseButton))]
internal static class Patch_MouseButton
{
    private static bool _rightPressPending;
    private static float2 _rightPressPos;

    // Called from [StarMapUnload] so no click state survives a mod reload.
    public static void Reset()
    {
        _rightPressPending = false;
        _rightPressPos = default;
    }

    [HarmonyPrefix]
    private static bool Prefix(GlfwMouseButton button, GlfwButtonAction action, GlfwModifier mods)
    {
        try
        {
            if (!MeasureState.IsArmed)
            {
                // Drop any half-tracked right click so its state cannot leak across
                // a disarm/re-arm cycle and cancel a point unexpectedly.
                _rightPressPending = false;
                return true;
            }
            // Mirror the original's own early-out: when the UI owns the mouse over the
            // main viewport the original ignores the click anyway, and a click on our
            // tool window must not place a point.
            if (ImGui.GetIO().WantCaptureMouse && Program.HoveredViewport == Program.MainViewport)
                return true;
            if (Program.HoveredViewport != Program.MainViewport)
                return true;

            if (button == GlfwMouseButton.Number2)
            {
                if (action == GlfwButtonAction.Press)
                {
                    _rightPressPending = MeasureState.Pending.Count > 0;
                    _rightPressPos = ImGui.GetIO().MousePos;
                }
                else if (action == GlfwButtonAction.Release && _rightPressPending)
                {
                    _rightPressPending = false;
                    if (float2.Distance(ImGui.GetIO().MousePos, _rightPressPos) < 4f)
                        MeasureState.CancelPending();
                }
                return true;
            }

            if (button != GlfwMouseButton.Number1 || action != GlfwButtonAction.Press)
                return true;
            // Shift (stock target-set) and alt (stock focus modifier) pass through;
            // ctrl is ours: place a free point on the ecliptic plane, even where
            // snapping would win. Unmodified free clicks use the camera plane.
            if ((mods & (GlfwModifier.Shift | GlfwModifier.Alt)) != 0)
                return true;
            bool eclipticFree = (mods & GlfwModifier.Control) != 0;

            Viewport viewport = Program.MainViewport;
            float2 mouseViewport = ImGui.GetIO().MousePos - viewport.Position;
            Anchor? anchor = MapPicker.Pick(viewport, mouseViewport, eclipticFree);
            if (anchor != null)
            {
                MeasureState.AddPoint(anchor);
            }
            else if (DebugConfig.Measure)
            {
                DefaultCategory.Log.Debug(
                    $"[MeasureTools] Placement click at {mouseViewport} resolved no anchor (mode {MeasureState.Mode}), click consumed.");
            }
            // Consume the click even when nothing resolved (plane edge-on): while the
            // tool is armed, unmodified left clicks in the map belong to it.
            return false;
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("mouse-prefix-" + ex.GetType().Name, $"[MeasureTools] Mouse intercept failed, passing click through: {ex}");
            return true;
        }
    }
}
