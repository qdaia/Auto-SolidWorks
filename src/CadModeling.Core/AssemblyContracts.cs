using System.Text.Json.Serialization;
using CadModeling.Ir;
namespace CadModeling.Core;

public sealed record AssemblyComponentSpec
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public string Configuration { get; init; } = "";
    public Vector3 TranslationMm { get; init; } = new(0,0,0);
    public Vector3 RotationDegrees { get; init; } = new(0,0,0);
    public bool Fixed { get; init; }
}
[JsonConverter(typeof(JsonStringEnumConverter<AssemblyMateKind>))]
public enum AssemblyMateKind { Coincident, Concentric, Parallel, Perpendicular, Distance, Angle, Lock }
public sealed record AssemblyMateEntity
{
    public required string ComponentId { get; init; }
    public EntityQuery? Entity { get; init; }
}
public sealed record AssemblyMateSpec
{
    public required string Name { get; init; }
    public AssemblyMateKind Kind { get; init; }
    public required AssemblyMateEntity First { get; init; }
    public required AssemblyMateEntity Second { get; init; }
    public double Value { get; init; }
    public bool AntiAligned { get; init; }
    public bool LockRotation { get; init; }
}
public sealed record AssemblyPlan
{
    public required string Name { get; init; }
    public required string NativePath { get; init; }
    public IReadOnlyList<string> ExportPaths { get; init; } = [];
    public IReadOnlyList<AssemblyComponentSpec> Components { get; init; } = [];
    public IReadOnlyList<AssemblyMateSpec> Mates { get; init; } = [];
    public bool OverwriteAllowed { get; init; }
    public bool CheckInterference { get; init; } = true;
}
public sealed record AssemblyComponentResult(string Id,string InstanceName,string Path,IReadOnlyList<double> Transform,bool Fixed);
public sealed record AssemblyInterference(double VolumeMm3,IReadOnlyList<string> Components);
public sealed record AssemblyResult(bool Success,string Message,string? NativePath=null,
    IReadOnlyList<AssemblyComponentResult>? Components=null,IReadOnlyList<string>? Mates=null,
    IReadOnlyList<AssemblyInterference>? Interferences=null,bool Reopened=false);

public static class AssemblyPlanValidation
{
    public static void Validate(AssemblyPlan p)
    {
        if(!Path.IsPathFullyQualified(p.NativePath)||!p.NativePath.EndsWith(".sldasm",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Assembly output requires an absolute SLDASM path.");
        if(p.Components.Count==0||p.Components.Select(c=>c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=p.Components.Count) throw new ArgumentException("Assembly needs components with unique IDs.");
        foreach(var c in p.Components)
        {
            if(string.IsNullOrWhiteSpace(c.Id)||!Path.IsPathFullyQualified(c.Path)||!File.Exists(c.Path)||
                !(c.Path.EndsWith(".sldprt",StringComparison.OrdinalIgnoreCase)||c.Path.EndsWith(".sldasm",StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"Component '{c.Id}' requires an existing native model.");
            if(!NativeFeatureValidation.Finite(c.TranslationMm)||!NativeFeatureValidation.Finite(c.RotationDegrees)) throw new ArgumentException("Component transforms must be finite.");
        }
        foreach(var output in p.ExportPaths.Prepend(p.NativePath))
        {
            if(!Path.IsPathFullyQualified(output)) throw new ArgumentException("Assembly outputs must be absolute.");
            if(p.Components.Any(c=>Path.GetFullPath(c.Path).Equals(Path.GetFullPath(output),StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Assembly output must not overwrite a source component.");
            if(File.Exists(output)&&!p.OverwriteAllowed) throw new IOException($"Output exists: {output}");
        }
        if(p.ExportPaths.Any(x=>!new[]{".step",".stp",".stl"}.Contains(Path.GetExtension(x).ToLowerInvariant()))) throw new ArgumentException("Assembly export supports STEP/STP/STL.");
        foreach(var m in p.Mates)
        {
            if(!double.IsFinite(m.Value)||m.Value<0) throw new ArgumentException("Mate value must be finite and nonnegative.");
            if(m.First.ComponentId==m.Second.ComponentId||!p.Components.Any(c=>c.Id==m.First.ComponentId)||!p.Components.Any(c=>c.Id==m.Second.ComponentId)) throw new ArgumentException("Mate endpoints must reference two different existing components.");
        }
    }
}
