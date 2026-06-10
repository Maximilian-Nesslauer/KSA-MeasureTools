using Brutal.Numerics;
using KSA;
using MeasureTools.Core;

namespace MeasureTools.Features.Measure;

// Resolves a cursor position in the map view to a measurement anchor: a body under
// the cursor, a point on a visible orbit line, or a free point on the construction
// plane. Used both for the placement click and, per frame, for the hover preview.
internal static class MapPicker
{
    // Point-like body snap radius in screen pixels; the stock hover box is similar
    // (max(0.5% of viewport height, 8 px) plus the projected body radius).
    private const float CenterSnapRadiusPx = 14f;

    // A body counts as a disc (its limb is a snap target) from this projected
    // radius up; below it the disc is dot-sized and the limb equals the center.
    private const float MinLimbDiscPx = 16f;

    // Snap band around the projected disc edge: inside it the limb wins over the
    // center, outside the disc it still catches a slightly-off click.
    private const float LimbSnapTolerancePx = 10f;

    // Our own acceptance radius for orbit-point candidates, as a fraction of the
    // viewport height (0.025 * height matches the 0.05 NDC threshold stock intends).
    private const float OrbitSnapMaxScreenFraction = 0.025f;

    // Plane semantics (Maxi's choice after testing): a plain click that snaps to
    // nothing lands on the camera-facing plane (always exactly under the cursor);
    // ctrl+click skips all snapping and lands on the ecliptic plane through the
    // reference body (or the previous point), the physically meaningful one.
    public static Anchor? Pick(Viewport viewport, float2 mouseViewport, bool eclipticFree = false)
    {
        if (MeasureState.SnapEnabled && !eclipticFree)
        {
            Anchor? body = PickBody(viewport, mouseViewport);
            if (body != null)
                return body;
            Anchor? orbitPoint = PickOrbitPoint(viewport, mouseViewport);
            if (orbitPoint != null)
                return orbitPoint;
        }
        return PickFreePoint(viewport, mouseViewport, eclipticFree);
    }

    // The eclipticFree flag flows through Pick, PickFreePoint and TryGetFreePlane
    // under this one name: ctrl held, skip snapping, use the ecliptic plane.

    // Unified body snap: discs and dots. The stock HoveredOrbiter flag is not used
    // here (it is a boolean box test that cannot distinguish center from limb); one
    // scan computes the projected center and disc radius for every body instead.
    private static Anchor? PickBody(Viewport viewport, float2 mouseViewport)
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MapPicker.PickBody");
#endif
        CelestialSystem? system = Universe.CurrentSystem;
        if (system == null)
            return null;
        Camera camera = viewport.GetCamera();

        // Tier 1: the cursor is on a body's projected disc. Among overlapping discs
        // the smallest wins (the most specific target, e.g. a moon over its planet).
        Astronomical? disc = null;
        float discRadiusPx = float.MaxValue;
        float discCenterDist = 0f;

        // Tier 2: point-like snap to the nearest projected center. Only bodies the
        // user can actually see qualify: those whose UI box the game drew most
        // recently (IOrbiter.DrawnUiBox, the same gate stock hover/click uses;
        // current frame on the preview path, previous frame on the click path since
        // input callbacks run before the UI draw) and stars (always relevant, never
        // boxed). Without this gate, every comet and asteroid in the system is a
        // snap target even when nothing marks it on screen, and free placement
        // becomes nearly impossible in a dense system.
        Astronomical? nearest = null;
        float nearestDist = CenterSnapRadiusPx;

        foreach (Astronomical astronomical in system.All.AsSpan())
        {
            float2 s = camera.EclToScreen(astronomical.GetPositionEcl());
            if (float.IsNaN(s.X) || float.IsNaN(s.Y))
                continue;
            float d = float2.Distance(s, mouseViewport);
            bool visibleMarker = astronomical is StellarBody
                || (astronomical is IOrbiter orbiter && orbiter.DrawnUiBox);
            if (visibleMarker && d < nearestDist)
            {
                nearestDist = d;
                nearest = astronomical;
            }

            double distance = (astronomical.GetPositionEcl() - camera.PositionEcl).Length();
            if (!(distance > astronomical.MeanRadius))
                continue;
            float radiusPx = (float)(camera.GetObjectDiameterPixelsAsDouble(astronomical.MeanRadius * 2.0, distance) * 0.5);
            if (radiusPx < MinLimbDiscPx || d > radiusPx + LimbSnapTolerancePx)
                continue;
            if (radiusPx < discRadiusPx)
            {
                disc = astronomical;
                discRadiusPx = radiusPx;
                discCenterDist = d;
            }
        }

