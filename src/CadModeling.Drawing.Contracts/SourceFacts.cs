using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CadModeling.Drawing.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<SourceFactKind>))]
public enum SourceFactKind
{
    LinearDimension,
    Diameter,
    Radius,
    Angle,
    Count,
    Depth,
    Thickness,
    FeatureRequirement,
    ProjectionConvention,
    UnitConvention,
    Unknown
}

public sealed record SourceFactCandidate
{
    public required string CandidateId { get; init; }
    public string RawLiteral { get; init; } = string.Empty;
    public double? NumericValue { get; init; }
    public MeasurementUnit? Unit { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public string Interpretation { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public EvidenceStatus EvidenceStatus { get; init; } = EvidenceStatus.Candidate;
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
}

/// <summary>
/// Restricted arithmetic derivation. Derived facts are intentionally represented as coefficients
/// instead of executable expressions so a source revision can be recomputed without evaluating code.
/// value = constant + sum(coefficient[fact_id] * fact[fact_id].numeric_value_in_same_unit)
/// </summary>
public sealed record SourceFactDerivation
{
    public string Formula { get; init; } = string.Empty;
    public double Constant { get; init; }
    public IReadOnlyDictionary<string, double> Coefficients { get; init; } =
        new SortedDictionary<string, double>(StringComparer.Ordinal);
}

public sealed record SourceFact
{
    public required string FactId { get; init; }
    public SourceFactKind Kind { get; init; } = SourceFactKind.Unknown;
    public int PageNumber { get; init; }
    public string? ViewId { get; init; }
    public string? SourceRegionId { get; init; }
    public IReadOnlyList<SourceFactCandidate> Candidates { get; init; } = [];
    public string InterpretedLiteral { get; init; } = string.Empty;
    public double? NumericValue { get; init; }
    public MeasurementUnit? Unit { get; init; }
    public int? Multiplicity { get; init; }
    public FactProvenance Fact { get; init; } = new();
    public SourceFactDerivation? Derivation { get; init; }
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
    public bool Critical { get; init; } = true;
}

/// <summary>
/// Source requirements independent from modeling operations. Nothing in this document references an
/// OperationId or a typed-model parameter path; those links are created later by DimensionBinding.
/// </summary>
public sealed record SourceFactsDocument : DrawingDocumentBase
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string RevisionId { get; init; }
    public string? ParentRevisionId { get; init; }
    public string RevisionRationale { get; init; } = string.Empty;
    public IReadOnlyList<SourceFact> Facts { get; init; } = [];
}

public sealed record SourceFactRevisionIssue(string Code, string Message, IReadOnlyList<string> FactIds);

public sealed record SourceFactRevisionResult(SourceFactsDocument Document, IReadOnlyList<SourceFactRevisionIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public static class SourceFactRevisions
{
    public static SourceFactRevisionResult Recompute(SourceFactsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var issues = ValidateShape(document).ToList();
        if (issues.Any(item => item.Code == "SRC_DUPLICATE_FACT"))
        {
            var unchanged = document with { RevisionId = RevisionId(document) };
            return new(unchanged, issues);
        }
        var facts = document.Facts.ToDictionary(item => item.FactId, StringComparer.Ordinal);
        var remaining = facts.Values.Where(item => item.Derivation is not null).Select(item => item.FactId).ToHashSet(StringComparer.Ordinal);

        // Bounded topological evaluation. Cycles and missing dependencies remain explicit unknowns.
        for (var pass = 0; pass < facts.Count && remaining.Count > 0; pass++)
        {
            var progressed = false;
            foreach (var id in remaining.ToArray())
            {
                var fact = facts[id];
                var derivation = fact.Derivation!;
                var dependencies = derivation.Coefficients.Keys.ToArray();
                if (dependencies.Any(dep => !facts.ContainsKey(dep)))
                {
                    issues.Add(new("SRC_DERIVATION_MISSING_INPUT", $"衍生事实 '{id}' 参考了一个缺失的输入。", [id, .. dependencies.Where(dep => !facts.ContainsKey(dep))]));
                    remaining.Remove(id);
                    facts[id] = AsUnknown(fact, "缺少 推导过程 输入。");
                    progressed = true;
                    continue;
                }
                if (dependencies.Any(remaining.Contains)) continue;
                var inputs = dependencies.Select(dep => facts[dep]).ToArray();
                if (inputs.Any(input => input.NumericValue is null || input.Fact.Status is FactStatus.Unknown or FactStatus.Assumed))
                {
                    facts[id] = AsUnknown(fact, "输入的衍生值未解决。");
                    remaining.Remove(id);
                    progressed = true;
                    continue;
                }
                var unit = fact.Unit ?? inputs.Select(item => item.Unit).FirstOrDefault(item => item.HasValue);
                if (unit is null || inputs.Any(input => input.Unit != unit))
                {
                    issues.Add(new("SRC_DERIVATION_UNIT", $"派生事实 '{id}' 混合了单位或没有单位。", [id, .. dependencies]));
                    facts[id] = AsUnknown(fact, "导出的单位不兼容。");
                    remaining.Remove(id);
                    progressed = true;
                    continue;
                }
                var value = derivation.Constant + derivation.Coefficients.Sum(term => term.Value * facts[term.Key].NumericValue!.Value);
                if (!double.IsFinite(value))
                {
                    issues.Add(new("SRC_DERIVATION_NONFINITE", $"派生事实{id}产生了非有限值。", [id]));
                    facts[id] = AsUnknown(fact, "求解产生了非有限值。");
                }
                else
                {
                    facts[id] = fact with
                    {
                        NumericValue = value,
                        Unit = unit,
                        Fact = new()
                        {
                            Status = FactStatus.Derived,
                            PreviousStatus = fact.Fact.Status,
                            SourceIds = dependencies,
                            Rationale = string.IsNullOrWhiteSpace(derivation.Formula) ? "受限算术推导" : derivation.Formula
                        }
                    };
                }
                remaining.Remove(id);
                progressed = true;
            }
            if (!progressed) break;
        }

        foreach (var id in remaining)
        {
            var dependencies = facts[id].Derivation!.Coefficients.Keys.ToArray();
            issues.Add(new("SRC_DERIVATION_CYCLE", $"派生事实 '{id}' 参与了一个派生循环。", [id, .. dependencies]));
            facts[id] = AsUnknown(facts[id], "依赖循环的导出。");
        }

        var ordered = document.Facts.Select(item => facts[item.FactId]).ToArray();
        var updated = document with { Facts = ordered };
        return new(updated with { RevisionId = RevisionId(updated) }, issues);
    }

    public static SourceFactRevisionResult CreateRevision(
        SourceFactsDocument prior,
        IReadOnlyList<SourceFact> replacements,
        string rationale,
        DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(replacements);
        if (string.IsNullOrWhiteSpace(rationale))
            throw new ArgumentException("源版本需要一个理由。", nameof(rationale));
        var priorIssues = ValidateShape(prior).Where(item => item.Code == "SRC_DUPLICATE_FACT").ToArray();
        var replacementDuplicates = replacements.GroupBy(item => item.FactId, StringComparer.Ordinal).Where(group => group.Count() > 1).ToArray();
        if (priorIssues.Length > 0 || replacementDuplicates.Length > 0)
        {
            var issues = priorIssues.Concat(replacementDuplicates.Select(group =>
                new SourceFactRevisionIssue("SRC_DUPLICATE_FACT", $"特征{group.Key}的替换源特征已复制。", [group.Key]))).ToArray();
            return new(prior, issues);
        }
        var map = prior.Facts.ToDictionary(item => item.FactId, StringComparer.Ordinal);
        foreach (var replacement in replacements) map[replacement.FactId] = replacement;
        var revision = prior with
        {
            ParentRevisionId = prior.RevisionId,
            RevisionId = "pending",
            RevisionRationale = rationale.Trim(),
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            Facts = prior.Facts.Select(item => map.Remove(item.FactId, out var replacement) ? replacement : item)
                .Concat(map.Values.OrderBy(item => item.FactId, StringComparer.Ordinal)).ToArray()
        };
        return Recompute(revision);
    }

    public static IReadOnlyList<SourceFactRevisionIssue> ValidateShape(SourceFactsDocument document)
    {
        var issues = new List<SourceFactRevisionIssue>();
        foreach (var duplicate in document.Facts.GroupBy(item => item.FactId, StringComparer.Ordinal).Where(group => group.Count() > 1))
            issues.Add(new("SRC_DUPLICATE_FACT", $"复制源特征 '{duplicate.Key}'。", [duplicate.Key]));
        foreach (var fact in document.Facts)
        {
            if (string.IsNullOrWhiteSpace(fact.FactId)) issues.Add(new("SRC_FACT_ID", "源特征ID是必需的。", [fact.FactId]));
            if (fact.PageNumber < 1) issues.Add(new("SRC_PAGE", $"源特征 '{fact.FactId}' 需要正数页码。", [fact.FactId]));
            if (fact.Fact.Status == FactStatus.Stated && fact.NumericValue is null && fact.Kind is not SourceFactKind.FeatureRequirement)
                issues.Add(new("SRC_VALUE", $"声明的数值事实 '{fact.FactId}' 没有数值值。", [fact.FactId]));
            if (fact.Fact.Status == FactStatus.Derived && fact.Derivation is null)
                issues.Add(new("SRC_DERIVATION_REQUIRED", $"派生事实 '{fact.FactId}' 需要显式的派生。", [fact.FactId]));
            if (fact.Fact.Status == FactStatus.Stated && fact.EvidenceIds.Count == 0)
                issues.Add(new("SRC_STATED_EVIDENCE", $"明确的陈述事实 '{fact.FactId}' 需要证据。", [fact.FactId]));
            if (fact.Derivation is not null && fact.Derivation.Coefficients.Count == 0)
                issues.Add(new("SRC_DERIVATION_EMPTY", $"派生事实 '{fact.FactId}' 没有输入。", [fact.FactId]));
            if (fact.Candidates.Any(candidate => candidate.Confidence is < 0 or > 1))
                issues.Add(new("SRC_CANDIDATE_CONFIDENCE", $"源特征 '{fact.FactId}' 的候选置信度位于 [0,1] 之外。", [fact.FactId]));
        }
        return issues;
    }

    public static string RevisionId(SourceFactsDocument document)
    {
        var canonical = DrawingContractJson.SerializeDeterministic(new
        {
            document.SourceSha256,
            document.ParentRevisionId,
            document.RevisionRationale,
            Facts = document.Facts.Select(fact => new
            {
                fact.FactId,
                fact.Kind,
                fact.PageNumber,
                fact.ViewId,
                fact.SourceRegionId,
                fact.Candidates,
                fact.InterpretedLiteral,
                fact.NumericValue,
                fact.Unit,
                fact.Multiplicity,
                fact.Fact,
                fact.Derivation,
                fact.EvidenceIds,
                fact.Critical
            }).ToArray()
        });
        return "srcfacts-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..24];
    }

    public static string FactFingerprint(SourceFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var canonical = DrawingContractJson.SerializeDeterministic(new
        {
            fact.FactId,
            fact.Kind,
            fact.PageNumber,
            fact.ViewId,
            fact.SourceRegionId,
            fact.InterpretedLiteral,
            fact.NumericValue,
            fact.Unit,
            fact.Multiplicity,
            fact.Fact,
            fact.Derivation,
            EvidenceIds = fact.EvidenceIds.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            fact.Critical
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static SourceFact AsUnknown(SourceFact fact, string rationale) => fact with
    {
        NumericValue = null,
        Fact = new()
        {
            Status = FactStatus.Unknown,
            PreviousStatus = fact.Fact.Status,
            Rationale = rationale
        }
    };
}
