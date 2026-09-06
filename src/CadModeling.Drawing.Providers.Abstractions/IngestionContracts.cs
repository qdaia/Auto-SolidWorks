using System.Text.Json.Serialization;
using CadModeling.Drawing.Contracts;

namespace CadModeling.Drawing.Providers.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter<DrawingInputKind>))]
public enum DrawingInputKind { Pdf, Png, Jpeg, Bmp, Tiff }

[JsonConverter(typeof(JsonStringEnumConverter<ObservationModality>))]
public enum ObservationModality { NativePdf, RasterLayout, RasterText, RasterPrimitive }

[JsonConverter(typeof(JsonStringEnumConverter<IngestionOutcome>))]
public enum IngestionOutcome { Succeeded, SucceededWithWarnings, Rejected }

public sealed record DrawingIngestionLimits
{
    public long MaximumFileBytes { get; init; } = 100 * 1024 * 1024;
    public int MaximumPageCount { get; init; } = 50;
    public long MaximumPixelsPerPage { get; init; } = 80_000_000;
    public int MinimumDpi { get; init; } = 72;
    public int MaximumDpi { get; init; } = 600;
    public int MaximumRuntimeSeconds { get; init; } = 300;
    public long MaximumArtifactBytes { get; init; } = 500 * 1024 * 1024;
}

public sealed record DrawingIngestionRequest
{
    public IReadOnlyList<DrawingViewRegionHint> ViewHints { get; init; } = [];
    public string InputPath { get; init; } = string.Empty;
    public IReadOnlyList<int> PageNumbers { get; init; } = [];
    public int RenderDpi { get; init; } = 300;
    public string PreprocessProfile { get; init; } = "engineering-default-v1";
    public string ProviderProfile { get; init; } = "offline-default-v1";
    public string ArtifactRoot { get; init; } = string.Empty;
    public DrawingIngestionLimits Limits { get; init; } = new();
    public int DeterministicSeed { get; init; } = 1729;
}

public sealed record DrawingIngestionResponse
{
    public IngestionOutcome Outcome { get; init; }
    public string RunId { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public DrawingSourceManifest? SourceManifest { get; init; }
    public IReadOnlyList<PageIngestionResult> Pages { get; init; } = [];
    public IReadOnlyList<IngestionDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record PageIngestionResult
{
    public int PageNumber { get; init; }
    public DrawingObservationDocument Observation { get; init; } = new();
    public string ObservationPath { get; init; } = string.Empty;
    public string PreprocessLedgerPath { get; init; } = string.Empty;
    public string ArtifactManifestPath { get; init; } = string.Empty;
    public string OverlayPath { get; init; } = string.Empty;
}

public sealed record IngestionDiagnostic
{
    public string Code { get; init; } = string.Empty;
    public ContractDiagnosticSeverity Severity { get; init; }
    public bool Blocking { get; init; }
    public string Message { get; init; } = string.Empty;
    public int? PageNumber { get; init; }
    public string? ProviderName { get; init; }
}

public sealed record ProviderProvenance
{
    public string ProviderName { get; init; } = string.Empty;
    public string ProviderVersion { get; init; } = string.Empty;
    public string? BinarySha256 { get; init; }
    public string? ModelSha256 { get; init; }
    public string ConfigurationSha256 { get; init; } = string.Empty;
    public int DeterministicSeed { get; init; }
    public string Hardware { get; init; } = string.Empty;
}

public sealed record ProviderPageContext
{
    public IReadOnlyList<DrawingViewRegionHint> ViewHints { get; init; } = [];
    public string SourcePath { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public DrawingInputKind InputKind { get; init; }
    public int PageNumber { get; init; }
    public int RenderDpi { get; init; }
    public string PageArtifactDirectory { get; init; } = string.Empty;
    public string ProviderProfile { get; init; } = string.Empty;
    public int DeterministicSeed { get; init; }
}

/// <summary>Agent-interpreted view regions in normalized page coordinates [0,1]. Rotation applies only to OCR, not the source image.</summary>
public sealed record DrawingViewRegionHint
{
    public required string Id { get; init; }
    public int PageNumber { get; init; } = 1;
    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; } = 1;
    public double Bottom { get; init; } = 1;
    public DrawingViewType ViewType { get; init; } = DrawingViewType.Unknown;
    public int OcrRotationClockwise { get; init; }
    public int OcrPageSegmentationMode { get; init; } = 11;
}

public sealed record NativePdfPageObservation
{
    public int PageNumber { get; init; }
    public double WidthPoints { get; init; }
    public double HeightPoints { get; init; }
    public int RotationDegrees { get; init; }
    public bool HasOptionalContent { get; init; }
    public IReadOnlyList<ObservationEntity> Observations { get; init; } = [];
    public ProviderProvenance Provenance { get; init; } = new();
    public IReadOnlyList<IngestionDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record RasterPage
{
    public int PageNumber { get; init; }
    public string ImagePath { get; init; } = string.Empty;
    public string MediaType { get; init; } = "image/png";
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public double Dpi { get; init; }
    public string ArtifactId { get; init; } = string.Empty;
    public string CoordinateFrameId { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}

public sealed record PreprocessStep
{
    public int Sequence { get; init; }
    public string Operation { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    public string BeforeArtifactId { get; init; } = string.Empty;
    public string AfterArtifactId { get; init; } = string.Empty;
    public IReadOnlyList<IReadOnlyList<double>> ForwardMatrix { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<double>> InverseMatrix { get; init; } = [];
}

public sealed record PreprocessLedger
{
    public string Profile { get; init; } = string.Empty;
    public int PageNumber { get; init; }
    public IReadOnlyList<PreprocessStep> Steps { get; init; } = [];
    public ProviderProvenance Provenance { get; init; } = new();
}

public sealed record RasterPreprocessResult
{
    public RasterPage SourcePage { get; init; } = new();
    public RasterPage NormalizedPage { get; init; } = new();
    public PreprocessLedger Ledger { get; init; } = new();
    public IReadOnlyList<TransformRecord> Transforms { get; init; } = [];
    public ProviderProvenance Provenance { get; init; } = new();
    public IReadOnlyList<IngestionDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record ProviderObservationBatch
{
    public int PageNumber { get; init; }
    public ObservationModality Modality { get; init; }
    public IReadOnlyList<SourceRegion> SourceRegions { get; init; } = [];
    public IReadOnlyList<ViewRegionObservation> ViewRegions { get; init; } = [];
    public IReadOnlyList<ObservationEntity> Observations { get; init; } = [];
    public ProviderProvenance Provenance { get; init; } = new();
    public IReadOnlyList<IngestionDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record ObservationFusionResult
{
    public IReadOnlyList<SourceRegion> SourceRegions { get; init; } = [];
    public IReadOnlyList<ViewRegionObservation> ViewRegions { get; init; } = [];
    public IReadOnlyList<ObservationEntity> Observations { get; init; } = [];
    public IReadOnlyList<IngestionDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record ArtifactManifestDocument
{
    public string SourceSha256 { get; init; } = string.Empty;
    public int PageNumber { get; init; }
    public IReadOnlyList<ArtifactRecord> Artifacts { get; init; } = [];
    public IReadOnlyList<ProviderProvenance> Providers { get; init; } = [];
}
