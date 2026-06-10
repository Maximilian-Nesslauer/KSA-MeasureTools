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

    public static int PointsNeeded => Mode == MeasureMode.Ruler ? 2 : 3;

    // The tool captures map clicks only while its window is open and the main
    // viewport is in map mode.
    public static bool IsArmed =>
        MeasureWindow.IsOpen
        && Universe.CurrentSystem != null
        && Program.MainViewport.Mode == CameraMode.Map;

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
                string value = measurement.Mode == MeasureMode.Ruler
                    ? $"distance={measurement.DistanceMeters():F1} m"
                    : $"angle={measurement.AngleRadians() * (180.0 / Math.PI):F3} deg";
                DefaultCategory.Log.Debug($"[MeasureTools] Measurement #{Measurements.Count} completed: {value}.");
            }
        }
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

    // Drop state that no longer resolves: a system change clears everything, a
    // removed body (e.g. deleted vehicle) drops the affected measurement.
    public static void Prune()
    {
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
        }
        for (int i = 0; i < Pending.Count; i++)
        {
            if (!Pending[i].IsValid(system))
            {
                if (DebugConfig.Measure)
                    DefaultCategory.Log.Debug($"[MeasureTools] Pending anchor '{Pending[i].Label}' lost its body, pending cleared.");
                Pending.Clear();
                StateVersion++;
                break;
            }
        }
        for (int i = Measurements.Count - 1; i >= 0; i--)
        {
            if (!Measurements[i].IsValid(system))
            {
                if (DebugConfig.Measure)
                    DefaultCategory.Log.Debug($"[MeasureTools] Measurement #{i + 1} lost an anchored body, removed.");
                Measurements.RemoveAt(i);
            }
        }
    }

    // The body whose frame anchors free points and carries the construction plane:
    // the user override, else the map camera focus (a vehicle defers to its SOI
    // parent so the plane sits at the body it orbits).
    public static Astronomical? ResolveReferenceBody(Viewport viewport)
    {
        if (ReferenceOverride != null)
            return ReferenceOverride;
        IFollowable? following = viewport.GetCamera().Following;
        if (following is Vehicle vehicle)
        {
            // Vehicle.Orbit is FlightPlan.Patches[0].Orbit and throws on an empty
            // flight plan, so guard before walking to the SOI parent.
            if (vehicle.FlightPlan.Patches.Count > 0 && vehicle.Orbit.Parent is Astronomical parent)
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
        ReferenceOverride = null;
        HighlightIndex = -1;
        Pending.Clear();
        Measurements.Clear();
        _system = null;
        StateVersion++;
    }
}
