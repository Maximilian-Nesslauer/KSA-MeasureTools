using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using MeasureTools.Core;

namespace MeasureTools.Features.Measure;

// Draws the measurements, the in-progress placement preview, the snap highlight and
// the free-placement construction plane onto the active view, on the background draw
// list, the same list the stock body-label and orbit overlays use. Everything is
// wrapped so a camera or projection change can never unwind into the render path.
internal static class MeasureOverlay
{
    private static readonly byte4 MeasureColor = new byte4(120, 220, 160, 235);
    // List-hover highlight: brighter and thicker so the row-to-line mapping is obvious.
    private static readonly byte4 HighlightColor = new byte4(215, 255, 235, 255);
    private static readonly byte4 PendingColor = new byte4(255, 220, 110, 245);
    private static readonly byte4 PreviewColor = new byte4(255, 220, 110, 160);
    private static readonly byte4 PreviewFaint = new byte4(255, 220, 110, 80);
    private static readonly byte4 PlaneColor = new byte4(150, 170, 200, 70);
    // Snappable-feature markers on the hovered part: green like settled
    // measurements so they stand apart from the yellow pending/preview set, and
    // opaque enough to read against a lit hull.
    private static readonly byte4 FeatureDotColor = new byte4(120, 220, 160, 200);
    private static readonly byte4 LabelColor = new byte4(236, 234, 222, 255);
    private static readonly byte4 LabelPlate = new byte4(8, 12, 16, 175);

    private const float ArcPx = 36f;
    private const int PlaneSegments = 64;
    private const int SurfaceArcSegments = 48;

    // The hover preview re-picks only every Nth frame: the orbit scan in
    // MapPicker.PickOrbitPoint costs ~1.6 ms per call on the main thread (measured
    // on KSA 2026.6.3.4568), and a ~50 ms stale preview is imperceptible. The
    // placement click always picks fresh, so accuracy is unaffected.
    private const int PreviewPickIntervalFrames = 3;
    // Starts at the interval so the very first armed frame picks immediately.
    private static int _previewFramesSincePick = PreviewPickIntervalFrames;
    private static Anchor? _previewCache;
    // Circle mode previews an anchor pair: the ring point in _previewCache, the
    // fitted center here. Null in every other mode.
    private static Anchor? _previewCacheSecondary;
    private static int _previewStateVersion = -1;
    private static bool _previewEclipticFree;

    // Must not touch ImGui (called from [StarMapUnload]).
    public static void Reset()
    {
        _previewFramesSincePick = PreviewPickIntervalFrames;
        _previewCache = null;
        _previewCacheSecondary = null;
        _previewStateVersion = -1;
        _previewEclipticFree = false;
    }

    public static void Draw(Viewport viewport)
    {
        try
        {
            if (!MeasureWindow.IsOpen)
                return;
            if (!MeasureState.IsSupportedViewMode(viewport.Mode))
                return;
            if (Universe.CurrentSystem == null)
                return;

            Camera camera = viewport.GetCamera();
            float2 vpPos = viewport.Position;
            ImDrawListPtr dl = viewport.Index == 0 ? ImGui.GetBackgroundDrawList() : ImGui.GetWindowDrawList();

            for (int i = 0; i < MeasureState.Measurements.Count; i++)
                DrawMeasurement(dl, camera, vpPos, MeasureState.Measurements[i], i == MeasureState.HighlightIndex);

            DrawPlacementPreview(dl, camera, viewport, vpPos);
        }
        catch (Exception ex)
        {
            // Spam control for this per-frame path: the first exception of each type
            // logs a full stack via {ex}, then stays quiet. Two different sites that
            // throw the same type share one log line, which is the accepted tradeoff.
            LogHelper.ErrorOnce("overlay-" + ex.GetType().Name, $"[MeasureTools] Overlay draw failed: {ex}");
        }
    }

