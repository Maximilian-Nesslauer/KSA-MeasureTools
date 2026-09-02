using System.Collections.Generic;
using Brutal.Logging;
using KSA;
using MeasureTools.Core;

namespace MeasureTools.Features.Measure;

// Central tool state shared by the window, the overlay and the input patch.
// Measurements are ephemeral: cleared when the tool closes or the system changes.
internal static class MeasureState
{
    public static MeasureMode Mode = MeasureMode.Ruler;

    public static bool SnapEnabled = true;

    // Part picking master switch: exact surface points under the cursor plus the
    // two tiers below. Off = vehicles snap only at their center marker.
    public static bool PartSnapEnabled = true;

    // Screen-space snap tier to attach nodes and part bounding-box centers.
    public static bool PartFeatureSnapEnabled = true;

    // Screen-space snap tier to mesh vertices of the part under the cursor.
    public static bool PartVertexSnapEnabled = true;

    // User-chosen reference body for free points; null = follow the map camera focus.
    public static Astronomical? ReferenceOverride;

    // Points of the in-progress measurement, in placement order.
    public static readonly List<Anchor> Pending = new();

    public static readonly List<Measurement> Measurements = new();

    private static CelestialSystem? _system;

    // Bumped on every placement-state change so cached pick results (the overlay's
    // throttled hover preview) are invalidated immediately instead of one interval
    // late, e.g. when the free-plane basis moves to a freshly placed point.
    public static int StateVersion { get; private set; }

    // Index of the measurement hovered in the window list this frame (-1 = none).
    // Written by the window draw, read by the overlay draw right after (Mod.Draw
    // runs them in that order), so the map highlight follows the list hover.
    public static int HighlightIndex = -1;

    public static int PointsNeeded => Mode switch
    {
        MeasureMode.Angle => 3,
        MeasureMode.Circle => 1,
        _ => 2,
    };

    // Whether the tool currently captures map clicks. The window can stay open
    // with the tool paused (short right-click with nothing pending), so the game
    // plays normally while the measurements stay visible.
    public static bool ToolActive = true;

    // The tool captures clicks only while its window is open, the tool is not
    // paused, and the viewport it lives in is in a supported view.
    public static bool IsArmed =>
        ToolActive
        && MeasureWindow.IsOpen
        && Universe.CurrentSystem != null
        && MeasureViewport.TryGetActive(out IGameViewport viewport)
        && IsSupportedViewMode(viewport.Mode);

    // The camera modes the tool operates in. Map is the orbital map; Orbit is the
    // default flight camera that follows the focused body or vehicle. Both navigate
    // with the middle and right mouse only (MapController.OnMouseButton,
    // OrbitController.OnMouseButton), so intercepting a left-click placement cannot
    // break camera control. Free (FlyController) looks around with left-drag, and
    // IVA and Fixed are special cockpit or static views, so the tool stays disarmed
    // in those to leave their input untouched. The projection math is identical in
    // all modes: Camera.EclToEgo is a pure translation, so ego axes are ECL axes and
    // the base camera projects points the same way the map camera does.
    public static bool IsSupportedViewMode(CameraMode mode)
    {
        return mode == CameraMode.Map || mode == CameraMode.Orbit;
    }

    public static void SetToolActive(bool active)
    {
        if (ToolActive == active)
            return;
        ToolActive = active;
        // Pausing drops a half-finished placement; settled measurements stay.
        if (!active)
            Pending.Clear();
        StateVersion++;
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug(active
                ? "[MeasureTools] Measuring resumed."
                : "[MeasureTools] Measuring paused, clicks pass through to the game.");
    }

    public static void SetMode(MeasureMode mode)
    {
        if (Mode == mode)
            return;
        Mode = mode;
        Pending.Clear();
        StateVersion++;
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug($"[MeasureTools] Mode set to {mode}, pending cleared.");
    }

    // Snap and the reference body change what MapPicker.Pick returns, so both go
    // through setters that bump StateVersion, like Mode and ToolActive. Writing the
    // fields directly would let the overlay's throttled preview cache lag the toggle
    // by up to the pick interval.
    public static void SetSnapEnabled(bool enabled)
    {
        if (SnapEnabled == enabled)
            return;
        SnapEnabled = enabled;
        StateVersion++;
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug($"[MeasureTools] Snap {(enabled ? "enabled" : "disabled")}.");
    }

