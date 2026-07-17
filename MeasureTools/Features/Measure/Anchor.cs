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
    // A point fixed to a vehicle part, stored in the part's local (asmb) frame so it
    // tracks the vehicle's motion and rotation, and the part's own motion inside the
    // vehicle (deploy animations, solar trackers). Gimbal deflection is render-only
    // in the game and is deliberately not tracked.
    PartPoint,
    // PartPoint's editor twin: the part lives in the vehicle editor's editing space
    // (Program.Editor.EditingSpace), not in a Vehicle. Tracks live part motion while
    // building, since part transforms recompute uncached while the editor is open.
    EditorPartPoint,
    // A free point while the editor is open, stored as an ECL-axes offset from the
    // editing space origin (the space never moves or rotates during a session).
    EditorFreePoint,
}

// A measurement endpoint, anchored in a body's frame rather than frozen in ECL.
internal sealed class Anchor
{
    // Built only through the factory methods below and never mutated afterward, so
    // every field is init-only.
    public AnchorKind Kind { get; init; }

    // BodyCenter: the snapped body. FreePoint: the reference body whose CCE frame
    // holds OffsetCce. Null for OrbitPoint, which uses OrbitParent instead.
    public Astronomical? Body { get; init; }

    // OrbitPoint: the parent body of the picked orbit.
    public IParentBody? OrbitParent { get; init; }

    // PartPoint / EditorPartPoint: the part whose local asmb frame holds
    // OffsetPartAsmb; the mesh-owning subpart for vertex/surface picks, the full
    // part for center/connector picks. Body holds the owning Vehicle for
    // PartPoint; editor anchors have no Body and reach the editing space through
    // Program.Editor instead of storing it, so a stale space cannot be resolved.
    public Part? Part { get; init; }

    // EditorFreePoint reuses this as the ECL-axes offset from the editing space.
    public double3 OffsetCce { get; init; }

    // PartPoint / EditorPartPoint only: the anchored point in Part's local asmb frame.
    public double3 OffsetPartAsmb { get; init; }

    // SurfacePin only, in degrees, body-fixed frame.
    public double Latitude { get; init; }
    public double Longitude { get; init; }

    public string Label { get; init; } = "";

    public double3 ResolveEcl()
    {
        return Kind switch
        {
            AnchorKind.BodyCenter => Body!.GetPositionEcl(),
            AnchorKind.OrbitPoint => OrbitParent!.GetPositionEclFromCce(OffsetCce),
            AnchorKind.SurfacePin => ((Celestial)Body!).GetPositionEclFromLatLon(Latitude, Longitude),
            AnchorKind.PartPoint => ResolvePartPointEcl(),
            AnchorKind.EditorPartPoint => ResolveEditorPartPointEcl(),
            AnchorKind.EditorFreePoint => Program.Editor is VehicleEditor editor
                ? editor.EditingSpace.PositionEcl + OffsetCce
                : InvalidPosition(),
            _ => Body!.GetPositionEclFromCce(OffsetCce),
        };
    }

    // Camera-free mirror of the game's render transform: Vehicle.GetMatrixAsmb2Ego
    // maps a vehicle-asmb point to ego as (posAsmb - CenterOfMassAsmb) rotated by
    // Asmb2Ego plus the vehicle's own position, and ego axes are ECL axes
    // (Camera.EgoToEcl is a pure translation), so the same rotation applied about
    // GetPositionEcl() yields the ECL position.
    private double3 ResolvePartPointEcl()
    {
        var vehicle = (Vehicle)Body!;
        double3 posVehicleAsmb = OffsetPartAsmb.Transform(Part!.MatrixAsmb2VehicleAsmb);
        return vehicle.GetPositionEcl()
            + (posVehicleAsmb - vehicle.CenterOfMassAsmb).Transform(vehicle.Asmb2Ego);
    }

    // Editor twin of ResolvePartPointEcl, mirroring VehicleEditingSpace.GetMatrixAsmb2Ego:
    // the editing space rotates by Asmb2Ecl about PositionEcl and, unlike a Vehicle,
    // has no center-of-mass term.
    private double3 ResolveEditorPartPointEcl()
    {
        // The overlay's throttled preview cache can hold an editor anchor for a
        // few frames past editor teardown (Prune only clears pending points and
        // settled measurements); NaN routes it into the existing Valid() filters
        // instead of throwing.
        VehicleEditor? editor = Program.Editor;
        if (editor == null)
            return InvalidPosition();
        double3 posVehicleAsmb = OffsetPartAsmb.Transform(Part!.MatrixAsmb2VehicleAsmb);
        return editor.EditingSpace.PositionEcl + posVehicleAsmb.Transform(editor.EditingSpace.Asmb2Ecl);
    }

