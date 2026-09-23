using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CadModeling.Ir;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<RepairKind>))]
public enum RepairKind { FilletSelection, ThroughDirection, GeometryRefRebind }

[JsonConverter(typeof(JsonStringEnumConverter<RepairPhase>))]
public enum RepairPhase { InitialExecution, Geometry }

[JsonConverter(typeof(JsonStringEnumConverter<RepairAttemptStatus>))]
public enum RepairAttemptStatus { Prepared, Rejected, BudgetExceeded, DuplicateFailure }

[JsonConverter(typeof(JsonStringEnumConverter<RepairValidationStatus>))]
public enum RepairValidationStatus { Passed, Failed, Unverifiable }

public sealed record RepairBudget
{
    public int MaxInitialExecutionAttempts { get; init; } = 2;
    public int MaxGeometryAttempts { get; init; } = 3;
    public int MaxTotalAttempts { get; init; } = 5;
    public int MaxElapsedSeconds { get; init; } = 120;
}

public sealed record RepairRule
{
    public required RepairKind Kind { get; init; }
    public required RepairPhase Phase { get; init; }
    public IReadOnlyList<string> RequiredEvidence { get; init; } = [];
    public IReadOnlyList<string> AllowedEdits { get; init; } = [];
    public IReadOnlyList<string> LockedRequirements { get; init; } = [];
    public IReadOnlyList<string> RequiredPostChecks { get; init; } = [];
}

public sealed record RepairRequest
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string RequestId { get; init; }
    public required string FailureId { get; init; }
    public RepairKind Kind { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string FailureFingerprint { get; init; }
    public string? NewEvidenceFingerprint { get; init; }
    public required string TargetOperationId { get; init; }
    public bool? DesiredReverseDirection { get; init; }
    public IReadOnlyList<GeometryRefResolution> GeometryResolutions { get; init; } = [];
}

public sealed record RepairAttempt
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string RequestId { get; init; }
    public RepairKind Kind { get; init; }
    public RepairAttemptStatus Status { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string SourceSha256 { get; init; }
    public required string FailureFingerprint { get; init; }
    public required string BeforePlanFingerprint { get; init; }
    public string? AfterPlanFingerprint { get; init; }
    public required string BaselineModelSha256 { get; init; }
    public required string FrozenRequiredScopeFingerprint { get; init; }
    public required string LockedRequirementsFingerprint { get; init; }
    public string? AfterLockedRequirementsFingerprint { get; init; }
    public required string TargetOperationId { get; init; }
    public IReadOnlyList<string> AffectedOperationIds { get; init; } = [];
    public IReadOnlyList<string> FrozenRequiredFactIds { get; init; } = [];
    public IReadOnlyList<string> FrozenAffectedFactIds { get; init; } = [];
    public IReadOnlyList<string> RequiredPostChecks { get; init; } = [];
    public int AttemptIndex { get; init; }
    public int PhaseAttemptIndex { get; init; }
    public string TypedPlanDiff { get; init; } = string.Empty;
    public string StopReason { get; init; } = string.Empty;
    public ModelingPlan? CandidatePlan { get; init; }
}

public sealed record RepairExecutionEvidence
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string ExecutionId { get; init; }
    public required string AttemptRequestId { get; init; }
    public int AttemptIndex { get; init; }
    public required string BeforePlanFingerprint { get; init; }
    public required string AfterPlanFingerprint { get; init; }
    public required string SourceSha256 { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string BaselineModelSha256 { get; init; }
    public required string CandidateModelSha256 { get; init; }
    public bool CandidateModelReopened { get; init; }
    public required string FrozenRequiredScopeFingerprint { get; init; }
    public IReadOnlyList<string> FrozenRequiredFactIds { get; init; } = [];
    public IReadOnlyList<string> FrozenAffectedFactIds { get; init; } = [];
    /// <summary>Must be "native_executor" in production; "synthetic_fixture" is accepted only for deterministic development tests.</summary>
    public required string EvidenceMode { get; init; }
}

