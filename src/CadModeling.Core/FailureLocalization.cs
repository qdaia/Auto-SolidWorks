using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CadModeling.Ir;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<FailureClass>))]
public enum FailureClass
{
    SourceGap,
    SourceBindingConflict,
    PlanCompileError,
    StaleGeometryRef,
    NativeKernelFailure,
    DimensionError,
    TopologyConnectivityError,
    ProjectionSectionError,
    Unsupported,
    Unverifiable,
    Unknown
}

public sealed record FailureSignal
{
    public required string FailureId { get; init; }
    public FailureClass FailureClass { get; init; }
    public IReadOnlyList<string> SourceFactIds { get; init; } = [];
    public IReadOnlyList<GeometryRef> GeometryReferences { get; init; } = [];
    public IReadOnlyList<string> OperationIds { get; init; } = [];
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
    public string Message { get; init; } = string.Empty;
}

public sealed record FailureLocation
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string FailureId { get; init; }
    public FailureClass FailureClass { get; init; }
    public bool Located { get; init; }
    public IReadOnlyList<string> SourceFactIds { get; init; } = [];
    public IReadOnlyList<string> GeometryRefIds { get; init; } = [];
    public string? OperationId { get; init; }
    public string? EarliestAffectedOperationId { get; init; }
    public IReadOnlyList<string> DownstreamOperationIds { get; init; } = [];
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
    public double Confidence { get; init; }
    public string WhyUnlocated { get; init; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelDiffStatus>))]
public enum ModelDiffStatus { Comparable, Incomparable }

[JsonConverter(typeof(JsonStringEnumConverter<GeometryShapeChangeKind>))]
public enum GeometryShapeChangeKind { Changed, Added, Removed, IdentityOnly }

public sealed record GeometryShapeRecord
{
    public required string RecordId { get; init; }
    public string? SemanticId { get; init; }
    public string? TopologyToken { get; init; }
    public string? OperationId { get; init; }
    public IReadOnlyList<string> SourceFactIds { get; init; } = [];
    public required GeometrySignature Signature { get; init; }
}

public sealed record RequirementRecheck(string RequirementId, RequirementCheckStatus Status, string EvidenceId);

public sealed record ModelDiffInput
{
    public required string BaselineModelSha256 { get; init; }
    public required string CandidateModelSha256 { get; init; }
    public bool BaselineModelReopened { get; init; }
    public bool CandidateModelReopened { get; init; }
    public bool BaselineCaptureComplete { get; init; }
    public bool CandidateCaptureComplete { get; init; }
    public IReadOnlyList<string> BaselineCaptureScopeIds { get; init; } = [];
    public IReadOnlyList<string> CandidateCaptureScopeIds { get; init; } = [];
    public IReadOnlyList<string> BaselineCaptureLimitations { get; init; } = [];
    public IReadOnlyList<string> CandidateCaptureLimitations { get; init; } = [];
    public IReadOnlyList<string> IntendedTargetIds { get; init; } = [];
    public IReadOnlyList<GeometryShapeRecord> BaselineGeometry { get; init; } = [];
    public IReadOnlyList<GeometryShapeRecord> CandidateGeometry { get; init; } = [];
    public IReadOnlyList<RequirementRecheck> UnaffectedRequirementRechecks { get; init; } = [];
}

