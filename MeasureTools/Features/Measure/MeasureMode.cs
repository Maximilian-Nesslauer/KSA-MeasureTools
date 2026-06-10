namespace MeasureTools.Features.Measure;

internal enum MeasureMode
{
    // Two points, straight-line distance.
    Ruler,
    // Three points (arm, apex, arm), true 3D angle at the apex.
    Angle,
}
