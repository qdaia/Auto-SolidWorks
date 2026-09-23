namespace CadModeling.Core;

public enum CacheArtifactKind
{
    SourceFacts,TypedPlan,NativeModel,SavedModelInspection,StepExport,Screenshot,ProjectionSection,
    MeasurementConnectivity,CoverageReport,ModelDiff,RepairValidation,NativeEditability
}

public sealed record ArtifactDependency(string Name,string Fingerprint);

public sealed record CachedArtifact
{
    public const string ContractVersion="autosolidworks.artifact-cache/v1";
    public string Contract { get; init; }=ContractVersion;
    public required string ArtifactId { get; init; }
    public CacheArtifactKind Kind { get; init; }
    public required string SourceRevisionId { get; init; }
    public long RequestSequence { get; init; }
    public required IReadOnlyList<ArtifactDependency> Dependencies { get; init; }
}

public sealed record FreshnessContext
{
    public required string SourceRevisionId { get; init; }
    public long CurrentRequestSequence { get; init; }
    public required IReadOnlyList<ArtifactDependency> Dependencies { get; init; }
}

public sealed record DependencyComparison(string Name,string? Expected,string? Actual,bool Match,string Reason);

public sealed record FreshnessDecision
{
    public required string ArtifactId { get; init; }
    public CacheArtifactKind Kind { get; init; }
    public bool CacheHit { get; init; }
    public required IReadOnlyList<DependencyComparison> Dependencies { get; init; }
    public required IReadOnlyList<string> InvalidationReasons { get; init; }
    public required IReadOnlyList<CacheArtifactKind> DownstreamInvalidations { get; init; }
    public required IReadOnlyList<string> ReusedScope { get; init; }
    public required IReadOnlyList<string> RecomputedScope { get; init; }
}

public static class ArtifactFreshness
{
    private static readonly IReadOnlyDictionary<CacheArtifactKind,IReadOnlySet<string>> RequiredDependencies=
        new Dictionary<CacheArtifactKind,IReadOnlySet<string>>
        {
            [CacheArtifactKind.SourceFacts]=Set("source.sha256","source.revision","vision.configuration","vision.model"),
            [CacheArtifactKind.TypedPlan]=Set("source.sha256","source.revision","source_facts.fingerprint","planner.version"),
            [CacheArtifactKind.NativeModel]=Set("source.sha256","source.revision","plan.fingerprint","compiler.version","executor.version","solidworks.version"),
            [CacheArtifactKind.SavedModelInspection]=Set("model.sha256","inspection.request.fingerprint","inspector.version","solidworks.version","model.reopened"),
            [CacheArtifactKind.StepExport]=Set("model.sha256","exporter.version"),
            [CacheArtifactKind.Screenshot]=Set("model.sha256","view.fingerprint","renderer.version"),
            [CacheArtifactKind.ProjectionSection]=Set("source.sha256","source.revision","model.sha256","projection_spec.fingerprint","validator.version","model.reopened"),
            [CacheArtifactKind.MeasurementConnectivity]=Set("source.sha256","source.revision","model.sha256","required_scope.fingerprint","validator.version","model.reopened"),
            [CacheArtifactKind.CoverageReport]=Set("source.sha256","source.revision","model.sha256","plan.fingerprint","required_scope.fingerprint","validator.version","model.reopened"),
            [CacheArtifactKind.ModelDiff]=Set("baseline.model.sha256","candidate.model.sha256","required_scope.fingerprint","diff.version","baseline.reopened","candidate.reopened"),
            [CacheArtifactKind.RepairValidation]=Set("source.sha256","source.revision","baseline.model.sha256","candidate.model.sha256","plan.fingerprint","required_scope.fingerprint","repair.version","execution_receipt.fingerprint","candidate.reopened"),
            [CacheArtifactKind.NativeEditability]=Set("model.sha256","feature_tree.fingerprint","executor.version","solidworks.version","model.reopened")
        };