    public static void SetPartSnapEnabled(bool enabled)
    {
        if (PartSnapEnabled == enabled)
            return;
        PartSnapEnabled = enabled;
        StateVersion++;
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug($"[MeasureTools] Part snap {(enabled ? "enabled" : "disabled")}.");
    }

    public static void SetPartFeatureSnapEnabled(bool enabled)
    {
        if (PartFeatureSnapEnabled == enabled)
            return;
        PartFeatureSnapEnabled = enabled;
        StateVersion++;
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug($"[MeasureTools] Part feature snap {(enabled ? "enabled" : "disabled")}.");
    }

    public static void SetPartVertexSnapEnabled(bool enabled)
    {
        if (PartVertexSnapEnabled == enabled)
            return;
        PartVertexSnapEnabled = enabled;
        StateVersion++;
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug($"[MeasureTools] Part vertex snap {(enabled ? "enabled" : "disabled")}.");
    }

    public static void SetReferenceOverride(Astronomical? body)
    {
        if (ReferenceEquals(ReferenceOverride, body))
            return;
        ReferenceOverride = body;
        StateVersion++;
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug($"[MeasureTools] Reference override set to {body?.Id ?? "auto"}.");
    }

    public static void AddPoint(Anchor anchor)
    {
        Pending.Add(anchor);
        StateVersion++;
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug(
                $"[MeasureTools] Point {Pending.Count}/{PointsNeeded} placed: {anchor.Kind} '{anchor.Label}' offsetCce={anchor.OffsetCce}.");
        if (Pending.Count >= PointsNeeded)
        {
            var measurement = new Measurement { Mode = Mode, Anchors = Pending.ToArray() };
            Measurements.Add(measurement);
            Pending.Clear();
            if (DebugConfig.Measure)
            {
                string value = measurement.Mode switch
                {
                    MeasureMode.Ruler => $"distance={measurement.DistanceMeters():F1} m",
                    MeasureMode.Surface => $"surface distance={measurement.SurfaceDistanceMeters():F1} m, bearing={measurement.BearingDegrees():F1} deg",
                    MeasureMode.FaceAngle => $"face angle={measurement.FaceAngleRadians() * (180.0 / Math.PI):F3} deg",
                    _ => $"angle={measurement.AngleRadians() * (180.0 / Math.PI):F3} deg",
                };
                DefaultCategory.Log.Debug($"[MeasureTools] Measurement #{Measurements.Count} completed: {value}.");
            }
        }
    }

    // Circle mode settles in one click but needs two anchors (center + rim), so
    // it bypasses the pending flow entirely.
    public static void AddCircle(Anchor center, Anchor rim)
    {
        var measurement = new Measurement { Mode = MeasureMode.Circle, Anchors = new[] { center, rim } };
        Measurements.Add(measurement);
        StateVersion++;
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug(
                $"[MeasureTools] Circle measurement #{Measurements.Count} completed: '{center.Label}' d={measurement.CircleDiameterMeters():F3} m.");
    }

    public static void CancelPending()
    {
        if (Pending.Count == 0)
            return;
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug($"[MeasureTools] Pending placement cancelled ({Pending.Count} point(s) dropped).");
        Pending.Clear();
        StateVersion++;
    }

    public static void ClearAll()
    {
        Pending.Clear();
        Measurements.Clear();
        StateVersion++;
    }

