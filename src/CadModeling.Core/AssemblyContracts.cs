using System.Text.Json.Serialization;
using CadModeling.Ir;
namespace CadModeling.Core;

public sealed record AssemblyComponentSpec
{
    public string? Name { get; init; }
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
    public bool RejectUnapprovedInterference { get; init; } = true;
    public IReadOnlyList<AssemblyAllowedInterference> AllowedInterferences { get; init; } = [];
    public bool RequireFullyConstrainedComponents { get; init; }
    public AssemblyMobilitySpec? Mobility { get; init; }
}
public sealed record AssemblyAllowedInterference(IReadOnlyList<string> ComponentIds,double MaximumVolumeMm3);
public sealed record AssemblyComponentResult(string Id,string InstanceName,string Path,IReadOnlyList<double> Transform,bool Fixed);
public sealed record AssemblyInterference(double VolumeMm3,IReadOnlyList<string> Components);
public sealed record AssemblyMateEndpointReadback(string ComponentId,string InstanceName,int ReferenceType,string? PersistentReference);
public sealed record AssemblyMateReadback(string Name,AssemblyMateKind Kind,int Alignment,bool? LockRotation,IReadOnlyList<AssemblyMateEndpointReadback> Endpoints);
public sealed record AssemblyResult(bool Success,string Message,string? NativePath=null,
    IReadOnlyList<AssemblyComponentResult>? Components=null,IReadOnlyList<string>? Mates=null,
    IReadOnlyList<AssemblyInterference>? Interferences=null,bool Reopened=false,DrawingExportResult? Drawing=null)
{
    public string? RequestId { get; init; }
    public bool OutcomeUnknown { get; init; }
    public IReadOnlyList<AssemblyMateReadback> MateReadbacks { get; init; } = [];
    public IReadOnlyList<AssemblyMobilityReceipt> MobilityReadbacks { get; init; } = [];
}

public static class AssemblyPlanValidation
{
    public static void Validate(AssemblyPlan p)
    {
        if(!Path.IsPathFullyQualified(p.NativePath)||!p.NativePath.EndsWith(".sldasm",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("输出的装配需要一个绝对的 SLDASM 路径。");
        DrawingExportValidation.ValidateOutputs(DrawingExportValidation.ForAssembly(p.NativePath));
        if(p.Components.Count==0||p.Components.Select(c=>c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=p.Components.Count) throw new ArgumentException("装配需要具有唯一ID的组件。");
        foreach(var c in p.Components)
        {
            if(string.IsNullOrWhiteSpace(c.Id)||!Path.IsPathFullyQualified(c.Path)||!File.Exists(c.Path)||
                !(c.Path.EndsWith(".sldprt",StringComparison.OrdinalIgnoreCase)||c.Path.EndsWith(".sldasm",StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"组件 '{c.Id}' 需要一个现有的原生模型。");
            if(!NativeFeatureValidation.Finite(c.TranslationMm)||!NativeFeatureValidation.Finite(c.RotationDegrees)) throw new ArgumentException("组件变换必须是有限的。");
        }
        foreach(var output in p.ExportPaths.Prepend(p.NativePath))
        {
            if(!Path.IsPathFullyQualified(output)) throw new ArgumentException("装配输出必须是绝对的。");
            if(p.Components.Any(c=>Path.GetFullPath(c.Path).Equals(Path.GetFullPath(output),StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("装配输出不能覆盖源组件。");
            if(File.Exists(output)&&!p.OverwriteAllowed) throw new IOException($"输出存在：{output}");
        }
        if(p.ExportPaths.Any(x=>!new[]{".step",".stp",".stl"}.Contains(Path.GetExtension(x).ToLowerInvariant()))) throw new ArgumentException("装配导出支持STEP/STP/STL。");
        if(p.Mates.Select(m=>m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=p.Mates.Count)
            throw new ArgumentException("配合名称必须唯一，以便保存重开后核对身份。");
        foreach(var m in p.Mates)
        {
            ValidateMate(m);
            if(m.First.ComponentId==m.Second.ComponentId||!p.Components.Any(c=>c.Id==m.First.ComponentId)||!p.Components.Any(c=>c.Id==m.Second.ComponentId)) throw new ArgumentException("配合必须参考两个不同的现有组件。");
        }
        foreach(var allowance in p.AllowedInterferences)
            if(allowance.ComponentIds.Count<2 || allowance.ComponentIds.Distinct(StringComparer.Ordinal).Count()!=allowance.ComponentIds.Count
                || allowance.ComponentIds.Any(id=>!p.Components.Any(c=>c.Id==id)) || !double.IsFinite(allowance.MaximumVolumeMm3) || allowance.MaximumVolumeMm3<=0)
                throw new ArgumentException("允许干涉需要现有且唯一的组件 ID 及正数体积上限。");
        AssemblyMobilityContract.Validate(p);
    }

    public static void ValidateMate(AssemblyMateSpec mate)
    {
        if(string.IsNullOrWhiteSpace(mate.Name)||!Enum.IsDefined(mate.Kind))throw new ArgumentException("配合需要名称和有效的类型。");
        if(!double.IsFinite(mate.Value)||mate.Value<0||mate.Kind==AssemblyMateKind.Angle&&mate.Value>180)
            throw new ArgumentException("配合值必须有限且非负，角度必须在 0..180 度内。");
        if(mate.Kind is not (AssemblyMateKind.Distance or AssemblyMateKind.Angle)&&mate.Value!=0)
            throw new ArgumentException("只有距离和角度配合可以指定驱动数值。");
        if(mate.LockRotation&&mate.Kind!=AssemblyMateKind.Concentric)throw new ArgumentException("旋转锁定只适用于同心配合。");
        if(mate.AntiAligned&&mate.Kind is AssemblyMateKind.Perpendicular or AssemblyMateKind.Lock)
            throw new ArgumentException("垂直和锁定配合没有可验收的反向对齐方向。");
        foreach(var endpoint in new[]{mate.First,mate.Second})
        {
            if(string.IsNullOrWhiteSpace(endpoint.ComponentId))throw new ArgumentException("配合端点需要组件 ID。");
            if(mate.Kind==AssemblyMateKind.Lock?endpoint.Entity is not null:endpoint.Entity is null or {AllMatches:true})
                throw new ArgumentException("锁定配合使用整个组件；几何配合必须使用单一实体查询。");
            if(endpoint.Entity is not null)
            {
                var errors=new List<ModelingDiagnostic>();
                AdvancedFeatureValidation.Validate(new(){Selections=[endpoint.Entity]},"mate.entity",errors);
                if(errors.Count>0)throw new ArgumentException("配合实体查询无效："+string.Join("; ",errors.Select(e=>e.Message)));
            }
        }
    }

    public static bool InterferenceAllowed(AssemblyPlan plan,IReadOnlyList<string> componentIds,double volumeMm3)
    {
        if(!double.IsFinite(volumeMm3)||volumeMm3<0||componentIds.Count<2||componentIds.Distinct(StringComparer.Ordinal).Count()!=componentIds.Count)return false;
        var ids=componentIds.ToHashSet(StringComparer.Ordinal);
        return plan.AllowedInterferences.Any(a=>ids.SetEquals(a.ComponentIds)&&volumeMm3<=a.MaximumVolumeMm3);
    }
}
