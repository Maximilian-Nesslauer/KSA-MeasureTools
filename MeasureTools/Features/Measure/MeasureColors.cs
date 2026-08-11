using System.Globalization;
using Brutal.Logging;
using Brutal.Numerics;
using MeasureTools.Core;

namespace MeasureTools.Features.Measure;

// A named snapshot of the overlay color scheme; the user switches between
// palettes to keep measurements readable against differently colored vehicles.
internal sealed class ColorPalette
{
    public string Name = "Palette";
    public byte4 Measure;
    public byte4 Highlight;
    public byte4 Pending;
    public byte4 FeatureDot;
    public byte4 Plane;
    public byte4 LabelText;
    public byte4 LabelPlate;

    public ColorPalette Clone(string name)
    {
        return new ColorPalette
        {
            Name = name,
            Measure = Measure,
            Highlight = Highlight,
            Pending = Pending,
            FeatureDot = FeatureDot,
            Plane = Plane,
            LabelText = LabelText,
            LabelPlate = LabelPlate,
        };
    }
}

// The palette collection and the active selection the overlay draws with.
// Persisted as colors.cfg next to the mod assembly: an "active=index" line,
// then one [Name] section with Name=r,g,b,a lines per palette. Written on
// window close and unload, loaded at mod load; a pre-palette flat file (color
// lines without any section) migrates into a "Custom" palette ahead of the
// shipped ones. Main thread only (draw hook and window).
internal static class MeasureColors
{
    private const string FileName = "colors.cfg";

    public static readonly List<ColorPalette> Palettes = new();

    private static int _activeIndex;
    private static bool _dirty;

    public static int ActiveIndex => _activeIndex;

    public static ColorPalette Active => Palettes[_activeIndex];

    // The draw code reads the scheme through these, unaware of palettes. The
    // preview variants keep the pending hue at reduced alpha, so one picker
    // governs the whole pending/preview family.
    public static byte4 Measure => Active.Measure;
    public static byte4 Highlight => Active.Highlight;
    public static byte4 Pending => Active.Pending;
    public static byte4 FeatureDot => Active.FeatureDot;
    public static byte4 Plane => Active.Plane;
    public static byte4 LabelText => Active.LabelText;
    public static byte4 LabelPlate => Active.LabelPlate;
    public static byte4 Preview => WithAlpha(Active.Pending, 160);
    public static byte4 PreviewFaint => WithAlpha(Active.Pending, 80);

    static MeasureColors()
    {
        RestoreDefaultPalettes();
    }

    // The four shipped palettes: warm red for green or neutral craft, the green
    // scheme for red craft, blue for warm paint jobs, white-on-orange for busy
    // or dark scenes. All of them stay user-editable like any custom palette.
    private static ColorPalette[] CreateDefaultPalettes()
    {
        var shared = new
        {
            Pending = new byte4(255, 220, 110, 245),
            Plane = new byte4(150, 170, 200, 70),
            LabelText = new byte4(236, 234, 222, 255),
            LabelPlate = new byte4(8, 12, 16, 175),
        };
        return new[]
        {
            new ColorPalette
            {
                Name = "Red",
                Measure = new byte4(235, 100, 90, 235),
                Highlight = new byte4(255, 180, 170, 255),
                FeatureDot = new byte4(235, 100, 90, 200),
                Pending = shared.Pending,
                Plane = shared.Plane,
                LabelText = shared.LabelText,
                LabelPlate = shared.LabelPlate,
            },
            new ColorPalette
            {
                Name = "Green",
                Measure = new byte4(120, 220, 160, 235),
                Highlight = new byte4(215, 255, 235, 255),
                FeatureDot = new byte4(120, 220, 160, 200),
                Pending = shared.Pending,
                Plane = shared.Plane,
                LabelText = shared.LabelText,
                LabelPlate = shared.LabelPlate,
            },
            new ColorPalette
            {
                Name = "Blue",
                Measure = new byte4(90, 170, 255, 235),
                Highlight = new byte4(180, 220, 255, 255),
                FeatureDot = new byte4(90, 170, 255, 200),
                Pending = shared.Pending,
                Plane = shared.Plane,
                LabelText = shared.LabelText,
                LabelPlate = shared.LabelPlate,
            },
            new ColorPalette
            {
                Name = "Contrast",
                Measure = new byte4(240, 240, 240, 235),
                Highlight = new byte4(255, 255, 255, 255),
                FeatureDot = new byte4(240, 240, 240, 200),
                Pending = new byte4(255, 150, 60, 245),
                Plane = shared.Plane,
                LabelText = shared.LabelText,
                LabelPlate = new byte4(0, 0, 0, 200),
            },
        };
    }

    public static void MarkDirty()
    {
        _dirty = true;
    }

    public static void SetActive(int index)
    {
        if (index < 0 || index >= Palettes.Count || index == _activeIndex)
            return;
        _activeIndex = index;
        _dirty = true;
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug($"[MeasureTools] Palette switched to '{Active.Name}'.");
    }

