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

// Intercepts the left mouse press while the measure tool is armed in a supported
// view (map or flight), so a placement click does not also focus a body, change the
// target or create a burn (Program.OnMouseButton dispatches those after the
// controllers). Camera navigation is untouched: both supported views pan/rotate with
// middle/right drag only (MapController.OnMouseButton, OrbitController.OnMouseButton)
// and leave the left button free, which is why the tool stays disarmed in the Free
// and IVA views that steer with left-drag (see MeasureState.IsSupportedViewMode).
// Modified clicks pass through so shift-click target setting keeps working. A short
// right click (press and release without movement) cancels the in-progress
// placement, or pauses the tool when nothing is pending; a real right drag still
// rotates.
[HarmonyPatch(typeof(Program), nameof(Program.OnMouseButton))]
internal static class Patch_MouseButton
{
    // A right press and release closer than this counts as a click (cancel/pause);
    // anything farther is a drag (camera rotate) and is left to the game.
    private const float RightClickDragThresholdPx = 4f;

    private static bool _rightPressPending;
    private static float2 _rightPressPos;

    // Set when a left press was consumed for a placement, so its release gets
    // consumed too. The editor grabs/places the highlighted part on the left
    // RELEASE (VehicleEditor.OnMouseButton), so eating only the press would place
    // a measure point AND hand the part to the cursor. Pairing keeps releases
    // whose press the tool did not take (started before arming, or over UI)
    // untouched.
    private static bool _leftPressConsumed;

    // Called from [StarMapUnload] so no click state survives a mod reload.
    public static void Reset()
    {
        _rightPressPending = false;
        _rightPressPos = default;
        _leftPressConsumed = false;
    }

    [HarmonyPrefix]
    private static bool Prefix(GlfwMouseButton button, GlfwButtonAction action, GlfwModifier mods)
    {
        try
        {
            if (!MeasureState.IsArmed)
            {
                // Drop any half-tracked click so its state cannot leak across a
                // disarm/re-arm cycle and cancel a point or eat a release
                // unexpectedly.
                _rightPressPending = false;
                _leftPressConsumed = false;
                return true;
            }
            // Mirror the original's own early-out: when the UI owns the mouse over the
            // main viewport the original ignores the click anyway, and a click on our
            // tool window must not place a point. Both early-outs drop the left-press
            // pairing so a release that lands here cannot leave the flag armed for a
            // later, unrelated release.
            if (ImGui.GetIO().WantCaptureMouse && Program.HoveredViewport == Program.MainViewport)
            {
                _leftPressConsumed = false;
                return true;
            }
            if (Program.HoveredViewport != Program.MainViewport)
            {
                _leftPressConsumed = false;
                return true;
            }

            if (button == GlfwMouseButton.Number2)
            {
                if (action == GlfwButtonAction.Press)
                {
                    _rightPressPending = true;
                    _rightPressPos = ImGui.GetIO().MousePos;
                }
                else if (action == GlfwButtonAction.Release && _rightPressPending)
                {
                    _rightPressPending = false;
                    if (float2.Distance(ImGui.GetIO().MousePos, _rightPressPos) < RightClickDragThresholdPx)
                    {
                        // Short right-click: cancel the in-progress placement, or
                        // pause the tool when nothing is pending so the game plays
                        // normally with the window still open.
                        if (MeasureState.Pending.Count > 0)
                        {
                            MeasureState.CancelPending();
                            // Consume the release so canceling does not also open
                            // the stock part context menu (Vehicle.OnMouseButton
                            // opens it for the hovered part on a short right
                            // release). Stock cancels the controller's pending
                            // drag the same way before consuming the release.
                            Program.HoveredViewport.GetActiveController().CancelMouseDrag();
                            return false;
                        }
                        // A short right-click over a part belongs to the stock
                        // part menu; pausing the tool at the same time would be
                        // surprising. The flight hover lives in
                        // Viewport.ClosestHoveredPart (Vehicle.UpdateHighlight),
                        // the editor's in VehicleEditor.Highlighted; the map view
                        // sets neither, so its behavior is unchanged.
                        bool overPart = Program.Editor != null
                            ? Program.Editor.Highlighted != null
                            : Program.MainViewport.ClosestHoveredPart != null;
                        if (!overPart)
                        {
                            MeasureState.SetToolActive(false);
                            // Consume the release for the same reason the cancel
                            // branch does: BurnContextMenu.TryOpen runs on the right
                            // release and would open the stock burn menu on top of
                            // the pause whenever the cursor sits on an orbit line
                            // that can take a burn. Nothing else wants this release,
                            // since we already know no part is hovered.
                            Program.HoveredViewport.GetActiveController().CancelMouseDrag();
                            return false;
                        }
                    }
                }
                return true;
            }

            if (button != GlfwMouseButton.Number1)
                return true;
            if (action == GlfwButtonAction.Release)
            {
                if (_leftPressConsumed)
                {
                    _leftPressConsumed = false;
                    return false;
                }
                return true;
            }
            if (action != GlfwButtonAction.Press)
                return true;
            // Shift (stock target-set) and alt (stock focus modifier, and part
            // duplication in the editor) pass through; ctrl is ours: place a free
            // point on the ecliptic plane, even where snapping would win.
            // Unmodified free clicks use the camera plane.
            if ((mods & (GlfwModifier.Shift | GlfwModifier.Alt)) != 0)
                return true;
            bool eclipticFree = (mods & GlfwModifier.Control) != 0;

            Viewport viewport = Program.MainViewport;
            float2 mouseViewport = ImGui.GetIO().MousePos - viewport.Position;
            // Circle mode settles a full measurement (center + rim pair) in one
            // click and so bypasses the single-anchor Pick/pending flow.
            if (MeasureState.Mode == MeasureMode.Circle)
            {
                if (MapPicker.PickCircle(viewport, mouseViewport, out Anchor? center, out Anchor? rim)
                    && center != null && rim != null)
                {
                    MeasureState.AddCircle(center, rim);
                }
                else if (DebugConfig.Measure)
                {
                    DefaultCategory.Log.Debug(
                        $"[MeasureTools] Circle click at {mouseViewport} found no circular edge, click consumed.");
                }
                _leftPressConsumed = true;
                return false;
            }
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
            // tool is armed, unmodified left clicks in a supported view belong to it.
            _leftPressConsumed = true;
            return false;
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("mouse-prefix-" + ex.GetType().Name, $"[MeasureTools] Mouse intercept failed, passing click through: {ex}");
            return true;
        }
    }
}
