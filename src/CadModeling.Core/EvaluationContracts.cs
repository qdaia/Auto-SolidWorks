using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CadModeling.Core.Evaluation;

[JsonConverter(typeof(JsonStringEnumConverter<EvaluationPartition>))]
public enum EvaluationPartition
{
    SyntheticRegression,
    RealDevelopment,
    RealHoldout,
    Challenge
}

[JsonConverter(typeof(JsonStringEnumConverter<EvaluationCaseStatus>))]
public enum EvaluationCaseStatus
{
    Passed,
    Failed,
    RejectedCorrectly,
    RejectedIncorrectly,
    Unverifiable,
    NotRun
}

/// <summary>
/// Immutable fingerprint of the code/install/tool surface against which an evaluation run is interpreted.
/// Evaluation truth is deliberately absent from this object.
/// </summary>
public sealed record BaselineManifest
{
    public const string ContractVersion = "1.0.0";
    public string Version { get; init; } = ContractVersion;
    public required string CodeFingerprint { get; init; }
    public required string InstallationFingerprint { get; init; }
    public required string ToolContractFingerprint { get; init; }
    public required string EnvironmentFingerprint { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// One evaluation input. PartIdentity groups every rendering/crop/noise/rotation variant of the same
/// physical part so a validator can prevent related variants from crossing development/holdout boundaries.
/// TruthArtifactIds are evaluator-side only and must never be present in GeneratorInputArtifactIds.
/// </summary>
public sealed record EvaluationCaseManifest
{
    public const string ContractVersion = "1.0.0";
    public string Version { get; init; } = ContractVersion;
    public required string CaseId { get; init; }
    public required string PartIdentity { get; init; }
    public required string VariantGroupId { get; init; }
    public required EvaluationPartition Partition { get; init; }
    public required string SourceSha256 { get; init; }
    public IReadOnlyList<string> GeneratorInputArtifactIds { get; init; } = [];
    public IReadOnlyList<string> TruthArtifactIds { get; init; } = [];
    public IReadOnlyList<string> RequiredRequirementIds { get; init; } = [];
    public bool CompleteBuildableInput { get; init; } = true;
    public bool ExpectedToStop { get; init; }
    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal);
}

public sealed record EvaluationAttemptResult
{
    public required string AttemptId { get; init; }
    public EvaluationCaseStatus Status { get; init; } = EvaluationCaseStatus.NotRun;
    public bool Built { get; init; }
    public bool Reopened { get; init; }
    public bool RequiredScopePassed { get; init; }
    public bool NativeEditable { get; init; }
    public bool Stopped { get; init; }
    public long DurationMilliseconds { get; init; }
    public int ExternalCallCount { get; init; }
    public IReadOnlyDictionary<string, string> RequirementOutcomes { get; init; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Evidence { get; init; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal);
}

public sealed record EvaluationCaseResult
{
    public required string CaseId { get; init; }
    public required string BaselineFingerprint { get; init; }
    public required string SourceSha256 { get; init; }
    public required EvaluationAttemptResult Initial { get; init; }
    public EvaluationAttemptResult? Final { get; init; }
}

public sealed record EvaluationMetricSummary
{
    public int CompleteBuildableDenominator { get; init; }
    public int BuiltNumerator { get; init; }
    public int ScopePassNumerator { get; init; }
    public int ExpectedStopDenominator { get; init; }
    public int FalseReleaseNumerator { get; init; }
    public int SupportedBuildableDenominator { get; init; }
    public int UnjustifiedRejectNumerator { get; init; }
    public int NativeEditabilityDenominator { get; init; }
    public int NativeEditableNumerator { get; init; }
    public long TotalDurationMilliseconds { get; init; }
    public int TotalExternalCallCount { get; init; }
}

public sealed record EvaluationValidationIssue(string Code, string Message, IReadOnlyList<string> CaseIds);

public static class EvaluationIsolation
{
    public static IReadOnlyList<EvaluationValidationIssue> Validate(IReadOnlyList<EvaluationCaseManifest> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var issues = new List<EvaluationValidationIssue>();
        var truthOwners = cases
            .SelectMany(item => item.TruthArtifactIds.Select(id => (ArtifactId: id, item.CaseId)))
            .Where(item => !string.IsNullOrWhiteSpace(item.ArtifactId))
            .GroupBy(item => item.ArtifactId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.CaseId).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        foreach (var duplicate in cases.GroupBy(item => item.CaseId, StringComparer.Ordinal).Where(group => group.Count() > 1))
            issues.Add(new("EVAL_DUPLICATE_CASE", $"Duplicate case id '{duplicate.Key}'.", duplicate.Select(item => item.CaseId).ToArray()));

        foreach (var item in cases)
        {
            if (string.IsNullOrWhiteSpace(item.CaseId) || string.IsNullOrWhiteSpace(item.PartIdentity) || string.IsNullOrWhiteSpace(item.VariantGroupId))
                issues.Add(new("EVAL_ID_REQUIRED", "Case id, part identity and variant group are required.", [item.CaseId]));
            if (!IsSha256(item.SourceSha256))
                issues.Add(new("EVAL_SOURCE_HASH", $"Case '{item.CaseId}' source hash is not SHA-256.", [item.CaseId]));
            var leaked = item.GeneratorInputArtifactIds.Where(truthOwners.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
            if (leaked.Length > 0)
            {
                var owners = leaked.SelectMany(id => truthOwners[id]).Append(item.CaseId).Distinct(StringComparer.Ordinal).ToArray();
                issues.Add(new("EVAL_TRUTH_LEAK", $"Case '{item.CaseId}' exposes evaluator truth from the evaluation run to the generator: {string.Join(", ", leaked)}.", owners));
            }
        }

        // All variants of one physical part must remain on one side of the development/holdout boundary.
        foreach (var group in cases.GroupBy(item => item.PartIdentity, StringComparer.Ordinal))
        {
            var partitions = group.Select(item => PartitionBoundary(item.Partition)).Distinct(StringComparer.Ordinal).ToArray();
            if (partitions.Length > 1)
                issues.Add(new("EVAL_PARTITION_LEAK", $"Physical part '{group.Key}' crosses evaluation boundaries.", group.Select(item => item.CaseId).ToArray()));
        }

        foreach (var group in cases.GroupBy(item => item.VariantGroupId, StringComparer.Ordinal))
        {
            var partitions = group.Select(item => PartitionBoundary(item.Partition)).Distinct(StringComparer.Ordinal).ToArray();
            if (partitions.Length > 1)
                issues.Add(new("EVAL_VARIANT_LEAK", $"Variant group '{group.Key}' crosses evaluation boundaries.", group.Select(item => item.CaseId).ToArray()));
        }
        return issues;
    }

    private static string PartitionBoundary(EvaluationPartition partition) => partition switch
    {
        EvaluationPartition.RealHoldout => "holdout",
        EvaluationPartition.RealDevelopment or EvaluationPartition.SyntheticRegression => "development",
        EvaluationPartition.Challenge => "challenge",
        _ => partition.ToString()
    };

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

public static class EvaluationMetrics
{
    public static EvaluationMetricSummary Compute(
        IReadOnlyList<EvaluationCaseManifest> cases,
        IReadOnlyList<EvaluationCaseResult> results,
        bool useFinalAttempt = true,
        string? expectedBaselineFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(results);
        var byId = results.GroupBy(item => item.CaseId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var complete = cases.Where(item => item.CompleteBuildableInput).ToArray();
        var expectedStop = cases.Where(item => item.ExpectedToStop).ToArray();
        var supported = complete.Where(item => !item.ExpectedToStop).ToArray();
        (EvaluationCaseResult Result, EvaluationAttemptResult Attempt)? ValidAttempt(EvaluationCaseManifest item)
        {
            if (!byId.TryGetValue(item.CaseId, out var result)) return null;
            if (!result.SourceSha256.Equals(item.SourceSha256, StringComparison.OrdinalIgnoreCase)) return null;
            if (expectedBaselineFingerprint is not null &&
                !result.BaselineFingerprint.Equals(expectedBaselineFingerprint, StringComparison.Ordinal)) return null;
            if (!IsSha256(result.SourceSha256) || !IsSha256(result.BaselineFingerprint)) return null;
            var attempt = useFinalAttempt && result.Final is not null ? result.Final : result.Initial;
            return (result, attempt);
        }

        bool RequirementsPassed(EvaluationCaseManifest item, EvaluationAttemptResult attempt) =>
            item.RequiredRequirementIds.All(id => attempt.RequirementOutcomes.TryGetValue(id, out var outcome) &&
                outcome.Equals("passed", StringComparison.OrdinalIgnoreCase));

        bool ScopeAccepted(EvaluationCaseManifest item, EvaluationAttemptResult attempt) =>
            attempt.Status == EvaluationCaseStatus.Passed && attempt.Built && attempt.Reopened &&
            attempt.RequiredScopePassed && RequirementsPassed(item, attempt);

        var attempted = cases.Select(item => ValidAttempt(item)?.Attempt).Where(item => item is not null).Cast<EvaluationAttemptResult>().ToArray();
        return new()
        {
            CompleteBuildableDenominator = complete.Length,
            BuiltNumerator = complete.Count(item => ValidAttempt(item)?.Attempt is { Built: true, Reopened: true }),
            ScopePassNumerator = complete.Count(item => ValidAttempt(item) is { } valid && ScopeAccepted(item, valid.Attempt)),
            ExpectedStopDenominator = expectedStop.Length,
            FalseReleaseNumerator = expectedStop.Count(item => ValidAttempt(item) is { } valid && !valid.Attempt.Stopped && ScopeAccepted(item, valid.Attempt)),
            SupportedBuildableDenominator = supported.Length,
            UnjustifiedRejectNumerator = supported.Count(item => ValidAttempt(item)?.Attempt is { Stopped: true }),
            NativeEditabilityDenominator = supported.Count(item => ValidAttempt(item)?.Attempt.Built == true),
            NativeEditableNumerator = supported.Count(item => ValidAttempt(item)?.Attempt is { Built: true, NativeEditable: true }),
            TotalDurationMilliseconds = attempted.Sum(item => Math.Max(0, item.DurationMilliseconds)),
            TotalExternalCallCount = attempted.Sum(item => Math.Max(0, item.ExternalCallCount))
        };
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

public static class EvaluationFingerprints
{
    public static string Baseline(BaselineManifest baseline) => Sha256(CanonicalJson(new
    {
        baseline.Version,
        baseline.CodeFingerprint,
        baseline.InstallationFingerprint,
        baseline.ToolContractFingerprint,
        baseline.EnvironmentFingerprint,
        baseline.Properties
    }));

    public static string StructuralResult(EvaluationCaseResult result) => Sha256(CanonicalJson(new
    {
        result.CaseId,
        result.BaselineFingerprint,
        result.SourceSha256,
        result.Initial.Status,
        result.Initial.Built,
        result.Initial.Reopened,
        result.Initial.RequiredScopePassed,
        result.Initial.NativeEditable,
        result.Initial.Stopped,
        result.Initial.RequirementOutcomes,
        Final = result.Final is null ? null : new
        {
            result.Final.Status,
            result.Final.Built,
            result.Final.Reopened,
            result.Final.RequiredScopePassed,
            result.Final.NativeEditable,
            result.Final.Stopped,
            result.Final.RequirementOutcomes
        }
    }));

    private static string CanonicalJson<T>(T value)
    {
        // Sorted dictionaries are used by all caller-controlled map fields. Record property order is fixed by the type.
        return JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        });
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
