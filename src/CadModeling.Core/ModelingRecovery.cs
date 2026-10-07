using System.Text.Json;
using CadModeling.Ir;
namespace CadModeling.Core;

public sealed record ModelingCheckpointManifest
{
    public const int CurrentFormatVersion = 2;
    public string Contract { get; init; } = "autosolidworks.checkpoint/v2";
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required string NativePath { get; init; }
    public required string NativeSha256 { get; init; }
    public required ModelingPlan OriginalPlan { get; init; }
    public string? SourceModelSha256 { get; init; }
    public string? SourceSha256 { get; init; }
    public string? SourceRevisionId { get; init; }
    public string? TypedPlanFingerprint { get; init; }
    public string? CompletedPrefixFingerprint { get; init; }
    public int CompletedOperationCount { get; init; }
    public IReadOnlyList<string> CompletedOperationIds { get; init; } = [];
    public required IReadOnlyList<StableFeatureReference> FeatureReferences { get; init; }
    public required GeometrySnapshot Geometry { get; init; }
    public bool ModelReopened { get; init; }
    public CheckpointWriteState WriteState { get; init; } = CheckpointWriteState.Legacy;
    public CheckpointEnvironmentIdentity? Environment { get; init; }
    public IReadOnlyList<CheckpointEvidenceState> EvidenceStates { get; init; } = [];
    public string? InFlightOperationId { get; init; }
    public InFlightOperationState InFlightState { get; init; } = InFlightOperationState.None;
}
public sealed record ModelingRecoveryState(string? ManifestPath, string? CheckpointPath,
    IReadOnlyList<string> CompletedOperationIds, IReadOnlyList<string> RemainingOperationIds,
    string? FailedOperationId, bool CanResume, string Message);