    private static void DrawMeasurement(ImDrawListPtr dl, Camera camera, float2 vpPos, Measurement m, bool highlighted)
    {
        byte4 color = highlighted ? HighlightColor : MeasureColor;
        float thickness = highlighted ? 5.25f : 3f;
        // Each anchor is resolved once into a local; ResolveEcl does a matrix or trig
        // transform, and the value is reused for both the screen point and the metric.
        if (m.Mode == MeasureMode.Surface)
        {
            DrawSurfaceMeasurement(dl, camera, vpPos, m, color, thickness);
        }
        else if (m.Mode == MeasureMode.Circle)
        {
            DrawCircleMeasurement(dl, camera, vpPos, m, color, thickness);
        }
        else if (m.Mode == MeasureMode.FaceAngle)
        {
            DrawFaceAngleMeasurement(dl, camera, vpPos, m, color, thickness);
        }
        else if (m.Mode == MeasureMode.Ruler)
        {
            double3 aEcl = m.Anchors[0].ResolveEcl();
            double3 bEcl = m.Anchors[1].ResolveEcl();
            float2 a = vpPos + camera.EclToScreen(aEcl);
            float2 b = vpPos + camera.EclToScreen(bEcl);
            if (!Valid(a) || !Valid(b))
                return;
            dl.AddLine(in a, in b, color, thickness);
            Dot(dl, a, color);
            Dot(dl, b, color);
            float2 labelPos = SegmentLabelPos(a, b);
            Label(dl, labelPos, FormatDistance((aEcl - bEcl).Length()));
            // Same-vehicle part measurements get the CAD-style component line:
            // along the stack axis and perpendicular to it.
            if (m.TryGetAxialRadialMeters(out double axial, out double radial))
                Label(dl, new float2(labelPos.X, labelPos.Y + LabelStackStep()),
                    "ax " + FormatDistance(axial) + "  rad " + FormatDistance(radial));
        }
        else if (m.Mode == MeasureMode.Angle)
        {
            double3 armAEcl = m.Anchors[0].ResolveEcl();
            double3 apexEcl = m.Anchors[1].ResolveEcl();
            double3 armBEcl = m.Anchors[2].ResolveEcl();
            float2 a = vpPos + camera.EclToScreen(armAEcl);
            float2 apex = vpPos + camera.EclToScreen(apexEcl);
            float2 b = vpPos + camera.EclToScreen(armBEcl);
            if (!Valid(a) || !Valid(apex) || !Valid(b))
                return;
            dl.AddLine(in apex, in a, color, thickness);
            dl.AddLine(in apex, in b, color, thickness);
            Dot(dl, a, color);
            Dot(dl, apex, color);
            Dot(dl, b, color);
            DrawAngleArcAndLabel(dl, apex, a, b, Measurement.AngleBetween(apexEcl, armAEcl, armBEcl), color);
            // Both arms carry their length, like ruler segments.
            Label(dl, SegmentLabelPos(apex, a), FormatDistance((armAEcl - apexEcl).Length()));
            Label(dl, SegmentLabelPos(apex, b), FormatDistance((armBEcl - apexEcl).Length()));
        }
    }

    // Circle measurement: Anchors[0] is the fitted center (carrying the plane
    // normal), Anchors[1] a rim point; the ring is drawn in 3D so it hugs the
    // part at any camera angle.
    private static void DrawCircleMeasurement(ImDrawListPtr dl, Camera camera, float2 vpPos, Measurement m, byte4 color, float thickness)
    {
        double3 centerEcl = m.Anchors[0].ResolveEcl();
        double3 rimEcl = m.Anchors[1].ResolveEcl();
        double3? normalEcl = m.Anchors[0].ResolveNormalEcl();
        float2 center = vpPos + camera.EclToScreen(centerEcl);
        if (Valid(center))
            Dot(dl, center, color);
        if (normalEcl != null)
            DrawWorldCircle(dl, camera, vpPos, centerEcl, rimEcl - centerEcl, normalEcl.Value, color, thickness);
        float2 rim = vpPos + camera.EclToScreen(rimEcl);
        float2 labelAnchor = Valid(rim) ? rim : center;
        if (Valid(labelAnchor))
            Label(dl, new float2(labelAnchor.X + 12f, labelAnchor.Y - 16f), "d " + FormatDistance(m.CircleDiameterMeters()));
    }

    // FaceAngle measurement: dots on both sampled points, their surface normals
    // as arrows, a thin connector, and the live angle between the normals.
    private static void DrawFaceAngleMeasurement(ImDrawListPtr dl, Camera camera, float2 vpPos, Measurement m, byte4 color, float thickness)
    {
        double3 aEcl = m.Anchors[0].ResolveEcl();
        double3 bEcl = m.Anchors[1].ResolveEcl();
        float2 a = vpPos + camera.EclToScreen(aEcl);
        float2 b = vpPos + camera.EclToScreen(bEcl);
        if (Valid(a))
            Dot(dl, a, color);
        if (Valid(b))
            Dot(dl, b, color);
        DrawNormalArrow(dl, camera, vpPos, aEcl, m.Anchors[0].ResolveNormalEcl(), color, thickness);
        DrawNormalArrow(dl, camera, vpPos, bEcl, m.Anchors[1].ResolveNormalEcl(), color, thickness);
        if (!Valid(a) || !Valid(b))
            return;
        dl.AddLine(in a, in b, color, 1f);
        double angle = m.FaceAngleRadians();
        Label(dl, SegmentLabelPos(a, b),
            double.IsNaN(angle) ? "undefined" : RadianReference.FromRadians(angle).ToStringDegrees());
    }

