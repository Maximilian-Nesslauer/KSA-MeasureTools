using System.Globalization;
using Brutal.Logging;
using Brutal.Numerics;
using MeasureTools.Core;

namespace MeasureTools.Features.Measure;

// The overlay color scheme, user-configurable through the window's Colors
// section so measurements stay readable against future vehicle paint jobs.
// Persisted as colors.cfg next to the mod assembly: one "Name=r,g,b,a" line per
// color, written on window close and unload, loaded at mod load. Main thread
// only (draw hook and window).
internal static class MeasureColors
{
    private const string FileName = "colors.cfg";

    // Declared BEFORE the mutable fields below: static initializers run in
    // textual order, so the defaults must already exist when the fields copy
    // them (the other way around every color starts as transparent black).
    // Warm red for lines and markers, yellow for the pending/preview family.
    private static readonly byte4 DefaultMeasure = new byte4(235, 100, 90, 235);
    private static readonly byte4 DefaultHighlight = new byte4(255, 180, 170, 255);
    private static readonly byte4 DefaultPending = new byte4(255, 220, 110, 245);
    private static readonly byte4 DefaultFeatureDot = new byte4(235, 100, 90, 200);
    private static readonly byte4 DefaultPlane = new byte4(150, 170, 200, 70);
    private static readonly byte4 DefaultLabelText = new byte4(236, 234, 222, 255);
    private static readonly byte4 DefaultLabelPlate = new byte4(8, 12, 16, 175);

    public static byte4 Measure = DefaultMeasure;
    public static byte4 Highlight = DefaultHighlight;
    public static byte4 Pending = DefaultPending;
    public static byte4 FeatureDot = DefaultFeatureDot;
    public static byte4 Plane = DefaultPlane;
    public static byte4 LabelText = DefaultLabelText;
    public static byte4 LabelPlate = DefaultLabelPlate;

    // The preview variants keep the pending hue at reduced alpha, so one picker
    // governs the whole pending/preview family.
    public static byte4 Preview => WithAlpha(Pending, 160);
    public static byte4 PreviewFaint => WithAlpha(Pending, 80);

    private static bool _dirty;

    public static void MarkDirty()
    {
        _dirty = true;
    }

    // Restores defaults without touching the dirty flag: the unload path resets
    // after saving, while the window's reset button marks dirty itself so the
    // restored defaults get persisted.
    public static void Reset()
    {
        Measure = DefaultMeasure;
        Highlight = DefaultHighlight;
        Pending = DefaultPending;
        FeatureDot = DefaultFeatureDot;
        Plane = DefaultPlane;
        LabelText = DefaultLabelText;
        LabelPlate = DefaultLabelPlate;
    }

    private static byte4 WithAlpha(byte4 color, byte alpha)
    {
        return new byte4(color.R, color.G, color.B, alpha);
    }

    public static float4 ToFloat4(byte4 color)
    {
        return new float4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    }

    public static byte4 FromFloat4(float4 color)
    {
        return new byte4(
            (byte)Math.Clamp(color.X * 255f + 0.5f, 0f, 255f),
            (byte)Math.Clamp(color.Y * 255f + 0.5f, 0f, 255f),
            (byte)Math.Clamp(color.Z * 255f + 0.5f, 0f, 255f),
            (byte)Math.Clamp(color.W * 255f + 0.5f, 0f, 255f));
    }

    private static string ConfigPath()
    {
        string directory = Path.GetDirectoryName(typeof(MeasureColors).Assembly.Location) ?? ".";
        return Path.Combine(directory, FileName);
    }

    public static void Load()
    {
        string path = ConfigPath();
        try
        {
            if (!File.Exists(path))
                return;
            foreach (string line in File.ReadAllLines(path))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0 || !TryParseColor(line[(separator + 1)..], out byte4 color))
                    continue;
                switch (line[..separator].Trim())
                {
                    case nameof(Measure): Measure = color; break;
                    case nameof(Highlight): Highlight = color; break;
                    case nameof(Pending): Pending = color; break;
                    case nameof(FeatureDot): FeatureDot = color; break;
                    case nameof(Plane): Plane = color; break;
                    case nameof(LabelText): LabelText = color; break;
                    case nameof(LabelPlate): LabelPlate = color; break;
                }
            }
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] Colors loaded from {path}.");
        }
        catch (Exception ex)
        {
            // File IO can legitimately fail (permissions, sync tools); defaults
            // keep the mod fully usable.
            LogHelper.WarnOnce("colors-load", $"[MeasureTools] Could not load {path}, using default colors: {ex.Message}");
        }
    }

    public static void SaveIfDirty()
    {
        if (!_dirty)
            return;
        string path = ConfigPath();
        try
        {
            File.WriteAllLines(path, new[]
            {
                Format(nameof(Measure), Measure),
                Format(nameof(Highlight), Highlight),
                Format(nameof(Pending), Pending),
                Format(nameof(FeatureDot), FeatureDot),
                Format(nameof(Plane), Plane),
                Format(nameof(LabelText), LabelText),
                Format(nameof(LabelPlate), LabelPlate),
            });
            _dirty = false;
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] Colors saved to {path}.");
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("colors-save", $"[MeasureTools] Could not save {path}: {ex.Message}");
        }
    }

    private static string Format(string name, byte4 color)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{name}={color.R},{color.G},{color.B},{color.A}");
    }

    private static bool TryParseColor(string text, out byte4 color)
    {
        color = default;
        string[] parts = text.Split(',');
        if (parts.Length != 4)
            return false;
        Span<byte> channels = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (!byte.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out channels[i]))
                return false;
        }
        color = new byte4(channels[0], channels[1], channels[2], channels[3]);
        return true;
    }
}
