using KSA;

namespace MeasureTools.Features.Measure;

// The tool runs in the main viewport only, and this is the single place that says
// so. ImGuiHelper.GetOverlayDrawList gives the persistent background list to no
// other viewport (the rest draw their world UI inside their own ImGui window, long
// closed by the time [StarMapAfterGui] runs), and they are built without
// ViewportOptionFlags.AllowSelection, so stock does no part hover there either.
internal static class MeasureViewport
{
    public static bool IsHost(IViewport? viewport)
    {
        return viewport != null && viewport.IsMain();
    }

    // Scanned rather than read off Program.MainViewport, which throws before the
    // registry has one.
    public static bool TryGetActive(out IGameViewport viewport)
    {
        ReadOnlySpan<IGameViewport> views = ViewportRegistry.GameViews;
        for (int i = 0; i < views.Length; i++)
        {
            if (views[i].IsMain())
            {
                viewport = views[i];
                return true;
            }
        }
        viewport = null!;
        return false;
    }
}