    // A 3D circle from its center, a radius vector in the plane, and the plane
    // normal, projected as a NaN-gapped polyline like the construction plane.
    private static void DrawWorldCircle(ImDrawListPtr dl, Camera camera, float2 vpPos, double3 centerEcl, double3 radiusVecEcl, double3 normalEcl, byte4 color, float thickness)
    {
        double3 u = radiusVecEcl;
        double3 w = double3.Cross(normalEcl.NormalizeOrZero(), u);
        if (w.LengthSquared() < 1e-12)
            return;
        const int segments = 48;
        float2 prev = default;
        bool hasPrev = false;
        for (int i = 0; i <= segments; i++)
        {
            double angle = Math.PI * 2.0 * i / segments;
            double3 p = centerEcl + u * Math.Cos(angle) + w * Math.Sin(angle);
            float2 s = vpPos + camera.EclToScreen(p);
            if (Valid(s))
            {
                if (hasPrev)
                    dl.AddLine(in prev, in s, color, thickness);
                prev = s;
                hasPrev = true;
            }
            else
            {
                hasPrev = false;
            }
        }
    }

    // A surface normal as a short arrow whose screen length is roughly constant
    // (about 40 px), so it reads the same at any zoom.
    private static void DrawNormalArrow(ImDrawListPtr dl, Camera camera, float2 vpPos, double3 originEcl, double3? normalEcl, byte4 color, float thickness)
    {
        if (normalEcl == null)
            return;
        double distance = (originEcl - camera.PositionEcl).Length();
        if (!(distance > 0.0))
            return;
        double pxPerMeter = camera.GetObjectDiameterPixelsAsDouble(1.0, distance);
        if (!(pxPerMeter > 1e-9))
            return;
        double3 tipEcl = originEcl + normalEcl.Value * (40.0 / pxPerMeter);
        float2 a = vpPos + camera.EclToScreen(originEcl);
        float2 b = vpPos + camera.EclToScreen(tipEcl);
        if (!Valid(a) || !Valid(b))
            return;
        dl.AddLine(in a, in b, color, thickness);
        dl.AddCircleFilled(in b, 3f, color);
    }

    // Surface measurement: the great-circle arc over the body, pins at both ends,
    // the great-circle distance as the headline and chord plus initial bearing as
    // a second label line.
    private static void DrawSurfaceMeasurement(ImDrawListPtr dl, Camera camera, float2 vpPos, Measurement m, byte4 color, float thickness)
    {
        if (m.Anchors[0].Body is not Celestial body)
            return;
        double3 centerEcl = body.GetPositionEcl();
        double3 aEcl = m.Anchors[0].ResolveEcl();
        double3 bEcl = m.Anchors[1].ResolveEcl();
        DrawGreatCircleArc(dl, camera, vpPos, centerEcl, aEcl, bEcl, color, thickness);

        float2 a = vpPos + camera.EclToScreen(aEcl);
        float2 b = vpPos + camera.EclToScreen(bEcl);
        if (Valid(a))
            Dot(dl, a, color);
        if (Valid(b))
            Dot(dl, b, color);

        float2 labelAnchor = GreatCircleMidScreen(camera, vpPos, centerEcl, aEcl, bEcl);
        if (!Valid(labelAnchor))
            return;
        var labelPos = new float2(labelAnchor.X + 10f, labelAnchor.Y - 32f);
        Label(dl, labelPos, FormatDistance(m.SurfaceDistanceMeters()));
        string detail = "chord " + FormatDistance((aEcl - bEcl).Length())
            + "  brg " + m.BearingDegrees().ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " deg";
        Label(dl, new float2(labelPos.X, labelPos.Y + LabelStackStep()), detail);
    }

    // The great-circle arc between two surface points, sampled along the sphere and
    // occlusion-culled: samples on the far hemisphere (facing away from the camera)
    // break the polyline instead of drawing through the planet disc.
    // Shared great-circle setup for the two surface-draw helpers: the radial unit
    // vector at the first point, the rotation axis, the separation angle and the two
    // radii. Returns false when either point sits at the body center (no radial
    // direction); the callers handle the zero-axis (coincident/antipodal) case
    // themselves, since they want different fallbacks.
    private readonly struct GreatCircleBasis
    {
        public readonly double3 Ua;
        public readonly double3 Axis;
        public readonly double Angle;
        public readonly double Ra;
        public readonly double Rb;

        public GreatCircleBasis(double3 ua, double3 axis, double angle, double ra, double rb)
        {
            Ua = ua;
            Axis = axis;
            Angle = angle;
            Ra = ra;
            Rb = rb;
        }

        public bool AxisIsZero => Axis.X == 0.0 && Axis.Y == 0.0 && Axis.Z == 0.0;
    }

