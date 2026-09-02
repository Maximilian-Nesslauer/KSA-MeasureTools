namespace MeasureTools.Core;

// Per-feature debug toggles. In DEBUG builds all flags default to true; set
// individual flags to false to reduce log noise while debugging a specific feature.
// In Release builds everything defaults off. The flags stay mutable (not const) so
// they can be toggled at runtime, so the guarded branches are still evaluated in
// Release rather than compiled out.
internal static class DebugConfig
{
#if DEBUG
    // Placement, picking and lifecycle events of the measure tool.
    public static bool Measure = true;
    public static bool Performance = true;
#else
    public static bool Measure = false;
    public static bool Performance = false;
#endif

    // The DEBUG window writes these at runtime.
    public static void Reset()
    {
#if DEBUG
        Measure = true;
        Performance = true;
#else
        Measure = false;
        Performance = false;
#endif
    }
}
