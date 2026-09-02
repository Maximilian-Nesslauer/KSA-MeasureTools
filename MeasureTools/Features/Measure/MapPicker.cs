using Brutal.GlfwApi;
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

    // Acceptance radius for orbit-point candidates, as a fraction of the viewport
    // height. Stock filters at 0.025 in NDC units (Orbit.GetNearestPosition), which
    // on a landscape viewport is 0.0125 * height vertically and 0.0125 * width
    // horizontally; an isotropic 0.025 * height circumscribes that ellipse from 16:9
    // on, so there this rejects only what IsOnScreenNearCursor exists for. Stock
    // scales the NDC x term by an INTEGER width / height, which is 0 on a portrait
    // viewport and drops the x term entirely, and against that unbounded band this
    // radius is the narrower gate.
    private const float OrbitSnapMaxScreenFraction = 0.025f;

    // Part picking runs only when the vehicle's bounding sphere projects at least
    // this large; below it part features are subpixel and the body-center snap is
    // the useful target.
    private const float PartVehicleMinDiameterPx = 24f;

    // Screen-space acceptance radius for part point features (attach nodes, part
    // centers, rim centers, the mirror point).
    private const float PartFeatureSnapRadiusPx = 16f;

    // Screen-space acceptance radius for mesh vertices and feature-edge midpoints
    // of the hit subpart.
    private const float PartVertexSnapRadiusPx = 12f;

    // Screen-space acceptance radius for the closest point on a feature edge.
    private const float PartEdgeSnapRadiusPx = 10f;

    // Screen-space acceptance radius from the cursor to a circle's ring in
    // Circle mode.
    private const float CircleSnapRadiusPx = 14f;

    // Plane semantics: a plain click that snaps to nothing lands on the camera-facing
    // plane (always exactly under the cursor). With eclipticFree (ctrl held) all
    // snapping is skipped and the point lands on the ecliptic plane through the
    // reference body (or the previous point), the physically meaningful one.
    // reuseStockHover lets the throttled hover preview opt into this frame's
    // stock hover raycast (flight Orbit view only). Placement clicks keep the
    // default full scan: input callbacks run before the frame's UI draw, where
    // the stock value is still a frame stale, so forgetting the flag costs a
    // redundant scan instead of a wrong pick.
    public static Anchor? Pick(IGameViewport viewport, float2 mouseViewport, bool eclipticFree = false, bool reuseStockHover = false)
    {
        // Surface mode has its own picking: ray versus the celestial spheres, no
        // body/orbit snapping and no free placement. FaceAngle picks raw part
        // surface hits only (the normal is the datum). Circle mode returns an
        // anchor PAIR and never routes through here (see PickCircle).
        if (MeasureState.Mode == MeasureMode.Surface)
            return PickSurface(viewport, mouseViewport);
        if (MeasureState.Mode == MeasureMode.FaceAngle)
            return PickFaceAngle(viewport, mouseViewport, reuseStockHover);
        if (MeasureState.Mode == MeasureMode.Circle)
            return null;
        if (MeasureState.SnapEnabled && !eclipticFree)
        {
            if (MeasureState.PartSnapEnabled)
            {
                Anchor? partPoint = PickPart(viewport, mouseViewport, reuseStockHover);
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
    private static Anchor? PickSurface(IViewport viewport, float2 mouseViewport)
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
        // camera position; lat/lon then come from the body-fixed frame. The origin
        // comes from the ray, not from the camera: ScreenToEgoRay starts a
        // perspective ray at the camera but an orthographic one at the cursor's own
        // near-plane point, which is what the vehicle editor's Projection toggle
        // switches to. EgoToEcl is a pure translation, so this is PositionEcl in the
        // perspective case.
        double3 hitEcl = camera.EgoToEcl(ray.Origin) + ray.Direction * bestT;
        double3 hitCce = best.GetPositionCceFromEcl(hitEcl);
        double latitude = best.GetLatitudeFromCce(hitCce);
        double longitude = best.GetLongitudeFromCce(hitCce);
        return Anchor.PinOnSurface(best, latitude, longitude);
    }

    // A part point-feature candidate (attach node, part center, rim center or
    // mirror point) found by the screen-space scan. Position is in the part's
    // local asmb frame unless IsVehicleAsmb (connectors, whose stock position is
    // computed in the vehicle-asmb frame). Vehicle stays null in the editor;
    // Normal carries a circle's plane normal.
    private struct FeatureCandidate
    {
        public Vehicle? Vehicle;
        public Part? Part;
        public double3 Position;
        public bool IsVehicleAsmb;
        public double3? Normal;
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
        public double3 NormalLocal;
        public double Distance = double.MaxValue;
        public double4x4 Matrix;

        public PartHit()
        {
        }
    }

    // Shared gated mesh-hit scan behind all part-based picking. With the editor
    // open it scans the editing space's tree plus the unattached (in-hand) trees
    // (the edited craft is not in the system list, and the original of an edited
    // vehicle still is - double-pick hazard); otherwise every vehicle that
    // projects large enough, with a bounding-sphere pre-check per vehicle.
    // Optionally collects point-feature candidates along the way so PickPart
    // shares the vehicle gating with the mode-specific pickers.
    private static bool TryGetMeshHit(IGameViewport viewport, float2 mouseViewport, bool scanFeatures, bool reuseStockHover,
        ref FeatureCandidate feature, out Camera camera, out Vehicle? hitVehicle, out PartHit hit)
    {
        camera = viewport.GetCamera();
        hitVehicle = null;
        hit = new PartHit();
        Ray ray = camera.ScreenToEgoRay(mouseViewport);
        // Stock normalizes before part raycasts (Vehicle.UpdateHighlight); the
        // watertight test needs a unit direction for its distances to be metric.
        ray.Direction = ray.Direction.NormalizeOrZero();
        if (ray.Direction.X == 0.0 && ray.Direction.Y == 0.0 && ray.Direction.Z == 0.0)
            return false;

        if (Program.Editor is VehicleEditor editor)
        {
            double4x4 matrixVehicleAsmb2Ego = editor.EditingSpace.GetMatrixAsmb2Ego(camera);
            if (scanFeatures)
            {
                ScanPartFeatures(camera, mouseViewport, editor.EditingSpace.AllParts, in matrixVehicleAsmb2Ego, ref feature);
                foreach (PartTree tree in editor.UnattachedPartTrees)
                    ScanPartFeatures(camera, mouseViewport, tree.Parts, in matrixVehicleAsmb2Ego, ref feature);
            }
            RaycastPartSpan(editor.EditingSpace.AllParts, in matrixVehicleAsmb2Ego, ray, ref hit);
            foreach (PartTree tree in editor.UnattachedPartTrees)
                RaycastPartSpan(tree.Parts, in matrixVehicleAsmb2Ego, ray, ref hit);
            return hit.SubPart != null;
        }

        CelestialSystem? system = Universe.CurrentSystem;
        if (system == null)
            return false;

        // Vehicle.UpdateHighlight already raycast every drawn vehicle into the
        // viewport's picker this frame, so reuse that instead of scanning again.
        // The gate is its own early-out: each clause is a case where the picker
        // reads null WITHOUT meaning "no part there".
        bool hoverShortcut = reuseStockHover
            && CursorTarget.IsHitTestViewport(viewport)
            && viewport.Mode == CameraMode.Orbit
            && Program.GetCursorMode() == GlfwCursorMode.Normal
            && !Program.IsModalOpen();
        Part? stockHovered = hoverShortcut ? viewport.PartPicker.Part : null;

        // With the shortcut active and stock reporting no hovered part, every
        // mesh raycast is a guaranteed miss; without a feature scan to run there
        // is nothing left to do. Accepted gap, documented: stock skips vehicles
        // failing its FOV check (unless controlled or targeted), so a
        // screen-edge vehicle the old full scan could hit stays invisible to
        // the shortcut; placement clicks still full-scan.
        if (hoverShortcut && stockHovered == null && !scanFeatures)
            return false;

        ScanFlightVehicles(system, camera, mouseViewport, ray, scanFeatures, hoverShortcut, stockHovered,
            ref feature, ref hit, ref hitVehicle);
        if (hoverShortcut && stockHovered != null && hit.SubPart == null)
        {
            // The single-part re-raycast can miss what stock reported: sub-frame
            // cursor drift at a silhouette, or an EVA kitten, whose hover comes
            // from a bounding-sphere test on a meshless part and can shadow a
            // vehicle behind it. Re-scan in full (features are already done) so
            // the preview never degrades below the pre-shortcut path.
            ScanFlightVehicles(system, camera, mouseViewport, ray, scanFeatures: false, hoverShortcut: false, null,
                ref feature, ref hit, ref hitVehicle);
        }
        return hit.SubPart != null;
    }

    private static void ScanFlightVehicles(CelestialSystem system, Camera camera, float2 mouseViewport, Ray ray,
        bool scanFeatures, bool hoverShortcut, Part? stockHovered,
        ref FeatureCandidate feature, ref PartHit hit, ref Vehicle? hitVehicle)
    {
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
            if (camera.GetObjectDiameterPixels(radius * 2.0, vehiclePosEgo.Length()) < PartVehicleMinDiameterPx)
                continue;

            double4x4 matrixVehicleAsmb2Ego = vehicle.GetMatrixAsmb2Ego(vehiclePosEgo);

            if (scanFeatures)
            {
                float featureDistBefore = feature.ScreenDist;
                ScanPartFeatures(camera, mouseViewport, tree.Parts, in matrixVehicleAsmb2Ego, ref feature);
                if (feature.ScreenDist < featureDistBefore)
                    feature.Vehicle = vehicle;
            }

            if (hoverShortcut)
            {
                // Only the stock-hovered part is re-raycast, and only when its
                // vehicle passed the size gate above (stock hover has no
                // minimum projected size).
                if (stockHovered == null || !ReferenceEquals(stockHovered.Tree, tree))
                    continue;
                double hitDistanceBefore = hit.Distance;
                RaycastPart(stockHovered, in matrixVehicleAsmb2Ego, ray, ref hit);
                if (hit.Distance < hitDistanceBefore)
                    hitVehicle = vehicle;
                continue;
            }

            // Sphere gate before the per-part mesh raycasts; slight padding so a
            // hull point right at the bounding sphere still passes.
            var sphere = new BoundingSphere3D(vehiclePosEgo, radius * 1.1);
            if (!ray.Raycast(sphere, out _, out _))
                continue;

            double hitDistanceBeforeSpan = hit.Distance;
            RaycastPartSpan(tree.Parts, in matrixVehicleAsmb2Ego, ray, ref hit);
            if (hit.Distance < hitDistanceBeforeSpan)
                hitVehicle = vehicle;
        }
    }

    // Builds the flight or editor variant of a part anchor; the editor is the
    // active context exactly when no owning vehicle was attributed.
    private static Anchor MakePartAnchor(Vehicle? vehicle, Part part, double3 offsetLocal, string partLabel, double3? normalLocal = null)
    {
        return vehicle != null
            ? Anchor.AtPartLocal(vehicle, part, offsetLocal, partLabel, normalLocal)
            : Anchor.AtEditorPartLocal(part, offsetLocal, partLabel, normalLocal);
    }

    private static Anchor MakePartAnchorVehicleAsmb(Vehicle? vehicle, Part part, double3 posVehicleAsmb, string partLabel)
    {
        return vehicle != null
            ? Anchor.AtPartVehicleAsmb(vehicle, part, posVehicleAsmb, partLabel)
            : Anchor.AtEditorPartVehicleAsmb(part, posVehicleAsmb, partLabel);
    }

    // Part-level picking, patterned after the stock flight-view hover raycast
    // (Vehicle.UpdateHighlight) and the debug editor's connector snapping
    // (VehicleEditor.HandleConnectorConnections, a screen-space proximity test).
    // Snap tiers, most intentional first: point features (attach nodes, part
    // centers, rim centers, the mirror point) > vertices and edge midpoints >
    // closest point on a feature edge > the raw watertight surface hit, which is
    // always exactly under the cursor and so stays the fallback. No result falls
    // through to the body/orbit/free picking.
    private static Anchor? PickPart(IGameViewport viewport, float2 mouseViewport, bool reuseStockHover)
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MapPicker.PickPart");
#endif
        var feature = new FeatureCandidate { ScreenDist = PartFeatureSnapRadiusPx };
        bool hasHit = TryGetMeshHit(viewport, mouseViewport, MeasureState.PartFeatureSnapEnabled, reuseStockHover,
            ref feature, out Camera camera, out Vehicle? hitVehicle, out PartHit hit);

        // The hit subpart's feature set is resolved once and threaded through the
        // tiers that need it (rim centers, vertices/midpoints, edges).
        MeshFeatureCache.MeshFeatures? hitFeatures = hasHit && hit.SubPart != null ? GetHitFeatures(in hit) : null;

        // Rim centers and the mirror point need the hit subpart, so they join
        // the point-feature tier after the general scan.
        if (hitFeatures != null && MeasureState.PartFeatureSnapEnabled)
        {
            AddCircleCenterCandidates(camera, mouseViewport, hitVehicle, in hit, hitFeatures, ref feature);
            AddMirrorCandidate(camera, mouseViewport, hitVehicle, in hit, ref feature);
        }

        if (feature.Part != null)
        {
            return feature.IsVehicleAsmb
                ? MakePartAnchorVehicleAsmb(feature.Vehicle, feature.Part, feature.Position, feature.Label)
                : MakePartAnchor(feature.Vehicle, feature.Part, feature.Position, feature.Label, feature.Normal);
        }

        if (hitFeatures == null || hit.FullPart == null || hit.SubPart == null)
            return null;

        if (MeasureState.PartVertexSnapEnabled)
        {
            if (TryPickVertexOrMidpoint(camera, mouseViewport, hit.SubPart, in hit.Matrix, hitFeatures, out double3 pointLocal, out bool isMidpoint))
            {
                return MakePartAnchor(hitVehicle, hit.SubPart, pointLocal,
                    hit.FullPart.DisplayName + (isMidpoint ? " edge mid" : " vertex"));
            }
            if (TryPickEdgePoint(camera, mouseViewport, hit.SubPart, in hit.Matrix, hitFeatures, out double3 edgeLocal))
                return MakePartAnchor(hitVehicle, hit.SubPart, edgeLocal, hit.FullPart.DisplayName + " edge");
        }

        return MakePartAnchor(hitVehicle, hit.SubPart, hit.LocalPos,
            hit.FullPart.DisplayName + " surface", NormalOrNull(hit.NormalLocal));
    }

    // Circle mode: one click on a circular feature edge. Produces the fitted
    // center (carrying the circle plane normal) plus the ring point nearest the
    // hit, both as part anchors; radius/diameter derive live from the pair.
    public static bool PickCircle(IGameViewport viewport, float2 mouseViewport, out Anchor? center, out Anchor? rim, bool reuseStockHover = false)
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MapPicker.PickCircle");
#endif
        center = null;
        rim = null;
        var unusedFeature = new FeatureCandidate();
        if (!TryGetMeshHit(viewport, mouseViewport, scanFeatures: false, reuseStockHover, ref unusedFeature,
                out Camera camera, out Vehicle? hitVehicle, out PartHit hit)
            || hit.SubPart == null || hit.FullPart == null)
            return false;
        MeshFeatureCache.MeshFeatures features = GetHitFeatures(in hit);
        if (features.Circles.Length == 0)
            return false;
        double4x4 matrixAsmb2Ego = hit.SubPart.MatrixAsmb2Ego(in hit.Matrix);

        int bestIndex = -1;
        double3 bestRimLocal = default;
        float bestDist = CircleSnapRadiusPx;
        for (int i = 0; i < features.Circles.Length; i++)
        {
            MeshFeatureCache.CircleFeature circle = features.Circles[i];
            // The ring point nearest the hit, in the subpart frame.
            double3 d = hit.LocalPos - circle.Center;
            double3 planar = d - circle.Normal * double3.Dot(d, circle.Normal);
            double3 dir = planar.NormalizeOrZero();
            if (dir.X == 0.0 && dir.Y == 0.0 && dir.Z == 0.0)
                continue;
            double3 rimLocal = circle.Center + dir * circle.Radius;
            float2 s = camera.EgoToScreen(rimLocal.Transform(matrixAsmb2Ego));
            if (float.IsNaN(s.X) || float.IsNaN(s.Y))
                continue;
            float dist = float2.Distance(s, mouseViewport);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
                bestRimLocal = rimLocal;
            }
        }
        if (bestIndex < 0)
            return false;
        MeshFeatureCache.CircleFeature best = features.Circles[bestIndex];
        string partName = hit.FullPart.DisplayName;
        center = MakePartAnchor(hitVehicle, hit.SubPart, best.Center, partName + " rim center", best.Normal);
        rim = MakePartAnchor(hitVehicle, hit.SubPart, bestRimLocal, partName + " rim");
        return true;
    }

    // FaceAngle mode: the raw surface hit only; snapping to points would move
    // the sample off the face whose normal is being measured.
    private static Anchor? PickFaceAngle(IGameViewport viewport, float2 mouseViewport, bool reuseStockHover)
    {
        var unusedFeature = new FeatureCandidate();
        if (!TryGetMeshHit(viewport, mouseViewport, scanFeatures: false, reuseStockHover, ref unusedFeature,
                out _, out Vehicle? hitVehicle, out PartHit hit)
            || hit.SubPart == null || hit.FullPart == null)
            return null;
        double3? normal = NormalOrNull(hit.NormalLocal);
        if (normal == null)
            return null;
        return MakePartAnchor(hitVehicle, hit.SubPart, hit.LocalPos, hit.FullPart.DisplayName + " face", normal);
    }

    private static double3? NormalOrNull(double3 normal)
    {
        double3 unit = normal.NormalizeOrZero();
        return unit.X == 0.0 && unit.Y == 0.0 && unit.Z == 0.0 ? null : unit;
    }

    private static MeshFeatureCache.MeshFeatures GetHitFeatures(ref readonly PartHit hit)
    {
        // The hit subpart was just raycast successfully, so its mesh view exists.
        return MeshFeatureCache.Get(hit.SubPart!.Modules.Get<MeshViewModule>()[0].MeshView);
    }

    // Fitted circle centers of the hit subpart ("rim center") as point features.
    private static void AddCircleCenterCandidates(Camera camera, float2 mouseViewport, Vehicle? hitVehicle,
        ref readonly PartHit hit, MeshFeatureCache.MeshFeatures features, ref FeatureCandidate best)
    {
        if (features.Circles.Length == 0)
            return;
        double4x4 matrixAsmb2Ego = hit.SubPart!.MatrixAsmb2Ego(in hit.Matrix);
        for (int i = 0; i < features.Circles.Length; i++)
        {
            MeshFeatureCache.CircleFeature circle = features.Circles[i];
            float2 s = camera.EgoToScreen(circle.Center.Transform(matrixAsmb2Ego));
            if (float.IsNaN(s.X) || float.IsNaN(s.Y))
                continue;
            float d = float2.Distance(s, mouseViewport);
            if (d < best.ScreenDist)
            {
                best.Vehicle = hitVehicle;
                best.Part = hit.SubPart;
                best.Position = circle.Center;
                best.IsVehicleAsmb = false;
                best.Normal = circle.Normal;
                best.Label = hit.FullPart!.DisplayName + " rim center";
                best.ScreenDist = d;
            }
        }
    }

    // The CAD "symmetric point": when the previous pending point sits on the hit
    // full part, its reflection across the part's axis is offered as a feature,
    // giving an exact antipodal second point (tank diameter on a box, strut to
    // strut) without hunting for it.
    private static void AddMirrorCandidate(Camera camera, float2 mouseViewport, Vehicle? hitVehicle,
        ref readonly PartHit hit, ref FeatureCandidate best)
    {
        if (MeasureState.Pending.Count == 0)
            return;
        Anchor previous = MeasureState.Pending[^1];
        Part fullPart = hit.FullPart!;
        if (previous.Part == null || !ReferenceEquals(previous.Part.FullPart, fullPart))
            return;

        double4x4 matrixFullPart2Ego = fullPart.MatrixAsmb2Ego(in hit.Matrix);
        double4x4.Invert(matrixFullPart2Ego, out double4x4 ego2FullPart);
        double3 previousLocal = (previous.ResolveEcl() - camera.PositionEcl).Transform(ego2FullPart);

        (double3 axisPoint, double3 axisDirection) = GetPartAxis(fullPart);
        double3 offset = previousLocal - axisPoint;
        double3 along = axisDirection * double3.Dot(offset, axisDirection);
        double3 mirroredLocal = axisPoint + along - (offset - along);

        float2 s = camera.EgoToScreen(mirroredLocal.Transform(matrixFullPart2Ego));
        if (float.IsNaN(s.X) || float.IsNaN(s.Y))
            return;
        float d = float2.Distance(s, mouseViewport);
        if (d < best.ScreenDist)
        {
            best.Vehicle = hitVehicle;
            best.Part = fullPart;
            best.Position = mirroredLocal;
            best.IsVehicleAsmb = false;
            best.Normal = null;
            best.Label = fullPart.DisplayName + " opposite";
            best.ScreenDist = d;
        }
    }

    // The part's symmetry axis in its own frame: the line through its two
    // most-opposing stack connectors (those without surface-attach flags), else
    // the part-frame X axis through the bounding-box center. Stack connectors
    // sit on part-frame X (CoreFuelTankAAssets.xml; the editor treats
    // double3(1,0,0) through a connector rotation as the facing axis in
    // VehicleEditor.HandleConnectorConnections).
    private static (double3 Point, double3 Direction) GetPartAxis(Part part)
    {
        Part.Connector? bestA = null;
        Part.Connector? bestB = null;
        // A connector pair only counts as the axis when facing within about 25
        // degrees of anti-parallel; anything looser falls through to the
        // bounding-box fallback below.
        const double minOpposingConnectorDot = -0.9;
        double bestDot = minOpposingConnectorDot;
        for (int i = 0; i < part.Connectors.Count; i++)
        {
            Part.Connector a = part.Connectors[i];
            if ((a.Flags & (Part.Connector.Flag.ToSurface | Part.Connector.Flag.FromSurface)) != 0)
                continue;
            double3 facingA = new double3(1.0, 0.0, 0.0).Transform(a.Asmb2ParentAsmb);
            for (int j = i + 1; j < part.Connectors.Count; j++)
            {
                Part.Connector b = part.Connectors[j];
                if ((b.Flags & (Part.Connector.Flag.ToSurface | Part.Connector.Flag.FromSurface)) != 0)
                    continue;
                double dot = double3.Dot(facingA, new double3(1.0, 0.0, 0.0).Transform(b.Asmb2ParentAsmb));
                if (dot < bestDot)
                {
                    bestDot = dot;
                    bestA = a;
                    bestB = b;
                }
            }
        }
        if (bestA != null && bestB != null)
        {
            double3 direction = (bestA.PositionParentAsmb - bestB.PositionParentAsmb).NormalizeOrZero();
            if (direction.X != 0.0 || direction.Y != 0.0 || direction.Z != 0.0)
                return (bestB.PositionParentAsmb, direction);
        }
        (double3 min, double3 max) = part.BoundingBoxPartAsmb;
        double3 center = min.X <= max.X ? (min + max) * 0.5 : double3.Zero;
        return (center, new double3(1.0, 0.0, 0.0));
    }

    // The stock per-part mesh raycast (Part.RayCastEgo) over one part span,
    // keeping the globally nearest hit in `hit`.
    private static void RaycastPartSpan(ReadOnlySpan<Part> parts, ref readonly double4x4 matrixVehicleAsmb2Ego, Ray ray, ref PartHit hit)
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MapPicker.RaycastPartSpan");
#endif
        for (int i = 0; i < parts.Length; i++)
            RaycastPart(parts[i], in matrixVehicleAsmb2Ego, ray, ref hit);
    }

    private static void RaycastPart(Part part, ref readonly double4x4 matrixVehicleAsmb2Ego, Ray ray, ref PartHit hit)
    {
        if (part.RayCastEgo(in matrixVehicleAsmb2Ego, ray, out double minDistance, out _,
                out double3 nearLocal, out double3 nearNormal, out _, out _, out Part? closestSubPart, out _)
            && minDistance > 0.0 && minDistance < hit.Distance && closestSubPart != null)
        {
            hit.FullPart = part;
            hit.SubPart = closestSubPart;
            hit.LocalPos = nearLocal;
            hit.NormalLocal = nearNormal;
            hit.Distance = minDistance;
            hit.Matrix = matrixVehicleAsmb2Ego;
        }
    }

    // Screen-space scan over attach nodes and part bounding-box centers, the same
    // proximity idea the editor's connector snap uses. Deliberately not ray-gated:
    // a node on the hull silhouette should snap even when the cursor is just off
    // the mesh. Occlusion is ignored; features are sparse and the preview shows
    // which one wins before the click. The caller owns FeatureCandidate.Vehicle
    // (null in the editor); Label is the owner-free part label the anchor
    // factories prefix themselves.
    private static void ScanPartFeatures(Camera camera, float2 mouseViewport, ReadOnlySpan<Part> parts,
        ref readonly double4x4 matrixVehicleAsmb2Ego, ref FeatureCandidate best)
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MapPicker.ScanPartFeatures");
#endif
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
                        best.Label = part.DisplayName + " center";
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
                    best.Label = part.DisplayName + " node '" + connector.Id + "'";
                    best.ScreenDist = d;
                }
            }
        }
    }

    // Nearest mesh vertex or feature-edge midpoint of the hit subpart within the
    // vertex snap radius, scanning the cache's welded vertex set (each position
    // once, unlike the index-unrolled PositionCompare).
    private static bool TryPickVertexOrMidpoint(Camera camera, float2 mouseViewport, Part subPart,
        ref readonly double4x4 matrixVehicleAsmb2Ego, MeshFeatureCache.MeshFeatures features, out double3 pointLocal, out bool isMidpoint)
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MapPicker.TryPickVertexOrMidpoint");
#endif
        pointLocal = default;
        isMidpoint = false;
        double3[] vertices = features.Vertices;
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
                pointLocal = vertices[i];
                found = true;
            }
        }
        MeshFeatureCache.EdgeSegment[] edges = features.Edges;
        for (int i = 0; i < edges.Length; i++)
        {
            double3 mid = edges[i].Mid;
            float2 s = camera.EgoToScreen(mid.Transform(matrixAsmb2Ego));
            if (float.IsNaN(s.X) || float.IsNaN(s.Y))
                continue;
            float d = float2.Distance(s, mouseViewport);
            if (d < bestDist)
            {
                bestDist = d;
                pointLocal = mid;
                isMidpoint = true;
                found = true;
            }
        }
        return found;
    }

    // Closest point on a feature edge of the hit subpart: the cursor-to-segment
    // distance is taken in screen space and the winning 2D parameter is mapped
    // back onto the 3D segment, so the point slides along a tank rim between its
    // vertices.
    private static bool TryPickEdgePoint(Camera camera, float2 mouseViewport, Part subPart,
        ref readonly double4x4 matrixVehicleAsmb2Ego, MeshFeatureCache.MeshFeatures features, out double3 edgePointLocal)
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MapPicker.TryPickEdgePoint");
#endif
        edgePointLocal = default;
        MeshFeatureCache.EdgeSegment[] edges = features.Edges;
        if (edges.Length == 0)
            return false;
        double4x4 matrixAsmb2Ego = subPart.MatrixAsmb2Ego(in matrixVehicleAsmb2Ego);
        float bestDist = PartEdgeSnapRadiusPx;
        bool found = false;
        for (int i = 0; i < edges.Length; i++)
        {
            float2 a = camera.EgoToScreen(edges[i].A.Transform(matrixAsmb2Ego));
            float2 b = camera.EgoToScreen(edges[i].B.Transform(matrixAsmb2Ego));
            if (float.IsNaN(a.X) || float.IsNaN(a.Y) || float.IsNaN(b.X) || float.IsNaN(b.Y))
                continue;
            float2 ab = b - a;
            float lengthSq = ab.X * ab.X + ab.Y * ab.Y;
            float t = 0f;
            if (lengthSq > 1e-6f)
            {
                float2 toMouse = mouseViewport - a;
                t = Math.Clamp((toMouse.X * ab.X + toMouse.Y * ab.Y) / lengthSq, 0f, 1f);
            }
            var closest = new float2(a.X + ab.X * t, a.Y + ab.Y * t);
            float d = float2.Distance(closest, mouseViewport);
            if (d < bestDist)
            {
                bestDist = d;
                edgePointLocal = edges[i].A + (edges[i].B - edges[i].A) * t;
                found = true;
            }
        }
        return found;
    }

    // Unified body snap: discs and dots. Stock's own arbitration (CursorTarget, fed
    // by Astronomical.UpdateMouseHover) is not the pick here: it resolves one winner
    // across bodies, burn gizmos and orbit points, it knows no limb and no tolerance
    // band outside the sphere, and it runs once per frame for the hovered viewport at
    // the live cursor, so a placement click (a GLFW callback ahead of that update)
    // would land on the previous frame's target. One scan computes the projected
    // center and disc radius for every body instead.
    private static Anchor? PickBody(IViewport viewport, float2 mouseViewport)
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
        // user can actually see qualify: those the game marked as cursor-hoverable on
        // its most recent UI pass (Astronomical.CursorHoverEligible, the gate stock
        // feeds its own hover test from; current frame on the preview path, previous
        // frame on the click path since input callbacks run before the UI draw) and
        // stars, which carry no orbit and so are never marked, yet are always relevant.
        // The flag also covers bodies whose outline box was suppressed for being too
        // large on screen; those stay safe because IOrbiter.OnDrawUi sets it under the
        // same predicate that draws the name label, and a body big enough to lose its
        // box is wider than CenterSnapRadiusPx, so the cursor is inside its visible
        // disc whenever it wins here. Without this gate, every comet and asteroid in
        // the system is a snap target even when nothing marks it on screen, and free
        // placement becomes nearly impossible in a dense system.
        // Both writes sit inside IOrbiter.OnDrawUi's ShowCelestialNames() branch, so
        // with names off this tier holds stars only, which is what the view marks.
        Astronomical? nearest = null;
        float nearestDist = CenterSnapRadiusPx;

        foreach (Astronomical astronomical in system.All.AsSpan())
        {
            float2 s = camera.EclToScreen(astronomical.GetPositionEcl());
            if (float.IsNaN(s.X) || float.IsNaN(s.Y))
                continue;
            float d = float2.Distance(s, mouseViewport);
            bool visibleMarker = astronomical is StellarBody || astronomical.CursorHoverEligible;
            if (visibleMarker && d < nearestDist)
            {
                nearestDist = d;
                nearest = astronomical;
            }

            // Only bodies with a real surface are disc and limb targets. A vehicle's
            // MeanRadius is its bounding-sphere radius, so without this a click in
            // the band around that invisible sphere would resolve to a "surface"
            // point touching no geometry; vehicles stay reachable through the centre
            // snap above and through part picking.
            if (astronomical is Vehicle)
                continue;

            double distance = (astronomical.GetPositionEcl() - camera.PositionEcl).Length();
            if (!(distance > astronomical.MeanRadius))
                continue;
            float radiusPx = (float)(camera.GetObjectDiameterPixels(astronomical.MeanRadius * 2.0, distance) * 0.5);
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
        double3 origin = camera.EgoToEcl(ray.Origin);
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
    private static Anchor? PickOrbitPoint(IViewport viewport, float2 mouseViewport)
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MapPicker.PickOrbitPoint");
#endif
        CelestialSystem? system = Universe.CurrentSystem;
        if (system == null)
            return null;
        Camera camera = viewport.GetCamera();

        var best = default(OrbitCandidate);

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
                    // Vehicle patches use the wider UI-or-lines gate, celestials below the
                    // narrower lines-only one. Stock draws the same distinction in
                    // SetNearestOrbitPoint; matching it keeps the candidate sets identical.
                    if (!Astronomical.ShouldDrawUiOrLines(patch.PrimaryBody, viewport, patch.Orbit))
                        continue;
                    if (patch.Orbit.GetNearestPosition(viewport, mouseViewport, patch, out CelestialPosition? pos, spliceVehicleFromNow: false))
                        TryAccept(pos, vehicle, camera, viewport, mouseViewport, ref best);
                }
                CelestialPosition? burnPos = null;
                if (vehicle.FlightComputer.BurnPlan.GetNearestOrbitPoint(viewport, mouseViewport, ref burnPos))
                    TryAccept(burnPos, vehicle, camera, viewport, mouseViewport, ref best);
            }
            else if (astronomical is Celestial celestial)
            {
                if (!celestial.ShowOrbit && !celestial.TargetOfControlledVehicle)
                    continue;
                if (!Astronomical.ShouldDrawLines(astronomical, viewport, celestial.Orbit))
                    continue;
                if (celestial.Orbit.GetNearestPosition(viewport, mouseViewport, null, out CelestialPosition? pos, spliceVehicleFromNow: false))
                    TryAccept(pos, celestial, camera, viewport, mouseViewport, ref best);
            }
        }

        if (!best.Position.HasValue || best.Owner == null)
            return null;
        CelestialPosition cp = best.Position.Value;
        return Anchor.OnOrbit(cp.Parent, cp.Point.PositionCce, best.Owner.Id);
    }

    // The best orbit-line candidate so far. The owner travels with the point because
    // stock's IsBetterThan breaks a near-tie in screen distance by camera depth and,
    // at equal depth, by the owner's radius (HoverRanking), so comparing two
    // candidates needs both their orbiters.
    private struct OrbitCandidate
    {
        public CelestialPosition? Position;
        public Astronomical? Owner;
    }

    // Keep the candidate if it is on screen near the cursor and beats the best so
    // far. Shared by the three orbit-candidate sources (flight-plan patches, the burn
    // plan, celestial orbits), all of which produce a nullable CelestialPosition.
    private static void TryAccept(CelestialPosition? candidate, Astronomical owner, Camera camera, IViewport viewport,
        float2 mouseViewport, ref OrbitCandidate best)
    {
        if (candidate.HasValue
            && IsOnScreenNearCursor(candidate.Value, camera, viewport, mouseViewport)
            && candidate.Value.IsBetterThan(camera, mouseViewport, best.Position,
                owner.MeanRadius, best.Owner?.MeanRadius ?? 0.0))
        {
            best.Position = candidate;
            best.Owner = owner;
        }
    }

    // Re-validate an orbit-point candidate on screen. Stock GetNearestPoint has a
    // latent NaN hole on closed orbits: a candidate behind the camera projects to
    // NaN (the screen projection drops behind-camera points), its NDC distance check
    // evaluates to (NaN > threshold) == false, and the bogus point is ACCEPTED, e.g.
    // a click near Earth grabbing a point on the Uranus orbit plane behind the
    // camera. Such a candidate also distorts IsBetterThan, which projects with
    // ignoreBehind: false and can score the mirrored position deceptively close,
    // shadowing real candidates. The hyperbolic branch guards its own samples.
    private static bool IsOnScreenNearCursor(CelestialPosition candidate, Camera camera, IViewport viewport, float2 mouseViewport)
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
    // Hale-Bopp at 89 deg inclination has huge Z). Camera.UpView = (0,1,0) is the
    // camera-up convention, NOT the ecliptic normal.
    public static bool TryGetFreePlane(IViewport viewport, bool eclipticPlane, out double3 planePointEcl, out double3 normalEcl, out Astronomical? refBody)
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
                : viewport.GetCamera().GetForwardEcl();
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
            : viewport.GetCamera().GetForwardEcl();
        return true;
    }

    private static Anchor? PickFreePoint(IViewport viewport, float2 mouseViewport, bool eclipticPlane)
    {
        if (!TryGetFreePlane(viewport, eclipticPlane, out double3 planePoint, out double3 normal, out Astronomical? refBody))
            return null;
        Camera camera = viewport.GetCamera();

        // Ego axes are ECL axes (Camera.EgoToEcl is a pure translation), so the ego
        // ray direction is an ECL direction and lifting its origin is a translation.
        // Taking the origin from the ray rather than assuming the camera keeps this
        // correct under the editor's orthographic projection, where every cursor
        // position gets the same direction and its own near-plane origin.
        Ray ray = camera.ScreenToEgoRay(mouseViewport);
        double3 originEcl = camera.EgoToEcl(ray.Origin);
        if (!MathEx.RayPlaneIntersection(originEcl, ray.Direction, planePoint, normal, out double t) || !(t > 0.0))
            return null;
        double3 pointEcl = originEcl + ray.Direction * t;
        if (Program.Editor != null)
            return Anchor.EditorFree(pointEcl - Program.Editor.EditingSpace.PositionEcl);
        if (refBody == null)
            return null;
        return Anchor.Free(refBody, refBody.GetPositionCceFromEcl(pointEcl));
    }
}
