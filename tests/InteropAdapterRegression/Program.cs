using System.Reflection;

var interop = Assembly.Load("SolidWorks.Interop.sldworks");
var version = interop.GetName().Version!;
var kernel = Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor", true)!;
object? Adapter(string method, params object?[] args) =>
    kernel.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
Type SwType(string name) => interop.GetType("SolidWorks.Interop.sldworks." + name, true)!;

object Fake(Type type, Func<MethodInfo, object?[], object?> callback)
{
    object proxy = DispatchProxy.Create(type, typeof(ManagedComDouble));
    ((ManagedComDouble)proxy).Callback = callback;
    return proxy;
}

var calls = new List<string>();
var bodyType = SwType("IBody2");
var sheet = Fake(bodyType, (method, _) => throw new Exception("Unexpected operation on sheet: " + method.Name));
object[] edges = [new object(), new object()];
var copy = Fake(bodyType, (method, arguments) =>
{
    calls.Add(method.Name);
    if (!ReferenceEquals(arguments[0], sheet) || (arguments.Length == 2 && !Equals(arguments[1], false)))
        throw new Exception("Wrong intersection tool or face-ID option");
    return edges;
});
var original = Fake(bodyType, (method, arguments) =>
{
    calls.Add(method.Name);
    if (arguments.Length == 1 && !Equals(arguments[0], true)) throw new Exception("Wrong copy option");
    return copy;
});
if (!ReferenceEquals(copy, Adapter("CopySectionBody", original)) ||
    !ReferenceEquals(edges, Adapter("SectionIntersectionEdges", copy, sheet)))
    throw new Exception("The copied body or paired edge results were lost");
string[] expectedCalls = version.Major < 28 ? ["Copy", "GetIntersectionEdges"] : ["Copy2", "GetIntersectionEdges2"];
if (!calls.SequenceEqual(expectedCalls)) throw new Exception("Unexpected section API: " + string.Join(", ", calls));

string templateRoot = (string)Adapter("TemplateDirectory")!;
if (Path.GetFileName(templateRoot) != $"SOLIDWORKS {1992 + version.Major}")
    throw new Exception("Template lookup crossed release years");

var bodies = new object[1];
double[] origins = [0, 0, 0], directions = [1, 0, 0];
var model = Fake(SwType("IModelDoc2"), (getter, _) =>
{
    if (getter.Name != "get_Extension") throw new Exception("Unexpected model call: " + getter.Name);
    return Fake(getter.ReturnType, (method, values) =>
    {
        if (method.Name != "RayIntersections" || !values.SequenceEqual(new object[] { bodies, origins, directions, 3, 0d, .0001d, true }))
            throw new Exception("Lost high precision or ray arguments");
        return 4;
    });
});
var hasHighPrecision = version >= new Version(26, 2);
try
{
    var count = Adapter("HighPrecisionIntersections", model, bodies, origins, directions, 3, 0d, .0001d, true);
    if (!hasHighPrecision || !Equals(count, 4)) throw new Exception("Unexpected ray result");
}
catch (TargetInvocationException ex) when (!hasHighPrecision && ex.InnerException is NotSupportedException) { }

Console.WriteLine($"{version}: section API dispatch, temporary-body arguments, paired edges, template year and ray capability passed with managed doubles.");

public class ManagedComDouble : DispatchProxy
{
    public Func<MethodInfo, object?[], object?> Callback = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Callback(method!, args ?? []);
}
