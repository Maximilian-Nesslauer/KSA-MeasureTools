using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Brutal.Numerics;
using KSA;
using Game = KSA.Program;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: MeasureTools.CircleChecks <game-directory> <MeasureTools.dll>");
    return 2;
}

string gameDir = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string path = Path.Combine(gameDir, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Assembly candidate = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
return Run(candidate);

[MethodImpl(MethodImplOptions.NoInlining)]
static int Run(Assembly candidate) => new CircleChecks(candidate).Run();

internal sealed class CircleChecks(Assembly candidate)
{
    private const string MeasureNamespace = "MeasureTools.Features.Measure.";
    private const double Tolerance = 1e-12;
    private const double Radius = 2.5;
    private static readonly double3 Center = new(0.125, 0.375, 0.25);
    private static readonly double3 Normal = new(0, 0, 1);
    private static readonly double3 FarEcl = new(140_000_000_000, -60_000_000_000, 20_000_000);
    private static readonly double3 Scale = new(1.2, 1.2, 0.75);

    private readonly Type _anchor = candidate.GetType(MeasureNamespace + "Anchor", throwOnError: true)!;
    private readonly Type _measurement = candidate.GetType(MeasureNamespace + "Measurement", throwOnError: true)!;
    private int _checks;
    private int _failures;

    public int Run()
    {
        CheckFlightTranslation();
        CheckEditorTranslationAndScale();
        CheckMixedFrames();
        Console.WriteLine(FormattableString.Invariant($"Circle checks: {_checks - _failures}/{_checks} passed."));
        return _failures == 0 ? 0 : 1;
    }

    private void CheckFlightTranslation()
    {
        Vehicle vehicle = CreateVehicle();
        Part parent = CreatePart(null, new double3(2, -3, 4), new double3(1), 0.17);
        Part subpart = CreatePart(parent, new double3(0.4, -0.7, 0.2), Scale, -0.31);
        object center = FlightAnchor(vehicle, subpart, Center, Normal);
        for (int i = 0; i < 16; i++)
        {
            double angle = (i + 0.125) * Math.Tau / 16;
            double3 rim = Center + Radius * new double3(Math.Cos(angle), Math.Sin(angle), 0);
            object measurement = Measure(center, FlightAnchor(vehicle, subpart, rim));
            SetPosition(vehicle, double3.Zero);
            Near($"Flight sample {i} at origin", 6, Read(measurement, "CircleDiameterMeters"));
            SetPosition(vehicle, FarEcl);
            Near($"Flight sample {i} at Earth-scale translation", 6, Read(measurement, "CircleDiameterMeters"));
        }

        double3 farPose = Invoke<double3>(center, "ResolveEcl");
        SetPosition(vehicle, double3.Zero);
        Check("Flight anchor pose follows the vehicle", farPose.Equals(FarEcl + Invoke<double3>(center, "ResolveEcl")));
        object circle = Measure(center, FlightAnchor(vehicle, subpart, Center + Radius * new double3(1, 0, 0)));
        subpart.Scale = new double3(2, 2, 2);
        Near("Flight uses edited uniform scale for radius", 5, Read(circle, "CircleRadiusMeters"));
        Near("Flight uses edited uniform scale for circumference", 10 * Math.PI, Read(circle, "CircleCircumferenceMeters"));
        subpart.Scale = new double3(-1.2, 1.2, 0.75);
        Near("Reflected circle preserves the diameter", 6, Read(circle, "CircleDiameterMeters"));
        subpart.Scale = new double3(1.2, 0.9, 0.75);
        ExpectNaN("Unequal in-plane scaling rejects an ellipse", circle);
        subpart.Scale = new double3(1.2, 1.2, 0);
        ExpectNaN("Singular scaling rejects the circle", circle);
        subpart.Scale = Scale;
        parent.Scale = new double3(2, 1, 1);
        ExpectNaN("A rotated subpart under anisotropic parent scaling rejects the distorted circle", circle);
        parent.Scale = new double3(1);
        // The fixture has no part tree to invalidate the subpart cache.
        subpart.ResetCachedPosMatrixValues();
        Near("Restoring circle-preserving scaling restores the diameter", 6, Read(circle, "CircleDiameterMeters"));
        object noNormal = FlightAnchor(vehicle, subpart, Center);
        object rimAnchor = FlightAnchor(vehicle, subpart, Center + new double3(Radius, 0, 0));
        ExpectNaN("Missing plane normal rejects the circle", Measure(noNormal, rimAnchor));
        ExpectNaN("Zero plane normal rejects the circle",
            Measure(FlightAnchor(vehicle, subpart, Center, double3.Zero), rimAnchor));
    }

    private void CheckEditorTranslationAndScale()
    {
        var editor = (VehicleEditor)RuntimeHelpers.GetUninitializedObject(typeof(VehicleEditor));
        editor.EditingSpace = (VehicleEditingSpace)RuntimeHelpers.GetUninitializedObject(typeof(VehicleEditingSpace));
        editor.EditingSpace.Asmb2Ecl = doubleQuat.CreateFromAxisAngle(new double3(0, 1, 0), 0.73);
        Game.Editor = editor;
        object measurement;
        try
        {
            Part subpart = CreatePart(CreatePart(null, new double3(4), new double3(1), -0.41), new double3(1), Scale, 0.2);
            object center = EditorAnchor(subpart, Center, Normal);
            object rim = EditorAnchor(subpart, Center + new double3(0, Radius, 0));
            measurement = Measure(center, rim);
            Near("Editor at origin", 6, Read(measurement, "CircleDiameterMeters"));
            double3 before = Invoke<double3>(center, "ResolveEcl");
            editor.EditingSpace.PositionEcl = FarEcl;
            Near("Editor at Earth-scale translation", 6, Read(measurement, "CircleDiameterMeters"));
            double3 after = Invoke<double3>(center, "ResolveEcl");
            Check("Editor anchor pose follows editing space", after.Equals(FarEcl + before));
            editor.EditingSpace.PositionEcl = double3.Zero;
            subpart.Scale = new double3(0.5, 0.5, 3);
            Near("Editor uses current plane-uniform scale", 2.5, Read(measurement, "CircleDiameterMeters"));
            subpart.Scale = new double3(0.5, 0.75, 3);
            ExpectNaN("Editor scale edit rejects an ellipse", measurement);
        }
        finally
        {
            Game.Editor = null;
        }
        ExpectNaN("Closed editor leaves the old editor circle unresolved", measurement);
    }

    private void CheckMixedFrames()
    {
        Vehicle first = CreateVehicle();
        Vehicle second = CreateVehicle();
        SetPosition(second, new double3(3, 4, 0));
        Part one = CreatePart(null, double3.Zero, new double3(1), 0);
        Part two = CreatePart(null, new double3(3, 4, 0), new double3(1), 0);
        object center = FlightAnchor(first, one, Center, Normal);
        Near("Distinct parts use their own anchor transforms", 10,
            Read(Measure(center, FlightAnchor(first, two, Center)), "CircleDiameterMeters"));
        Near("Distinct owners use their own anchor transforms", 10,
            Read(Measure(center, FlightAnchor(second, one, Center)), "CircleDiameterMeters"));
        Near("Coincident anchors on one part retain zero diameter", 0,
            Read(Measure(center, FlightAnchor(first, one, Center)), "CircleDiameterMeters"));
    }

    private static Vehicle CreateVehicle()
    {
        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle));
        var orientation = doubleQuat.CreateFromAxisAngle(new double3(1, 0, 0), 0.37);
        typeof(Vehicle).GetProperty(nameof(Vehicle.Body2Cce))!.SetValue(vehicle, orientation);
        return vehicle;
    }

    private static void SetPosition(Vehicle vehicle, double3 position) =>
        typeof(Vehicle).GetField("_positionEcl", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vehicle, position);

    private static Part CreatePart(Part? parent, double3 position, double3 scale, double angle)
    {
        var part = (Part)RuntimeHelpers.GetUninitializedObject(typeof(Part));
        part.PartParent = parent;
        part.PositionParentAsmb = position;
        part.Scale = scale;
        part.Asmb2ParentAsmb = doubleQuat.CreateFromAxisAngle(Normal, angle);
        return part;
    }

    private object FlightAnchor(Vehicle owner, Part part, double3 local, double3? normal = null) =>
        _anchor.GetMethod("AtPartLocal")!.Invoke(null, [owner, part, local, "fixture", normal])!;

    private object EditorAnchor(Part part, double3 local, double3? normal = null) =>
        _anchor.GetMethod("AtEditorPartLocal")!.Invoke(null, [part, local, "fixture", normal])!;

    private object Measure(object center, object rim)
    {
        object measurement = Activator.CreateInstance(_measurement)!;
        Array anchors = Array.CreateInstance(_anchor, 2);
        anchors.SetValue(center, 0);
        anchors.SetValue(rim, 1);
        _measurement.GetProperty("Anchors")!.SetValue(measurement, anchors);
        PropertyInfo mode = _measurement.GetProperty("Mode")!;
        mode.SetValue(measurement, Enum.Parse(mode.PropertyType, "Circle"));
        return measurement;
    }

    private static T Invoke<T>(object target, string method) =>
        (T)target.GetType().GetMethod(method)!.Invoke(target, null)!;

    private static double Read(object measurement, string method) => Invoke<double>(measurement, method);

    private void ExpectNaN(string label, object measurement) =>
        Check(label, double.IsNaN(Read(measurement, "CircleDiameterMeters")));

    private void Near(string label, double expected, double actual) =>
        Check(FormattableString.Invariant($"{label} expected {expected:R}, got {actual:R}"), Math.Abs(expected - actual) < Tolerance);

    private void Check(string label, bool passed)
    {
        _checks++;
        if (passed)
            Console.WriteLine("PASS " + label);
        else
        {
            _failures++;
            Console.WriteLine("FAIL " + label);
        }
    }
}
