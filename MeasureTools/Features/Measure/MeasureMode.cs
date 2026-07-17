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
    // One click on a circular part edge (tank rim): diameter, radius,
    // circumference from a fitted circle feature.
    Circle,
    // Two clicks on part surfaces: angle between the two surface normals
    // (engine cant, fin angles).
    FaceAngle,
}