public sealed record ModelDiffChange
{
    public required GeometryShapeChangeKind Kind { get; init; }
    public string? BaselineRecordId { get; init; }
    public string? CandidateRecordId { get; init; }
    public string? SemanticId { get; init; }
    public string? OperationId { get; init; }
    public bool Intended { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record ModelDiff
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string BaselineModelSha256 { get; init; }
    public required string CandidateModelSha256 { get; init; }
    public bool BaselineModelReopened { get; init; }
    public bool CandidateModelReopened { get; init; }
    public bool BaselineCaptureComplete { get; init; }
    public bool CandidateCaptureComplete { get; init; }
    public IReadOnlyList<string> CaptureScopeIds { get; init; } = [];
    public ModelDiffStatus Status { get; init; }
    public IReadOnlyList<ModelDiffChange> Changes { get; init; } = [];
    public IReadOnlyList<ModelDiffChange> UnexpectedChanges { get; init; } = [];
    public IReadOnlyList<string> FailedUnaffectedRequirementIds { get; init; } = [];
    public string IncomparableReason { get; init; } = string.Empty;
    public bool HasUnexpectedImpact => UnexpectedChanges.Count > 0 || FailedUnaffectedRequirementIds.Count > 0;
}

public static class FailureLocator
{
    public static FailureLocation Locate(ModelingPlan plan, FailureSignal signal)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(signal);
        if (string.IsNullOrWhiteSpace(signal.FailureId)) throw new ArgumentException("Failure id is required.", nameof(signal));
        foreach (var reference in signal.GeometryReferences) GeometryRefResolver.Validate(reference);
        var operationOrder = plan.Operations.Select((op, index) => (op.Id, index)).ToDictionary(x => x.Id, x => x.index, StringComparer.Ordinal);
        var sourceIds = signal.SourceFactIds.Concat(signal.GeometryReferences.SelectMany(r => r.SourceFactIds)).Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var operationIds = signal.OperationIds.Concat(signal.GeometryReferences.Select(r => r.OperationId).OfType<string>())
            .Where(operationOrder.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
        var evidenceIds = signal.EvidenceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();

        if (operationIds.Length == 0)
            return Unlocated("No source/GeometryRef evidence identifies an actual operation; random nearest-feature repair is forbidden.");
        if (sourceIds.Length == 0 && signal.FailureClass is not FailureClass.PlanCompileError and not FailureClass.NativeKernelFailure)
            return Unlocated("No source lineage is bound to the implicated operation, so the failure cannot be safely localized for repair.");

        var earliest = operationIds.OrderBy(id => operationOrder[id]).First();
        var affected = operationIds.SelectMany(id => Downstream(plan, id)).ToHashSet(StringComparer.Ordinal);
        var downstream = plan.Operations.Where(operation => affected.Contains(operation.Id)).Select(operation => operation.Id).ToArray();
        return new()
        {
            FailureId = signal.FailureId,
            FailureClass = signal.FailureClass,
            Located = true,
            SourceFactIds = sourceIds,
            GeometryRefIds = signal.GeometryReferences.Select(r => r.RefId).Distinct(StringComparer.Ordinal).ToArray(),
            OperationId = operationIds.Length == 1 ? operationIds[0] : null,
            EarliestAffectedOperationId = earliest,
            DownstreamOperationIds = downstream,
            EvidenceIds = evidenceIds,
            Confidence = operationIds.Length == 1 && sourceIds.Length > 0 ? 1.0 : 0.8
        };

        FailureLocation Unlocated(string reason) => new()
        {
            FailureId = signal.FailureId,
            FailureClass = signal.FailureClass,
            Located = false,
            SourceFactIds = sourceIds,
            GeometryRefIds = signal.GeometryReferences.Select(r => r.RefId).Distinct(StringComparer.Ordinal).ToArray(),
            EvidenceIds = evidenceIds,
            Confidence = 0,
            WhyUnlocated = reason
        };
    }

    public static IReadOnlyList<string> Downstream(ModelingPlan plan, string operationId)
    {
        var ids = plan.Operations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        if (!ids.Contains(operationId)) return [];
        var affected = new HashSet<string>(StringComparer.Ordinal) { operationId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var op in plan.Operations)
                if (!affected.Contains(op.Id) && op.DependsOn.Any(affected.Contains))
                { affected.Add(op.Id); changed = true; }
        }
        return plan.Operations.Where(op => affected.Contains(op.Id)).Select(op => op.Id).ToArray();
    }
}

