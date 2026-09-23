using System.Text.Json.Serialization;

namespace CadModeling.Ir;

// Candidate locations are normalized page coordinates: top-left origin, x right, y down.
public sealed record DrawingReviewBox(double Left, double Top, double Right, double Bottom);

[JsonConverter(typeof(JsonStringEnumConverter<DrawingCandidateKind>))]
public enum DrawingCandidateKind { Annotation, Geometry, ResidualInk }
[JsonConverter(typeof(JsonStringEnumConverter<DrawingCandidateDisposition>))]
public enum DrawingCandidateDisposition { Unresolved, Feature, NonModel, Duplicate }
[JsonConverter(typeof(JsonStringEnumConverter<DrawingReviewState>))]
public enum DrawingReviewState { Unresolved, Reviewed, Conflict }

public sealed record DrawingOmissionCandidate
{
    public required string Id { get; init; }
    public int PageNumber { get; init; }
    public DrawingCandidateKind Kind { get; init; }
    public required DrawingReviewBox Bounds { get; init; }
    public string Literal { get; init; } = "";
    public IReadOnlyList<string> ObservationIds { get; init; } = [];
    public IReadOnlyList<string> ReviewRegionIds { get; init; } = [];
    public IReadOnlyList<string> RelatedCandidateIds { get; init; } = [];
    public string Provider { get; init; } = "";
    public string GeometryHint { get; init; } = "";
    public bool Conflict { get; init; }
    public string? DimensionKind { get; init; }
    public double? NumericValue { get; init; }
    public int? Multiplicity { get; init; }
    public double? SecondaryValue { get; init; }
}

public sealed record DrawingReviewRegion
{
    public required string Id { get; init; }
    public int PageNumber { get; init; }
    public required DrawingReviewBox Bounds { get; init; }
    public string Purpose { get; init; } = "detail";
    public required string ImagePath { get; init; }
    public required string ImageSha256 { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public long InkPixels { get; init; }
    public long ResidualInkPixels { get; init; }
}

public sealed record DrawingOmissionPage
{
    public int PageNumber { get; init; }
    public bool Ingested { get; init; }
    public string? ObservationPath { get; init; }
    public string? ObservationSha256 { get; init; }
    public string? OverviewPath { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>Ingestion-issued inventory, independent of modeling operations. Hashes detect accidental changes, not malicious authorship.</summary>
public sealed record DrawingOmissionInventory
{
    public string SchemaVersion { get; init; } = "1.0";
    public string Producer { get; init; } = "auto-solidworks-omission-v1";
    public required string SourcePath { get; init; }
    public required string SourceSha256 { get; init; }
    public int TotalPages { get; init; }
    public bool CompleteExtraction { get; init; } = true;
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public IReadOnlyList<DrawingOmissionPage> Pages { get; init; } = [];
    public IReadOnlyList<DrawingReviewRegion> Regions { get; init; } = [];
    public IReadOnlyList<DrawingOmissionCandidate> Candidates { get; init; } = [];
}

public sealed record DrawingCandidateReview
{
    public string CandidateId { get; init; } = "";
    /// <summary>Optional explicit ID batch sharing the same explanation; use exactly one of CandidateId or CandidateIds.</summary>
    public IReadOnlyList<string> CandidateIds { get; init; } = [];
    public DrawingCandidateDisposition Disposition { get; init; }
    public IReadOnlyList<string> FeatureIds { get; init; } = [];
    public IReadOnlyList<string> DimensionIds { get; init; } = [];
    public string? DuplicateOf { get; init; }
    /// <summary>NonModel classification: border, title_block, dimension_graphic, centerline, hatch, note, noise.</summary>
    public string? NonModelCategory { get; init; }
    public string Rationale { get; init; } = "";
    /// <summary>Literal read from the actual crop when correcting OCR; preserve the original inventory.</summary>
    public string? CorrectedLiteral { get; init; }
    public string? CorrectionReason { get; init; }
}

public sealed record DrawingRegionReview
{
    public required string RegionId { get; init; }
    public DrawingReviewState State { get; init; }
    public IReadOnlyList<string> FeatureIds { get; init; } = [];
    public string Findings { get; init; } = "";
}

public sealed record DrawingCrossViewReview
{
    public required string Id { get; init; }
    public IReadOnlyList<string> ViewIds { get; init; } = [];
    public IReadOnlyList<string> FeatureIds { get; init; } = [];
    public DrawingReviewState State { get; init; }
    public string Evidence { get; init; } = "";
}

public sealed record DrawingAdditionalFinding
{
    public required string Id { get; init; }
    public int PageNumber { get; init; }
    public required DrawingReviewBox Bounds { get; init; }
    public string Description { get; init; } = "";
    public DrawingReviewState State { get; init; }
    public IReadOnlyList<string> FeatureIds { get; init; } = [];
}

public sealed record DrawingOmissionReview
{
    public required string InventoryPath { get; init; }
    public required string InventorySha256 { get; init; }
    public IReadOnlyList<DrawingCandidateReview> Candidates { get; init; } = [];
    public IReadOnlyList<DrawingRegionReview> Regions { get; init; } = [];
    public IReadOnlyList<DrawingCrossViewReview> CrossViewChecks { get; init; } = [];
    public IReadOnlyList<DrawingAdditionalFinding> AdditionalFindings { get; init; } = [];
}