    // The New button: clone the active palette under a fresh name and select it.
    public static void AddPalette()
    {
        int n = Palettes.Count + 1;
        string name;
        do
        {
            name = "Palette " + n;
            n++;
        } while (Palettes.Exists(p => p.Name == name));
        Palettes.Add(Active.Clone(name));
        _activeIndex = Palettes.Count - 1;
        _dirty = true;
    }

    // The Delete button; the last remaining palette stays (the overlay always
    // needs an active scheme).
    public static void DeleteActive()
    {
        if (Palettes.Count <= 1)
            return;
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug($"[MeasureTools] Palette '{Active.Name}' deleted.");
        Palettes.RemoveAt(_activeIndex);
        if (_activeIndex >= Palettes.Count)
            _activeIndex = Palettes.Count - 1;
        _dirty = true;
    }

    // Re-adds the shipped palettes and restores their colors, matched by name;
    // custom palettes are untouched. Also the initial population, hence the
    // static constructor call.
    public static void RestoreDefaultPalettes()
    {
        foreach (ColorPalette shipped in CreateDefaultPalettes())
        {
            int existing = Palettes.FindIndex(p => p.Name == shipped.Name);
            if (existing >= 0)
                Palettes[existing] = shipped;
            else
                Palettes.Add(shipped);
        }
        _dirty = true;
    }

    // Drops all custom palettes and the selection, back to the shipped set;
    // used on unload after saving so a reload starts clean. Clears the dirty
    // flag, since this state is not meant to be persisted.
    public static void Reset()
    {
        Palettes.Clear();
        _activeIndex = 0;
        RestoreDefaultPalettes();
        _dirty = false;
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
            var loaded = new List<ColorPalette>();
            ColorPalette? current = null;
            ColorPalette? flat = null;
            int active = 0;
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    string name = line[1..^1].Trim();
                    current = new ColorPalette { Name = name.Length > 0 ? name : "Palette" };
                    loaded.Add(current);
                    continue;
                }
                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;
                string key = line[..separator].Trim();
                string value = line[(separator + 1)..];
                if (key == "active")
                {
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out active);
                    continue;
                }
                if (!TryParseColor(value, out byte4 color))
                    continue;
                // Color lines before any [section] come from the pre-palette
                // flat format and migrate into a "Custom" palette.
                ColorPalette target = current ?? (flat ??= new ColorPalette { Name = "Custom" });
                switch (key)
                {
                    case nameof(ColorPalette.Measure): target.Measure = color; break;
                    case nameof(ColorPalette.Highlight): target.Highlight = color; break;
                    case nameof(ColorPalette.Pending): target.Pending = color; break;
                    case nameof(ColorPalette.FeatureDot): target.FeatureDot = color; break;
                    case nameof(ColorPalette.Plane): target.Plane = color; break;
                    case nameof(ColorPalette.LabelText): target.LabelText = color; break;
                    case nameof(ColorPalette.LabelPlate): target.LabelPlate = color; break;
                }
            }
            if (flat != null)
                loaded.Insert(0, flat);
            if (loaded.Count == 0)
                return;
            Palettes.Clear();
            Palettes.AddRange(loaded);
            if (flat != null)
            {
                // Migration keeps the user's tweaked colors active and adds the
                // shipped palettes alongside.
                RestoreDefaultPalettes();
                _dirty = true;
                active = 0;
            }
            else
            {
                // A clean load matches the file; without this the dirty flag
                // from the static constructor forces a pointless rewrite on the
                // first close of every session.
                _dirty = false;
            }
            _activeIndex = Math.Clamp(active, 0, Palettes.Count - 1);
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] {Palettes.Count} palette(s) loaded from {path}, active '{Active.Name}'.");
        }
        catch (Exception ex)
        {
            // File IO can legitimately fail (permissions, sync tools); the
            // shipped palettes keep the mod fully usable. Clearing the dirty flag
            // the static constructor set is what stops the unload save from
            // overwriting a file we could not read with those defaults.
            _dirty = false;
            LogHelper.WarnOnce("colors-load", $"[MeasureTools] Could not load {path}, keeping default palettes in memory and leaving {FileName} untouched: {ex.Message}");
        }
    }

    public static void SaveIfDirty()
    {
        if (!_dirty)
            return;
        string path = ConfigPath();
        try
        {
            var lines = new List<string>(1 + Palettes.Count * 8)
            {
                "active=" + _activeIndex.ToString(CultureInfo.InvariantCulture),
            };
            foreach (ColorPalette palette in Palettes)
            {
                lines.Add("[" + palette.Name + "]");
                lines.Add(Format(nameof(ColorPalette.Measure), palette.Measure));
                lines.Add(Format(nameof(ColorPalette.Highlight), palette.Highlight));
                lines.Add(Format(nameof(ColorPalette.Pending), palette.Pending));
                lines.Add(Format(nameof(ColorPalette.FeatureDot), palette.FeatureDot));
                lines.Add(Format(nameof(ColorPalette.Plane), palette.Plane));
                lines.Add(Format(nameof(ColorPalette.LabelText), palette.LabelText));
                lines.Add(Format(nameof(ColorPalette.LabelPlate), palette.LabelPlate));
            }
            File.WriteAllLines(path, lines);
            _dirty = false;
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] {Palettes.Count} palette(s) saved to {path}.");
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
