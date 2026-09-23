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
        if(plan.Recovery.Directory is { } directory&&!Path.IsPathFullyQualified(directory)) Error("Checkpoint directory must be absolute.");
        if(!plan.Recovery.Enabled&&plan.Recovery.ResumeManifestPath is not null) Error("Resume requires recovery.enabled=true.");
        foreach(var id in plan.Recovery.AfterOperationIds)
            if(!plan.Operations.Any(o=>o.Id==id&&o is not ProfileSketchOperation)) Error($"Checkpoint '{id}' must follow an existing completed non-sketch operation.");
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
        if(!Path.IsPathFullyQualified(path)||!File.Exists(path)) throw new ArgumentException("Resume requires an existing absolute executor checkpoint manifest.");
        var m=JsonSerializer.Deserialize<ModelingCheckpointManifest>(File.ReadAllText(path),ModelingIrJson.Options)
            ?? throw new InvalidOperationException("Checkpoint manifest is empty.");
        if(m.FormatVersion is <1 or >ModelingCheckpointManifest.CurrentFormatVersion||m.CompletedOperationCount<1||m.CompletedOperationCount>plan.Operations.Count||m.CompletedOperationCount>m.OriginalPlan.Operations.Count)
            throw new InvalidOperationException("Checkpoint format or completed operation count is invalid.");
        if(!Path.IsPathFullyQualified(m.NativePath)||!m.NativePath.EndsWith(".sldprt",StringComparison.OrdinalIgnoreCase)||!File.Exists(m.NativePath)||
           DrawingPlanValidation.FileHash(m.NativePath)!=m.NativeSha256) throw new InvalidOperationException("Checkpoint model is missing or changed. Resume was rejected.");
        if(plan.Output.NativePath is { } output&&Path.IsPathFullyQualified(output)&&Path.GetFullPath(output).Equals(Path.GetFullPath(m.NativePath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resume must save a separate final model, never overwrite its checkpoint.");
        if(plan.DrawingSourceSha256!=m.OriginalPlan.DrawingSourceSha256||plan.SourceModelPath!=m.OriginalPlan.SourceModelPath)
            throw new InvalidOperationException("Source model/drawing identity changed. This checkpoint cannot be reused.");
        if(plan.SourceModelPath is { } source&&(!File.Exists(source)||DrawingPlanValidation.FileHash(source)!=m.SourceModelSha256))
            throw new InvalidOperationException("The original source model changed after the checkpoint.");
        var ids=plan.Operations.Take(m.CompletedOperationCount).Select(o=>o.Id).ToHashSet(StringComparer.Ordinal);
        if(m.FeatureReferences.Count!=ids.Count||m.FeatureReferences.Select(r=>r.OperationId).Distinct(StringComparer.Ordinal).Count()!=ids.Count||m.FeatureReferences.Any(r=>!ids.Contains(r.OperationId)))
            throw new InvalidOperationException("Checkpoint feature map is incomplete.");
        if(m.FormatVersion>=2)
        {
            if(m.WriteState!=CheckpointWriteState.Complete||!m.ModelReopened)
                throw new InvalidOperationException("Checkpoint was not atomically completed and reopened; resume was rejected.");
            if(m.CompletedOperationIds.Count!=m.CompletedOperationCount||!m.CompletedOperationIds.SequenceEqual(plan.Operations.Take(m.CompletedOperationCount).Select(o=>o.Id),StringComparer.Ordinal))
                throw new InvalidOperationException("Checkpoint completed-operation identity is incomplete or stale.");
            if(!string.Equals(m.SourceSha256,plan.DrawingSourceSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Checkpoint source SHA-256 differs from the current source.");
            if(plan.DrawingSourceSha256 is not null&&string.IsNullOrWhiteSpace(plan.Recovery.SourceRevisionId))
                throw new InvalidOperationException("Drawing-backed resume requires the current source revision identity.");
            if(!string.Equals(m.SourceRevisionId,plan.Recovery.SourceRevisionId,StringComparison.Ordinal))
                throw new InvalidOperationException("Checkpoint source revision differs from the current source revision.");
            if(string.IsNullOrWhiteSpace(m.TypedPlanFingerprint)||string.IsNullOrWhiteSpace(m.CompletedPrefixFingerprint)||
               !m.CompletedPrefixFingerprint.Equals(PrefixFingerprint(plan,m.CompletedOperationCount),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Checkpoint native-prefix identity is missing or stale; this snapshot cannot be truncated to an earlier operation boundary.");
            if(m.InFlightState is InFlightOperationState.Running or InFlightOperationState.Uncertain)
                throw new InvalidOperationException("Checkpoint records an unresolved in-flight COM operation; probe its actual outcome before reuse.");
        }
        return m;
    }
}
