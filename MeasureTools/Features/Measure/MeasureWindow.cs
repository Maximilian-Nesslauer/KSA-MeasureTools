using System;
using Brutal.ImGuiApi;
using Brutal.Logging;
using Brutal.Numerics;
using KSA;
using MeasureTools.Core;

namespace MeasureTools.Features.Measure;

// The tool window: mode selection, snap and plane options, the reference body
// override and the list of measurements. Extends the stock ImGuiWindow base for
// Begin/End, the menu bar and pin/focus handling.
internal sealed class MeasureWindow : ImGuiWindow
{
    private static MeasureWindow? _instance;

    // Lazily created from the menu hook, inside an active ImGui frame, which the
    // ImGuiWindow base constructor requires.
    public static MeasureWindow Instance => _instance ??= new MeasureWindow();

    public static bool IsOpen => _instance != null && _instance.IsShown;

    private MeasureWindow()
        : base(new float2(500f, 460f), lockAspectRatio: false, show: false)
    {
        SetWindowTitle("Measure");
        // Default to the upper left, right of the stock Map View panel, so the
        // window stays clear of the map area where measurements are placed. The base
        // constructor already calls into ImGui, and this only runs from the menu
        // hook, so an ImGui frame is always active here.
        ImGuiViewportPtr mainViewport = ImGui.GetMainViewport();
        _initialPosition = new float2(mainViewport.Pos.X + 320f, mainViewport.Pos.Y + 80f);
    }

    // Draw the window if it exists and is shown. Does not create the instance, so
    // the draw hook never touches ImGui state before the user first opens the tool.
    public static void DrawActive(Viewport viewport)
    {
        if (_instance == null || !_instance.IsShown)
            return;
        _instance.OnDrawUi(viewport);
        // The title bar close button flips _show without going through Close();
        // measurements are ephemeral, so treat it the same.
        if (!_instance.IsShown)
        {
            _instance.OnClosed();
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug("[MeasureTools] Tool closed via title bar, measurements cleared.");
        }
    }

    // Must not touch ImGui (can run outside a frame, from [StarMapUnload]).
    public static void ResetStatic()
    {
        _instance = null;
    }

    public void Open()
    {
        _show = true;
        // Opening always arms the tool, even if it was paused before closing.
        MeasureState.SetToolActive(true);
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug("[MeasureTools] Tool opened.");
    }

    public void Close()
    {
        _show = false;
        OnClosed();
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug("[MeasureTools] Tool closed, measurements cleared.");
    }

    // Shared teardown for both close paths (Close and the title-bar button):
    // measurements are ephemeral by design, the combo cache must not outlive
    // the window, and edited colors persist here.
    private void OnClosed()
    {
        MeasureState.ClearAll();
        ResetAutoPreview();
        MeasureColors.SaveIfDirty();
    }

    // Drops the cached Auto-combo body so a closed window does not keep a body
    // from an unloaded system alive; the text rebuilds on the next combo draw.
    private void ResetAutoPreview()
    {
        _autoPreviewBody = null;
        _autoPreviewText = "Auto (none)";
    }

    public override void DrawContent(Viewport viewport)
    {
        if (Universe.CurrentSystem == null)
        {
            ImGui.TextDisabled("No system loaded."u8);
            return;
        }

        if (!MeasureState.IsSupportedViewMode(viewport.Mode))
            ImGui.TextWrapped("Switch to the map or flight view to place measurements."u8);

        DrawModeToolbar();
        DrawStatus(viewport);

        if (ImGui.CollapsingHeader("Snapping"u8, ImGuiTreeNodeFlags.DefaultOpen))
            DrawSnappingSection(viewport);

        int count = MeasureState.Measurements.Count;
        if (count != _measurementsHeaderCount)
        {
            _measurementsHeaderCount = count;
            // ### keeps the header id stable while the visible count changes.
            _measurementsHeader = "Measurements (" + count + ")###measurements";
        }
        if (ImGui.CollapsingHeader(_measurementsHeader, ImGuiTreeNodeFlags.DefaultOpen))
            DrawMeasurementList();
        else
            MeasureState.HighlightIndex = -1;

        if (ImGui.CollapsingHeader("Colors"u8))
            DrawColorsSection();

#if DEBUG
        // Runtime switches for the DebugConfig flags (mutable by design), so
        // measure/perf logging can be toggled without a rebuild. Performance
        // logging drives the PerfTracker scopes reporting avg/min/max per 5 s.
        if (ImGui.CollapsingHeader("Debug logging"u8))
        {
            bool logMeasure = DebugConfig.Measure;
            if (ImGui.Checkbox("Measure events"u8, ref logMeasure))
                DebugConfig.Measure = logMeasure;
            ImGui.SameLine();
            bool logPerformance = DebugConfig.Performance;
            if (ImGui.Checkbox("Performance"u8, ref logPerformance))
                DebugConfig.Performance = logPerformance;
        }
#endif
    }

