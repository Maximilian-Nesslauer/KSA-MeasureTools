namespace MeasureTools.Core;

// Per-feature debug toggles. In DEBUG builds all flags default to true;
// set individual flags to false to reduce log noise while debugging a
// specific feature. In Release builds everything is off and the JIT
// eliminates dead branches.
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

    public static bool Any => Measure || Performance;
}
