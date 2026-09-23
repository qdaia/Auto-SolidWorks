using System.Text.Json.Serialization;

namespace CadModeling.Drawing.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ViewMapStatus>))]
public enum ViewMapStatus { Resolved, Candidate, Conflict, Unverifiable }

public sealed record ViewMapEntry
{
    public required string ViewId { get; init; }
    public DrawingViewType ViewType { get; init; } = DrawingViewType.Unknown;
    public int PageNumber { get; init; }
    public required string SourceRegionId { get; init; }
    public required string SourceFrameId { get; init; }
    public required string ViewMillimeterFrameId { get; init; }
    public required string ModelFrameId { get; init; }
    public required string SourceToViewTransformId { get; init; }
    public required string ViewToModelTransformId { get; init; }
    public string CalibrationBasis { get; init; } = string.Empty;
    public double PositionUncertaintyMm { get; init; }
    public ViewMapStatus Status { get; init; } = ViewMapStatus.Candidate;
    public FactProvenance Fact { get; init; } = new();
}

public sealed record DrawingViewMapDocument : DrawingDocumentBase
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public ProjectionConvention ProjectionConvention { get; init; } = ProjectionConvention.Unknown;
    public FactProvenance ProjectionFact { get; init; } = new();
    public MeasurementUnit DrawingUnit { get; init; } = MeasurementUnit.Millimeter;
    public FactProvenance UnitFact { get; init; } = new();
    public IReadOnlyList<ViewMapEntry> Views { get; init; } = [];
}

public sealed record ProjectionConventionEvidence
{
    public required string EvidenceId { get; init; }
    public ProjectionConvention Convention { get; init; } = ProjectionConvention.Unknown;
    public string Basis { get; init; } = string.Empty;
    public bool Explicit { get; init; } = true;
}

public sealed record ProjectionConventionResolution(
    ProjectionConvention Convention,
    FactProvenance Fact,
    ViewMapStatus Status,
    IReadOnlyList<string> ConflictingEvidenceIds);

public static class ProjectionConventionResolver
{
    public static ProjectionConventionResolution Resolve(IReadOnlyList<ProjectionConventionEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var explicitKnown = evidence.Where(item => item.Explicit && item.Convention is not ProjectionConvention.Unknown)
            .ToArray();
        if (explicitKnown.Any(item => item.Convention == ProjectionConvention.Mirrored))
            return new(ProjectionConvention.Unknown,
                new() { Status = FactStatus.Unknown, Rationale = "Mirrored projection evidence is not accepted as a projection convention." },
                ViewMapStatus.Conflict, explicitKnown.Select(item => item.EvidenceId).ToArray());
        var conventions = explicitKnown.Select(item => item.Convention).Distinct().ToArray();
        if (conventions.Length > 1)
            return new(ProjectionConvention.Unknown,
                new() { Status = FactStatus.Unknown, Rationale = "Explicit projection evidence conflicts." },
                ViewMapStatus.Conflict, explicitKnown.Select(item => item.EvidenceId).ToArray());
        if (conventions.Length == 1)
            return new(conventions[0], new()
            {
                Status = FactStatus.Stated,
                SourceIds = explicitKnown.Select(item => item.EvidenceId).ToArray(),
                Rationale = string.Join("; ", explicitKnown.Select(item => item.Basis).Where(item => !string.IsNullOrWhiteSpace(item)))
            }, ViewMapStatus.Resolved, []);

        // Required project convention for an unmarked, non-conflicting three-view drawing.
        return new(ProjectionConvention.FirstAngle, new()
        {
            Status = FactStatus.Assumed,
            Rationale = "No explicit projection symbol/label conflict; project default is first-angle.",
            AssumptionAuthorizationId = "project-default-first-angle"
        }, ViewMapStatus.Candidate, []);
    }
}