    private int _measurementsHeaderCount = -1;
    private string _measurementsHeader = "Measurements (0)###measurements";

    // One toolbar button per mode, the active one drawn in the pressed style.
    // While paused (short right-click in the view) no mode is highlighted;
    // clicking any button re-arms measuring. A window too narrow for five
    // buttons splits the row 3 + 2.
    private static void DrawModeToolbar()
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        float spacing = style.ItemSpacing.X;
        float available = ImGui.GetContentRegionAvail().X;
        // "Protractor" is the widest mode label; when five buttons cannot fit
        // it, both rows are sized as three-button rows (five buttons share four
        // gaps, three share two).
        float minWidth = ImGui.CalcTextSize("Protractor").X + style.FramePadding.X * 2f;
        float fiveAcross = (available - spacing * 4f) / 5f;
        bool oneRow = fiveAcross >= minWidth;
        float width = oneRow ? fiveAcross : (available - spacing * 2f) / 3f;

        DrawModeButton("Ruler"u8, MeasureMode.Ruler, width);
        ImGui.SameLine();
        DrawModeButton("Protractor"u8, MeasureMode.Angle, width);
        ImGui.SameLine();
        DrawModeButton("Surface"u8, MeasureMode.Surface, width);
        if (oneRow)
            ImGui.SameLine();
        DrawModeButton("Circle"u8, MeasureMode.Circle, width);
        ImGui.SameLine();
        DrawModeButton("Face angle"u8, MeasureMode.FaceAngle, width);
    }

    private static void DrawModeButton(ImString label, MeasureMode mode, float width)
    {
        bool selected = MeasureState.ToolActive && MeasureState.Mode == mode;
        if (selected)
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyleColorVec4(ImGuiCol.ButtonActive));
        if (ImGui.Button(label, new float2(width, 0f)))
        {
            MeasureState.SetMode(mode);
            MeasureState.SetToolActive(true);
        }
        if (selected)
            ImGui.PopStyleColor();
    }

    private void DrawSnappingSection(Viewport viewport)
    {
        // Snap and the reference body only apply to ruler/protractor picking;
        // surface mode ray-casts the celestial spheres, circle and face-angle
        // modes have their own part picking.
        bool snapControlsApply = MeasureState.Mode == MeasureMode.Ruler || MeasureState.Mode == MeasureMode.Angle;
        ImGui.BeginDisabled(!MeasureState.ToolActive || !snapControlsApply);
        bool snap = MeasureState.SnapEnabled;
        if (ImGui.Checkbox("Bodies and orbit lines"u8, ref snap))
            MeasureState.SetSnapEnabled(snap);
        // Part snapping is a refinement of general snapping, so the sub-controls
        // disable together with the master snap toggle.
        ImGui.BeginDisabled(!snap);
        bool snapParts = MeasureState.PartSnapEnabled;
        if (ImGui.Checkbox("Parts"u8, ref snapParts))
            MeasureState.SetPartSnapEnabled(snapParts);
        if (ImGui.IsItemHovered())
            ImGuiHelper.DrawTooltip("Pick points on vehicle parts: exact surface points under the cursor,\nrefined by the tiers below. Off: vehicles snap at their center marker only."u8);
        ImGui.Indent();
        ImGui.BeginDisabled(!snapParts);
        bool snapNodes = MeasureState.PartFeatureSnapEnabled;
        if (ImGui.Checkbox("Nodes, centers, rims"u8, ref snapNodes))
            MeasureState.SetPartFeatureSnapEnabled(snapNodes);
        if (ImGui.IsItemHovered())
            ImGuiHelper.DrawTooltip("Point targets: attach nodes, part centers, fitted rim centers,\nand the mirror of the previous point across the part axis."u8);
        bool snapVertices = MeasureState.PartVertexSnapEnabled;
        if (ImGui.Checkbox("Vertices and edges"u8, ref snapVertices))
            MeasureState.SetPartVertexSnapEnabled(snapVertices);
        if (ImGui.IsItemHovered())
            ImGuiHelper.DrawTooltip("Vertices, feature-edge midpoints, and sliding along feature edges\n(tank rims and other sharp or boundary edges)."u8);
        ImGui.EndDisabled();
        ImGui.Unindent();
        ImGui.EndDisabled();
        // In the editor free points anchor to the editing space, so the system
        // reference bodies do not apply.
        if (Program.Editor != null)
        {
            ImGui.TextDisabled("Reference: edited vehicle"u8);
        }
        else
        {
            ImGui.Text("Reference"u8);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            DrawReferenceCombo(viewport);
        }
        ImGui.EndDisabled();
    }

    private static void DrawColorsSection()
    {
        // The preview shades derive from Pending by alpha, so one row covers the
        // whole pending/preview family.
        bool changed = false;
        changed |= DrawColorRow("Lines"u8, ref MeasureColors.Measure);
        changed |= DrawColorRow("Line highlight"u8, ref MeasureColors.Highlight);
        changed |= DrawColorRow("Pending and preview"u8, ref MeasureColors.Pending);
        changed |= DrawColorRow("Snap markers"u8, ref MeasureColors.FeatureDot);
        changed |= DrawColorRow("Construction plane"u8, ref MeasureColors.Plane);
        changed |= DrawColorRow("Label text"u8, ref MeasureColors.LabelText);
        changed |= DrawColorRow("Label background"u8, ref MeasureColors.LabelPlate);
        if (changed)
            MeasureColors.MarkDirty();
        if (ImGui.SmallButton("Reset colors"u8))
        {
            MeasureColors.Reset();
            MeasureColors.MarkDirty();
        }
    }

    private static bool DrawColorRow(ImString label, ref byte4 color)
    {
        float4 value = MeasureColors.ToFloat4(color);
        if (!ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
            return false;
        color = MeasureColors.FromFloat4(value);
        return true;
    }

    // The "Auto (...)" preview rebuilt only when the resolved body changes, not
    // per frame; the override case reuses the body's existing Id string.
    private Astronomical? _autoPreviewBody;
    private string _autoPreviewText = "Auto (none)";

    private void DrawReferenceCombo(Viewport viewport)
    {
        Astronomical? auto = MeasureState.ReferenceOverride == null
            ? MeasureState.ResolveReferenceBody(viewport)
            : null;
        if (MeasureState.ReferenceOverride == null && !ReferenceEquals(auto, _autoPreviewBody))
        {
            _autoPreviewBody = auto;
            _autoPreviewText = "Auto (" + (auto?.Id ?? "none") + ")";
        }
        string preview = MeasureState.ReferenceOverride?.Id ?? _autoPreviewText;
        if (!ImGui.BeginCombo("##reference"u8, preview))
            return;
        if (ImGui.Selectable("Auto"u8, MeasureState.ReferenceOverride == null))
            MeasureState.SetReferenceOverride(null);
        // Celestials and stars anchor free points to a fixed body; vehicles are
        // offered too so a free point can track a craft, which is what the flight
        // view wants when measuring around the ship you are following.
        foreach (Astronomical astronomical in Universe.CurrentSystem!.All.AsSpan())
        {
            if (astronomical is not Celestial && astronomical is not StellarBody && astronomical is not Vehicle)
                continue;
            if (ImGui.Selectable(astronomical.Id, MeasureState.ReferenceOverride == astronomical))
                MeasureState.SetReferenceOverride(astronomical);
        }
        ImGui.EndCombo();
    }

    private void DrawStatus(Viewport viewport)
    {
        if (!MeasureState.IsSupportedViewMode(viewport.Mode))
            return;
        if (!MeasureState.ToolActive)
        {
            ImGui.TextDisabled("Measuring paused, clicks pass through to the game."u8);
            ImGui.TextDisabled("Select a tool above to resume."u8);
            return;
        }
        int have = MeasureState.Pending.Count;
        string status = MeasureState.Mode switch
        {
            MeasureMode.Ruler => have == 0 ? "Click in the view: place the first point" : "Click in the view: place the second point",
            MeasureMode.Surface => have == 0
                ? "Click on a body: place the first surface point"
                : "Click the same body: place the second surface point",
            MeasureMode.Circle => "Click a circular part edge (e.g. a tank rim)",
            MeasureMode.FaceAngle => have == 0
                ? "Click a part surface: sample the first face"
                : "Click a part surface: sample the second face",
            // MeasureMode.Angle, the three-point protractor.
            _ => have switch
            {
                0 => "Click in the view: place the first arm",
                1 => "Click in the view: place the apex",
                _ => "Click in the view: place the second arm",
            },
        };
        ImGui.Text(status);
        ImGui.SameLine();
        ImGui.TextDisabled("(?)"u8);
        if (ImGui.IsItemHovered())
        {
            if (MeasureState.Mode == MeasureMode.Surface)
                ImGuiHelper.DrawTooltip("Points pin to the surface and track the body's rotation.\nShort right-click: cancel point, or pause measuring when nothing is pending."u8);
            else if (MeasureState.Mode == MeasureMode.Circle)
                ImGuiHelper.DrawTooltip("One click on a circular part edge measures its diameter, radius\nand circumference from a fitted circle.\nShort right-click: pause measuring."u8);
            else if (MeasureState.Mode == MeasureMode.FaceAngle)
                ImGuiHelper.DrawTooltip("Angle between two sampled surface normals (0 deg = parallel faces\nfacing the same way).\nShort right-click: cancel point, or pause measuring when nothing is pending."u8);
            else
                ImGuiHelper.DrawTooltip("Free clicks land on the camera plane.\nCtrl-click: free point on the ecliptic plane.\nShort right-click: cancel point, or pause measuring when nothing is pending."u8);
        }
        if (have > 0 && ImGui.SmallButton("Cancel point placement"u8))
            MeasureState.CancelPending();
    }

    private void DrawMeasurementList()
    {
#if DEBUG
        using var perfScope = new PerfTracker.Scope("MeasureWindow.DrawMeasurementList");
#endif
        // Hover sync: rebuilt every frame; the overlay draws right after this and
        // brightens the hovered measurement on the map.
        MeasureState.HighlightIndex = -1;

        if (MeasureState.Measurements.Count == 0)
        {
            ImGui.TextDisabled("none"u8);
            return;
        }

        // Clear all sits right-aligned above the table; 10f covers the small
        // button's frame padding.
        float clearWidth = ImGui.CalcTextSize("Clear all").X + 10f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X - clearWidth));
        if (ImGui.SmallButton("Clear all"u8))
        {
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] Clear all: dropping {MeasureState.Measurements.Count} measurement(s).");
            MeasureState.ClearAll();
            return;
        }

        if (!ImGui.BeginTable("measurements"u8, 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable))
            return;
        ImGui.TableSetupColumn(""u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Value"u8, ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Points"u8, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        Span<char> buffer = stackalloc char[64];
        int removeAt = -1;
        for (int i = 0; i < MeasureState.Measurements.Count; i++)
        {
            Measurement m = MeasureState.Measurements[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("x"u8))
                removeAt = i;
            bool hovered = ImGui.IsItemHovered();

            ImGui.TableNextColumn();
            string value = FormatValue(m, buffer);
            string endpoints = m.Anchors.Length == 2
                ? m.Anchors[0].Label + " - " + m.Anchors[1].Label
                : m.Anchors[0].Label + " - " + m.Anchors[1].Label + " - " + m.Anchors[2].Label;

            // Right-aligned value cell.
            ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new float2(1f, 0f));
            bool copy = ImGui.Selectable(value);
            ImGui.PopStyleVar();
            hovered |= CopyTooltipOnHover(endpoints);

            ImGui.TableNextColumn();
            copy |= ImGui.Selectable(endpoints);
            hovered |= CopyTooltipOnHover(endpoints);

            if (copy)
            {
                // Built only on the click: the full data set resolves anchors and
                // concatenates several strings, waste on the 99.9% of frames
                // where no row is clicked.
                string copyText = BuildCopyText(m, value, endpoints, buffer);
                ImGui.SetClipboardText(copyText);
                if (DebugConfig.Measure)
                    DefaultCategory.Log.Debug($"[MeasureTools] Copied measurement #{i + 1} to clipboard: {copyText}");
            }
            if (hovered)
                MeasureState.HighlightIndex = i;
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (removeAt >= 0)
        {
            MeasureState.Measurements.RemoveAt(removeAt);
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] Measurement #{removeAt + 1} removed via list.");
        }
    }

    // Tooltip for the last drawn item; returns whether it was hovered. Shows the
    // full endpoints text, since the Points column truncates long part names.
    private static bool CopyTooltipOnHover(string endpoints)
    {
        if (!ImGui.IsItemHovered())
            return false;
        ImGuiHelper.DrawTooltip(endpoints + "\nClick to copy");
        return true;
    }

    private static string FormatValue(Measurement m, Span<char> buffer)
    {
        if (m.Mode == MeasureMode.Ruler)
            return new string(DistanceReference.ToNearest(m.DistanceMeters(), buffer));
        if (m.Mode == MeasureMode.Surface)
            return new string(DistanceReference.ToNearest(m.SurfaceDistanceMeters(), buffer));
        if (m.Mode == MeasureMode.Circle)
            return "d " + new string(DistanceReference.ToNearest(m.CircleDiameterMeters(), buffer));
        if (m.Mode == MeasureMode.FaceAngle)
        {
            double faceAngle = m.FaceAngleRadians();
            // NaN while a normal cannot resolve (stale owner mid-transition).
            return double.IsNaN(faceAngle) ? "undefined" : new string(RadianReference.FromRadians(faceAngle).ToStringDegrees(buffer));
        }
        double angle = m.AngleRadians();
        // NaN when an arm coincides with the apex (e.g. both on one body).
        return double.IsNaN(angle) ? "undefined" : new string(RadianReference.FromRadians(angle).ToStringDegrees(buffer));
    }

    // The clipboard gets the full data set, not just the headline value: arm
    // lengths for the protractor, chord and bearing for surface measurements.
    private static string BuildCopyText(Measurement m, string value, string endpoints, Span<char> buffer)
    {
        switch (m.Mode)
        {
            case MeasureMode.Angle:
            {
                double3 apexEcl = m.Anchors[1].ResolveEcl();
                string armA = new string(DistanceReference.ToNearest((m.Anchors[0].ResolveEcl() - apexEcl).Length(), buffer));
                string armB = new string(DistanceReference.ToNearest((m.Anchors[2].ResolveEcl() - apexEcl).Length(), buffer));
                return value + ", arms " + armA + " / " + armB + "  (" + endpoints + ")";
            }
            case MeasureMode.Surface:
            {
                string chord = new string(DistanceReference.ToNearest(m.DistanceMeters(), buffer));
                return value + ", chord " + chord + ", bearing "
                    + m.BearingDegrees().ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + " deg  (" + endpoints + ")";
            }
            case MeasureMode.Circle:
            {
                string radius = new string(DistanceReference.ToNearest(m.CircleRadiusMeters(), buffer));
                string circumference = new string(DistanceReference.ToNearest(m.CircleCircumferenceMeters(), buffer));
                return value + ", r " + radius + ", C " + circumference + "  (" + endpoints + ")";
            }
            case MeasureMode.Ruler when m.TryGetAxialRadialMeters(out double axial, out double radial):
            {
                string axialText = new string(DistanceReference.ToNearest(axial, buffer));
                string radialText = new string(DistanceReference.ToNearest(radial, buffer));
                return value + ", axial " + axialText + ", radial " + radialText + "  (" + endpoints + ")";
            }
            default:
                return value + "  (" + endpoints + ")";
        }
    }
}
