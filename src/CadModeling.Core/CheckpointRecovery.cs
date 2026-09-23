using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using CadModeling.Ir;

namespace CadModeling.Core;

public enum CheckpointWriteState { Legacy, Writing, Complete }
public enum InFlightOperationState { None, Running, Committed, Failed, Uncertain }

public sealed record CheckpointEnvironmentIdentity
{
    public required string CompilerVersion { get; init; }
    public required string ExecutorVersion { get; init; }
    public required string VerifierVersion { get; init; }
    public string? RulesFingerprint { get; init; }
    public string? SolidWorksRevision { get; init; }
}

public static class ComponentContentIdentity
{
    public static string ForType(Type type,string roleContract)
    {
        ArgumentNullException.ThrowIfNull(type);ArgumentException.ThrowIfNullOrWhiteSpace(roleContract);
        var location=type.Assembly.Location;
        if(string.IsNullOrWhiteSpace(location)||!File.Exists(location))
            throw new InvalidOperationException($"Cannot fingerprint loaded component '{type.FullName}' because its assembly file is unavailable.");
        return ForFile(location,roleContract+"|"+type.FullName);
    }

    public static string ForFile(string path,string roleContract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);ArgumentException.ThrowIfNullOrWhiteSpace(roleContract);
        var full=Path.GetFullPath(path);
        if(!File.Exists(full))throw new FileNotFoundException("Component binary is unavailable for content identity.",full);
        var assemblySha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)));
        return Digest(roleContract+"|"+assemblySha);
    }

    public static string Rules(params string[] contracts)=>Digest(string.Join("|",contracts));
    private static string Digest(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record CheckpointEvidenceState
{
    public required string EvidenceId { get; init; }
    public required string Producer { get; init; }
    public required string Status { get; init; }
    public string? SourceSha256 { get; init; }
    public string? SourceRevisionId { get; init; }
    public string? ModelSha256 { get; init; }
    public string? PlanFingerprint { get; init; }
    public string? RequiredScopeFingerprint { get; init; }
    public bool ModelReopened { get; init; }
}

public sealed record ResumeDecision
{
    public required IReadOnlyList<string> ReusedOperationIds { get; init; }
    public required IReadOnlyList<string> ReplayedOperationIds { get; init; }
    public required IReadOnlyList<string> ReusedEvidenceIds { get; init; }
    public required IReadOnlyList<string> ReverificationEvidenceIds { get; init; }
    public string? EarliestInvalidatedOperationId { get; init; }
    public required IReadOnlyList<string> InvalidationReasons { get; init; }
    public string InFlightHandling { get; init; } = "none";
    public bool RequiresCleanRebuild { get; init; }
    public int InvalidReuseCount { get; init; }
    public bool CanResume => !RequiresCleanRebuild && InvalidReuseCount == 0;
}

public sealed record PauseSnapshot(bool PauseRequested,string? InFlightOperationId,InFlightOperationState InFlightState);

/// <summary>
/// Pure boundary gate used by orchestration/tests. RequestPause never interrupts a synchronous COM call;
/// it only prevents submission of the next operation. While a call is active its outcome is uncertain
/// until CompleteOperation records the actual boundary result.
/// </summary>
public sealed class PauseExecutionGate
{
    private bool _pauseRequested;
    private string? _inFlight;
    private InFlightOperationState _state=InFlightOperationState.None;

    public void RequestPause()
    {
        _pauseRequested=true;
        if(_inFlight is not null)_state=InFlightOperationState.Uncertain;
    }

    public bool TryBeginOperation(string operationId)
    {
        if(string.IsNullOrWhiteSpace(operationId))throw new ArgumentException("Operation id is required.",nameof(operationId));
        if(_inFlight is not null)throw new InvalidOperationException("A COM operation is already in flight.");
        if(_pauseRequested)return false;
        _inFlight=operationId;_state=InFlightOperationState.Running;return true;
    }

    public void CompleteOperation(string operationId,bool committed)
    {
        if(_inFlight is null||!_inFlight.Equals(operationId,StringComparison.Ordinal))
            throw new InvalidOperationException("Completion does not match the in-flight operation.");
        _state=committed?InFlightOperationState.Committed:InFlightOperationState.Failed;
        _inFlight=null;
    }

    public PauseSnapshot Snapshot()=>new(_pauseRequested,_inFlight,
        _inFlight is null&&_state==InFlightOperationState.Running?InFlightOperationState.Uncertain:_state);
}

public static partial class ModelingRecovery
{
    public static string TypedPlanFingerprint(ModelingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var normalized=plan with
        {
            Output=new OutputSpec(),
            Recovery=new ModelingRecoveryOptions{Enabled=plan.Recovery.Enabled,SourceRevisionId=plan.Recovery.SourceRevisionId}
        };
        return ModelingPlanIdentity.Fingerprint(normalized);
    }

    public static string PrefixFingerprint(ModelingPlan plan,int completedOperationCount)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if(completedOperationCount<0||completedOperationCount>plan.Operations.Count)throw new ArgumentOutOfRangeException(nameof(completedOperationCount));
        var normalized=plan with
        {
            Operations=plan.Operations.Take(completedOperationCount).ToArray(),
            Output=new OutputSpec(),
            Recovery=new ModelingRecoveryOptions{Enabled=plan.Recovery.Enabled,SourceRevisionId=plan.Recovery.SourceRevisionId},
            // Validation/acceptance changes invalidate evidence, not an already saved native prefix.
            Verification=new ModelVerificationSpec(),Acceptance=new AcceptanceSpec(),DrawingContext=null,DrawingBindingDigest=null
        };
        return ModelingPlanIdentity.Fingerprint(normalized);
    }

    public static ResumeDecision DecideResume(ModelingPlan current,ModelingCheckpointManifest checkpoint,
        InFlightOperationState probedInFlightState=InFlightOperationState.None,string? probedInFlightOperationId=null,
        CheckpointEnvironmentIdentity? currentEnvironment=null)
    {
        ArgumentNullException.ThrowIfNull(current);ArgumentNullException.ThrowIfNull(checkpoint);
        var reasons=new List<string>();var reusableCount=Math.Min(checkpoint.CompletedOperationCount,current.Operations.Count);
        var clean=false;var invalidReuse=0;

        if(!string.Equals(checkpoint.SourceSha256,current.DrawingSourceSha256,StringComparison.OrdinalIgnoreCase))
        { reasons.Add("source-sha-changed");clean=true;reusableCount=0; }
        if(checkpoint.FormatVersion>=2&&!string.Equals(checkpoint.SourceRevisionId,current.Recovery.SourceRevisionId,StringComparison.Ordinal))
        { reasons.Add("source-revision-changed");clean=true;reusableCount=0; }
        if(checkpoint.WriteState is CheckpointWriteState.Writing||checkpoint.FormatVersion>=2&&!checkpoint.ModelReopened)
        { reasons.Add("checkpoint-incomplete-or-not-reopened");clean=true;reusableCount=0;invalidReuse++; }
        var verifierEnvironmentCompatible=true;
        if(checkpoint.FormatVersion>=2&&currentEnvironment is not null)
        {
            if(checkpoint.Environment is null)
            {reasons.Add("checkpoint-environment-identity-missing");clean=true;reusableCount=0;invalidReuse++;verifierEnvironmentCompatible=false;}
            else
            {
                var executionEnvironmentChanged=!checkpoint.Environment.CompilerVersion.Equals(currentEnvironment.CompilerVersion,StringComparison.Ordinal)||
                    !checkpoint.Environment.ExecutorVersion.Equals(currentEnvironment.ExecutorVersion,StringComparison.Ordinal)||
                    !string.Equals(checkpoint.Environment.RulesFingerprint,currentEnvironment.RulesFingerprint,StringComparison.Ordinal)||
                    !string.Equals(checkpoint.Environment.SolidWorksRevision,currentEnvironment.SolidWorksRevision,StringComparison.Ordinal);
                if(executionEnvironmentChanged)
                {reasons.Add("compiler-executor-or-solidworks-version-changed");clean=true;reusableCount=0;invalidReuse++;}
                verifierEnvironmentCompatible=checkpoint.Environment.VerifierVersion.Equals(currentEnvironment.VerifierVersion,StringComparison.Ordinal);
                if(!verifierEnvironmentCompatible)reasons.Add("verifier-version-changed-reverify-evidence");
            }
        }

        if(!clean)
        {
            for(var i=0;i<reusableCount;i++)
            {
                var oldOperation=checkpoint.OriginalPlan.Operations[i];
                var currentOperation=current.Operations[i];
                var oldJson=JsonSerializer.Serialize(oldOperation,oldOperation.GetType(),ModelingIrJson.Options);
                var nowJson=JsonSerializer.Serialize(currentOperation,currentOperation.GetType(),ModelingIrJson.Options);
                if(oldJson==nowJson)continue;
                reasons.Add("operation-prefix-changed:"+current.Operations[i].Id);
                // This manifest contains one native snapshot at CompletedOperationCount.  It is
                // unsafe to pretend that file is an earlier checkpoint by merely lowering the
                // operation count; downstream features are already present in the B-Rep/tree.
                clean=true;reusableCount=0;invalidReuse++;break;
            }
            if(checkpoint.FormatVersion>=2&&reusableCount==checkpoint.CompletedOperationCount&&
               !string.Equals(checkpoint.CompletedPrefixFingerprint,PrefixFingerprint(current,reusableCount),StringComparison.OrdinalIgnoreCase))
            { reasons.Add("prefix-identity-changed");clean=true;reusableCount=0;invalidReuse++; }
        }

        var inFlightState=probedInFlightState==InFlightOperationState.None?checkpoint.InFlightState:probedInFlightState;
        var inFlightId=probedInFlightOperationId??checkpoint.InFlightOperationId;
        var inFlightHandling="none";
        if(inFlightState is InFlightOperationState.Running or InFlightOperationState.Uncertain)
        {
            reasons.Add("in-flight-outcome-unresolved"+(!string.IsNullOrWhiteSpace(inFlightId)?":"+inFlightId:""));
            inFlightHandling="probe-actual-native-state-before-replay";clean=true;reusableCount=0;invalidReuse++;
        }
        else if(inFlightState==InFlightOperationState.Committed)
            inFlightHandling="do-not-blindly-replay-confirmed-committed-operation";
        else if(inFlightState==InFlightOperationState.Failed)
            inFlightHandling="replay-from-last-confirmed-checkpoint-boundary";

        var reused=current.Operations.Take(reusableCount).Select(o=>o.Id).ToArray();
        var replay=current.Operations.Skip(reusableCount).Select(o=>o.Id).ToArray();
        var typedSame=checkpoint.TypedPlanFingerprint is {Length:>0} fp&&fp.Equals(TypedPlanFingerprint(current),StringComparison.OrdinalIgnoreCase);
        var evidenceReusable=checkpoint.FormatVersion>=2&&typedSame&&!clean&&verifierEnvironmentCompatible
            ? checkpoint.EvidenceStates.Where(EvidenceMatches).Select(e=>e.EvidenceId).ToArray():[];
        var reverification=checkpoint.EvidenceStates.Select(e=>e.EvidenceId).Except(evidenceReusable,StringComparer.Ordinal).ToArray();
        if(checkpoint.FormatVersion<2&&checkpoint.EvidenceStates.Count>0)reasons.Add("legacy-evidence-identity-not-reusable");
        if(!typedSame&&checkpoint.FormatVersion>=2)reasons.Add("full-plan-changed-reverify-plan-bound-evidence");
        return new()
        {
            ReusedOperationIds=reused,ReplayedOperationIds=replay,ReusedEvidenceIds=evidenceReusable,
            ReverificationEvidenceIds=reverification,EarliestInvalidatedOperationId=replay.FirstOrDefault(),
            InvalidationReasons=reasons,InFlightHandling=inFlightHandling,RequiresCleanRebuild=clean,
            InvalidReuseCount=invalidReuse
        };

        bool EvidenceMatches(CheckpointEvidenceState evidence)=>
            (evidence.SourceSha256 is null||string.Equals(evidence.SourceSha256,current.DrawingSourceSha256,StringComparison.OrdinalIgnoreCase))&&
            (evidence.SourceRevisionId is null||string.Equals(evidence.SourceRevisionId,current.Recovery.SourceRevisionId,StringComparison.Ordinal))&&
            (evidence.PlanFingerprint is null||evidence.PlanFingerprint.Equals(TypedPlanFingerprint(current),StringComparison.OrdinalIgnoreCase))&&
            (evidence.ModelSha256 is null||evidence.ModelSha256.Equals(checkpoint.NativeSha256,StringComparison.OrdinalIgnoreCase))&&
            evidence.ModelReopened;
    }

    private static string Digest(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
