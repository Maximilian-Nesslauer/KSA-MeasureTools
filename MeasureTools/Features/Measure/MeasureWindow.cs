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
        // Default high on the screen, horizontally centered, so the window stays
        // clear of the map area where measurements are placed.
        try
        {
            ImGuiViewportPtr mainViewport = ImGui.GetMainViewport();
            _initialPosition = new float2(mainViewport.GetCenter().X - 230f, mainViewport.Pos.Y + 80f);
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

        if (ImGui.RadioButton("Ruler"u8, MeasureState.Mode == MeasureMode.Ruler))
            MeasureState.SetMode(MeasureMode.Ruler);
        ImGui.SameLine();
        if (ImGui.RadioButton("Protractor"u8, MeasureState.Mode == MeasureMode.Angle))
            MeasureState.SetMode(MeasureMode.Angle);

        bool snap = MeasureState.SnapEnabled;
        if (ImGui.Checkbox("Snap to bodies and orbit lines"u8, ref snap))
            MeasureState.SnapEnabled = snap;

        DrawReferenceCombo(viewport);

        ImGui.Separator();
        DrawStatus(viewport);

        ImGui.Separator();
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
        int have = MeasureState.Pending.Count;
        string status = MeasureState.Mode == MeasureMode.Ruler
            ? (have == 0 ? "Click in the map: place the first point" : "Click in the map: place the second point")
            : have switch
            {
                0 => "Click in the map: place the first arm",
                1 => "Click in the map: place the apex",
                _ => "Click in the map: place the second arm",
            };
        ImGui.Text(status);
        ImGui.TextDisabled("Free clicks land on the camera plane."u8);
        ImGui.TextDisabled("Ctrl-click: free point on the ecliptic plane."u8);
        ImGui.TextDisabled("Short right-click: cancel point."u8);
        if (have > 0 && ImGui.SmallButton("Cancel point placement"u8))
            MeasureState.CancelPending();
    }

    private void DrawMeasurementList()
    {
        ImGui.Text("Measurements"u8);
        if (MeasureState.Measurements.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear all"u8))
                MeasureState.ClearAll();
        }

        // Hover sync: rebuilt every frame; the overlay draws right after this and
        // brightens the hovered measurement on the map.
        MeasureState.HighlightIndex = -1;

        if (MeasureState.Measurements.Count == 0)
        {
            ImGui.TextDisabled("none"u8);
            return;
        }

        if (!ImGui.BeginTable("measurements"u8, 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;
        ImGui.TableSetupColumn(""u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("value"u8, ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("points"u8, ImGuiTableColumnFlags.WidthStretch);

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
            string value;
            if (m.Mode == MeasureMode.Ruler)
            {
                value = new string(DistanceReference.ToNearest(m.DistanceMeters(), buffer));
            }
            else
            {
                double angle = m.AngleRadians();
                // NaN when an arm coincides with the apex (e.g. both on one body).
                value = double.IsNaN(angle) ? "undefined" : RadianReference.FromRadians(angle).ToStringDegrees();
            }
            // Right-aligned, clickable: a click copies the value to the clipboard.
            ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new float2(1f, 0f));
            if (ImGui.Selectable(value))
                ImGui.SetClipboardText(value);
            ImGui.PopStyleVar();
            if (ImGui.IsItemHovered())
            {
                hovered = true;
                ImGuiHelper.DrawTooltip("Click to copy"u8);
            }

            ImGui.TableNextColumn();
            string endpoints = m.Mode == MeasureMode.Ruler
                ? m.Anchors[0].Label + " - " + m.Anchors[1].Label
                : m.Anchors[0].Label + " - " + m.Anchors[1].Label + " - " + m.Anchors[2].Label;
            ImGui.Text(endpoints);
            hovered |= ImGui.IsItemHovered();

            if (hovered)
                MeasureState.HighlightIndex = i;
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (removeAt >= 0)
            MeasureState.Measurements.RemoveAt(removeAt);
    }
}
