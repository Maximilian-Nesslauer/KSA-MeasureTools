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

    // Surface mode: great-circle distance over the body's mean-radius sphere, the
    // headline number. Haversine in the atan2 form, stable at all separations.
    public double SurfaceDistanceMeters()
    {
        if (Anchors[0].Body is not Celestial body)
            return double.NaN;
        return GreatCircleMeters(body, Anchors[0].Latitude, Anchors[0].Longitude, Anchors[1].Latitude, Anchors[1].Longitude);
    }

    public static double GreatCircleMeters(Celestial body, double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
    {
        double phi1 = lat1Deg * (Math.PI / 180.0);
        double phi2 = lat2Deg * (Math.PI / 180.0);
        double sinDPhi = Math.Sin((phi2 - phi1) * 0.5);
        double sinDLambda = Math.Sin((lon2Deg - lon1Deg) * (Math.PI / 180.0) * 0.5);
        double a = sinDPhi * sinDPhi + Math.Cos(phi1) * Math.Cos(phi2) * sinDLambda * sinDLambda;
        double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1.0 - a)));
        return body.MeanRadius * c;
    }

    // Surface mode: initial bearing (forward azimuth) from the first pin to the
    // second, 0..360 degrees, 0 = north.
    public double BearingDegrees()
    {
        double phi1 = Anchors[0].Latitude * (Math.PI / 180.0);
        double phi2 = Anchors[1].Latitude * (Math.PI / 180.0);
        double dLambda = (Anchors[1].Longitude - Anchors[0].Longitude) * (Math.PI / 180.0);
        double y = Math.Sin(dLambda) * Math.Cos(phi2);
        double x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda);
        double deg = Math.Atan2(y, x) * (180.0 / Math.PI);
        return (deg + 360.0) % 360.0;
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
