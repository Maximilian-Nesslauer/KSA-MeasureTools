using System;
using Brutal.ImGuiApi;
using Brutal.Logging;
using Brutal.Numerics;
using KSA;
using MeasureTools.Core;

namespace MeasureTools.Features.Measure;

// The tool window: mode selection, snap and plane options, the reference body
// override and the list of measurements. Extends the stock ImGuiWindow base for
// Begin/End, the menu bar and pin/focus handling (the DeltaVMap pattern).
internal sealed class MeasureWindow : ImGuiWindow
{
    private static MeasureWindow? _instance;

    // Lazily created from the menu hook, inside an active ImGui frame, which the
    // ImGuiWindow base constructor requires.
    public static MeasureWindow Instance => _instance ??= new MeasureWindow();

    public static bool IsOpen => _instance != null && _instance.IsShown;

    private MeasureWindow()
        : base(new float2(460f, 380f), lockAspectRatio: false, show: false)
    {
        SetWindowTitle("Measure");
        // Default to the upper left, right of the stock Map View panel, so the
        // window stays clear of the map area where measurements are placed.
        try
        {
            ImGuiViewportPtr mainViewport = ImGui.GetMainViewport();
            _initialPosition = new float2(mainViewport.Pos.X + 320f, mainViewport.Pos.Y + 80f);
        }
        catch
        {
            // Outside an ImGui frame; keep the base class default position.
        }
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
            MeasureState.ClearAll();
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
        // Ephemeral by design: leaving the tool clears all measurements.
        MeasureState.ClearAll();
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug("[MeasureTools] Tool closed, measurements cleared.");
    }

    public override void DrawContent(Viewport viewport)
    {
        if (Universe.CurrentSystem == null)
        {
            ImGui.TextDisabled("No system loaded."u8);
            return;
        }

        if (viewport.Mode != CameraMode.Map)
            ImGui.TextWrapped("Switch to the map view to place measurements."u8);

        // While paused (short right-click in the map), no tool is selected; picking
        // a tool re-arms measuring.
        bool active = MeasureState.ToolActive;
        if (ImGui.RadioButton("Ruler"u8, active && MeasureState.Mode == MeasureMode.Ruler))
        {
            MeasureState.SetMode(MeasureMode.Ruler);
            MeasureState.SetToolActive(true);
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Protractor"u8, active && MeasureState.Mode == MeasureMode.Angle))
        {
            MeasureState.SetMode(MeasureMode.Angle);
            MeasureState.SetToolActive(true);
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Surface"u8, active && MeasureState.Mode == MeasureMode.Surface))
        {
            MeasureState.SetMode(MeasureMode.Surface);
            MeasureState.SetToolActive(true);
        }

        // Snap and the reference body only apply to ruler/protractor picking;
        // surface mode always ray-casts the celestial spheres.
        ImGui.BeginDisabled(!active || MeasureState.Mode == MeasureMode.Surface);
        bool snap = MeasureState.SnapEnabled;
        if (ImGui.Checkbox("Snap to bodies and orbit lines"u8, ref snap))
            MeasureState.SnapEnabled = snap;
        DrawReferenceCombo(viewport);
        ImGui.EndDisabled();

        ImGui.Separator();
        DrawStatus(viewport);

        ImGui.SeparatorText("Measurements"u8);
        DrawMeasurementList();
    }

    private void DrawReferenceCombo(Viewport viewport)
    {
        Astronomical? auto = MeasureState.ReferenceOverride == null
            ? MeasureState.ResolveReferenceBody(viewport)
            : null;
        string preview = MeasureState.ReferenceOverride?.Id ?? "Auto (" + (auto?.Id ?? "none") + ")";
        if (!ImGui.BeginCombo("Reference"u8, preview))
            return;
        if (ImGui.Selectable("Auto"u8, MeasureState.ReferenceOverride == null))
            MeasureState.ReferenceOverride = null;
        foreach (Astronomical astronomical in Universe.CurrentSystem!.All.AsSpan())
        {
            if (astronomical is not Celestial && astronomical is not StellarBody)
                continue;
            if (ImGui.Selectable(astronomical.Id, MeasureState.ReferenceOverride == astronomical))
                MeasureState.ReferenceOverride = astronomical;
        }
        ImGui.EndCombo();
    }

    private void DrawStatus(Viewport viewport)
    {
        if (viewport.Mode != CameraMode.Map)
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
            MeasureMode.Ruler => have == 0 ? "Click in the map: place the first point" : "Click in the map: place the second point",
            MeasureMode.Surface => have == 0
                ? "Click on a body: place the first surface point"
                : "Click the same body: place the second surface point",
            _ => have switch
            {
                0 => "Click in the map: place the first arm",
                1 => "Click in the map: place the apex",
                _ => "Click in the map: place the second arm",
            },
        };
        ImGui.Text(status);
        ImGui.SameLine();
        ImGui.TextDisabled("(?)"u8);
        if (ImGui.IsItemHovered())
        {
            ImGuiHelper.DrawTooltip(MeasureState.Mode == MeasureMode.Surface
                ? "Points pin to the surface and track the body's rotation.\nShort right-click: cancel point, or pause measuring when nothing is pending."u8
                : "Free clicks land on the camera plane.\nCtrl-click: free point on the ecliptic plane.\nShort right-click: cancel point, or pause measuring when nothing is pending."u8);
        }
        if (have > 0 && ImGui.SmallButton("Cancel point placement"u8))
            MeasureState.CancelPending();
    }

    private void DrawMeasurementList()
    {
        // Hover sync: rebuilt every frame; the overlay draws right after this and
        // brightens the hovered measurement on the map.
        MeasureState.HighlightIndex = -1;

        if (MeasureState.Measurements.Count == 0)
        {
            ImGui.TextDisabled("none"u8);
            return;
        }

        // Clear all sits right-aligned above the table.
        float clearWidth = ImGui.CalcTextSize("Clear all").X + 10f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X - clearWidth));
        if (ImGui.SmallButton("Clear all"u8))
        {
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] Clear all: dropping {MeasureState.Measurements.Count} measurement(s).");
            MeasureState.ClearAll();
            return;
        }

        if (!ImGui.BeginTable("measurements"u8, 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
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
            // A click on any cell copies the full data set of the row.
            string copyText = BuildCopyText(m, value, endpoints, buffer);

            // Right-aligned value cell.
            ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new float2(1f, 0f));
            bool copy = ImGui.Selectable(value);
            ImGui.PopStyleVar();
            hovered |= CopyTooltipOnHover();

            ImGui.TableNextColumn();
            copy |= ImGui.Selectable(endpoints);
            hovered |= CopyTooltipOnHover();

            if (copy)
            {
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

    // Tooltip for the last drawn item; returns whether it was hovered.
    private static bool CopyTooltipOnHover()
    {
        if (!ImGui.IsItemHovered())
            return false;
        ImGuiHelper.DrawTooltip("Click to copy"u8);
        return true;
    }

    private static string FormatValue(Measurement m, Span<char> buffer)
    {
        if (m.Mode == MeasureMode.Ruler)
            return new string(DistanceReference.ToNearest(m.DistanceMeters(), buffer));
        if (m.Mode == MeasureMode.Surface)
            return new string(DistanceReference.ToNearest(m.SurfaceDistanceMeters(), buffer));
        double angle = m.AngleRadians();
        // NaN when an arm coincides with the apex (e.g. both on one body).
        return double.IsNaN(angle) ? "undefined" : RadianReference.FromRadians(angle).ToStringDegrees();
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
            default:
                return value + "  (" + endpoints + ")";
        }
    }
}