    private static bool TryGreatCircleBasis(double3 centerEcl, double3 aEcl, double3 bEcl, out GreatCircleBasis basis)
    {
        basis = default;
        double3 da = aEcl - centerEcl;
        double3 db = bEcl - centerEcl;
        double ra = da.Length();
        double rb = db.Length();
        if (!(ra > 0.0) || !(rb > 0.0))
            return false;
        double3 ua = da * (1.0 / ra);
        double3 ub = db * (1.0 / rb);
        double angle = Math.Acos(Math.Clamp(double3.Dot(ua, ub), -1.0, 1.0));
        double3 axis = double3.Cross(ua, ub).NormalizeOrZero();
        basis = new GreatCircleBasis(ua, axis, angle, ra, rb);
        return true;
    }

    private static void DrawGreatCircleArc(ImDrawListPtr dl, Camera camera, float2 vpPos, double3 centerEcl, double3 aEcl, double3 bEcl, byte4 color, float thickness)
    {
        if (!TryGreatCircleBasis(centerEcl, aEcl, bEcl, out GreatCircleBasis basis))
            return;
        double3 ua = basis.Ua;
        double3 axis = basis.Axis;
        double angle = basis.Angle;
        double ra = basis.Ra;
        double rb = basis.Rb;
        if (angle < 1e-9 || basis.AxisIsZero)
        {
            // Coincident or antipodal points: no unique great circle, draw nothing
            // (the endpoint dots and the label still render).
            return;
        }

        float2 prev = default;
        bool hasPrev = false;
        for (int i = 0; i <= SurfaceArcSegments; i++)
        {
            double t = (double)i / SurfaceArcSegments;
            double3 dir = Rotate(ua, axis, angle * t);
            double radius = ra + (rb - ra) * t;
            double3 pointEcl = centerEcl + dir * radius;
            // Visible while the surface normal faces the camera.
            bool facing = double3.Dot(dir, camera.PositionEcl - pointEcl) > 0.0;
            float2 s = vpPos + camera.EclToScreen(pointEcl);
            if (facing && Valid(s))
            {
                if (hasPrev)
                    dl.AddLine(in prev, in s, color, thickness);
                prev = s;
                hasPrev = true;
            }
            else
            {
                hasPrev = false;
            }
        }
    }

    // Screen position of the arc's halfway point, for label placement.
    private static float2 GreatCircleMidScreen(Camera camera, float2 vpPos, double3 centerEcl, double3 aEcl, double3 bEcl)
    {
        if (!TryGreatCircleBasis(centerEcl, aEcl, bEcl, out GreatCircleBasis basis))
            return new float2(float.NaN, float.NaN);
        // Coincident or antipodal points: fall back to the first endpoint so the label
        // still has an anchor.
        if (basis.AxisIsZero)
            return vpPos + camera.EclToScreen(aEcl);
        double3 mid = Rotate(basis.Ua, basis.Axis, basis.Angle * 0.5);
        return vpPos + camera.EclToScreen(centerEcl + mid * ((basis.Ra + basis.Rb) * 0.5));
    }

    // Rodrigues' rotation of a vector about a unit axis by the given angle.
    private static double3 Rotate(double3 v, double3 axis, double angle)
    {
        double c = Math.Cos(angle);
        double s = Math.Sin(angle);
        return v * c + double3.Cross(axis, v) * s + axis * (double3.Dot(axis, v) * (1.0 - c));
    }