    // Repair or drop state that no longer resolves: a system change clears
    // everything; a stale part anchor is re-homed to its part's current owner
    // (staging, docking, editor transitions); only anchors beyond repair (body
    // gone, part deleted) drop the affected measurement.
    public static void Prune()
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MeasureState.Prune");
#endif
        CelestialSystem? system = Universe.CurrentSystem;
        if (!ReferenceEquals(system, _system))
        {
            // Only worth a log line when something is actually dropped (the very
            // first frame also lands here, transitioning from no system).
            if (DebugConfig.Measure && (Measurements.Count > 0 || Pending.Count > 0))
                DefaultCategory.Log.Debug(
                    $"[MeasureTools] System changed, clearing {Measurements.Count} measurement(s) and {Pending.Count} pending point(s).");
            _system = system;
            Pending.Clear();
            Measurements.Clear();
            ReferenceOverride = null;
            StateVersion++;
            return;
        }
        if (system == null)
            return;
        if (ReferenceOverride != null && !ReferenceEquals(system.Get(ReferenceOverride.Id), ReferenceOverride))
        {
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] Reference override '{ReferenceOverride.Id}' no longer resolves, back to auto.");
            ReferenceOverride = null;
            StateVersion++;
        }
        for (int i = 0; i < Pending.Count; i++)
        {
            if (Pending[i].IsValid(system))
                continue;
            Anchor? rehomed = Pending[i].Rehome(system);
            if (rehomed != null)
            {
                if (DebugConfig.Measure)
                    DefaultCategory.Log.Debug($"[MeasureTools] Pending anchor '{Pending[i].Label}' re-homed as '{rehomed.Label}'.");
                Pending[i] = rehomed;
                StateVersion++;
                continue;
            }
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] Pending anchor '{Pending[i].Label}' could not be re-homed (part or body gone), pending cleared.");
            Pending.Clear();
            StateVersion++;
            break;
        }
        for (int i = Measurements.Count - 1; i >= 0; i--)
        {
            if (RehomeAnchors(Measurements[i].Anchors, system))
                continue;
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] Measurement #{i + 1} holds an unrecoverable anchor, removed.");
            // Removing a settled measurement does not change what the next pick
            // returns, so no StateVersion bump is needed here.
            Measurements.RemoveAt(i);
        }
    }

    // Repairs a measurement's anchors in place: an invalid part anchor whose part
    // still exists somewhere (decoupled, docked, grabbed in the editor, launched)
    // is swapped for a re-homed twin. False means an anchor is beyond repair; the
    // caller removes the measurement.
    private static bool RehomeAnchors(Anchor[] anchors, CelestialSystem system)
    {
        for (int i = 0; i < anchors.Length; i++)
        {
            if (anchors[i].IsValid(system))
                continue;
            Anchor? rehomed = anchors[i].Rehome(system);
            if (rehomed == null)
            {
                if (DebugConfig.Measure)
                    DefaultCategory.Log.Debug($"[MeasureTools] Anchor '{anchors[i].Label}' could not be re-homed (part or body gone).");
                return false;
            }
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] Anchor '{anchors[i].Label}' re-homed as '{rehomed.Label}'.");
            anchors[i] = rehomed;
            StateVersion++;
        }
        return true;
    }

    // The body whose frame anchors free points and carries the construction plane:
    // the user override, else the camera focus. A followed vehicle is handled by
    // view: in the map view it defers to its SOI parent so the plane sits at the body
    // it orbits (the natural plane for orbital geometry), but in the flight view the
    // camera sits right on the vehicle, so the vehicle itself is the reference.
    // Otherwise the construction plane would sit at the parent, often millions of
    // metres away, and a free point placed near the vehicle would land at that
    // distance instead of under the cursor.
    public static Astronomical? ResolveReferenceBody(IViewport viewport)
    {
        if (ReferenceOverride != null)
            return ReferenceOverride;
        IFollowable? following = viewport.GetCamera().Following;
        if (following is Vehicle vehicle)
        {
            // Vehicle.Orbit is FlightPlan.Patches[0].Orbit and throws on an empty
            // flight plan, so guard before walking to the SOI parent.
            if (viewport.Mode == CameraMode.Map
                && vehicle.FlightPlan.Patches.Count > 0 && vehicle.Orbit.Parent is Astronomical parent)
                return parent;
            return vehicle;
        }
        if (following is Astronomical astronomical)
            return astronomical;
        return Universe.CurrentSystem?.HomeBody as Astronomical;
    }

    public static void Reset()
    {
        Mode = MeasureMode.Ruler;
        SnapEnabled = true;
        PartSnapEnabled = true;
        PartFeatureSnapEnabled = true;
        PartVertexSnapEnabled = true;
        ToolActive = true;
        ReferenceOverride = null;
        HighlightIndex = -1;
        Pending.Clear();
        Measurements.Clear();
        _system = null;
        StateVersion++;
    }
}