public sealed record RepairCheckResult(
    string CheckId,
    RequirementCheckStatus Status,
    string EvidenceId,
    string SourceSha256,
    string SourceRevisionId,
    string ModelSha256,
    string PlanFingerprint,
    string AttemptRequestId);

public sealed record RepairValidationResult
{
    public required string RequestId { get; init; }
    public RepairValidationStatus Status { get; init; }
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
    public IReadOnlyList<string> MissingOrFailedChecks { get; init; } = [];
    public string Message { get; init; } = string.Empty;
    // Identity binding is copied from the validated attempt/execution receipt so downstream
    // gates never have to treat a naked Passed status as portable evidence.
    public string? ExecutionId { get; init; }
    public int? AttemptIndex { get; init; }
    public string? SourceSha256 { get; init; }
    public string? SourceRevisionId { get; init; }
    public string? BaselineModelSha256 { get; init; }
    public string? CandidateModelSha256 { get; init; }
    public string? BeforePlanFingerprint { get; init; }
    public string? AfterPlanFingerprint { get; init; }
    public string? FrozenRequiredScopeFingerprint { get; init; }
    public IReadOnlyList<string> FrozenRequiredFactIds { get; init; } = [];
    public IReadOnlyList<string> FrozenAffectedFactIds { get; init; } = [];
    public bool CandidateModelReopened { get; init; }
    public string? EvidenceMode { get; init; }
}

public sealed class ConstrainedRepairSession
{
    private readonly RepairBudget _budget;
    private int _totalAttempts;
    private int _initialAttempts;
    private int _geometryAttempts;
    private readonly DateTime _startedUtc=DateTime.UtcNow;
    private readonly Dictionary<string, string?> _lastEvidenceByFailure = new(StringComparer.OrdinalIgnoreCase);

    public ConstrainedRepairSession(RepairBudget? budget = null)
    {
        _budget = budget ?? new();
        if (_budget.MaxInitialExecutionAttempts < 1 || _budget.MaxGeometryAttempts < 1 || _budget.MaxTotalAttempts < 1 || _budget.MaxElapsedSeconds < 1 ||
            _budget.MaxTotalAttempts > _budget.MaxInitialExecutionAttempts + _budget.MaxGeometryAttempts)
            throw new ArgumentException("Repair budgets must be positive and bounded by the phase budgets.", nameof(budget));
    }

    public RepairAttempt Prepare(ModelingPlan plan, RepairRequest request, CoverageReport coverage, FailureLocation location)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("repair.prepare");
        ArgumentNullException.ThrowIfNull(plan);
        Validate(request);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(location);
        var beforeFingerprint = ModelingPlanIdentity.Fingerprint(plan);
        var lockedFingerprint = LockedRequirementsFingerprint(plan);
        var rule = Rule(request.Kind);