    // The hover preview while armed: the snap highlight under the cursor, the
    // rubber-band line(s) from the pending points with a live value, and the
    // construction plane when the cursor would place a free point.
    private static void DrawPlacementPreview(ImDrawListPtr dl, Camera camera, Viewport viewport, float2 vpPos)
    {
        var io = ImGui.GetIO();
        if (!MeasureState.IsArmed || io.WantCaptureMouse)
        {
            // Not previewing (tool disarmed or cursor over UI): drop the cache so the
            // first frame back over the view picks fresh.
            Reset();
            return;
        }

        float2 mouseViewport = io.MousePos - vpPos;
        // Ctrl previews (and places) a free point on the ecliptic plane even where
        // snapping would win; a modifier change re-picks immediately so the preview
        // flips with the key.
        bool eclipticFree = io.KeyCtrl;
        _previewFramesSincePick++;
        if (_previewFramesSincePick >= PreviewPickIntervalFrames
            || _previewStateVersion != MeasureState.StateVersion
            || _previewEclipticFree != eclipticFree)
        {
            if (MeasureState.Mode == MeasureMode.Circle)
            {
                MapPicker.PickCircle(viewport, mouseViewport, out Anchor? circleCenter, out Anchor? circleRim);
                _previewCache = circleRim;
                _previewCacheSecondary = circleCenter;
            }
            else
            {
                _previewCache = MapPicker.Pick(viewport, mouseViewport, eclipticFree);
                _previewCacheSecondary = null;
            }
            _previewFramesSincePick = 0;
            _previewStateVersion = MeasureState.StateVersion;
            _previewEclipticFree = eclipticFree;
        }
        Anchor? preview = _previewCache;

        // Pending points are always shown, even with no resolvable preview.
        var pending = MeasureState.Pending;
        Span<float2> pendingScreen = stackalloc float2[3];
        bool pendingValid = true;
        for (int i = 0; i < pending.Count; i++)
        {
            pendingScreen[i] = Project(camera, vpPos, pending[i]);
            if (Valid(pendingScreen[i]))
                Dot(dl, pendingScreen[i], PendingColor);
            else
                pendingValid = false;
        }

        if (preview == null)
            return;

        float2 cursor = Project(camera, vpPos, preview);
        if (!Valid(cursor))
            return;

        // Circle mode previews the candidate ring with its live diameter; there
        // is no pending flow and no generic snap highlight.
        if (MeasureState.Mode == MeasureMode.Circle)
        {
            Anchor? circleCenter = _previewCacheSecondary;
            if (circleCenter != null)
            {
                double3 centerEcl = circleCenter.ResolveEcl();
                double3 rimEcl = preview.ResolveEcl();
                double3? normalEcl = circleCenter.ResolveNormalEcl();
                if (normalEcl != null)
                    DrawWorldCircle(dl, camera, vpPos, centerEcl, rimEcl - centerEcl, normalEcl.Value, PendingColor, 2.4f);
                dl.AddCircleFilled(in cursor, 3.5f, PreviewColor);
                Label(dl, new float2(cursor.X + 12f, cursor.Y - 16f),
                    "d " + FormatDistance((rimEcl - centerEcl).Length() * 2.0));
            }
            return;
        }

        DrawSnapHighlight(dl, camera, viewport, vpPos, cursor, preview, _previewEclipticFree);

        if (!pendingValid || pending.Count == 0)
            return;

        if (MeasureState.Mode == MeasureMode.Ruler)
        {
            // One pending point: rubber-band line with the live distance.
            float2 a = pendingScreen[0];
            dl.AddLine(in a, in cursor, PendingColor, 2.4f);
            double meters = (pending[0].ResolveEcl() - preview.ResolveEcl()).Length();
            Label(dl, SegmentLabelPos(a, cursor), FormatDistance(meters));
        }
        else if (MeasureState.Mode == MeasureMode.Surface)
        {
            // One pending pin: live great-circle arc with the live distance.
            if (preview.Kind == AnchorKind.SurfacePin
                && pending[0].Body is Celestial body
                && ReferenceEquals(preview.Body, body))
            {
                double3 centerEcl = body.GetPositionEcl();
                DrawGreatCircleArc(dl, camera, vpPos, centerEcl, pending[0].ResolveEcl(), preview.ResolveEcl(), PendingColor, 2.4f);
                double meters = Measurement.GreatCircleMeters(
                    body, pending[0].Latitude, pending[0].Longitude, preview.Latitude, preview.Longitude);
                Label(dl, SegmentLabelPos(pendingScreen[0], cursor), FormatDistance(meters));
            }
        }
        else if (MeasureState.Mode == MeasureMode.FaceAngle)
        {
            // First face sampled, cursor previews the second: both normals as
            // arrows plus the live angle between them.
            float2 a = pendingScreen[0];
            dl.AddLine(in a, in cursor, PendingColor, 1f);
            double3? n0 = pending[0].ResolveNormalEcl();
            double3? n1 = preview.ResolveNormalEcl();
            DrawNormalArrow(dl, camera, vpPos, pending[0].ResolveEcl(), n0, PendingColor, 2.4f);
            DrawNormalArrow(dl, camera, vpPos, preview.ResolveEcl(), n1, PendingColor, 2.4f);
            string text = n0 != null && n1 != null
                ? RadianReference.FromRadians(Measurement.AngleBetweenNormals(n0.Value, n1.Value)).ToStringDegrees()
                : "undefined";
            Label(dl, SegmentLabelPos(a, cursor), text);
        }
        else if (MeasureState.Mode == MeasureMode.Angle && pending.Count == 1)
        {
            // Arm placed, cursor previews the apex: live arm length.
            float2 a = pendingScreen[0];
            dl.AddLine(in cursor, in a, PendingColor, 2.4f);
            double meters = (pending[0].ResolveEcl() - preview.ResolveEcl()).Length();
            Label(dl, SegmentLabelPos(cursor, a), FormatDistance(meters));
        }
        else if (MeasureState.Mode == MeasureMode.Angle)
        {
            // Arm and apex placed, cursor previews the second arm: live angle plus
            // both arm lengths, like the settled protractor rendering.
            float2 a = pendingScreen[0];
            float2 apex = pendingScreen[1];
            dl.AddLine(in apex, in a, PendingColor, 2.4f);
            dl.AddLine(in apex, in cursor, PendingColor, 2.4f);
            double3 apexEcl = pending[1].ResolveEcl();
            double3 armAEcl = pending[0].ResolveEcl();
            double3 armBEcl = preview.ResolveEcl();
            double angle = Measurement.AngleBetween(apexEcl, armAEcl, armBEcl);
            DrawAngleArcAndLabel(dl, apex, a, cursor, angle, PendingColor);
            Label(dl, SegmentLabelPos(apex, a), FormatDistance((armAEcl - apexEcl).Length()));
            Label(dl, SegmentLabelPos(apex, cursor), FormatDistance((armBEcl - apexEcl).Length()));
        }
    }