    private static double3 InvalidPosition()
    {
        return new double3(double.NaN, double.NaN, double.NaN);
    }

    // The anchor dies with its body (e.g. a deleted vehicle). Identity check via the
    // system lookup so a same-named replacement does not silently re-anchor.
    public bool IsValid(CelestialSystem system)
    {
        if (Kind == AnchorKind.EditorPartPoint || Kind == AnchorKind.EditorFreePoint)
            return IsValidEditor();
        Astronomical? anchorBody = Kind == AnchorKind.OrbitPoint ? OrbitParent as Astronomical : Body;
        if (anchorBody == null)
            return false;
        if (!ReferenceEquals(system.Get(anchorBody.Id), anchorBody))
            return false;
        if (Kind != AnchorKind.PartPoint)
            return true;
        // A part anchor also dies with its part: decoupling moves the part into a
        // new debris vehicle (the stored vehicle's tree no longer finds it),
        // destruction removes it entirely. Subparts never migrate between full
        // parts, so the full part's presence covers a stored subpart too.
        Part? fullPart = Part?.FullPart;
        return fullPart != null && Body is Vehicle vehicle
            && ReferenceEquals(vehicle.Parts?.Find(fullPart.InstanceId), fullPart);
    }

    // Editor anchors die when the editor closes (Prune drops them a frame after
    // exit) and, for part anchors, when the part leaves the edited craft. A part
    // being carried in the hand lives in an unattached tree, still a valid home.
    private bool IsValidEditor()
    {
        VehicleEditor? editor = Program.Editor;
        if (editor == null)
            return false;
        if (Kind == AnchorKind.EditorFreePoint)
            return true;
        Part? fullPart = Part?.FullPart;
        if (fullPart == null)
            return false;
        if (ReferenceEquals(editor.EditingSpace.Parts?.Find(fullPart.InstanceId), fullPart))
            return true;
        foreach (PartTree tree in editor.UnattachedPartTrees)
        {
            if (ReferenceEquals(tree.Find(fullPart.InstanceId), fullPart))
                return true;
        }
        return false;
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

    // For picks whose position is already known in the part's local asmb frame
    // (mesh raycast hits, mesh vertices, the bounding-box center).
    public static Anchor AtPartLocal(Vehicle vehicle, Part part, double3 offsetPartAsmb, string label)
    {
        return new Anchor
        {
            Kind = AnchorKind.PartPoint,
            Body = vehicle,
            Part = part,
            OffsetPartAsmb = offsetPartAsmb,
            Label = label,
        };
    }

    // For picks known in the vehicle-asmb frame (connector positions). Inverting
    // the part matrix once here round-trips exactly through ResolveEcl, and
    // sidesteps Connector.PositionVehicleAsmb applying rotation and translation
    // but not the part scale.
    public static Anchor AtPartVehicleAsmb(Vehicle vehicle, Part part, double3 posVehicleAsmb, string label)
    {
        double4x4.Invert(part.MatrixAsmb2VehicleAsmb, out double4x4 inverse);
        return AtPartLocal(vehicle, part, posVehicleAsmb.Transform(inverse), label);
    }

    // Editor twins of the two factories above; no Body, the editing space is
    // reached through Program.Editor at resolve time.
    public static Anchor AtEditorPartLocal(Part part, double3 offsetPartAsmb, string label)
    {
        return new Anchor
        {
            Kind = AnchorKind.EditorPartPoint,
            Part = part,
            OffsetPartAsmb = offsetPartAsmb,
            Label = label,
        };
    }

    public static Anchor AtEditorPartVehicleAsmb(Part part, double3 posVehicleAsmb, string label)
    {
        double4x4.Invert(part.MatrixAsmb2VehicleAsmb, out double4x4 inverse);
        return AtEditorPartLocal(part, posVehicleAsmb.Transform(inverse), label);
    }

    public static Anchor EditorFree(double3 offsetFromSpaceEcl)
    {
        return new Anchor
        {
            Kind = AnchorKind.EditorFreePoint,
            OffsetCce = offsetFromSpaceEcl,
            Label = "free (editor)",
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
