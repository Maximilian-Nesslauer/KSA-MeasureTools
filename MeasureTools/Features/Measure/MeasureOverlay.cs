using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using MeasureTools.Core;

namespace MeasureTools.Features.Measure;

// Draws the measurements, the in-progress placement preview, the snap highlight and
// the free-placement construction plane onto the map view, on the background draw
// list (the stock body-label and DeltaVMap overlay pattern). Everything is wrapped
// so a camera or projection change can never unwind into the render path.
internal static class MeasureOverlay
{
    private static readonly byte4 MeasureColor = new byte4(120, 220, 160, 235);
    // List-hover highlight: brighter and thicker so the row-to-line mapping is obvious.
    private static readonly byte4 HighlightColor = new byte4(215, 255, 235, 255);
    private static readonly byte4 PendingColor = new byte4(255, 220, 110, 245);
    private static readonly byte4 PreviewColor = new byte4(255, 220, 110, 160);
    private static readonly byte4 PreviewFaint = new byte4(255, 220, 110, 80);
    private static readonly byte4 PlaneColor = new byte4(150, 170, 200, 70);
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
    private static int _previewStateVersion = -1;
    private static bool _previewEclipticFree;

    // Must not touch ImGui (called from [StarMapUnload]).
    public static void Reset()
    {
        _previewFramesSincePick = PreviewPickIntervalFrames;
        _previewCache = null;
        _previewStateVersion = -1;
        _previewEclipticFree = false;
    }

    public static void Draw(Viewport viewport)
    {
        try
        {
            if (!MeasureWindow.IsOpen)
                return;
            if (viewport.Mode != CameraMode.Map)
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
            // Key on the exception type so a second, different failure mode is not
            // silenced by the first.
            LogHelper.ErrorOnce("overlay-" + ex.GetType().Name, $"[MeasureTools] Overlay draw failed: {ex}");
        }
    }

    private static void DrawMeasurement(ImDrawListPtr dl, Camera camera, float2 vpPos, Measurement m, bool highlighted)
    {
        byte4 color = highlighted ? HighlightColor : MeasureColor;
        float thickness = highlighted ? 3.5f : 2f;
        if (m.Mode == MeasureMode.Surface)
        {
            DrawSurfaceMeasurement(dl, camera, vpPos, m, color, thickness);
        }
        else if (m.Mode == MeasureMode.Ruler)
        {
            float2 a = Project(camera, vpPos, m.Anchors[0]);
            float2 b = Project(camera, vpPos, m.Anchors[1]);
            if (!Valid(a) || !Valid(b))
                return;
            dl.AddLine(in a, in b, color, thickness);
            Dot(dl, a, color);
            Dot(dl, b, color);
            Label(dl, SegmentLabelPos(a, b), FormatDistance(m.DistanceMeters()));
        }
        else
        {
            float2 a = Project(camera, vpPos, m.Anchors[0]);
            float2 apex = Project(camera, vpPos, m.Anchors[1]);
            float2 b = Project(camera, vpPos, m.Anchors[2]);
            if (!Valid(a) || !Valid(apex) || !Valid(b))
                return;
            dl.AddLine(in apex, in a, color, thickness);
            dl.AddLine(in apex, in b, color, thickness);
            Dot(dl, a, color);
            Dot(dl, apex, color);
            Dot(dl, b, color);
            DrawAngleArcAndLabel(dl, apex, a, b, m.AngleRadians(), color);
            // Both arms carry their length, like ruler segments.
            double3 apexEcl = m.Anchors[1].ResolveEcl();
            Label(dl, SegmentLabelPos(apex, a), FormatDistance((m.Anchors[0].ResolveEcl() - apexEcl).Length()));
            Label(dl, SegmentLabelPos(apex, b), FormatDistance((m.Anchors[2].ResolveEcl() - apexEcl).Length()));
        }
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
        Label(dl, new float2(labelPos.X, labelPos.Y + 15f), detail);
    }