    private static void DrawSnapHighlight(ImDrawListPtr dl, Camera camera, Viewport viewport, float2 vpPos, float2 cursor, Anchor preview, bool eclipticPlane)
    {
        switch (preview.Kind)
        {
            case AnchorKind.BodyCenter:
                dl.AddCircle(in cursor, 11f, PreviewColor, 24, 2f);
                Label(dl, new float2(cursor.X + 14f, cursor.Y - 16f), preview.Label);
                break;
            case AnchorKind.OrbitPoint:
                dl.AddCircleFilled(in cursor, 5f, PreviewColor);
                dl.AddCircle(in cursor, 8f, PreviewColor, 20, 1.5f);
                Label(dl, new float2(cursor.X + 12f, cursor.Y - 16f), preview.Label);
                break;
            case AnchorKind.SurfaceSnap:
                DrawLimbRing(dl, camera, vpPos, preview);
                dl.AddCircleFilled(in cursor, 5f, PreviewColor);
                Label(dl, new float2(cursor.X + 12f, cursor.Y - 16f), preview.Label);
                break;
            case AnchorKind.SurfacePin:
                dl.AddCircleFilled(in cursor, 4f, PreviewColor);
                dl.AddCircle(in cursor, 8f, PreviewColor, 20, 1.5f);
                Label(dl, new float2(cursor.X + 12f, cursor.Y - 16f), preview.Label);
                break;
            case AnchorKind.PartPoint:
            case AnchorKind.EditorPartPoint:
                DrawPartFeatureDots(dl, camera, vpPos, preview);
                dl.AddCircleFilled(in cursor, 3.5f, PreviewColor);
                // Four segments render as a diamond, distinct from the body circle
                // and the orbit-point ring.
                dl.AddCircle(in cursor, 9f, PreviewColor, 4, 1.5f);
                Label(dl, new float2(cursor.X + 12f, cursor.Y - 16f), preview.Label);
                break;
            default:
                Cross(dl, cursor, 7f, PreviewColor);
                DrawConstructionPlane(dl, camera, viewport, vpPos, cursor, eclipticPlane);
                // Spell out which plane the point will land on, so an unexpected
                // plane mode or reference body is visible before the click.
                string plane = eclipticPlane ? "ecliptic plane" : "camera plane";
                string reference = preview.Kind == AnchorKind.EditorFreePoint
                    ? "editor"
                    : preview.Body?.Id ?? "?";
                Label(dl, new float2(cursor.X + 12f, cursor.Y - 16f), plane + " @ " + reference);
                break;
        }
    }

