using System.Text.Json;
using CadModeling.Ir;
namespace CadModeling.Core;

public sealed record ModelingCheckpointManifest
{
    public int FormatVersion { get; init; } = 1;
    public required string NativePath { get; init; }
    public required string NativeSha256 { get; init; }
    public required ModelingPlan OriginalPlan { get; init; }
    public string? SourceModelSha256 { get; init; }
    public int CompletedOperationCount { get; init; }
    public required IReadOnlyList<StableFeatureReference> FeatureReferences { get; init; }
    public required GeometrySnapshot Geometry { get; init; }
}
public sealed record ModelingRecoveryState(string? ManifestPath, string? CheckpointPath,
    IReadOnlyList<string> CompletedOperationIds, IReadOnlyList<string> RemainingOperationIds,
    string? FailedOperationId, bool CanResume, string Message);

public static class ModelingRecovery
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
        if(m.FormatVersion!=1||m.CompletedOperationCount<1||m.CompletedOperationCount>plan.Operations.Count||m.CompletedOperationCount>m.OriginalPlan.Operations.Count)
            throw new InvalidOperationException("Checkpoint format or completed operation count is invalid.");
        if(!Path.IsPathFullyQualified(m.NativePath)||!m.NativePath.EndsWith(".sldprt",StringComparison.OrdinalIgnoreCase)||!File.Exists(m.NativePath)||
           DrawingPlanValidation.FileHash(m.NativePath)!=m.NativeSha256) throw new InvalidOperationException("Checkpoint model is missing or changed. Resume was rejected.");
        if(plan.Output.NativePath is { } output&&Path.IsPathFullyQualified(output)&&Path.GetFullPath(output).Equals(Path.GetFullPath(m.NativePath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resume must save a separate final model, never overwrite its checkpoint.");
        string Json<T>(T value)=>JsonSerializer.Serialize(value,ModelingIrJson.Options);
        if(Json(plan.Operations.Take(m.CompletedOperationCount).ToArray())!=Json(m.OriginalPlan.Operations.Take(m.CompletedOperationCount).ToArray()))
            throw new InvalidOperationException("An operation before the checkpoint changed. Select an earlier compatible checkpoint or rebuild.");
        if(Json(plan.DrawingContext)!=Json(m.OriginalPlan.DrawingContext)||Json(plan.Verification)!=Json(m.OriginalPlan.Verification)||
           plan.DrawingSourceSha256!=m.OriginalPlan.DrawingSourceSha256||plan.SourceModelPath!=m.OriginalPlan.SourceModelPath||
           Json(plan.Acceptance)!=Json(m.OriginalPlan.Acceptance))
            throw new InvalidOperationException("Source requirements, verification or acceptance changed. This checkpoint cannot be reused.");
        if(plan.SourceModelPath is { } source&&(!File.Exists(source)||DrawingPlanValidation.FileHash(source)!=m.SourceModelSha256))
            throw new InvalidOperationException("The original source model changed after the checkpoint.");
        var ids=plan.Operations.Take(m.CompletedOperationCount).Select(o=>o.Id).ToHashSet(StringComparer.Ordinal);
        if(m.FeatureReferences.Count!=ids.Count||m.FeatureReferences.Select(r=>r.OperationId).Distinct(StringComparer.Ordinal).Count()!=ids.Count||m.FeatureReferences.Any(r=>!ids.Contains(r.OperationId)))
            throw new InvalidOperationException("Checkpoint feature map is incomplete.");
        return m;
    }
}
