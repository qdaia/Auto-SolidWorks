using System;
using System.Linq;
using System.Reflection;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;

var passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

var executorType = Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor")
    ?? throw new Exception("SolidWorksComExecutor type missing.");
var method = executorType.GetMethod("CylinderLateralBoundaryExcluded", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new Exception("CylinderLateralBoundaryExcluded method missing.");

ISurface CylinderSurface() => Proxy.Make<ISurface>((name, _) => name switch
{
    "IsCylinder" => true,
    "get_CylinderParams" => new double[] { 0, 0, 0, 0, 0, 1, .005 },
    _ => throw new NotSupportedException(name)
});

ISurface PlaneSurface() => Proxy.Make<ISurface>((name, _) => name switch
{
    "IsCylinder" => false,
    _ => throw new NotSupportedException(name)
});

IFace2 SupportFace(ISurface surface) => Proxy.Make<IFace2>((name, _) => name switch
{
    "GetSurface" => surface,
    _ => throw new NotSupportedException(name)
});

IEdge AxialEdge(params IFace2[] adjacent)
{
    var curve = Proxy.Make<ICurve>((name, _) => name switch
    {
        "IsLine" => true,
        "IsCircle" => false,
        "get_LineParams" => new double[] { .005, 0, 0, 0, 0, 1 },
        _ => throw new NotSupportedException(name)
    });
    return Proxy.Make<IEdge>((name, _) => name switch
    {
        "GetCurve" => curve,
        "GetTwoAdjacentFaces2" => adjacent.Cast<object>().ToArray(),
        _ => throw new NotSupportedException(name)
    });
}

IEdge EndArc(double z)
{
    var curve = Proxy.Make<ICurve>((name, _) => name switch
    {
        "IsLine" => false,
        "IsCircle" => true,
        "get_CircleParams" => new double[] { 0, 0, z, 0, 0, 1, .005 },
        _ => throw new NotSupportedException(name)
    });
    return Proxy.Make<IEdge>((name, _) => name == "GetCurve" ? curve : throw new NotSupportedException(name));
}

bool Evaluate(params IEdge[] edges)
{
    var face = Proxy.Make<IFace2>((name, _) => name == "GetEdges" ? edges.Cast<object>().ToArray() : throw new NotSupportedException(name));
    return (bool)(method.Invoke(null, new object[]
    {
        face, new Vector3(0, 0, 0), new Vector3(0, 0, 10), new Vector3(0, 0, 1), 5d, .02d
    }) ?? false);
}

var cylinderA = SupportFace(CylinderSurface());
var cylinderB = SupportFace(CylinderSurface());
var plane = SupportFace(PlaneSurface());

Check(Evaluate(AxialEdge(cylinderA, cylinderA), EndArc(0), EndArc(.01)),
    "T08 production adapter accepts a periodic cylindrical seam with both end boundaries");
Check(Evaluate(AxialEdge(cylinderA, cylinderB), EndArc(0), EndArc(.01)),
    "T08 production adapter accepts a split cylindrical wall shared by matching coaxial cylinder faces");
Check(!Evaluate(AxialEdge(cylinderA, plane), AxialEdge(cylinderA, plane), EndArc(0), EndArc(.01)),
    "T08 production adapter rejects an open half-cylinder slot whose axial lips meet planar faces");
Check(!Evaluate(AxialEdge(cylinderA, cylinderA), EndArc(0)),
    "T08 production adapter refuses incomplete end-boundary coverage");

Console.WriteLine($"{passed} stage2 adapter checks passed.");

public class Proxy : DispatchProxy
{
    public Func<string, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!.Name, args);
    public static T Make<T>(Func<string, object?[]?, object?> handler) where T : class
    {
        var value = Create<T, Proxy>();
        ((Proxy)(object)value).Handler = handler;
        return value;
    }
}