public static partial class ModelingRecovery
{
    public static IReadOnlySet<string> CheckpointAfter(ModelingPlan plan)
    {
        if(!plan.Recovery.Enabled) return new HashSet<string>();
        if(plan.Recovery.AfterOperationIds.Count>0) return plan.Recovery.AfterOperationIds.ToHashSet(StringComparer.Ordinal);
        var first=plan.Operations.Count>2?plan.Operations.FirstOrDefault(o=>o is ExtrudeBossOperation ||
            o is NativeFeatureOperation { Options.Kind: NativeFeatureKind.RevolveBoss or NativeFeatureKind.LoftBoss or NativeFeatureKind.SweepBoss }):null;
        return first is null?new HashSet<string>():new HashSet<string>(StringComparer.Ordinal){first.Id};
    }
    public static IReadOnlyList<ModelingDiagnostic> Validate(ModelingPlan plan)
    {
        var errors=new List<ModelingDiagnostic>();
        void Error(string text)=>errors.Add(new("RECOVERY_INVALID",DiagnosticSeverity.Error,text,"recovery"));
        if(plan.Recovery.Directory is { } directory&&!Path.IsPathFullyQualified(directory)) Error("检查点目录必须是绝对路径。");
        if(!plan.Recovery.Enabled&&plan.Recovery.ResumeManifestPath is not null) Error("恢复需要 recovery.enabled=true。");
        foreach(var id in plan.Recovery.AfterOperationIds)
            if(!plan.Operations.Any(o=>o.Id==id&&o is not ProfileSketchOperation)) Error($"检查点 '{id}' 必须跟随一个现有的已完成的非草图操作。");
        if(plan.Recovery.ResumeManifestPath is { } path)
        {
            try { _=ReadAndValidate(plan,path); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            { Error(ex.Message); }
        }
        return errors;
    }
    public static ModelingCheckpointManifest ReadAndValidate(ModelingPlan plan,string path)
    {
        if(!Path.IsPathFullyQualified(path)||!File.Exists(path)) throw new ArgumentException("恢复需要现有的绝对执行器检查点manifest。");
        var m=JsonSerializer.Deserialize<ModelingCheckpointManifest>(File.ReadAllText(path),ModelingIrJson.Options)
            ?? throw new InvalidOperationException("检查点清单为空。");
        if(m.FormatVersion is <1 or >ModelingCheckpointManifest.CurrentFormatVersion||m.CompletedOperationCount<1||m.CompletedOperationCount>plan.Operations.Count||m.CompletedOperationCount>m.OriginalPlan.Operations.Count)
            throw new InvalidOperationException("检查点格式或已完成的操作计数无效。");
        if(!Path.IsPathFullyQualified(m.NativePath)||!m.NativePath.EndsWith(".sldprt",StringComparison.OrdinalIgnoreCase)||!File.Exists(m.NativePath)||
           DrawingPlanValidation.FileHash(m.NativePath)!=m.NativeSha256) throw new InvalidOperationException("检查点模型缺失或已更改。恢复被拒绝。");
        if(plan.Output.NativePath is { } output&&Path.IsPathFullyQualified(output)&&Path.GetFullPath(output).Equals(Path.GetFullPath(m.NativePath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("重建必须保存一个独立的最终模型，绝不能覆盖其检查点。");
        if(plan.DrawingSourceSha256!=m.OriginalPlan.DrawingSourceSha256||plan.SourceModelPath!=m.OriginalPlan.SourceModelPath)
            throw new InvalidOperationException("源模型/图纸标识已更改。此检查点无法重复使用。");
        if(plan.SourceModelPath is { } source&&(!File.Exists(source)||DrawingPlanValidation.FileHash(source)!=m.SourceModelSha256))
            throw new InvalidOperationException("原生模型在检查点后发生变化。");
        var ids=plan.Operations.Take(m.CompletedOperationCount).Select(o=>o.Id).ToHashSet(StringComparer.Ordinal);
        if(m.FeatureReferences.Count!=ids.Count||m.FeatureReferences.Select(r=>r.OperationId).Distinct(StringComparer.Ordinal).Count()!=ids.Count||m.FeatureReferences.Any(r=>!ids.Contains(r.OperationId)))
            throw new InvalidOperationException("检查点特征图不完整。");
        if(m.FormatVersion>=2)
        {
            if(m.WriteState!=CheckpointWriteState.Complete||!m.ModelReopened)
                throw new InvalidOperationException("检查点未原子完成并重新打开；恢复被拒绝。");
            if(m.CompletedOperationIds.Count!=m.CompletedOperationCount||!m.CompletedOperationIds.SequenceEqual(plan.Operations.Take(m.CompletedOperationCount).Select(o=>o.Id),StringComparer.Ordinal))
                throw new InvalidOperationException("检查点完成-操作的身份不完整或过时。");
            if(!string.Equals(m.SourceSha256,plan.DrawingSourceSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("检查点源码 SHA-256 与当前源码不同。");
            if(plan.DrawingSourceSha256 is not null&&string.IsNullOrWhiteSpace(plan.Recovery.SourceRevisionId))
                throw new InvalidOperationException("基于草图的恢复要求当前源修订的身份标识。");
            if(!string.Equals(m.SourceRevisionId,plan.Recovery.SourceRevisionId,StringComparison.Ordinal))
                throw new InvalidOperationException("检查点源修订版本与当前源修订版本不同。");
            if(string.IsNullOrWhiteSpace(m.TypedPlanFingerprint)||string.IsNullOrWhiteSpace(m.CompletedPrefixFingerprint)||
               !m.CompletedPrefixFingerprint.Equals(PrefixFingerprint(plan,m.CompletedOperationCount),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("检查点原生前缀标识缺失或过时；此快照无法截断到更早的操作边界。");
            if(m.InFlightState is InFlightOperationState.Running or InFlightOperationState.Uncertain)
                throw new InvalidOperationException("检查点记录了一个未解决的飞行中COM操作；在重复使用前探查其实际结果。");
        }
        return m;
    }
}
