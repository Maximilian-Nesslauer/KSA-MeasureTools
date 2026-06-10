using System;
using Brutal.Numerics;
using KSA;

namespace MeasureTools.Features.Measure;

// A completed measurement: two anchors for a ruler, three (arm, apex, arm) for an
// angle. Values are computed live from the anchors so they stay correct as the
// anchored bodies move.
internal sealed class Measurement
{
    public MeasureMode Mode;

    public Anchor[] Anchors = Array.Empty<Anchor>();

    public bool IsValid(CelestialSystem system)
    {
        foreach (Anchor anchor in Anchors)
        {
            if (!anchor.IsValid(system))
                return false;
        }
        return true;
    }

    public double DistanceMeters()
    {
        return (Anchors[0].ResolveEcl() - Anchors[1].ResolveEcl()).Length();
    }

    // True 3D angle at the apex (Anchors[1]), independent of the camera projection.
    public double AngleRadians()
    {
        return AngleBetween(Anchors[1].ResolveEcl(), Anchors[0].ResolveEcl(), Anchors[2].ResolveEcl());
    }

    // NaN when an arm coincides with the apex (zero-length arm).
    public static double AngleBetween(double3 apexEcl, double3 armAEcl, double3 armBEcl)
    {
        double3 u = armAEcl - apexEcl;
        double3 v = armBEcl - apexEcl;
        double lu = u.Length();
        double lv = v.Length();
        if (!(lu > 0.0) || !(lv > 0.0))
            return double.NaN;
        double c = double3.Dot(u, v) / (lu * lv);
        return Math.Acos(Math.Clamp(c, -1.0, 1.0));
    }
}