    // Faint dots on the hovered part's attach nodes and bounding-box center while
    // a part pick is previewed, so the snappable features are visible before the
    // click (the editor shows its connector points the same way during a grab).
    // Skipped when the feature tier is disabled, so the dots never suggest a snap
    // that will not happen.
    private static void DrawPartFeatureDots(ImDrawListPtr dl, Camera camera, float2 vpPos, Anchor preview)
    {
        if (!MeasureState.PartFeatureSnapEnabled)
            return;
        if (preview.Part == null)
            return;
        double4x4 matrixVehicleAsmb2Ego;
        if (preview.Kind == AnchorKind.EditorPartPoint)
        {
            VehicleEditor? editor = Program.Editor;
            if (editor == null)
                return;
            matrixVehicleAsmb2Ego = editor.EditingSpace.GetMatrixAsmb2Ego(camera);
        }
        else if (preview.Body is Vehicle vehicle)
        {
            matrixVehicleAsmb2Ego = vehicle.GetMatrixAsmb2Ego(camera);
        }
        else
        {
            return;
        }
        Part fullPart = preview.Part.FullPart;
        (double3 min, double3 max) = fullPart.BoundingBoxPartAsmb;
        if (min.X <= max.X)
        {
            double3 centerEgo = ((min + max) * 0.5).Transform(fullPart.MatrixAsmb2Ego(in matrixVehicleAsmb2Ego));
            float2 s = vpPos + camera.EgoToScreen(centerEgo);
            if (Valid(s))
                dl.AddCircleFilled(in s, 8f, FeatureDotColor);
        }
        for (int i = 0; i < fullPart.Connectors.Count; i++)
        {
            float2 s = vpPos + camera.EgoToScreen(fullPart.Connectors[i].PositionEgo(in matrixVehicleAsmb2Ego));
            if (Valid(s))
                dl.AddCircle(in s, 12f, FeatureDotColor, 16, 2f);
        }
    }

    // A faint ring along the body's projected disc edge while the limb snap is
    // active, so the snap band the cursor sits in is visible.
    private static void DrawLimbRing(ImDrawListPtr dl, Camera camera, float2 vpPos, Anchor preview)
    {
        Astronomical? body = preview.Body;
        if (body == null)
            return;
        float2 center = vpPos + camera.EclToScreen(body.GetPositionEcl());
        if (!Valid(center))
            return;
        double distance = (body.GetPositionEcl() - camera.PositionEcl).Length();
        if (!(distance > body.MeanRadius))
            return;
        float radiusPx = (float)(camera.GetObjectDiameterPixelsAsDouble(body.MeanRadius * 2.0, distance) * 0.5);
        dl.AddCircle(in center, radiusPx, PreviewFaint, 64, 1f);
    }

    // A faint disc in the construction plane (center at the plane anchor, radius
    // scaled to the view depth) plus a spoke to the previewed point, so the user
    // sees where free points will land and how the plane is tilted.
    private static void DrawConstructionPlane(ImDrawListPtr dl, Camera camera, Viewport viewport, float2 vpPos, float2 cursor, bool eclipticPlane)
    {
        if (!MapPicker.TryGetFreePlane(viewport, eclipticPlane, out double3 planePoint, out double3 normal, out _))
            return;

        double depth = (planePoint - camera.PositionEcl).Length();
        if (!(depth > 0.0))
            return;
        // Spans roughly 40% of the half view height at the plane's distance.
        double radius = 0.4 * depth * Math.Tan(camera.GetFieldOfView() * 0.5);

        // Basis vectors in the plane.
        double3 n = double3.Normalize(normal);
        double3 seed = Math.Abs(double3.Dot(n, Double3Ex.Right)) < 0.9 ? Double3Ex.Right : Double3Ex.Forward;
        double3 u = double3.Cross(n, seed).Normalized();
        double3 w = double3.Cross(n, u).Normalized();

        float2 prev = default;
        bool hasPrev = false;
        for (int i = 0; i <= PlaneSegments; i++)
        {
            double a = Math.PI * 2.0 * i / PlaneSegments;
            double3 p = planePoint + u * (radius * Math.Cos(a)) + w * (radius * Math.Sin(a));
            float2 s = vpPos + camera.EclToScreen(p);
            if (Valid(s))
            {
                if (hasPrev)
                    dl.AddLine(in prev, in s, PlaneColor, 1f);
                prev = s;
                hasPrev = true;
            }
            else
            {
                hasPrev = false;
            }
        }

        float2 center = vpPos + camera.EclToScreen(planePoint);
        if (Valid(center))
        {
            dl.AddCircleFilled(in center, 2.5f, PlaneColor);
            dl.AddLine(in center, in cursor, PlaneColor, 1f);
        }
    }