public static class ModelDiffAnalyzer
{
    public static ModelDiff Compare(ModelDiffInput input)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("review.diff");
        ArgumentNullException.ThrowIfNull(input);
        if (!Hash(input.BaselineModelSha256) || !Hash(input.CandidateModelSha256))
            throw new ArgumentException("ModelDiff requires SHA-256 identities for both actual model files.", nameof(input));
        if (!input.BaselineModelReopened || !input.CandidateModelReopened)
            return Incomparable("ModelDiff requires both baseline and candidate geometry to come from saved and reopened models.");
        if (!input.BaselineCaptureComplete || !input.CandidateCaptureComplete ||
            input.BaselineCaptureLimitations.Count > 0 || input.CandidateCaptureLimitations.Count > 0)
            return Incomparable("Geometry capture is incomplete or has declared limitations; absence of captured change is not evidence of no change.");
        if (input.BaselineCaptureScopeIds.Count == 0 || input.CandidateCaptureScopeIds.Count == 0 ||
            input.BaselineCaptureScopeIds.Any(string.IsNullOrWhiteSpace) || input.CandidateCaptureScopeIds.Any(string.IsNullOrWhiteSpace) ||
            input.BaselineCaptureScopeIds.Distinct(StringComparer.Ordinal).Count() != input.BaselineCaptureScopeIds.Count ||
            input.CandidateCaptureScopeIds.Distinct(StringComparer.Ordinal).Count() != input.CandidateCaptureScopeIds.Count ||
            !input.BaselineCaptureScopeIds.ToHashSet(StringComparer.Ordinal).SetEquals(input.CandidateCaptureScopeIds))
            return Incomparable("Baseline/candidate geometry capture scopes are missing, invalid, or not identical.");
        if (input.BaselineGeometry.Count == 0 || input.CandidateGeometry.Count == 0)
            return Incomparable("Complete supported-part comparison requires actual captured geometry; an empty geometry inventory cannot certify no collateral change.");
        ValidateRecords(input.BaselineGeometry, "baseline");
        ValidateRecords(input.CandidateGeometry, "candidate");
        if (input.IntendedTargetIds.Any(string.IsNullOrWhiteSpace) || input.IntendedTargetIds.Distinct(StringComparer.Ordinal).Count() != input.IntendedTargetIds.Count)
            throw new ArgumentException("Intended target ids must be nonempty and unique.", nameof(input));

        var changes = new List<ModelDiffChange>();
        var usedCandidate = new HashSet<string>(StringComparer.Ordinal);
        var intended = input.IntendedTargetIds.ToHashSet(StringComparer.Ordinal);
        foreach (var before in input.BaselineGeometry)
        {
            var exact = before.SemanticId is { Length: > 0 }
                ? input.CandidateGeometry.Where(after => after.SemanticId == before.SemanticId && !usedCandidate.Contains(after.RecordId)).ToArray()
                : [];
            GeometryShapeRecord? after = null;
            if (exact.Length > 1) return Incomparable($"Semantic identity '{before.SemanticId}' is duplicated in the candidate model.");
            if (exact.Length == 1) after = exact[0];
            if (after is null)
            {
                var equivalent = input.CandidateGeometry.Where(candidate => !usedCandidate.Contains(candidate.RecordId) && Equivalent(before.Signature, candidate.Signature)).ToArray();
                if (equivalent.Length > 1) return Incomparable($"Geometry '{before.RecordId}' has multiple equivalent candidate matches; topology churn cannot be resolved uniquely.");
                if (equivalent.Length == 1) after = equivalent[0];
            }
            if (after is null)
            {
                changes.Add(Change(GeometryShapeChangeKind.Removed, before, null, "Baseline geometry is absent from the candidate model."));
                continue;
            }
            usedCandidate.Add(after.RecordId);
            if (!Equivalent(before.Signature, after.Signature))
                changes.Add(Change(GeometryShapeChangeKind.Changed, before, after, "Bound geometry changed shape or placement."));
            else if (before.RecordId != after.RecordId || before.TopologyToken != after.TopologyToken || before.SemanticId != after.SemanticId)
                changes.Add(Change(GeometryShapeChangeKind.IdentityOnly, before, after, "Persistent/topology identity changed while bounded geometry remained equivalent."));
        }
        foreach (var after in input.CandidateGeometry.Where(record => !usedCandidate.Contains(record.RecordId)))
            changes.Add(Change(GeometryShapeChangeKind.Added, null, after, "Candidate model contains additional geometry."));

        var failedRechecks = input.UnaffectedRequirementRechecks
            .Where(r => r.Status != RequirementCheckStatus.Passed).Select(r => r.RequirementId).Distinct(StringComparer.Ordinal).ToArray();
        var unexpected = changes.Where(change => change.Kind != GeometryShapeChangeKind.IdentityOnly && !change.Intended).ToArray();
        return new()
        {
            BaselineModelSha256 = input.BaselineModelSha256,
            CandidateModelSha256 = input.CandidateModelSha256,
            BaselineModelReopened = input.BaselineModelReopened,
            CandidateModelReopened = input.CandidateModelReopened,
            BaselineCaptureComplete = input.BaselineCaptureComplete,
            CandidateCaptureComplete = input.CandidateCaptureComplete,
            CaptureScopeIds = input.BaselineCaptureScopeIds.ToArray(),
            Status = ModelDiffStatus.Comparable,
            Changes = changes,
            UnexpectedChanges = unexpected,
            FailedUnaffectedRequirementIds = failedRechecks
        };