        if (!Hash(coverage.SourceSha256) || !Hash(coverage.CandidateModelSha256) || !Hash(coverage.RequiredScopeFingerprint) || coverage.RequiredFactIds.Count == 0 ||
            coverage.RequiredFactIds.Any(string.IsNullOrWhiteSpace) || coverage.RequiredFactIds.Distinct(StringComparer.Ordinal).Count() != coverage.RequiredFactIds.Count)
            return Rejected("T12 repair input must carry a nonempty frozen required-fact scope and source/model identities.");
        if (!coverage.SourceRevisionId.Equals(request.SourceRevisionId, StringComparison.Ordinal))
            return Rejected("T12 coverage belongs to a different source revision.");
        if (!coverage.SourceSha256.Equals(plan.DrawingSourceSha256, StringComparison.OrdinalIgnoreCase))
            return Rejected("T12 coverage source SHA does not match the typed plan's frozen drawing source.");
        if (string.IsNullOrWhiteSpace(coverage.CandidatePlanFingerprint) ||
            !coverage.CandidatePlanFingerprint.Equals(beforeFingerprint, StringComparison.OrdinalIgnoreCase))
            return Rejected("T12 coverage is not bound to the exact pre-repair typed plan fingerprint.");
        if (!location.FailureId.Equals(request.FailureId,StringComparison.Ordinal))
            return Rejected("T13 failure identity does not match this repair request.");
        if (!location.Located || !string.Equals(location.EarliestAffectedOperationId, request.TargetOperationId, StringComparison.Ordinal) &&
                                 !location.DownstreamOperationIds.Contains(request.TargetOperationId, StringComparer.Ordinal))
            return Rejected("T13 did not locate this repair target inside the proven affected scope.");
        if (location.SourceFactIds.Any(id=>!coverage.RequiredFactIds.Contains(id,StringComparer.Ordinal)))
            return Rejected("T13 source lineage is not fully represented in the current T12 required-fact inventory.");
        if (request.GeometryResolutions.Any(resolution=>!resolution.ResolvedModelSha256.Equals(coverage.CandidateModelSha256,StringComparison.OrdinalIgnoreCase)||
                resolution.Reference.SourceRevisionId is {Length:>0} revision&&!revision.Equals(request.SourceRevisionId,StringComparison.Ordinal)))
            return Rejected("Geometry repair evidence belongs to another model/source revision.");
        if (_lastEvidenceByFailure.TryGetValue(request.FailureFingerprint, out var priorEvidence) &&
            string.Equals(priorEvidence, request.NewEvidenceFingerprint, StringComparison.OrdinalIgnoreCase))
            return Attempt(RepairAttemptStatus.DuplicateFailure, "The same failure fingerprint repeated without new evidence; repair loop stopped.");
        if (BudgetExceeded(rule.Phase))
            return Attempt(RepairAttemptStatus.BudgetExceeded, "Repair attempt budget is exhausted.");

        _totalAttempts++;
        var phaseIndex = rule.Phase == RepairPhase.InitialExecution ? ++_initialAttempts : ++_geometryAttempts;
        _lastEvidenceByFailure[request.FailureFingerprint] = request.NewEvidenceFingerprint;

        ModelingPlan candidate;
        string diff;
        try
        {
            (candidate, diff) = request.Kind switch
            {
                RepairKind.FilletSelection => RepairFilletSelection(plan, request),
                RepairKind.ThroughDirection => RepairThroughDirection(plan, request),
                RepairKind.GeometryRefRebind => RepairGeometryRef(plan, request),
                _ => throw new InvalidOperationException("Unsupported repair kind.")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Attempt(RepairAttemptStatus.Rejected, ex.Message, _totalAttempts, phaseIndex);
        }

        var afterLocked = LockedRequirementsFingerprint(candidate);
        if (!lockedFingerprint.Equals(afterLocked, StringComparison.OrdinalIgnoreCase))
            return Attempt(RepairAttemptStatus.Rejected, "Candidate repair changed locked source requirements/acceptance and was rejected.", _totalAttempts, phaseIndex);
        var beforeOperation = plan.Operations.Single(operation => operation.Id == request.TargetOperationId);
        var afterOperation = candidate.Operations.Single(operation => operation.Id == request.TargetOperationId);
        if (!RepairPolicy.IsAllowedOperationMutation(beforeOperation, afterOperation, request.Kind))
            return Attempt(RepairAttemptStatus.Rejected, "Candidate repair changed fields outside the rule's explicit edit allowance.", _totalAttempts, phaseIndex);

        return new()
        {
            RequestId = request.RequestId,
            Kind = request.Kind,
            Status = RepairAttemptStatus.Prepared,
            SourceRevisionId = request.SourceRevisionId,
            SourceSha256 = coverage.SourceSha256,
            FailureFingerprint = request.FailureFingerprint,
            BeforePlanFingerprint = beforeFingerprint,
            AfterPlanFingerprint = ModelingPlanIdentity.Fingerprint(candidate),
            BaselineModelSha256 = coverage.CandidateModelSha256,
            FrozenRequiredScopeFingerprint = coverage.RequiredScopeFingerprint,
            LockedRequirementsFingerprint = lockedFingerprint,
            AfterLockedRequirementsFingerprint = afterLocked,
            TargetOperationId = request.TargetOperationId,
            AffectedOperationIds = location.DownstreamOperationIds,
            FrozenRequiredFactIds = coverage.RequiredFactIds.ToArray(),
            FrozenAffectedFactIds = location.SourceFactIds.ToArray(),
            RequiredPostChecks = rule.RequiredPostChecks,
            AttemptIndex = _totalAttempts,
            PhaseAttemptIndex = phaseIndex,
            TypedPlanDiff = diff,
            StopReason = "Prepared only; execution and required post-checks must run before this repair can be accepted.",
            CandidatePlan = candidate
        };

        RepairAttempt Rejected(string reason) => Attempt(RepairAttemptStatus.Rejected, reason);
        RepairAttempt Attempt(RepairAttemptStatus status, string reason, int? attemptIndex = null, int? phaseAttemptIndex = null) => new()
        {
            RequestId = request.RequestId,
            Kind = request.Kind,
            Status = status,
            SourceRevisionId = request.SourceRevisionId,
            SourceSha256 = coverage.SourceSha256,
            FailureFingerprint = request.FailureFingerprint,
            BeforePlanFingerprint = beforeFingerprint,
            BaselineModelSha256 = coverage.CandidateModelSha256,
            FrozenRequiredScopeFingerprint = coverage.RequiredScopeFingerprint,
            LockedRequirementsFingerprint = lockedFingerprint,
            TargetOperationId = request.TargetOperationId,
            AffectedOperationIds = location.DownstreamOperationIds,
            FrozenRequiredFactIds = coverage.RequiredFactIds.ToArray(),
            FrozenAffectedFactIds = location.SourceFactIds.ToArray(),
            RequiredPostChecks = rule.RequiredPostChecks,
            AttemptIndex = attemptIndex ?? _totalAttempts,
            PhaseAttemptIndex = phaseAttemptIndex ?? (rule.Phase == RepairPhase.InitialExecution ? _initialAttempts : _geometryAttempts),
            StopReason = reason
        };
    }

