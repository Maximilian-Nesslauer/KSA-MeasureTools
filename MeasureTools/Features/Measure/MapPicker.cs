using Brutal.Numerics;
using KSA;
using MeasureTools.Core;

namespace MeasureTools.Features.Measure;

// Resolves a cursor position in the active view to a measurement anchor: a body
// under the cursor, a point on a visible orbit line, or a free point on the
// construction plane. Used both for the placement click and, per frame, for the
// hover preview. The map camera and the flight (orbit) camera project points the
// same way, so the same picking serves both.
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

    // Part picking runs only when the vehicle's bounding sphere projects at least
    // this large; below it part features are subpixel and the body-center snap is
    // the useful target.
    private const float PartVehicleMinDiameterPx = 24f;

    // Screen-space acceptance radius for part features (attach nodes, part centers).
    private const float PartFeatureSnapRadiusPx = 16f;

    // Screen-space acceptance radius for mesh vertices of the hit subpart.
    private const float PartVertexSnapRadiusPx = 12f;

    // Plane semantics: a plain click that snaps to nothing lands on the camera-facing
    // plane (always exactly under the cursor). With eclipticFree (ctrl held) all
    // snapping is skipped and the point lands on the ecliptic plane through the
    // reference body (or the previous point), the physically meaningful one.
    public static Anchor? Pick(Viewport viewport, float2 mouseViewport, bool eclipticFree = false)
    {
        // Surface mode has its own picking: ray versus the celestial spheres, no
        // body/orbit snapping and no free placement.
        if (MeasureState.Mode == MeasureMode.Surface)
            return PickSurface(viewport, mouseViewport);
        if (MeasureState.SnapEnabled && !eclipticFree)
        {
            if (MeasureState.PartSnapEnabled)
            {
                Anchor? partPoint = PickPart(viewport, mouseViewport);
                if (partPoint != null)
                    return partPoint;
            }
            Anchor? body = PickBody(viewport, mouseViewport);
            if (body != null)
                return body;
            Anchor? orbitPoint = PickOrbitPoint(viewport, mouseViewport);
            if (orbitPoint != null)
                return orbitPoint;
        }
        return PickFreePoint(viewport, mouseViewport, eclipticFree);
    }

    // Surface mode: cast the cursor ray against the mean-radius sphere of every
    // celestial (nearest hit wins) and pin the hit as lat/lon in the body-fixed
    // frame, so it tracks rotation like a ground marker. Once the first pin is
    // down, only its body is a valid target; the great-circle math needs both
    // points on one sphere.
    private static Anchor? PickSurface(Viewport viewport, float2 mouseViewport)
    {
        CelestialSystem? system = Universe.CurrentSystem;
        if (system == null)
            return null;
        Camera camera = viewport.GetCamera();
        Ray ray = camera.ScreenToEgoRay(mouseViewport);
        Celestial? required = MeasureState.Pending.Count > 0
            ? MeasureState.Pending[0].Body as Celestial
            : null;

        Celestial? best = null;
        double bestT = double.MaxValue;
        foreach (Astronomical astronomical in system.All.AsSpan())
        {
            if (astronomical is not Celestial celestial)
                continue;
            if (required != null && celestial != required)
                continue;
            var sphere = new BoundingSphere3D(camera.GetPositionEgo(celestial), celestial.MeanRadius);
            if (!ray.Raycast(sphere, out double t, out bool inside) || inside || !(t > 0.0))
                continue;
            if (t < bestT)
            {
                bestT = t;
                best = celestial;
            }
        }
        if (best == null)
            return null;

        // Ego axes are ECL axes, so lifting the hit point is a translation by the
        // camera position; lat/lon then come from the body-fixed frame.
        double3 hitEcl = camera.PositionEcl + ray.Direction * bestT;
        double3 hitCce = best.GetPositionCceFromEcl(hitEcl);
        double latitude = best.GetLatitudeFromCce(hitCce);
        double longitude = best.GetLongitudeFromCce(hitCce);
        return Anchor.PinOnSurface(best, latitude, longitude);
    }

    // A part feature candidate (attach node or part center) found by the
    // screen-space scan. Position is in the part's local asmb frame unless
    // IsVehicleAsmb (connectors, whose stock position is computed in the
    // vehicle-asmb frame). Vehicle stays null in the editor.
    private struct FeatureCandidate
    {
        public Vehicle? Vehicle;
        public Part? Part;
        public double3 Position;
        public bool IsVehicleAsmb;
        public string Label = "";
        public float ScreenDist;

        public FeatureCandidate()
        {
        }
    }

    // The nearest mesh raycast hit across all scanned part spans.
    private struct PartHit
    {
        public Part? FullPart;
        public Part? SubPart;
        public double3 LocalPos;
        public double Distance = double.MaxValue;
        public double4x4 Matrix;

        public PartHit()
        {
        }
    }

    // Part-level picking, patterned after the stock flight-view hover raycast
    // (Vehicle.UpdateHighlight) and the debug editor's connector snapping
    // (VehicleEditor.HandleConnectorConnections, a screen-space proximity test):
    // an exact watertight mesh raycast finds the part surface point under the
    // cursor, and two screen-space snap tiers refine it to attach nodes / part
    // centers and mesh vertices. Feature snap outranks vertex snap outranks the
    // raw surface hit (features are sparse, intentional targets; the surface hit
    // is always exactly under the cursor, so it stays the fallback). No result
    // falls through to the body/orbit/free picking.
    private static Anchor? PickPart(Viewport viewport, float2 mouseViewport)
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MapPicker.PickPart");
#endif
        Camera camera = viewport.GetCamera();
        Ray ray = camera.ScreenToEgoRay(mouseViewport);
        // Stock normalizes before part raycasts (Vehicle.UpdateHighlight); the
        // watertight test needs a unit direction for its distances to be metric.
        ray.Direction = ray.Direction.NormalizeOrZero();
        if (ray.Direction.X == 0.0 && ray.Direction.Y == 0.0 && ray.Direction.Z == 0.0)
            return null;

        // The edited craft lives in the editing space, not in the system's vehicle
        // list; and while editing an existing vehicle, the original still exists in
        // the system at the same location, so the flight scan must not run too.
        if (Program.Editor != null)
            return PickPartEditor(camera, mouseViewport, ray);

        CelestialSystem? system = Universe.CurrentSystem;
        if (system == null)
            return null;

        Vehicle? hitVehicle = null;
        var hit = new PartHit();
        var feature = new FeatureCandidate { ScreenDist = PartFeatureSnapRadiusPx };

        foreach (Astronomical astronomical in system.All.AsSpan())
        {
            if (astronomical is not Vehicle vehicle)
                continue;
            PartTree? tree = vehicle.Parts;
            if (tree == null || tree.Parts.Length == 0)
                continue;
            double3 vehiclePosEgo = camera.GetPositionEgo(vehicle);
            // Vehicle.MeanRadius is the bounding-sphere radius about the CoM, the
            // same point vehiclePosEgo refers to.
            double radius = vehicle.MeanRadius;
            if (!(radius > 0.0))
                continue;
            if (camera.GetObjectDiameterPixelsAsDouble(radius * 2.0, vehiclePosEgo.Length()) < PartVehicleMinDiameterPx)
                continue;

            double4x4 matrixVehicleAsmb2Ego = vehicle.GetMatrixAsmb2Ego(vehiclePosEgo);

            if (MeasureState.PartFeatureSnapEnabled)
            {
                float featureDistBefore = feature.ScreenDist;
                ScanPartFeatures(camera, mouseViewport, tree.Parts, vehicle.Id + " ", in matrixVehicleAsmb2Ego, ref feature);
                if (feature.ScreenDist < featureDistBefore)
                    feature.Vehicle = vehicle;
            }

            // Sphere gate before the per-part mesh raycasts; slight padding so a
            // hull point right at the bounding sphere still passes.
            var sphere = new BoundingSphere3D(vehiclePosEgo, radius * 1.1);
            if (!ray.Raycast(sphere, out _, out _))
                continue;

            double hitDistanceBefore = hit.Distance;
            RaycastPartSpan(tree.Parts, in matrixVehicleAsmb2Ego, ray, ref hit);
            if (hit.Distance < hitDistanceBefore)
                hitVehicle = vehicle;
        }

        if (feature.Vehicle != null && feature.Part != null)
        {
            return feature.IsVehicleAsmb
                ? Anchor.AtPartVehicleAsmb(feature.Vehicle, feature.Part, feature.Position, feature.Label)
                : Anchor.AtPartLocal(feature.Vehicle, feature.Part, feature.Position, feature.Label);
        }

        if (hitVehicle == null || hit.FullPart == null || hit.SubPart == null)
            return null;

        if (MeasureState.PartVertexSnapEnabled
            && TryPickVertex(camera, mouseViewport, hit.SubPart, in hit.Matrix, out double3 vertexLocal))
        {
            return Anchor.AtPartLocal(hitVehicle, hit.SubPart, vertexLocal,
                hitVehicle.Id + " " + hit.FullPart.DisplayName + " vertex");
        }

        return Anchor.AtPartLocal(hitVehicle, hit.SubPart, hit.LocalPos,
            hitVehicle.Id + " " + hit.FullPart.DisplayName + " surface");
    }

    // Editor variant: same snap tiers over the editing space's part tree plus any
    // unattached (grabbed/floating) trees, using the editing space transform. No
    // size or sphere gates; the editor camera always sits close to the craft, and
    // the stock editor raycasts the same parts every frame anyway.
    private static Anchor? PickPartEditor(Camera camera, float2 mouseViewport, Ray ray)
    {
        VehicleEditor editor = Program.Editor!;
        VehicleEditingSpace space = editor.EditingSpace;
        double4x4 matrixVehicleAsmb2Ego = space.GetMatrixAsmb2Ego(camera);

        var hit = new PartHit();
        var feature = new FeatureCandidate { ScreenDist = PartFeatureSnapRadiusPx };

        if (MeasureState.PartFeatureSnapEnabled)
        {
            ScanPartFeatures(camera, mouseViewport, space.AllParts, "", in matrixVehicleAsmb2Ego, ref feature);
            foreach (PartTree tree in editor.UnattachedPartTrees)
                ScanPartFeatures(camera, mouseViewport, tree.Parts, "", in matrixVehicleAsmb2Ego, ref feature);
        }
        RaycastPartSpan(space.AllParts, in matrixVehicleAsmb2Ego, ray, ref hit);
        foreach (PartTree tree in editor.UnattachedPartTrees)
            RaycastPartSpan(tree.Parts, in matrixVehicleAsmb2Ego, ray, ref hit);

        if (feature.Part != null)
        {
            return feature.IsVehicleAsmb
                ? Anchor.AtEditorPartVehicleAsmb(feature.Part, feature.Position, feature.Label)
                : Anchor.AtEditorPartLocal(feature.Part, feature.Position, feature.Label);
        }

        if (hit.FullPart == null || hit.SubPart == null)
            return null;

        if (MeasureState.PartVertexSnapEnabled
            && TryPickVertex(camera, mouseViewport, hit.SubPart, in hit.Matrix, out double3 vertexLocal))
        {
            return Anchor.AtEditorPartLocal(hit.SubPart, vertexLocal, hit.FullPart.DisplayName + " vertex");
        }

        return Anchor.AtEditorPartLocal(hit.SubPart, hit.LocalPos, hit.FullPart.DisplayName + " surface");
    }

    // The stock per-part mesh raycast (Part.RayCastEgo) over one part span,
    // keeping the globally nearest hit in `hit`.
    private static void RaycastPartSpan(ReadOnlySpan<Part> parts, ref readonly double4x4 matrixVehicleAsmb2Ego, Ray ray, ref PartHit hit)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].RayCastEgo(in matrixVehicleAsmb2Ego, ray, out double minDistance, out _,
                    out double3 nearLocal, out _, out _, out _, out Part? closestSubPart, out _)
                && minDistance > 0.0 && minDistance < hit.Distance && closestSubPart != null)
            {
                hit.FullPart = parts[i];
                hit.SubPart = closestSubPart;
                hit.LocalPos = nearLocal;
                hit.Distance = minDistance;
                hit.Matrix = matrixVehicleAsmb2Ego;
            }
        }
    }

    // Screen-space scan over attach nodes and part bounding-box centers, the same
    // proximity idea the editor's connector snap uses. Deliberately not ray-gated:
    // a node on the hull silhouette should snap even when the cursor is just off
    // the mesh. Occlusion is ignored; features are sparse and the preview shows
    // which one wins before the click. The caller owns FeatureCandidate.Vehicle
    // (null in the editor).
    private static void ScanPartFeatures(Camera camera, float2 mouseViewport, ReadOnlySpan<Part> parts,
        string labelPrefix, ref readonly double4x4 matrixVehicleAsmb2Ego, ref FeatureCandidate best)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            Part part = parts[i];
            (double3 min, double3 max) = part.BoundingBoxPartAsmb;
            // Meshless parts (e.g. an EVA kitten's root) have a degenerate box.
            if (min.X <= max.X)
            {
                double3 centerLocal = (min + max) * 0.5;
                float2 s = camera.EgoToScreen(centerLocal.Transform(part.MatrixAsmb2Ego(in matrixVehicleAsmb2Ego)));
                if (!float.IsNaN(s.X) && !float.IsNaN(s.Y))
                {
                    float d = float2.Distance(s, mouseViewport);
                    if (d < best.ScreenDist)
                    {
                        best.Part = part;
                        best.Position = centerLocal;
                        best.IsVehicleAsmb = false;
                        best.Label = labelPrefix + part.DisplayName + " center";
                        best.ScreenDist = d;
                    }
                }
            }
            for (int j = 0; j < part.Connectors.Count; j++)
            {
                Part.Connector connector = part.Connectors[j];
                float2 s = camera.EgoToScreen(connector.PositionEgo(in matrixVehicleAsmb2Ego));
                if (float.IsNaN(s.X) || float.IsNaN(s.Y))
                    continue;
                float d = float2.Distance(s, mouseViewport);
                if (d < best.ScreenDist)
                {
                    best.Part = part;
                    best.Position = connector.PositionVehicleAsmb;
                    best.IsVehicleAsmb = true;
                    best.Label = labelPrefix + part.DisplayName + " node '" + connector.Id + "'";
                    best.ScreenDist = d;
                }
            }
        }
    }

    // Nearest mesh vertex of the hit subpart within the vertex snap radius.
    // PositionCompare is the index-unrolled triangle list the game's own raycast
    // uses (three entries per triangle, in the subpart's local frame); scanning it
    // revisits shared vertices but needs no extra assembly references. Only called
    // after RayCastEgo hit this subpart, so the mesh view and its arrays exist.
    private static bool TryPickVertex(Camera camera, float2 mouseViewport, Part subPart,
        ref readonly double4x4 matrixVehicleAsmb2Ego, out double3 vertexLocal)
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MapPicker.TryPickVertex");
#endif
        vertexLocal = default;
        Span<MeshViewModule> meshViews = subPart.Modules.Get<MeshViewModule>();
        if (meshViews.IsEmpty)
            return false;
        double3[] vertices = meshViews[0].MeshView.PositionCompare;
        double4x4 matrixAsmb2Ego = subPart.MatrixAsmb2Ego(in matrixVehicleAsmb2Ego);
        float bestDist = PartVertexSnapRadiusPx;
        bool found = false;
        for (int i = 0; i < vertices.Length; i++)
        {
            float2 s = camera.EgoToScreen(vertices[i].Transform(matrixAsmb2Ego));
            if (float.IsNaN(s.X) || float.IsNaN(s.Y))
                continue;
            float d = float2.Distance(s, mouseViewport);
            if (d < bestDist)
            {
                bestDist = d;
                vertexLocal = vertices[i];
                found = true;
            }
        }
        return found;
    }

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
                    if (patch.Orbit.GetNearestPosition(viewport, mouseViewport, patch, out CelestialPosition? pos, lerp: true))
                        TryAccept(pos, vehicle.Id, camera, viewport, mouseViewport, ref best, ref bestId);
                }
                CelestialPosition? burnPos = null;
                if (vehicle.FlightComputer.BurnPlan.GetNearestOrbitPoint(viewport, mouseViewport, ref burnPos))
                    TryAccept(burnPos, vehicle.Id, camera, viewport, mouseViewport, ref best, ref bestId);
            }
            else if (astronomical is Celestial celestial)
            {
                if (!celestial.ShowOrbit && !celestial.TargetOfControlledVehicle)
                    continue;
                if (!Astronomical.ShouldDrawLines(astronomical, viewport, celestial.Orbit))
                    continue;
                if (celestial.Orbit.GetNearestPosition(viewport, mouseViewport, null, out CelestialPosition? pos, lerp: true))
                    TryAccept(pos, celestial.Id, camera, viewport, mouseViewport, ref best, ref bestId);
            }
        }

        if (!best.HasValue)
            return null;
        CelestialPosition cp = best.Value;
        return Anchor.OnOrbit(cp.Parent, cp.Point.PositionCce, bestId);
    }

    // Keep the candidate if it is on screen near the cursor and closer than the best
    // so far. Shared by the three orbit-candidate sources (flight-plan patches, the
    // burn plan, celestial orbits), all of which produce a nullable CelestialPosition.
    private static void TryAccept(CelestialPosition? candidate, string id, Camera camera, Viewport viewport, float2 mouseViewport, ref CelestialPosition? best, ref string bestId)
    {
        if (candidate.HasValue
            && IsOnScreenNearCursor(candidate.Value, camera, viewport, mouseViewport)
            && candidate.Value.IsBetterThan(camera, mouseViewport, best))
        {
            best = candidate;
            bestId = id;
        }
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
        refBody = null;
        // In the editor free points anchor to the editing space (the reference
        // combo is disabled there); the system reference bodies stay out of it.
        if (Program.Editor != null)
        {
            planePointEcl = MeasureState.Pending.Count > 0
                ? MeasureState.Pending[^1].ResolveEcl()
                : Program.Editor.EditingSpace.PositionEcl;
            normalEcl = eclipticPlane
                ? double3.UnitZ
                : viewport.GetCamera().GetForward();
            return true;
        }
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
        if (!TryGetFreePlane(viewport, eclipticPlane, out double3 planePoint, out double3 normal, out Astronomical? refBody))
            return null;
        Camera camera = viewport.GetCamera();

        // Ego axes are ECL axes (Camera.EgoToEcl is a pure translation), so the ego
        // ray direction is an ECL direction and the ray origin is the camera position.
        Ray ray = camera.ScreenToEgoRay(mouseViewport);
        if (!MathEx.RayPlaneIntersection(camera.PositionEcl, ray.Direction, planePoint, normal, out double t) || !(t > 0.0))
            return null;
        double3 pointEcl = camera.PositionEcl + ray.Direction * t;
        if (Program.Editor != null)
            return Anchor.EditorFree(pointEcl - Program.Editor.EditingSpace.PositionEcl);
        if (refBody == null)
            return null;
        return Anchor.Free(refBody, refBody.GetPositionCceFromEcl(pointEcl));
    }
}
