using System.Reflection;
using Brutal.Logging;
using HarmonyLib;

namespace MeasureTools.Core;

// Centralized registry of all reflection targets for game internals.
// Fields are resolved once at assembly load time. Per-feature validation
// methods check that all targets resolved, enabling graceful per-feature
// degradation when game versions change.
internal static class GameReflection
{
    #region MyFeature

    // public static readonly FieldInfo? SomeClass_someField =
    //     AccessTools.Field(typeof(SomeClass), "_someField");

    #endregion

    #region Validation

    // public static bool ValidateMyFeature()
    // {
    //     var targets = new (string name, object? target)[]
    //     {
    //         ("SomeClass._someField", SomeClass_someField),
    //     };
    //     return ValidateTargets("MyFeature", targets);
    // }

    private static bool ValidateTargets(string feature, (string name, object? target)[] targets)
    {
        bool allOk = true;
        foreach (var (name, target) in targets)
        {
            if (target == null)
            {
                DefaultCategory.Log.Error(
                    $"[MeasureTools] {feature}: {name} not found - game version may have changed.");
                allOk = false;
            }
        }
        return allOk;
    }

    #endregion
}