    private bool BudgetExceeded(RepairPhase phase) => (DateTime.UtcNow-_startedUtc).TotalSeconds>_budget.MaxElapsedSeconds ||
        _totalAttempts >= _budget.MaxTotalAttempts ||
        phase == RepairPhase.InitialExecution && _initialAttempts >= _budget.MaxInitialExecutionAttempts ||
        phase == RepairPhase.Geometry && _geometryAttempts >= _budget.MaxGeometryAttempts;

    private static (ModelingPlan Plan, string Diff) RepairFilletSelection(ModelingPlan plan, RepairRequest request)
    {
        var operation = Find<NativeFeatureOperation>(plan, request.TargetOperationId);
        if (operation.Options.Kind != NativeFeatureKind.Fillet)
            throw new InvalidOperationException("Fillet selection repair requires a native fillet operation.");
        var queries = QueriesFromUniqueResolutions(request.GeometryResolutions, EntityKind.Edge);
        if (queries.Count == 0) throw new InvalidOperationException("Fillet repair requires at least one uniquely resolved target edge from T06/T13 evidence.");
        var updated = operation with { Options = operation.Options with { Selections = queries } };
        return (Replace(plan, updated), $"{operation.Id}.options.selections: {operation.Options.Selections.Count} -> {queries.Count}; radius_mm locked at {operation.Options.RadiusMm:R}");
    }

    private static (ModelingPlan Plan, string Diff) RepairThroughDirection(ModelingPlan plan, RepairRequest request)
    {
        if (request.DesiredReverseDirection is null) throw new InvalidOperationException("Through-direction repair requires the evidenced desired direction.");
        var op = plan.Operations.SingleOrDefault(item => item.Id == request.TargetOperationId)
            ?? throw new InvalidOperationException("Repair target operation does not exist.");
        return op switch
        {
            ExtrudeCutOperation cut when cut.EndCondition == ExtrudeEndCondition.ThroughAll =>
                (Replace(plan, cut with { ReverseDirection = request.DesiredReverseDirection.Value }),
                    $"{cut.Id}.reverse_direction: {cut.ReverseDirection} -> {request.DesiredReverseDirection.Value}; end_condition locked ThroughAll"),
            ExtrudeBossOperation boss when boss.EndCondition == ExtrudeEndCondition.ThroughAll =>
                (Replace(plan, boss with { ReverseDirection = request.DesiredReverseDirection.Value }),
                    $"{boss.Id}.reverse_direction: {boss.ReverseDirection} -> {request.DesiredReverseDirection.Value}; end_condition locked ThroughAll"),
            _ => throw new InvalidOperationException("Direction repair is allowed only for an existing ThroughAll extrude/cut; through/blind semantics cannot be changed.")
        };
    }