        ModelDiffChange Change(GeometryShapeChangeKind kind, GeometryShapeRecord? before, GeometryShapeRecord? after, string message)
        {
            var semantic = after?.SemanticId ?? before?.SemanticId;
            var op = after?.OperationId ?? before?.OperationId;
            var isIntended = (semantic is not null && intended.Contains(semantic)) ||
                             (before is not null && intended.Contains(before.RecordId)) || (after is not null && intended.Contains(after.RecordId));
            return new()
            {
                Kind = kind,
                BaselineRecordId = before?.RecordId,
                CandidateRecordId = after?.RecordId,
                SemanticId = semantic,
                OperationId = op,
                Intended = isIntended,
                Message = message
            };
        }
        ModelDiff Incomparable(string reason) => new()
        {
            BaselineModelSha256 = input.BaselineModelSha256,
            CandidateModelSha256 = input.CandidateModelSha256,
            BaselineModelReopened = input.BaselineModelReopened,
            CandidateModelReopened = input.CandidateModelReopened,
            BaselineCaptureComplete = input.BaselineCaptureComplete,
            CandidateCaptureComplete = input.CandidateCaptureComplete,
            CaptureScopeIds = input.BaselineCaptureScopeIds.ToArray(),
            Status = ModelDiffStatus.Incomparable,
            IncomparableReason = reason
        };
    }

    public static string ShapeFingerprint(GeometryShapeRecord record)
    {
        GeometryRefResolver.Validate(record.Signature);
        var signature = record.Signature;
        var canonical = string.Join("|", signature.EntityKind, signature.GeometryKind, V(signature.AnchorMm), V(signature.Direction),
            D(signature.RadiusMm), D(signature.AreaMm2));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool Equivalent(GeometrySignature a, GeometrySignature b)
    {
        if (a.EntityKind != b.EntityKind || a.GeometryKind != b.GeometryKind) return false;
        // Candidate-side tolerance inflation must never turn real drift into identity-only churn.
        // Use the stricter frozen bound from the two independently captured signatures.
        if (!Near(a.AnchorMm, b.AnchorMm, Math.Min(a.PositionToleranceMm, b.PositionToleranceMm))) return false;
        if (!Near(a.RadiusMm, b.RadiusMm, Math.Min(a.RadiusToleranceMm, b.RadiusToleranceMm))) return false;
        if (!Near(a.AreaMm2, b.AreaMm2, Math.Min(a.AreaToleranceMm2, b.AreaToleranceMm2))) return false;
        if (a.Direction is null != (b.Direction is null)) return false;
        if (a.Direction is { } ad && b.Direction is { } bd)
        {
            var an = ModelVerification.Norm(ad); var bn = ModelVerification.Norm(bd);
            if (an <= 1e-12 || bn <= 1e-12) return false;
            var cosine = Math.Clamp(Math.Abs(ModelVerification.Dot(ad, bd) / (an * bn)), -1, 1);
            var angle = Math.Acos(cosine) * 180 / Math.PI;
            if (angle > Math.Min(a.DirectionToleranceDegrees, b.DirectionToleranceDegrees)) return false;
        }
        return true;
    }

    private static bool Near(Vector3? a, Vector3? b, double tolerance) => a is null == (b is null) &&
        (a is null || ModelVerification.Norm(new(a.X - b!.X, a.Y - b.Y, a.Z - b.Z)) <= tolerance);
    private static bool Near(double? a, double? b, double tolerance) => a is null == (b is null) && (a is null || Math.Abs(a.Value - b!.Value) <= tolerance);
    private static void ValidateRecords(IReadOnlyList<GeometryShapeRecord> records, string label)
    {
        if (records.Any(r => string.IsNullOrWhiteSpace(r.RecordId)) || records.Select(r => r.RecordId).Distinct(StringComparer.Ordinal).Count() != records.Count)
            throw new ArgumentException($"ModelDiff {label} record ids must be nonempty and unique.");
        foreach (var record in records) GeometryRefResolver.Validate(record.Signature);
    }
    private static bool Hash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string D(double? value) => value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "";
    private static string V(Vector3? value) => value is null ? "" : string.Join(',', D(value.X), D(value.Y), D(value.Z));
}
