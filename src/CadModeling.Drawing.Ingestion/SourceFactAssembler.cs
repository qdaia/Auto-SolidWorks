using CadModeling.Drawing.Contracts;

namespace CadModeling.Drawing.Ingestion;

/// <summary>
/// Converts observation-layer dimension candidates into a versioned source-fact inventory.
/// Candidate OCR/vision output is intentionally not promoted to stated truth here.
/// </summary>
public static class SourceFactAssembler
{
    public static SourceFactsDocument Assemble(
        string sourceSha256,
        IReadOnlyList<DrawingObservationDocument> pages,
        string producerName,
        string producerVersion,
        string configurationHash,
        DateTimeOffset createdAt)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("drawing.source_facts");
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        ArgumentNullException.ThrowIfNull(pages);
        var facts = new List<SourceFact>();
        foreach (var page in pages.OrderBy(item => PageNumber(item)))
        {
            var regions = page.SourceRegions.ToDictionary(item => item.RegionId, StringComparer.Ordinal);
            var views = page.ViewRegions.GroupBy(item => item.SourceRegionId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var dimension in page.DimensionObservations)
            {
                var pageNumber = regions.TryGetValue(dimension.SourceRegionId, out var region) ? region.PageNumber : PageNumber(page);
                views.TryGetValue(dimension.SourceRegionId, out var view);
                var kind = Kind(dimension.DimensionKind);
                var unit = dimension.CandidateUnit ?? DefaultUnit(kind);
                var evidenceIds = new[] { dimension.DimensionObservationId, dimension.SourceObservationId }
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
                var candidate = new SourceFactCandidate
                {
                    CandidateId = dimension.DimensionObservationId,
                    RawLiteral = dimension.RawLiteral,
                    NumericValue = dimension.CandidateNumericValue,
                    Unit = unit,
                    Symbol = string.IsNullOrWhiteSpace(dimension.CandidateSymbol) ? "linear" : dimension.CandidateSymbol,
                    Interpretation = dimension.DimensionKind,
                    Confidence = Math.Clamp(dimension.Confidence, 0, 1),
                    EvidenceStatus = dimension.EvidenceStatus,
                    EvidenceIds = evidenceIds
                };
                facts.Add(new()
                {
                    FactId = "fact-" + dimension.DimensionObservationId,
                    Kind = kind,
                    PageNumber = pageNumber,
                    ViewId = view?.ViewRegionId,
                    SourceRegionId = dimension.SourceRegionId,
                    Candidates = [candidate],
                    InterpretedLiteral = dimension.RawLiteral,
                    NumericValue = null,
                    Unit = unit,
                    Multiplicity = dimension.Count > 1 ? dimension.Count : null,
                    Fact = new()
                    {
                        Status = FactStatus.Unknown,
                        Rationale = "候选观察被保留而不进行真值提升。"
                    },
                    EvidenceIds = evidenceIds,
                    Critical = true
                });
            }
        }

        var initial = new SourceFactsDocument
        {
            RevisionId = "pending",
            RevisionRationale = "初始独立来源库存自绘图观察。",
            DocumentId = "source-facts",
            SourceSha256 = sourceSha256,
            ProducerName = producerName,
            ProducerVersion = producerVersion,
            ConfigurationHash = configurationHash,
            CreatedAt = createdAt,
            Status = facts.Count == 0 ? DocumentStatus.PendingClarification : DocumentStatus.Candidate,
            EvidenceStatus = facts.Count == 0 ? EvidenceStatus.Unreadable : EvidenceStatus.Candidate,
            FactStatus = FactStatus.Unknown,
            Facts = facts
        };
        return initial with { RevisionId = SourceFactRevisions.RevisionId(initial) };
    }

    public static SourceFact ConfirmCandidate(SourceFact fact, string candidateId, FactStatus status = FactStatus.Stated,
        string? assumptionAuthorizationId = null)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (status is not (FactStatus.Stated or FactStatus.Assumed))
            throw new ArgumentOutOfRangeException(nameof(status), "候选确认只能创建已声明或明确假设的事实。");
        if (status == FactStatus.Assumed && string.IsNullOrWhiteSpace(assumptionAuthorizationId))
            throw new ArgumentException("假设的源事实需要明确的授权ID。", nameof(assumptionAuthorizationId));
        var candidate = fact.Candidates.SingleOrDefault(item => item.CandidateId == candidateId)
            ?? throw new ArgumentException($"候选项 '{candidateId}' 不属于源事实 '{fact.FactId}'。", nameof(candidateId));
        if (candidate.NumericValue is null && fact.Kind is not SourceFactKind.FeatureRequirement)
            throw new InvalidOperationException("无法确认一个数值源事实，除非有一个数值候选项。");
        return fact with
        {
            NumericValue = candidate.NumericValue,
            Unit = candidate.Unit,
            InterpretedLiteral = string.IsNullOrWhiteSpace(candidate.Interpretation) ? candidate.RawLiteral : candidate.Interpretation,
            Fact = new()
            {
                Status = status,
                PreviousStatus = fact.Fact.Status,
                SourceIds = status == FactStatus.Stated ? candidate.EvidenceIds : [],
                Rationale = status == FactStatus.Stated ? "确认显式源候选项。" : "明确授权的假设。",
                AssumptionAuthorizationId = status == FactStatus.Assumed ? assumptionAuthorizationId : null
            },
            EvidenceIds = candidate.EvidenceIds
        };
    }

    private static int PageNumber(DrawingObservationDocument document) =>
        document.SourceRegions.Select(item => item.PageNumber).Where(number => number > 0).DefaultIfEmpty(1).Min();

    private static SourceFactKind Kind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "diameter" => SourceFactKind.Diameter,
        "radius" => SourceFactKind.Radius,
        "angle" or "chamfer" => SourceFactKind.Angle,
        "count" => SourceFactKind.Count,
        "depth" => SourceFactKind.Depth,
        "thickness" => SourceFactKind.Thickness,
        "linear" => SourceFactKind.LinearDimension,
        _ => SourceFactKind.Unknown
    };

    private static MeasurementUnit? DefaultUnit(SourceFactKind kind) => kind switch
    {
        SourceFactKind.Angle => MeasurementUnit.Degree,
        SourceFactKind.Count => MeasurementUnit.Unitless,
        SourceFactKind.LinearDimension or SourceFactKind.Diameter or SourceFactKind.Radius or SourceFactKind.Depth or SourceFactKind.Thickness => MeasurementUnit.Millimeter,
        _ => null
    };
}