    private static (ModelingPlan Plan, string Diff) RepairGeometryRef(ModelingPlan plan, RepairRequest request)
    {
        var operation = Find<NativeFeatureOperation>(plan, request.TargetOperationId);
        var queries = QueriesFromUniqueResolutions(request.GeometryResolutions, null);
        if (queries.Count == 0) throw new InvalidOperationException("GeometryRef repair requires one or more uniquely resolved/rebound T06 references.");
        var updated = operation with { Options = operation.Options with { Selections = queries } };
        return (Replace(plan, updated), $"{operation.Id}.options.selections rebound from T06-resolved geometry; all feature parameters locked");
    }

    private static IReadOnlyList<EntityQuery> QueriesFromUniqueResolutions(IReadOnlyList<GeometryRefResolution> resolutions, EntityKind? requiredKind)
    {
        var queries = new List<EntityQuery>();
        foreach (var resolution in resolutions)
        {
            if (resolution.Status != GeometryRefResolutionStatus.Resolved || resolution.Candidate is null || resolution.CandidateIds.Count != 1)
                throw new InvalidOperationException("Ambiguous, stale, missing or unsupported GeometryRef evidence cannot drive automatic rebinding.");
            var candidate = resolution.Candidate;
            var signature = candidate.Signature;
            if (requiredKind is { } kind && signature.EntityKind != kind)
                throw new InvalidOperationException($"Repair requires {kind} geometry but resolved evidence is {signature.EntityKind}.");
            queries.Add(new()
            {
                Kind = signature.EntityKind,
                FeatureId = candidate.FeatureId,
                PersistentReference = candidate.NativePersistentReference,
                Geometry = signature.GeometryKind,
                PositionMm = signature.AnchorMm,
                Direction = signature.Direction,
                RadiusMm = signature.RadiusMm,
                ToleranceMm = signature.PositionToleranceMm,
                AllMatches = false
            });
        }
        return queries;
    }

    private static T Find<T>(ModelingPlan plan, string id) where T : ModelingOperation =>
        plan.Operations.SingleOrDefault(operation => operation.Id == id) as T
        ?? throw new InvalidOperationException($"Repair target '{id}' is not a {typeof(T).Name}.");
    private static ModelingPlan Replace(ModelingPlan plan, ModelingOperation replacement) =>
        plan with { Operations = plan.Operations.Select(operation => operation.Id == replacement.Id ? replacement : operation).ToArray() };

