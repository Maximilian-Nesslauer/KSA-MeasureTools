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

    // Part anchors picked on a surface (and circle centers) carry the surface or
    // plane normal in the part's local frame, the datum for the face-angle mode
    // and the circle overlay.
    public double3? NormalPartAsmb { get; init; }

    // SurfacePin only, in degrees, body-fixed frame.
    public double Latitude { get; init; }
    public double Longitude { get; init; }

    public string Label { get; init; } = "";

    // PartPoint / EditorPartPoint: the owner-free part description ("TankA node
    // 'top'", "TankA vertex", ...). Flight anchors derive Label by prefixing the
    // owning vehicle's id, editor anchors use it as is, and Rehome rebuilds the
    // label from it when the part changes owner (decoupling, docking, editor
    // transitions).
    public string PartLabel { get; init; } = "";

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

    // The stored part-local normal rotated into ECL axes. Rotation-only chain
    // (part scale is not applied), mirroring how the game transforms hit normals
    // (VehicleEditor.TryComputeAttachedMountQuat rotates by Asmb2VehicleAsmb).
    // Null when the anchor carries no normal or its owner is stale.
    public double3? ResolveNormalEcl()
    {
        if (NormalPartAsmb == null || Part == null)
            return null;
        double3 normalVehicleAsmb = NormalPartAsmb.Value.Transform(Part.Asmb2VehicleAsmb);
        if (Kind == AnchorKind.EditorPartPoint)
        {
            VehicleEditor? editor = Program.Editor;
            if (editor == null)
                return null;
            return normalVehicleAsmb.Transform(editor.EditingSpace.Asmb2Ecl);
        }
        if (Body is not Vehicle vehicle)
            return null;
        return normalVehicleAsmb.Transform(vehicle.Asmb2Ego);
    }

    // A false result marks the anchor stale, not dead: for part anchors Prune
    // tries Rehome first (staging, docking and editor transitions just move the
    // part to a new owner), and only an unrecoverable anchor drops state. For
    // body-bound kinds a stale body means the anchor is gone for good. Identity
    // check via the system lookup so a same-named replacement does not silently
    // re-anchor.
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
        // A part anchor turns stale when its part leaves the stored vehicle's
        // tree (decoupled, docked away, grabbed in the editor); Rehome then finds
        // the new owner. Subparts never migrate between full parts, so the full
        // part's presence covers a stored subpart too.
        Part? fullPart = Part?.FullPart;
        return fullPart != null && Body is Vehicle vehicle
            && ReferenceEquals(vehicle.Parts?.Find(fullPart.InstanceId), fullPart);
    }

    // Editor anchors turn stale when the editor closes or the part leaves the
    // editor trees; part anchors are then re-homed (to the original or launched
    // vehicle), while editor free points drop for good. A part being carried in
    // the hand lives in an unattached tree, still a valid home.
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
    // (mesh raycast hits, mesh vertices, the bounding-box center). partLabel is
    // the owner-free description; the vehicle id prefix is derived here so Rehome
    // can rebuild it for a new owner.
    public static Anchor AtPartLocal(Vehicle vehicle, Part part, double3 offsetPartAsmb, string partLabel, double3? normalPartAsmb = null)
    {
        return new Anchor
        {
            Kind = AnchorKind.PartPoint,
            Body = vehicle,
            Part = part,
            OffsetPartAsmb = offsetPartAsmb,
            NormalPartAsmb = normalPartAsmb,
            Label = vehicle.Id + " " + partLabel,
            PartLabel = partLabel,
        };
    }

    // For picks known in the vehicle-asmb frame (connector positions). Inverting
    // the part matrix once here round-trips exactly through ResolveEcl, and
    // sidesteps Connector.PositionVehicleAsmb applying rotation and translation
    // but not the part scale.
    public static Anchor AtPartVehicleAsmb(Vehicle vehicle, Part part, double3 posVehicleAsmb, string partLabel)
    {
        double4x4.Invert(part.MatrixAsmb2VehicleAsmb, out double4x4 inverse);
        return AtPartLocal(vehicle, part, posVehicleAsmb.Transform(inverse), partLabel);
    }

    // Editor twins of the two factories above; no Body, the editing space is
    // reached through Program.Editor at resolve time.
    public static Anchor AtEditorPartLocal(Part part, double3 offsetPartAsmb, string partLabel, double3? normalPartAsmb = null)
    {
        return new Anchor
        {
            Kind = AnchorKind.EditorPartPoint,
            Part = part,
            OffsetPartAsmb = offsetPartAsmb,
            NormalPartAsmb = normalPartAsmb,
            Label = partLabel,
            PartLabel = partLabel,
        };
    }

    public static Anchor AtEditorPartVehicleAsmb(Part part, double3 posVehicleAsmb, string partLabel)
    {
        double4x4.Invert(part.MatrixAsmb2VehicleAsmb, out double4x4 inverse);
        return AtEditorPartLocal(part, posVehicleAsmb.Transform(inverse), partLabel);
    }

    // A stale part anchor's part usually still exists, just under a new owner:
    // decoupling and docking move it between vehicles, the editor moves it
    // between the shared editing tree, the unattached (in-hand) trees, and the
    // launched vehicle. Part objects survive all of these transitions (verified
    // against Vehicle.CreateVehicle, Vehicle.MergeFrom, VehicleEditor.Build
    // and VehicleEditor.Dispose), and the launch rebase only rewrites part
    // transforms within the vehicle, never the part's own frame, so the stored
    // offset transfers exactly. Returns the re-homed anchor, or null when the
    // part is in no tree anymore (truly deleted).
    public Anchor? Rehome(CelestialSystem system)
    {
        if (Kind != AnchorKind.PartPoint && Kind != AnchorKind.EditorPartPoint)
            return null;
        Part? fullPart = Part?.FullPart;
        if (fullPart == null)
            return null;
        VehicleEditor? editor = Program.Editor;
        if (editor != null)
        {
            if (ReferenceEquals(editor.EditingSpace.Parts?.Find(fullPart.InstanceId), fullPart))
                return AtEditorPartLocal(Part!, OffsetPartAsmb, PartLabel, NormalPartAsmb);
            foreach (PartTree tree in editor.UnattachedPartTrees)
            {
                if (ReferenceEquals(tree.Find(fullPart.InstanceId), fullPart))
                    return AtEditorPartLocal(Part!, OffsetPartAsmb, PartLabel, NormalPartAsmb);
            }
        }
        foreach (Astronomical astronomical in system.All.AsSpan())
        {
            if (astronomical is Vehicle vehicle
                && ReferenceEquals(vehicle.Parts?.Find(fullPart.InstanceId), fullPart))
                return AtPartLocal(vehicle, Part!, OffsetPartAsmb, PartLabel, NormalPartAsmb);
        }
        return null;
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