    private static void DrawAngleArcAndLabel(ImDrawListPtr dl, float2 apex, float2 armA, float2 armB, double angleRadians, byte4 color)
    {
        float2 uA = Unit(armA - apex);
        float2 uB = Unit(armB - apex);
        if (IsZero(uA) || IsZero(uB) || double.IsNaN(angleRadians))
            return;
        ScreenArc(dl, apex, ArcPx, uA, uB, color, 2.4f);

        // Label along the angular bisector of the drawn (projected) arc; the value is
        // the true 3D angle, the projected arc is only a visual cue.
        double a0 = Math.Atan2(uA.Y, uA.X);
        double a1 = Math.Atan2(uB.Y, uB.X);
        double d = a1 - a0;
        while (d > Math.PI) d -= 2.0 * Math.PI;
        while (d < -Math.PI) d += 2.0 * Math.PI;
        double am = a0 + d * 0.5;
        var lp = new float2(apex.X + (float)Math.Cos(am) * (ArcPx + 18f), apex.Y + (float)Math.Sin(am) * (ArcPx + 18f));
        Label(dl, lp, RadianReference.FromRadians(angleRadians).ToStringDegrees());
    }

    // A thin arc between two screen-space unit directions around a center, the short
    // way, as line segments (the binding exposes no path-arc helper).
    private static void ScreenArc(ImDrawListPtr dl, float2 center, float r, float2 uFrom, float2 uTo, byte4 color, float thickness)
    {
        double a0 = Math.Atan2(uFrom.Y, uFrom.X);
        double a1 = Math.Atan2(uTo.Y, uTo.X);
        double delta = a1 - a0;
        while (delta > Math.PI) delta -= 2.0 * Math.PI;
        while (delta < -Math.PI) delta += 2.0 * Math.PI;

        const int segments = 20;
        float2 prev = new float2(center.X + r * (float)Math.Cos(a0), center.Y + r * (float)Math.Sin(a0));
        for (int i = 1; i <= segments; i++)
        {
            double a = a0 + delta * (i / (double)segments);
            float2 cur = new float2(center.X + r * (float)Math.Cos(a), center.Y + r * (float)Math.Sin(a));
            dl.AddLine(in prev, in cur, color, thickness);
            prev = cur;
        }
    }

    private static float2 Project(Camera camera, float2 vpPos, Anchor anchor)
    {
        return vpPos + camera.EclToScreen(anchor.ResolveEcl());
    }

    private static string FormatDistance(double meters)
    {
        Span<char> buffer = stackalloc char[64];
        return new string(DistanceReference.ToNearest(meters, buffer));
    }

    private static void Dot(ImDrawListPtr dl, float2 s, byte4 color)
    {
        dl.AddCircleFilled(in s, 4f, color);
    }

    private static void Cross(ImDrawListPtr dl, float2 s, float r, byte4 color)
    {
        var l1 = new float2(s.X - r, s.Y);
        var r1 = new float2(s.X + r, s.Y);
        var t1 = new float2(s.X, s.Y - r);
        var b1 = new float2(s.X, s.Y + r);
        dl.AddLine(in l1, in r1, color, 1.5f);
        dl.AddLine(in t1, in b1, color, 1.5f);
    }

    // Vertical step between labels stacked at the same anchor: one text line
    // plus the plate padding (2 px top and bottom) and a small gap, so the
    // plates never touch at any font size.
    private static float LabelStackStep()
    {
        return ImGui.GetTextLineHeightWithSpacing() + 6f;
    }

    private static void Label(ImDrawListPtr dl, float2 pos, string text)
    {
        // Background plate so labels stay readable over orbit lines, planet discs
        // and other labels.
        float2 size = ImGui.CalcTextSize(text);
        var pMin = new float2(pos.X - 4f, pos.Y - 2f);
        var pMax = new float2(pos.X + size.X + 4f, pos.Y + size.Y + 2f);
        dl.AddRectFilled(in pMin, in pMax, LabelPlate, 3f);
        dl.AddText(in pos, LabelColor, text);
    }

    // Label position for a segment: at the midpoint, offset perpendicular to the
    // segment so the text clears the line at any slope, biased to the upper side.
    private static float2 SegmentLabelPos(float2 a, float2 b)
    {
        var mid = new float2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
        float2 dir = Unit(b - a);
        if (IsZero(dir))
            return new float2(mid.X + 10f, mid.Y - 20f);
        var perp = new float2(-dir.Y, dir.X);
        if (perp.Y > 0f)
            perp = new float2(-perp.X, -perp.Y);
        // Extra upward shift accounts for the text rendering downward from its anchor.
        return new float2(mid.X + perp.X * 16f, mid.Y + perp.Y * 16f - 8f);
    }

    private static bool Valid(float2 s)
    {
        return !float.IsNaN(s.X) && !float.IsNaN(s.Y) && !float.IsInfinity(s.X) && !float.IsInfinity(s.Y);
    }

    private static bool IsZero(float2 v)
    {
        return v.X == 0f && v.Y == 0f;
    }

    private static float2 Unit(float2 v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        if (len < 1e-4f)
            return new float2(0f, 0f);
        return new float2(v.X / len, v.Y / len);
    }
}
