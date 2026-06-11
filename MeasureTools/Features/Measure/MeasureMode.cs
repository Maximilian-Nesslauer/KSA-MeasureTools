namespace MeasureTools.Features.Measure;

internal enum MeasureMode
{
    // Two points, straight-line distance.
    Ruler,
    // Three points (arm, apex, arm), true 3D angle at the apex.
    Angle,
    // Two points pinned to one body's surface: great-circle distance, chord,
    // and initial bearing.
    Surface,
}