    public static RepairRule Rule(RepairKind kind) => kind switch
    {
        RepairKind.FilletSelection => new()
        {
            Kind = kind,
            Phase = RepairPhase.Geometry,
            RequiredEvidence = ["T12 coverage", "T13 located failure", "unique T06 edge resolution"],
            AllowedEdits = ["fillet selection set"],
            LockedRequirements = ["fillet radius", "source dimensions", "feature count", "tolerances"],
            RequiredPostChecks = ["T12:affected-source-coverage", "T13:affected-scope", "unaffected-requirement-sample"]
        },
        RepairKind.ThroughDirection => new()
        {
            Kind = kind,
            Phase = RepairPhase.InitialExecution,
            RequiredEvidence = ["T12 coverage", "T13 located failure", "evidenced desired extrusion direction"],
            AllowedEdits = ["reverse_direction"],
            LockedRequirements = ["diameter/position", "ThroughAll end condition", "source dimensions", "feature count", "tolerances"],
            RequiredPostChecks = ["T08:connectivity", "T12:affected-source-coverage", "T13:affected-scope", "unaffected-requirement-sample"]
        },
        RepairKind.GeometryRefRebind => new()
        {
            Kind = kind,
            Phase = RepairPhase.Geometry,
            RequiredEvidence = ["T12 coverage", "T13 located failure", "unique T06 GeometryRef resolution"],
            AllowedEdits = ["supported entity selection binding"],
            LockedRequirements = ["all feature parameters", "source dimensions", "counts", "through/blind semantics", "tolerances"],
            RequiredPostChecks = ["T07:targeted-measurement", "T12:affected-source-coverage", "T13:affected-scope", "unaffected-requirement-sample"]
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string LockedRequirementsFingerprint(ModelingPlan plan)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            plan.SourceText,
            plan.SourceModelPath,
            plan.Assumptions,
            plan.DocumentKind,
            plan.LengthUnit,
            plan.DrawingSourceSha256,
            plan.DrawingBindingDigest,
            plan.DrawingContext,
            plan.Verification,
            plan.Acceptance
        }, ModelingIrJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void Validate(RepairRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Contract != RepairRequest.ContractVersion || string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.FailureId) ||
            string.IsNullOrWhiteSpace(request.SourceRevisionId) || string.IsNullOrWhiteSpace(request.TargetOperationId) ||
            !Hash(request.FailureFingerprint) || request.NewEvidenceFingerprint is { Length: > 0 } evidence && !Hash(evidence))
            throw new ArgumentException("Repair request requires versioned identity, source revision, target operation and SHA-256 failure/evidence fingerprints.", nameof(request));
    }
    private static bool Hash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>Legacy call sites cannot accept a repair without an execution receipt binding the candidate model to this attempt.</summary>
    public static RepairValidationResult ValidatePostRepair(RepairAttempt attempt, CoverageReport coverage, ModelDiff diff,
        IReadOnlyList<RepairCheckResult> checks) => new()
    {
        RequestId = attempt?.RequestId ?? string.Empty,
        Status = RepairValidationStatus.Unverifiable,
        MissingOrFailedChecks = ["execution-receipt-required"],
        Message = "Post-repair acceptance requires versioned execution evidence binding this attempt, plan fingerprints and reopened candidate model."
    };

    public static RepairValidationResult ValidatePostRepair(RepairAttempt attempt, RepairExecutionEvidence execution,
        CoverageReport coverage, ModelDiff diff, IReadOnlyList<RepairCheckResult> checks)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("repair.validate");
        ArgumentNullException.ThrowIfNull(attempt);ArgumentNullException.ThrowIfNull(execution);ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(diff);ArgumentNullException.ThrowIfNull(checks);
        var evidence=checks.Select(item=>item.EvidenceId).Where(item=>!string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray();
        var problems=new List<string>();
        var canonicalRule=Rule(attempt.Kind);

        if(attempt.Status!=RepairAttemptStatus.Prepared||attempt.CandidatePlan is null||attempt.AfterPlanFingerprint is null)
            problems.Add("repair-attempt-not-prepared");
        else if(!attempt.AfterPlanFingerprint.Equals(ModelingPlanIdentity.Fingerprint(attempt.CandidatePlan),StringComparison.OrdinalIgnoreCase))
            problems.Add("candidate-plan-fingerprint-changed");
        if(!attempt.RequiredPostChecks.SequenceEqual(canonicalRule.RequiredPostChecks,StringComparer.Ordinal))
            problems.Add("repair-rule-post-checks-changed");
        if(attempt.FrozenRequiredFactIds.Count==0||attempt.FrozenAffectedFactIds.Any(id=>!attempt.FrozenRequiredFactIds.Contains(id,StringComparer.Ordinal)))
            problems.Add("repair-frozen-scope-invalid");

        var executionValid=execution.Contract==RepairExecutionEvidence.ContractVersion&&
            !string.IsNullOrWhiteSpace(execution.ExecutionId)&&execution.AttemptRequestId.Equals(attempt.RequestId,StringComparison.Ordinal)&&
            execution.AttemptIndex==attempt.AttemptIndex&&execution.BeforePlanFingerprint.Equals(attempt.BeforePlanFingerprint,StringComparison.OrdinalIgnoreCase)&&
            execution.AfterPlanFingerprint.Equals(attempt.AfterPlanFingerprint,StringComparison.OrdinalIgnoreCase)&&
            execution.SourceSha256.Equals(attempt.SourceSha256,StringComparison.OrdinalIgnoreCase)&&
            execution.SourceRevisionId.Equals(attempt.SourceRevisionId,StringComparison.Ordinal)&&
            execution.BaselineModelSha256.Equals(attempt.BaselineModelSha256,StringComparison.OrdinalIgnoreCase)&&
            Hash(execution.CandidateModelSha256)&&execution.CandidateModelReopened&&
            execution.FrozenRequiredScopeFingerprint.Equals(attempt.FrozenRequiredScopeFingerprint,StringComparison.OrdinalIgnoreCase)&&
            SameSet(execution.FrozenRequiredFactIds,attempt.FrozenRequiredFactIds)&&
            SameSet(execution.FrozenAffectedFactIds,attempt.FrozenAffectedFactIds)&&
            execution.EvidenceMode is "native_executor" or "synthetic_fixture";
        if(!executionValid) problems.Add("execution-receipt-identity");

        var coverageComplete=coverage.FullPass&&coverage.RequiredFactIds.Count>0&&
            SameSet(coverage.RequiredFactIds,attempt.FrozenRequiredFactIds)&&
            SameSet(coverage.Items.Select(item=>item.FactId).ToArray(),attempt.FrozenRequiredFactIds)&&
            coverage.Items.All(item=>item.Status==RequirementCheckStatus.Passed&&attempt.FrozenRequiredFactIds.Contains(item.FactId,StringComparer.Ordinal));
        if(!coverage.SourceSha256.Equals(attempt.SourceSha256,StringComparison.OrdinalIgnoreCase)||
           !coverage.SourceRevisionId.Equals(attempt.SourceRevisionId,StringComparison.Ordinal)||!coverageComplete||
           !coverage.RequiredScopeFingerprint.Equals(attempt.FrozenRequiredScopeFingerprint,StringComparison.OrdinalIgnoreCase)||
           !string.Equals(coverage.CandidatePlanFingerprint,attempt.AfterPlanFingerprint,StringComparison.OrdinalIgnoreCase)||
           !coverage.CandidateModelSha256.Equals(execution.CandidateModelSha256,StringComparison.OrdinalIgnoreCase))
            problems.Add("T12:affected-source-coverage");

        var diffComplete=Hash(diff.BaselineModelSha256)&&Hash(diff.CandidateModelSha256)&&diff.Status==ModelDiffStatus.Comparable&&
            diff.BaselineModelReopened&&diff.CandidateModelReopened&&diff.BaselineCaptureComplete&&diff.CandidateCaptureComplete&&diff.CaptureScopeIds.Count>0;
        if(!diffComplete||diff.HasUnexpectedImpact||
           !diff.BaselineModelSha256.Equals(execution.BaselineModelSha256,StringComparison.OrdinalIgnoreCase)||
           !diff.CandidateModelSha256.Equals(execution.CandidateModelSha256,StringComparison.OrdinalIgnoreCase))
            problems.Add("T13:affected-scope");

        var checkIdentityInvalid=checks.Any(item=>string.IsNullOrWhiteSpace(item.CheckId)||string.IsNullOrWhiteSpace(item.EvidenceId)||
            !item.SourceSha256.Equals(execution.SourceSha256,StringComparison.OrdinalIgnoreCase)||
            !item.SourceRevisionId.Equals(execution.SourceRevisionId,StringComparison.Ordinal)||
            !item.ModelSha256.Equals(execution.CandidateModelSha256,StringComparison.OrdinalIgnoreCase)||
            !item.PlanFingerprint.Equals(execution.AfterPlanFingerprint,StringComparison.OrdinalIgnoreCase)||
            !item.AttemptRequestId.Equals(execution.AttemptRequestId,StringComparison.Ordinal))||
            checks.Select(item=>item.CheckId).Distinct(StringComparer.Ordinal).Count()!=checks.Count;
        if(checkIdentityInvalid) problems.Add("post-check-evidence-invalid");

        foreach(var required in canonicalRule.RequiredPostChecks.Where(item=>!item.StartsWith("T12:",StringComparison.Ordinal)&&!item.StartsWith("T13:",StringComparison.Ordinal)))
        {
            var matching=checks.Where(item=>item.CheckId.Equals(required,StringComparison.Ordinal)).ToArray();
            if(matching.Length!=1||matching[0].Status!=RequirementCheckStatus.Passed) problems.Add(required);
        }

        // Any actual deterministic failure on the bound candidate is fatal, even if it is additional
        // to the rule's minimum named post-checks.  Evaluate this before the success branch.
        var hardFail=checks.Any(item=>item.Status==RequirementCheckStatus.Failed)||coverage.Conclusion==CoverageConclusion.Failed||
                     diff.UnexpectedChanges.Count>0||diff.FailedUnaffectedRequirementIds.Count>0;
        var unique=problems.Distinct(StringComparer.Ordinal).ToArray();
        if(hardFail)
            return Bound(RepairValidationStatus.Failed,"Repair candidate has bound deterministic failure evidence and must not be accepted.",unique);
        if(unique.Length==0)
            return Bound(RepairValidationStatus.Passed,"Repair candidate is bound to this attempt/execution and passed frozen T12 scope, complete T13 diff and all required/actual post-checks.",[]);
        return Bound(RepairValidationStatus.Unverifiable,"Repair candidate lacks complete identity-bound post-validation evidence and remains unverified.",unique);

        RepairValidationResult Bound(RepairValidationStatus status,string message,IReadOnlyList<string> missing)=>new()
        {
            RequestId=attempt.RequestId,Status=status,EvidenceIds=evidence,MissingOrFailedChecks=missing,Message=message,
            ExecutionId=execution.ExecutionId,AttemptIndex=execution.AttemptIndex,
            SourceSha256=execution.SourceSha256,SourceRevisionId=execution.SourceRevisionId,
            BaselineModelSha256=execution.BaselineModelSha256,CandidateModelSha256=execution.CandidateModelSha256,
            BeforePlanFingerprint=execution.BeforePlanFingerprint,AfterPlanFingerprint=execution.AfterPlanFingerprint,
            FrozenRequiredScopeFingerprint=execution.FrozenRequiredScopeFingerprint,
            FrozenRequiredFactIds=execution.FrozenRequiredFactIds,FrozenAffectedFactIds=execution.FrozenAffectedFactIds,
            CandidateModelReopened=execution.CandidateModelReopened,EvidenceMode=execution.EvidenceMode
        };
    }

    private static bool SameSet(IReadOnlyList<string> left,IReadOnlyList<string> right) =>
        left.Count==right.Count&&left.Distinct(StringComparer.Ordinal).Count()==left.Count&&right.Distinct(StringComparer.Ordinal).Count()==right.Count&&
        left.ToHashSet(StringComparer.Ordinal).SetEquals(right);
}

public static class RepairPolicy
{
    public static bool IsAllowedOperationMutation(ModelingOperation before, ModelingOperation after, RepairKind kind)
    {
        if (before.GetType() != after.GetType() || before.Id != after.Id || before.Name != after.Name || !before.DependsOn.SequenceEqual(after.DependsOn))
            return false;
        return kind switch
        {
            RepairKind.FilletSelection or RepairKind.GeometryRefRebind when before is NativeFeatureOperation a && after is NativeFeatureOperation b =>
                SameNativeOptionsExceptSelections(a.Options, b.Options),
            RepairKind.ThroughDirection when before is ExtrudeCutOperation a && after is ExtrudeCutOperation b =>
                a == b with { ReverseDirection = a.ReverseDirection },
            RepairKind.ThroughDirection when before is ExtrudeBossOperation a && after is ExtrudeBossOperation b =>
                a == b with { ReverseDirection = a.ReverseDirection },
            _ => false
        };
    }

    private static bool SameNativeOptionsExceptSelections(NativeFeatureOptions before, NativeFeatureOptions after)
    {
        var normalized = after with { Selections = before.Selections };
        return before == normalized;
    }
}
