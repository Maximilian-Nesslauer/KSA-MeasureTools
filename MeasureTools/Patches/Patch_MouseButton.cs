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

// Takes the left press while the tool is armed, so a placement click does not also
// focus a body, set a target or create a burn. Camera navigation is untouched: the
// map and orbit views pan and rotate on middle/right drag only, which is why the
// tool stays disarmed in the left-dragging Free and IVA views
// (MeasureState.IsSupportedViewMode). A short right click cancels the pending point,
// or pauses the tool when nothing is pending; a real right drag still rotates.
[HarmonyPatch(typeof(Program), nameof(Program.OnMouseButton))]
internal static class Patch_MouseButton
{
    // A right press and release closer than this counts as a click (cancel/pause);
    // anything farther is a drag (camera rotate) and is left to the game.
    private const float RightClickDragThresholdPx = 4f;

    private static bool _rightPressPending;
    private static float2 _rightPressPos;

    private static bool _leftPressConsumed;

    // Called from [StarMapUnload] so no click state survives a mod reload.
    public static void Reset()
    {
        _rightPressPending = false;
        _rightPressPos = default;
        _leftPressConsumed = false;
    }

    // A consumed release never reaches the controller, so end its drag here or the
    // map camera keeps spinning with the cursor captured (MapController clears
    // RotateMouseDragging in its own OnMouseButton, which stock's CancelMouseDrag
    // does not cover). Both latches then have to be handed back (GameReflection);
    // declining to consume costs a stock menu on top of the cancel, leaking one
    // costs the session.
    private static bool TryConsumeRightRelease()
    {
        if (!GameReflection.CanReleaseMouseLatch)
            return false;
        Controller controller = Program.InputViewport.GetActiveController();
        controller.CancelMouseDrag();
        if (controller is MapController map)
            map.RotateMouseDragging = false;
        GameReflection.ReleaseMouseLatch(GlfwMouseButton.Number2);
        GameReflection.ClearBurnRightClickLatch();
        return true;
    }

    // VehicleEditor drops a grabbed part and ends a gizmo drag on the left RELEASE,
    // so eating those clicks strands whatever is in hand: it follows the cursor with
    // no way to put it down while the tool is armed.
    private static bool EditorHoldsDrag()
    {
        VehicleEditor? editor = Program.Editor;
        return editor != null && (editor.GizmoGrabbed || (editor.Highlighted?.Grabbed ?? false));
    }

    [HarmonyPrefix]
    private static bool Prefix(GlfwMouseButton button, GlfwButtonAction action, GlfwModifier mods)
    {
        try
        {
            // A consumed press owns its release: the game never saw the press, and an
            // unpaired release makes the editor grab the highlighted part. Every gate
            // below can flip while the button is down, so this decides first. A press
            // opens a fresh pair, so a release the OS never delivered cannot arm a
            // later, unrelated one.
            if (button == GlfwMouseButton.Number1)
            {
                if (action == GlfwButtonAction.Press)
                {
                    _leftPressConsumed = false;
                }
                else if (action == GlfwButtonAction.Release && _leftPressConsumed)
                {
                    _leftPressConsumed = false;
                    return false;
                }
            }

            if (!MeasureState.IsArmed)
            {
                // Drop a half-tracked right click so its state cannot leak across a
                // disarm/re-arm cycle and cancel a point unexpectedly.
                _rightPressPending = false;
                return true;
            }
            // InputViewport is what the original tests: a press latches its viewport
            // for the whole sequence, so a release still belongs to where the drag
            // started. Not ours means another viewport (see MeasureViewport) or ImGui
            // holding the mouse, where the original ignores the click anyway.
            IGameViewport inputViewport = Program.InputViewport;
            if (!MeasureViewport.IsHost(inputViewport) || ImGui.GetIO().WantCaptureMouse)
                return true;
            if (button == GlfwMouseButton.Number1 && EditorHoldsDrag())
                return true;

            if (button == GlfwMouseButton.Number2)
            {
                if (action == GlfwButtonAction.Press)
                {
                    _rightPressPending = true;
                    _rightPressPos = Cursor.DesktopPosition;
                }
                else if (action == GlfwButtonAction.Release && _rightPressPending)
                {
                    _rightPressPending = false;
                    if (float2.Distance(Cursor.DesktopPosition, _rightPressPos) < RightClickDragThresholdPx)
                    {
                        // Cancel the pending point, else pause so the game plays
                        // normally with the window still open.
                        if (MeasureState.Pending.Count > 0)
                        {
                            MeasureState.CancelPending();
                            // Consumed so cancelling does not also open the stock
                            // part menu, which Vehicle.OnMouseButton opens here.
                            return !TryConsumeRightRelease();
                        }
                        // Over a part the click belongs to the stock part menu, so
                        // do not pause on top of it. The map view fills neither
                        // source. The picker is one frame stale, which only shows on
                        // a cursor that just crossed a part edge.
                        bool overPart = Program.Editor != null
                            ? Program.Editor.Highlighted != null
                            : inputViewport.PartPicker.Part != null;
                        if (!overPart)
                        {
                            MeasureState.SetToolActive(false);
                            // Likewise, or BurnContextMenu.TryOpen opens the burn
                            // menu on top of the pause over an orbit line.
                            return !TryConsumeRightRelease();
                        }
                    }
                }
                return true;
            }

            if (button != GlfwMouseButton.Number1)
                return true;
            if (action != GlfwButtonAction.Press)
                return true;
            // Shift (target-set) and alt (focus, editor duplicate) stay with stock.
            // Ctrl is ours: a free point on the ecliptic plane instead of the camera
            // plane, even where snapping would win.
            if ((mods & (GlfwModifier.Shift | GlfwModifier.Alt)) != 0)
                return true;
            bool eclipticFree = (mods & GlfwModifier.Control) != 0;

            float2 mouseViewport = Cursor.GetPosition(inputViewport);
            // Circle settles a whole measurement in one click, so it bypasses the
            // single-anchor pending flow.
            if (MeasureState.Mode == MeasureMode.Circle)
            {
                if (MapPicker.PickCircle(inputViewport, mouseViewport, out Anchor? center, out Anchor? rim)
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
            Anchor? anchor = MapPicker.Pick(inputViewport, mouseViewport, eclipticFree);
            if (anchor != null)
            {
                MeasureState.AddPoint(anchor);
            }
            else if (DebugConfig.Measure)
            {
                DefaultCategory.Log.Debug(
                    $"[MeasureTools] Placement click at {mouseViewport} resolved no anchor (mode {MeasureState.Mode}), click consumed.");
            }
            // Consumed even when nothing resolved: while armed, unmodified left
            // clicks belong to the tool.
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
