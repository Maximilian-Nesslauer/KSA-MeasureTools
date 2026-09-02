using System.Reflection;
using HarmonyLib;
using Brutal.GlfwApi;
using KSA;

namespace MeasureTools.Core;

// Program.OnMouseButton takes a mouse latch on the first press and gives it back in
// a finally on the last release. A prefix returning false skips both. Consumed
// presses balance out by themselves (the bit is never set); the consumed right
// release does not, and a stuck latch pins Program's hovered viewport for the rest
// of the session.
internal static class GameReflection
{
    private static readonly FieldInfo? Program_heldMouseButtons =
        AccessTools.Field(typeof(Program), "_heldMouseButtons");

    private static readonly FieldInfo? Program_inputViewport =
        AccessTools.Field(typeof(Program), "_inputViewport");

    // Armed by BurnContextMenu.OnRightClick on the press, cleared only by TryOpen on
    // the release, so a consumed release leaves it armed over stale coordinates.
    private static readonly FieldInfo? BurnContextMenu_rightClickValid =
        AccessTools.Field(typeof(BurnContextMenu), "_rightClickValid");

    // Both fields or neither: half-releasing the latch is worse than not consuming.
    // Types checked so drift reads as drift instead of throwing mid-click.
    public static bool CanReleaseMouseLatch =>
        Program_heldMouseButtons?.FieldType == typeof(int)
        && Program_inputViewport != null
        && Program_inputViewport.FieldType.IsAssignableFrom(typeof(IGameViewport));

    // Mirrors the finally in Program.OnMouseButton.
    public static void ReleaseMouseLatch(GlfwMouseButton button)
    {
        if (!CanReleaseMouseLatch)
            return;

        int held = (int)Program_heldMouseButtons!.GetValue(null)!;
        held &= ~(1 << (int)button);
        Program_heldMouseButtons.SetValue(null, held);
        if (held == 0)
            Program_inputViewport!.SetValue(null, null);
    }

    // Stands in for the skipped TryOpen. Best effort: a stale burn latch self-heals
    // on the next right press stock does see.
    public static void ClearBurnRightClickLatch()
    {
        if (BurnContextMenu_rightClickValid?.FieldType == typeof(bool))
            BurnContextMenu_rightClickValid.SetValue(null, false);
    }

    public static void LogDriftIfUnavailable()
    {
        if (!CanReleaseMouseLatch)
            LogHelper.WarnOnce("reflect-mouse-latch",
                "[MeasureTools] Program._heldMouseButtons / _inputViewport not found or changed shape, "
                + "game version may have changed. Right-click cancel and pause will not suppress the "
                + "stock context menus.");
        if (BurnContextMenu_rightClickValid?.FieldType != typeof(bool))
            LogHelper.WarnOnce("reflect-burn-latch",
                "[MeasureTools] BurnContextMenu._rightClickValid not found or changed shape, game "
                + "version may have changed. A cancelled right-click can leave the stock burn menu "
                + "armed until the next right-click.");
    }
}