    public static FreshnessDecision Evaluate(CachedArtifact cached,FreshnessContext current)
    {
        ArgumentNullException.ThrowIfNull(cached);ArgumentNullException.ThrowIfNull(current);
        var reasons=new List<string>();var comparisons=new List<DependencyComparison>();
        if(cached.Contract!=CachedArtifact.ContractVersion)reasons.Add("cache-contract-version-changed");
        if(string.IsNullOrWhiteSpace(cached.ArtifactId)||string.IsNullOrWhiteSpace(cached.SourceRevisionId)||cached.RequestSequence<0)
            reasons.Add("cached-artifact-identity-invalid");
        if(!cached.SourceRevisionId.Equals(current.SourceRevisionId,StringComparison.Ordinal))reasons.Add("source-revision-changed");
        // Request generation is a write-ownership concern, not an input dependency.  A published
        // artifact remains readable by later equivalent requests when every declared dependency matches.

        var old=Unique(cached.Dependencies,"cached",reasons);
        var now=Unique(current.Dependencies,"current",reasons);
        if(!RequiredDependencies.TryGetValue(cached.Kind,out var required))
        {
            reasons.Add("unknown-cache-artifact-kind");
            required=new HashSet<string>(StringComparer.Ordinal);
        }
        foreach(var name in required.Order(StringComparer.Ordinal))
        {
            old.TryGetValue(name,out var expected);now.TryGetValue(name,out var actual);
            var match=expected is not null&&actual is not null&&expected.Equals(actual,StringComparison.Ordinal);
            var reason=expected is null?"cached-artifact-never-declared-required-input":actual is null?"current-required-input-missing":
                match?"matched":"dependency-changed";
            comparisons.Add(new(name,expected,actual,match,reason));
            if(!match)reasons.Add(name+":"+reason);
        }
        foreach(var dependency in old.Keys.Except(required,StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var expected=old[dependency];now.TryGetValue(dependency,out var actual);
            var match=actual is not null&&expected.Equals(actual,StringComparison.Ordinal);
            comparisons.Add(new(dependency,expected,actual,match,match?"matched":"declared-dependency-changed"));
            if(!match)reasons.Add(dependency+":declared-dependency-changed");
        }
        var hit=reasons.Count==0;
        return new()
        {
            ArtifactId=cached.ArtifactId,Kind=cached.Kind,CacheHit=hit,Dependencies=comparisons,
            InvalidationReasons=reasons.Distinct(StringComparer.Ordinal).ToArray(),
            DownstreamInvalidations=hit?[]:Downstream(cached.Kind),
            ReusedScope=hit?[cached.ArtifactId]:[],RecomputedScope=hit?[]:[cached.ArtifactId]
        };
    }

    public static bool CanPublishCompletion(CachedArtifact completed,string currentSourceRevisionId,long currentRequestSequence,out string reason)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if(!completed.SourceRevisionId.Equals(currentSourceRevisionId,StringComparison.Ordinal))
        {reason="completion belongs to an older/different source revision";return false;}
        if(completed.RequestSequence<currentRequestSequence)
        {reason="older request completed after a newer request became current";return false;}
        reason="current revision/request owns this completion";return true;
    }

    private static Dictionary<string,string> Unique(IReadOnlyList<ArtifactDependency> values,string side,List<string> reasons)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var item in values)
        {
            if(string.IsNullOrWhiteSpace(item.Name)||string.IsNullOrWhiteSpace(item.Fingerprint))
            {reasons.Add(side+"-dependency-invalid");continue;}
            if(!result.TryAdd(item.Name,item.Fingerprint))reasons.Add(side+"-dependency-duplicated:"+item.Name);
        }
        return result;
    }

    private static IReadOnlySet<string> Set(params string[] values)=>values.ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<CacheArtifactKind> Downstream(CacheArtifactKind changed)=>changed switch
    {
        CacheArtifactKind.SourceFacts=>Enum.GetValues<CacheArtifactKind>().Where(k=>k!=CacheArtifactKind.SourceFacts).ToArray(),
        CacheArtifactKind.TypedPlan=>[CacheArtifactKind.NativeModel,CacheArtifactKind.StepExport,CacheArtifactKind.Screenshot,CacheArtifactKind.ProjectionSection,CacheArtifactKind.MeasurementConnectivity,CacheArtifactKind.CoverageReport,CacheArtifactKind.ModelDiff,CacheArtifactKind.RepairValidation,CacheArtifactKind.NativeEditability],
        CacheArtifactKind.NativeModel=>[CacheArtifactKind.StepExport,CacheArtifactKind.Screenshot,CacheArtifactKind.ProjectionSection,CacheArtifactKind.MeasurementConnectivity,CacheArtifactKind.CoverageReport,CacheArtifactKind.ModelDiff,CacheArtifactKind.RepairValidation,CacheArtifactKind.NativeEditability],
        CacheArtifactKind.CoverageReport=>[CacheArtifactKind.RepairValidation],
        CacheArtifactKind.ModelDiff=>[CacheArtifactKind.RepairValidation],
        _=>[]
    };
}

