using System.Globalization;
using Brutal.Numerics;
using KSA;

namespace MeasureTools.Features.Measure;

internal enum AnchorKind
{
    // The center of a snapped body; follows the body.
    BodyCenter,
    // A point on an orbit line, stored as a CCE offset from the orbit's parent;
    // stays on the orbit geometry as the parent moves.
    OrbitPoint,
    // A point on a body's sphere (limb snap), stored as a CCE offset of length
    // MeanRadius. CCE does not rotate with the body, so the point keeps facing the
    // direction it was placed toward instead of tracking a surface feature
    // (SurfacePin is the rotating variant).
    SurfaceSnap,
    // A lat/lon pin on a celestial's surface (surface mode). Resolves through the
    // body-fixed frame, so it tracks the body's rotation like a ground marker.
    SurfacePin,
    // A free point on the construction plane, stored as a CCE offset from the
    // reference body so it tracks that body instead of drifting off in absolute space.
    FreePoint,
}

// A measurement endpoint, anchored in a body's frame rather than frozen in ECL.
internal sealed class Anchor
{
    public AnchorKind Kind;

    // BodyCenter: the snapped body. FreePoint: the reference body whose CCE frame
    // holds OffsetCce. Null for OrbitPoint, which uses OrbitParent instead.
    public Astronomical? Body;

    // OrbitPoint: the parent body of the picked orbit.
    public IParentBody? OrbitParent;

    public double3 OffsetCce;

    // SurfacePin only, in degrees, body-fixed frame.
    public double Latitude;
    public double Longitude;

    public string Label = "";

    public double3 ResolveEcl()
    {
        return Kind switch
        {
            AnchorKind.BodyCenter => Body!.GetPositionEcl(),
            AnchorKind.OrbitPoint => OrbitParent!.GetPositionEclFromCce(OffsetCce),
            AnchorKind.SurfacePin => ((Celestial)Body!).GetPositionEclFromLatLon(Latitude, Longitude),
            _ => Body!.GetPositionEclFromCce(OffsetCce),
        };
    }

    // The anchor dies with its body (e.g. a deleted vehicle). Identity check via the
    // system lookup so a same-named replacement does not silently re-anchor.
    public bool IsValid(CelestialSystem system)
    {
        Astronomical? anchorBody = Kind == AnchorKind.OrbitPoint ? OrbitParent as Astronomical : Body;
        if (anchorBody == null)
            return false;
        return ReferenceEquals(system.Get(anchorBody.Id), anchorBody);
    }

    public static Anchor AtBody(Astronomical body)
    {
        return new Anchor { Kind = AnchorKind.BodyCenter, Body = body, Label = body.Id };
    }

    public static Anchor OnOrbit(IParentBody parent, double3 offsetCce, string orbiterId)
    {
        return new Anchor
        {
            Kind = AnchorKind.OrbitPoint,
            OrbitParent = parent,
            OffsetCce = offsetCce,
            Label = orbiterId + " orbit",
        };
    }

    public static Anchor AtSurface(Astronomical body, double3 offsetCce)
    {
        return new Anchor
        {
            Kind = AnchorKind.SurfaceSnap,
            Body = body,
            OffsetCce = offsetCce,
            Label = body.Id + " surface",
        };
    }

    public static Anchor PinOnSurface(Celestial body, double latitudeDeg, double longitudeDeg)
    {
        return new Anchor
        {
            Kind = AnchorKind.SurfacePin,
            Body = body,
            Latitude = latitudeDeg,
            Longitude = longitudeDeg,
            Label = body.Id + " " + FormatLatLon(latitudeDeg, longitudeDeg),
        };
    }

    public static Anchor Free(Astronomical refBody, double3 offsetCce)
    {
        return new Anchor
        {
            Kind = AnchorKind.FreePoint,
            Body = refBody,
            OffsetCce = offsetCce,
            Label = "free (" + refBody.Id + ")",
        };
    }

    private static string FormatLatLon(double latDeg, double lonDeg)
    {
        string ns = latDeg >= 0.0 ? "N" : "S";
        string ew = lonDeg >= 0.0 ? "E" : "W";
        return Math.Abs(latDeg).ToString("0.00", CultureInfo.InvariantCulture) + ns + " "
            + Math.Abs(lonDeg).ToString("0.00", CultureInfo.InvariantCulture) + ew;
    }
}
