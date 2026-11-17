using Brutal.Numerics;

namespace MeasureTools.Features.Measure;

internal static class MeasurementGeometry
{
    public static double3? TransformNormal(double3 normal, double4x4 transform)
    {
        // Only the linear part contributes to a normal, including under a reflection.
        transform.M41 = transform.M42 = transform.M43 = 0.0;
        if (!double4x4.Invert(transform, out double4x4 inverse))
            return null;
        return Normalize(double3.TransformNormal(normal, double4x4.Transpose(inverse)));
    }

    public static bool PreservesCircle(double3 normal, double4x4 transform)
    {
        double3? unitNormal = Normalize(normal);
        if (unitNormal == null || TransformNormal(normal, transform) == null)
            return false;
        double3 n = unitNormal.Value;
        double3 seed = Math.Abs(n.X) < 0.9 ? new double3(1.0, 0.0, 0.0) : new double3(0.0, 1.0, 0.0);
        double3 u = double3.Cross(n, seed);
        u /= u.Length();
        double3 v = double3.Cross(n, u);
        u = double3.TransformNormal(u, transform);
        v = double3.TransformNormal(v, transform);
        double uu = double3.Dot(u, u);
        double vv = double3.Dot(v, v);
        double uv = double3.Dot(u, v);
        if (!(uu > 0.0) || !(vv > 0.0) || !double.IsFinite(uu) || !double.IsFinite(vv) || !double.IsFinite(uv))
            return false;

        // The plane metric must be a scalar identity, with a relative allowance for roundoff.
        double tolerance = 1e-8 * Math.Max(uu, vv);
        return Math.Abs(uu - vv) <= tolerance && Math.Abs(uv) <= tolerance;
    }

    private static double3? Normalize(double3 vector)
    {
        double length = vector.Length();
        return length > 0.0 && double.IsFinite(length) ? vector / length : null;
    }
}
