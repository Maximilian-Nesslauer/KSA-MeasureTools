namespace MeasureTools.Core;

// Per-feature debug toggles. In DEBUG builds all flags default to true;
// set individual flags to false to reduce log noise while debugging a
// specific feature. In Release builds everything is off and the JIT
// eliminates dead branches.
internal static class DebugConfig
{
#if DEBUG
    // Add one field per feature, e.g.:
    // public static bool MyFeature = true;
    public static bool Performance = true;
#else
    // Mirror the same fields, all false:
    // public static bool MyFeature = false;
    public static bool Performance = false;
#endif

    public static bool Any => Performance;
    // Update Any to OR all feature flags together, e.g.:
    // public static bool Any => MyFeature || Performance;
}