        if (disc != null)
        {
            // Edge band snaps to the limb, the disc interior to the center.
            if (discCenterDist >= discRadiusPx - LimbSnapTolerancePx)
            {
                Anchor? limb = SnapToLimb(camera, mouseViewport, disc);
                if (limb != null)
                    return limb;
            }
            return Anchor.AtBody(disc);
        }
        return nearest != null ? Anchor.AtBody(nearest) : null;
    }

    // The point on the body's sphere in the cursor's direction: drop the cursor
    // ray's closest point to the body center onto the sphere. At map distances this
    // is the visible limb toward the cursor (the exact tangent circle is tilted
    // toward the camera by radius/distance, negligible here).
    private static Anchor? SnapToLimb(Camera camera, float2 mouseViewport, Astronomical body)
    {
        Ray ray = camera.ScreenToEgoRay(mouseViewport);
        double3 origin = camera.PositionEcl;
        double3 center = body.GetPositionEcl();
        double t = double3.Dot(center - origin, ray.Direction);
        if (!(t > 0.0))
            return null;
        double3 closest = origin + ray.Direction * t;
        double3 dir = (closest - center).NormalizeOrZero();
        if (dir.X == 0.0 && dir.Y == 0.0 && dir.Z == 0.0)
            return null;
        return Anchor.AtSurface(body, dir * body.MeanRadius);
    }

    // Nearest point on any visible orbit line, mirroring the candidate set of the
    // stock burn-click picker (CelestialSystem.SetNearestOrbitPoint): flight-plan
    // patches and burn-plan orbits for shown vehicles, plain orbits for shown
    // celestials. Unlike stock, results are used for every body, not only the
    // controlled vehicle. Stock runs the same scan per frame on a worker thread;
    // this runs it on the main thread per preview frame and per click. The math is
    // closed-form per orbit, but if a dense save ever shows up in the PerfTracker
    // numbers, this is the place to optimize.
    private static Anchor? PickOrbitPoint(Viewport viewport, float2 mouseViewport)
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MapPicker.PickOrbitPoint");
#endif
        CelestialSystem? system = Universe.CurrentSystem;
        if (system == null)
            return null;
        Camera camera = viewport.GetCamera();

        CelestialPosition? best = null;
        string bestId = "";

        foreach (Astronomical astronomical in system.All.AsSpan())
        {
            if (astronomical is not IOrbiter)
                continue;
            if (astronomical is Vehicle vehicle)
            {
                if (!vehicle.ShowOrbit && !vehicle.TargetOfControlledVehicle && Program.ControlledVehicle != vehicle)
                    continue;
                var patches = vehicle.FlightPlan.Patches;
                for (int i = patches.Count - 1; i >= 0; i--)
                {
                    PatchedConic patch = patches[i];
                    if (!Astronomical.ShouldDrawLines(patch.PrimaryBody, viewport, patch.Orbit))
                        continue;
                    if (patch.Orbit.GetNearestPosition(viewport, mouseViewport, patch, out CelestialPosition? pos, lerp: true)
                        && pos.HasValue && IsOnScreenNearCursor(pos.Value, camera, viewport, mouseViewport)
                        && pos.Value.IsBetterThan(camera, mouseViewport, best))
                    {
                        best = pos;
                        bestId = vehicle.Id;
                    }
                }
                CelestialPosition? burnPos = null;
                if (vehicle.FlightComputer.BurnPlan.GetNearestOrbitPoint(viewport, mouseViewport, ref burnPos)
                    && burnPos.HasValue && IsOnScreenNearCursor(burnPos.Value, camera, viewport, mouseViewport)
                    && burnPos.Value.IsBetterThan(camera, mouseViewport, best))
                {
                    best = burnPos;
                    bestId = vehicle.Id;
                }
            }
            else if (astronomical is Celestial celestial)
            {
                if (!celestial.ShowOrbit && !celestial.TargetOfControlledVehicle)
                    continue;
                if (!Astronomical.ShouldDrawLines(astronomical, viewport, celestial.Orbit))
                    continue;
                if (celestial.Orbit.GetNearestPosition(viewport, mouseViewport, null, out CelestialPosition? pos, lerp: true)
                    && pos.HasValue && IsOnScreenNearCursor(pos.Value, camera, viewport, mouseViewport)
                    && pos.Value.IsBetterThan(camera, mouseViewport, best))
                {
                    best = pos;
                    bestId = celestial.Id;
                }
            }
        }

        if (!best.HasValue)
            return null;
        CelestialPosition cp = best.Value;
        return Anchor.OnOrbit(cp.Parent, cp.Point.PositionCce, bestId);
    }

    // Re-validate an orbit-point candidate on screen. Stock GetNearestPoint has a
    // latent NaN hole: a candidate that projects behind the camera has a NaN screen
    // position (EclToScreen with ignoreBehind), its NDC distance check evaluates to
    // (NaN > threshold) == false, and the bogus point is ACCEPTED (e.g. a click near
    // Earth grabbing a point on the Uranus orbit plane behind the camera). Such a
    // candidate also distorts IsBetterThan, which projects through the NDC path
    // without a behind-camera guard and can yield a deceptively small distance for
    // it, shadowing real candidates. Stock never hits this because it only consumes
    // results for the controlled vehicle's nearby orbits; our scan over every
    // celestial does.
    private static bool IsOnScreenNearCursor(CelestialPosition candidate, Camera camera, Viewport viewport, float2 mouseViewport)
    {
        float2 s = candidate.Point.GetPositionScreen(candidate.Parent, camera);
        if (float.IsNaN(s.X) || float.IsNaN(s.Y) || float.IsInfinity(s.X) || float.IsInfinity(s.Y))
            return false;
        float maxPx = MathF.Max(24f, viewport.Size.Y * OrbitSnapMaxScreenFraction);
        return float2.Distance(s, mouseViewport) <= maxPx;
    }

    // The construction plane for free placement: through the previous pending point
    // (so all points of one measurement share a depth basis), else through the
    // reference body. The ecliptic plane is the ECL XY plane, normal double3.UnitZ;
    // verified against orbit-point CCE offsets in-game (Earth's orbit has tiny Z,
    // Hale-Bopp at 89 deg inclination has huge Z). Double3Ex.Up = (0,1,0) is the
    // camera-up convention, NOT the ecliptic normal.
    public static bool TryGetFreePlane(Viewport viewport, bool eclipticPlane, out double3 planePointEcl, out double3 normalEcl, out Astronomical? refBody)
    {
        planePointEcl = default;
        normalEcl = default;
        refBody = MeasureState.ResolveReferenceBody(viewport);
        if (refBody == null)
            return false;
        planePointEcl = MeasureState.Pending.Count > 0
            ? MeasureState.Pending[^1].ResolveEcl()
            : refBody.GetPositionEcl();
        normalEcl = eclipticPlane
            ? double3.UnitZ
            : viewport.GetCamera().GetForward();
        return true;
    }

    private static Anchor? PickFreePoint(Viewport viewport, float2 mouseViewport, bool eclipticPlane)
    {
        if (!TryGetFreePlane(viewport, eclipticPlane, out double3 planePoint, out double3 normal, out Astronomical? refBody) || refBody == null)
            return null;
        Camera camera = viewport.GetCamera();

        // Ego axes are ECL axes (Camera.EgoToEcl is a pure translation), so the ego
        // ray direction is an ECL direction and the ray origin is the camera position.
        Ray ray = camera.ScreenToEgoRay(mouseViewport);
        if (!MathEx.RayPlaneIntersection(camera.PositionEcl, ray.Direction, planePoint, normal, out double t) || !(t > 0.0))
            return null;
        double3 pointEcl = camera.PositionEcl + ray.Direction * t;
        return Anchor.Free(refBody, refBody.GetPositionCceFromEcl(pointEcl));
    }
}
