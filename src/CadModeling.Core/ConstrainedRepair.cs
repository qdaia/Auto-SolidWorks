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
            throw new ArgumentException("修复预算必须是正数且受相位预算的限制。", nameof(budget));
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
            return Rejected("T12 修复输入必须携带一个非空的已锁定的必要事实范围和源/模型的身份标识。");
        if (!coverage.SourceRevisionId.Equals(request.SourceRevisionId, StringComparison.Ordinal))
            return Rejected("T12 覆盖属于不同的源修订版本。");
        if (!coverage.SourceSha256.Equals(plan.DrawingSourceSha256, StringComparison.OrdinalIgnoreCase))
            return Rejected("T12 覆盖源 SHA 不匹配类型化计划的冻结图纸源。");
        if (string.IsNullOrWhiteSpace(coverage.CandidatePlanFingerprint) ||
            !coverage.CandidatePlanFingerprint.Equals(beforeFingerprint, StringComparison.OrdinalIgnoreCase))
            return Rejected("T12 覆盖不绑定到精确的预修复类型计划指纹。");
        if (!location.FailureId.Equals(request.FailureId,StringComparison.Ordinal))
            return Rejected("T13 修复身份不匹配此维修请求。");
        if (!location.Located || !string.Equals(location.EarliestAffectedOperationId, request.TargetOperationId, StringComparison.Ordinal) &&
                                 !location.DownstreamOperationIds.Contains(request.TargetOperationId, StringComparer.Ordinal))
            return Rejected("T13 未能在已证实受影响的范围内定位此修复目标。");
        if (location.SourceFactIds.Any(id=>!coverage.RequiredFactIds.Contains(id,StringComparer.Ordinal)))
            return Rejected("T13 源关联 未在当前的 T12 所需-fact 仓库中完全体现。");
        if (request.GeometryResolutions.Any(resolution=>!resolution.ResolvedModelSha256.Equals(coverage.CandidateModelSha256,StringComparison.OrdinalIgnoreCase)||
                resolution.Reference.SourceRevisionId is {Length:>0} revision&&!revision.Equals(request.SourceRevisionId,StringComparison.Ordinal)))
            return Rejected("几何修复证据属于另一模型/源修订。");
        if (_lastEvidenceByFailure.TryGetValue(request.FailureFingerprint, out var priorEvidence) &&
            string.Equals(priorEvidence, request.NewEvidenceFingerprint, StringComparison.OrdinalIgnoreCase))
            return Attempt(RepairAttemptStatus.DuplicateFailure, "相同的失败指纹重复出现，缺乏新证据；修复循环停止。");
        if (BudgetExceeded(rule.Phase))
            return Attempt(RepairAttemptStatus.BudgetExceeded, "修复尝试预算已耗尽。");

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
                _ => throw new InvalidOperationException("不支持的修复类型。")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Attempt(RepairAttemptStatus.Rejected, ex.Message, _totalAttempts, phaseIndex);
        }

        var afterLocked = LockedRequirementsFingerprint(candidate);
        if (!lockedFingerprint.Equals(afterLocked, StringComparison.OrdinalIgnoreCase))
            return Attempt(RepairAttemptStatus.Rejected, "候选项修复更改了锁定源要求/接受准则，并被拒绝。", _totalAttempts, phaseIndex);
        var beforeOperation = plan.Operations.Single(operation => operation.Id == request.TargetOperationId);
        var afterOperation = candidate.Operations.Single(operation => operation.Id == request.TargetOperationId);
        if (!RepairPolicy.IsAllowedOperationMutation(beforeOperation, afterOperation, request.Kind))
            return Attempt(RepairAttemptStatus.Rejected, "候选修复改变了规则明确编辑允许范围之外的字段。", _totalAttempts, phaseIndex);

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
            StopReason = "仅准备完成；修复必须在这些执行和要求的后检查完成后再被接受。",
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
            throw new InvalidOperationException("修复圆角选择需要原生的圆角操作。");
        var queries = QueriesFromUniqueResolutions(request.GeometryResolutions, EntityKind.Edge);
        if (queries.Count == 0) throw new InvalidOperationException("圆角修复需要至少一个由 T06/T13 证据唯一确定的目标边。");
        var updated = operation with { Options = operation.Options with { Selections = queries } };
        return (Replace(plan, updated), $"{operation.Id}.options.selections:{operation.Options.Selections.Count}->{queries.Count}; radius_mm 已锁定在{operation.Options.RadiusMm:R}");
    }

    private static (ModelingPlan Plan, string Diff) RepairThroughDirection(ModelingPlan plan, RepairRequest request)
    {
        if (request.DesiredReverseDirection is null) throw new InvalidOperationException("通过方向的修复需要证实所需的定向。");
        var op = plan.Operations.SingleOrDefault(item => item.Id == request.TargetOperationId)
            ?? throw new InvalidOperationException("修复目标操作不存在。");
        return op switch
        {
            ExtrudeCutOperation cut when cut.EndCondition == ExtrudeEndCondition.ThroughAll =>
                (Replace(plan, cut with { ReverseDirection = request.DesiredReverseDirection.Value }),
                    $"{cut.Id}.reverse_direction：{cut.ReverseDirection}->{request.DesiredReverseDirection.Value}；end_condition 保持锁定为 ThroughAll。"),
            ExtrudeBossOperation boss when boss.EndCondition == ExtrudeEndCondition.ThroughAll =>
                (Replace(plan, boss with { ReverseDirection = request.DesiredReverseDirection.Value }),
                    $"{boss.Id}.reverse_direction：{boss.ReverseDirection}->{request.DesiredReverseDirection.Value}；end_condition 保持锁定为 ThroughAll。"),
            _ => throw new InvalidOperationException("方向修复仅允许对现有 ThroughAll 打孔/切割；通过/盲语义无法更改。")
        };
    }

    private static (ModelingPlan Plan, string Diff) RepairGeometryRef(ModelingPlan plan, RepairRequest request)
    {
        var operation = Find<NativeFeatureOperation>(plan, request.TargetOperationId);
        var queries = QueriesFromUniqueResolutions(request.GeometryResolutions, null);
        if (queries.Count == 0) throw new InvalidOperationException("GeometryRef 修复需要一个或多个唯一解析/反弹的 T06 参考。");
        var updated = operation with { Options = operation.Options with { Selections = queries } };
        return (Replace(plan, updated), $"{operation.Id}.options.selections 重建从 T06 已解决几何体；所有特征参数已锁定");
    }

    private static IReadOnlyList<EntityQuery> QueriesFromUniqueResolutions(IReadOnlyList<GeometryRefResolution> resolutions, EntityKind? requiredKind)
    {
        var queries = new List<EntityQuery>();
        foreach (var resolution in resolutions)
        {
            if (resolution.Status != GeometryRefResolutionStatus.Resolved || resolution.Candidate is null || resolution.CandidateIds.Count != 1)
                throw new InvalidOperationException("模糊、过期、缺失或不支持的 GeometryRef 证据无法驱动自动绑定。");
            if(!GeometryRefResolver.IsVerifiedResolution(resolution))
                throw new InvalidOperationException("GeometryRef 签名或完整语义历史回执未通过重验，不能驱动修复。");
            var candidate = resolution.Candidate;
            var signature = candidate.Signature;
            if (requiredKind is { } kind && signature.EntityKind != kind)
                throw new InvalidOperationException($"修复需要{kind}几何，但已解决的证据是{signature.EntityKind}。");
            queries.Add(GeometryRefResolver.SelectionQuery(resolution));
        }
        return queries;
    }

    private static T Find<T>(ModelingPlan plan, string id) where T : ModelingOperation =>
        plan.Operations.SingleOrDefault(operation => operation.Id == id) as T
        ?? throw new InvalidOperationException($"修复目标 '{id}' 不是{typeof(T).Name}。");
    private static ModelingPlan Replace(ModelingPlan plan, ModelingOperation replacement) =>
        plan with { Operations = plan.Operations.Select(operation => operation.Id == replacement.Id ? replacement : operation).ToArray() };

    public static RepairRule Rule(RepairKind kind) => kind switch
    {
        RepairKind.FilletSelection => new()
        {
            Kind = kind,
            Phase = RepairPhase.Geometry,
            RequiredEvidence = ["T12 覆盖检查", "在位置T13发生失败", "唯一 T06 边线分辨率"],
            AllowedEdits = ["圆角选择集"],
            LockedRequirements = ["圆角半径", "源尺寸", "特征数量", "tolerances"],
            RequiredPostChecks = ["T12:affected-source-coverage", "T13:affected-scope", "unaffected-requirement-sample"]
        },
        RepairKind.ThroughDirection => new()
        {
            Kind = kind,
            Phase = RepairPhase.InitialExecution,
            RequiredEvidence = ["T12 覆盖检查", "在位置T13发生失败", "证实期望的拉伸方向"],
            AllowedEdits = ["reverse_direction"],
            LockedRequirements = ["diameter/position", "ThroughAll 结束条件", "源尺寸", "特征数量", "tolerances"],
            RequiredPostChecks = ["T08:connectivity", "T12:affected-source-coverage", "T13:affected-scope", "unaffected-requirement-sample"]
        },
        RepairKind.GeometryRefRebind => new()
        {
            Kind = kind,
            Phase = RepairPhase.Geometry,
            RequiredEvidence = ["T12 覆盖检查", "在位置T13发生失败", "唯一 T06 GeometryRef 分辨率"],
            AllowedEdits = ["支持的实体选择绑定"],
            LockedRequirements = ["所有特征参数", "源尺寸", "counts", "通过/盲语义", "tolerances"],
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
            throw new ArgumentException("修复请求需要版本化的身份、源修订版、目标操作和SHA-256失败/证据指纹。", nameof(request));
    }
    private static bool Hash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>Legacy call sites cannot accept a repair without an execution receipt binding the candidate model to this attempt.</summary>
    public static RepairValidationResult ValidatePostRepair(RepairAttempt attempt, CoverageReport coverage, ModelDiff diff,
        IReadOnlyList<RepairCheckResult> checks) => new()
    {
        RequestId = attempt?.RequestId ?? string.Empty,
        Status = RepairValidationStatus.Unverifiable,
        MissingOrFailedChecks = ["execution-receipt-required"],
        Message = "修复后验收需要版本化的执行证据绑定此次尝试、计划指纹和重新打开的候选项模型。"
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
            return Bound(RepairValidationStatus.Failed,"修复候选体已绑定确定性失败证据，不得接受。",unique);
        if(unique.Length==0)
            return Bound(RepairValidationStatus.Passed,"修复候选对象受此修复尝试/执行限制，并且已锁定 T12 范围，完成 T13 差异并执行所有必需/实际的后检查。",[]);
        return Bound(RepairValidationStatus.Unverifiable,"候选修复缺乏完全绑定验证的完整证据，仍然未验证。",unique);

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