    // The great-circle arc between two surface points, sampled along the sphere and
    // occlusion-culled: samples on the far hemisphere (facing away from the camera)
    // break the polyline instead of drawing through the planet disc.
    private static void DrawGreatCircleArc(ImDrawListPtr dl, Camera camera, float2 vpPos, double3 centerEcl, double3 aEcl, double3 bEcl, byte4 color, float thickness)
    {
        double3 da = aEcl - centerEcl;
        double3 db = bEcl - centerEcl;
        double ra = da.Length();
        double rb = db.Length();
        if (!(ra > 0.0) || !(rb > 0.0))
            return;
        double3 ua = da * (1.0 / ra);
        double3 ub = db * (1.0 / rb);
        double angle = Math.Acos(Math.Clamp(double3.Dot(ua, ub), -1.0, 1.0));
        double3 axis = double3.Cross(ua, ub).NormalizeOrZero();
        if (angle < 1e-9 || (axis.X == 0.0 && axis.Y == 0.0 && axis.Z == 0.0))
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
        double3 da = aEcl - centerEcl;
        double3 db = bEcl - centerEcl;
        double ra = da.Length();
        double rb = db.Length();
        if (!(ra > 0.0) || !(rb > 0.0))
            return new float2(float.NaN, float.NaN);
        double3 ua = da * (1.0 / ra);
        double3 ub = db * (1.0 / rb);
        double angle = Math.Acos(Math.Clamp(double3.Dot(ua, ub), -1.0, 1.0));
        double3 axis = double3.Cross(ua, ub).NormalizeOrZero();
        if (axis.X == 0.0 && axis.Y == 0.0 && axis.Z == 0.0)
            return vpPos + camera.EclToScreen(aEcl);
        double3 mid = Rotate(ua, axis, angle * 0.5);
        return vpPos + camera.EclToScreen(centerEcl + mid * ((ra + rb) * 0.5));
    }

    // Rodrigues' rotation about a unit axis (DeltaVMap pattern).
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
        if (!MeasureState.IsArmed || ImGui.GetIO().WantCaptureMouse)
        {
            // Not previewing (tool disarmed or cursor over UI): drop the cache so the
            // first frame back over the map picks fresh.
            Reset();
            return;
        }

        float2 mouseViewport = ImGui.GetIO().MousePos - vpPos;
        // Ctrl previews (and places) a free point on the ecliptic plane even where
        // snapping would win; a modifier change re-picks immediately so the preview
        // flips with the key.
        bool eclipticFree = ImGui.GetIO().KeyCtrl;
        _previewFramesSincePick++;
        if (_previewFramesSincePick >= PreviewPickIntervalFrames
            || _previewStateVersion != MeasureState.StateVersion
            || _previewEclipticFree != eclipticFree)
        {
            _previewCache = MapPicker.Pick(viewport, mouseViewport, eclipticFree);
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

        DrawSnapHighlight(dl, camera, viewport, vpPos, cursor, preview, _previewEclipticFree);

        if (!pendingValid || pending.Count == 0)
            return;

        if (MeasureState.Mode == MeasureMode.Ruler)
        {
            // One pending point: rubber-band line with the live distance.
            float2 a = pendingScreen[0];
            dl.AddLine(in a, in cursor, PendingColor, 1.6f);
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
                DrawGreatCircleArc(dl, camera, vpPos, centerEcl, pending[0].ResolveEcl(), preview.ResolveEcl(), PendingColor, 1.6f);
                double meters = Measurement.GreatCircleMeters(
                    body, pending[0].Latitude, pending[0].Longitude, preview.Latitude, preview.Longitude);
                Label(dl, SegmentLabelPos(pendingScreen[0], cursor), FormatDistance(meters));
            }
        }
        else if (pending.Count == 1)
        {
            // Arm placed, cursor previews the apex: live arm length.
            float2 a = pendingScreen[0];
            dl.AddLine(in cursor, in a, PendingColor, 1.6f);
            double meters = (pending[0].ResolveEcl() - preview.ResolveEcl()).Length();
            Label(dl, SegmentLabelPos(cursor, a), FormatDistance(meters));
        }
        else
        {
            // Arm and apex placed, cursor previews the second arm: live angle plus
            // both arm lengths, like the settled protractor rendering.
            float2 a = pendingScreen[0];
            float2 apex = pendingScreen[1];
            dl.AddLine(in apex, in a, PendingColor, 1.6f);
            dl.AddLine(in apex, in cursor, PendingColor, 1.6f);
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
            default:
                Cross(dl, cursor, 7f, PreviewColor);
                DrawConstructionPlane(dl, camera, viewport, vpPos, cursor, eclipticPlane);
                // Spell out which plane the point will land on, so an unexpected
                // plane mode or reference body is visible before the click.
                string plane = eclipticPlane ? "ecliptic plane" : "camera plane";
                Label(dl, new float2(cursor.X + 12f, cursor.Y - 16f),
                    plane + " @ " + (preview.Body?.Id ?? "?"));
                break;
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
        ScreenArc(dl, apex, ArcPx, uA, uB, color, 1.6f);

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