/// <summary>
/// Thread-safe published-artifact store.  Fresh reads are dependency based; publishing and the
/// request-generation ownership check occur under the same lock so an older in-flight request
/// cannot win a check-then-write race after a newer request becomes current.
/// </summary>
public sealed class ArtifactCacheStore
{
    private readonly object _gate=new();
    private readonly Dictionary<string,CachedArtifact> _published=new(StringComparer.Ordinal);
    private readonly Dictionary<string,object> _payloads=new(StringComparer.Ordinal);
    private readonly Dictionary<string,(string Revision,long Sequence)> _current=new(StringComparer.Ordinal);

    public void MarkCurrent(string slot,string sourceRevisionId,long requestSequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);ArgumentException.ThrowIfNullOrWhiteSpace(sourceRevisionId);
        if(requestSequence<0)throw new ArgumentOutOfRangeException(nameof(requestSequence));
        lock(_gate)
        {
            if(_current.TryGetValue(slot,out var prior)&&
               (requestSequence<prior.Sequence||requestSequence==prior.Sequence&&!prior.Revision.Equals(sourceRevisionId,StringComparison.Ordinal)))
                throw new InvalidOperationException("Cannot move an artifact slot back to an older generation or reuse one generation for another source revision.");
            _current[slot]=(sourceRevisionId,requestSequence);
        }
    }

    public bool TryPublish(string slot,CachedArtifact completed,out string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);ArgumentNullException.ThrowIfNull(completed);
        lock(_gate)
        {
            if(!_current.TryGetValue(slot,out var current))
            {reason="artifact slot has no current request generation";return false;}
            if(!ArtifactFreshness.CanPublishCompletion(completed,current.Revision,current.Sequence,out reason))return false;
            _published[slot]=completed;return true;
        }
    }

    public bool TryPublish<T>(string slot,CachedArtifact completed,T payload,out string reason) where T:notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);ArgumentNullException.ThrowIfNull(completed);ArgumentNullException.ThrowIfNull(payload);
        lock(_gate)
        {
            if(!_current.TryGetValue(slot,out var current))
            {reason="artifact slot has no current request generation";return false;}
            if(!ArtifactFreshness.CanPublishCompletion(completed,current.Revision,current.Sequence,out reason))return false;
            _published[slot]=completed;_payloads[slot]=payload;return true;
        }
    }

    public FreshnessDecision? TryGetFresh(string slot,FreshnessContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);ArgumentNullException.ThrowIfNull(context);
        lock(_gate)return _published.TryGetValue(slot,out var cached)?ArtifactFreshness.Evaluate(cached,context):null;
    }

    public bool TryGetFresh<T>(string slot,FreshnessContext context,out T? payload,out FreshnessDecision? decision) where T:class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);ArgumentNullException.ThrowIfNull(context);
        lock(_gate)
        {
            payload=null;decision=null;
            if(!_published.TryGetValue(slot,out var cached))return false;
            decision=ArtifactFreshness.Evaluate(cached,context);
            if(!decision.CacheHit||!_payloads.TryGetValue(slot,out var raw)||raw is not T typed)return false;
            payload=typed;return true;
        }
    }

    public CachedArtifact? GetPublished(string slot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        lock(_gate)return _published.GetValueOrDefault(slot);
    }
}
