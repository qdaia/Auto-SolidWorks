using System.Text.Json.Serialization;

namespace CadModeling.Drawing.Contracts;

public static class DrawingContractSchema
{
    public const string CurrentVersion = "1.0.0";
    public const string LegacyVersion = "0.9.0";
    public static IReadOnlySet<string> SupportedVersions { get; } =
        new HashSet<string>(StringComparer.Ordinal) { CurrentVersion };
}

[JsonConverter(typeof(JsonStringEnumConverter<DocumentStatus>))]
public enum DocumentStatus { Valid, Candidate, Conflict, PendingClarification, Rejected }

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceStatus>))]
public enum EvidenceStatus { Confirmed, Candidate, Conflict, Unreadable }

[JsonConverter(typeof(JsonStringEnumConverter<FactStatus>))]
public enum FactStatus { Stated, Derived, Assumed, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<ContractDiagnosticSeverity>))]
public enum ContractDiagnosticSeverity { Info, Warning, Error }

[JsonConverter(typeof(JsonStringEnumConverter<CoordinateSpace>))]
public enum CoordinateSpace
{
    SourcePixel,
    NormalizedPixel,
    SheetSpace,
    ViewLocal,
    ObjectXyz,
    SolidworksModel
}

[JsonConverter(typeof(JsonStringEnumConverter<MeasurementUnit>))]
public enum MeasurementUnit { Pixel, PdfPoint, Millimeter, Inch, Meter, Degree, Unitless }

[JsonConverter(typeof(JsonStringEnumConverter<ArtifactKind>))]
public enum ArtifactKind
{
    SourceFile,
    SourcePage,
    NormalizedPage,
    ViewCrop,
    JsonDocument,
    Preview,
    NativeModel,
    NeutralModel,
    ValidationReport,
    Other
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceRepresentation>))]
public enum SourceRepresentation { NativeVector, Raster, Hybrid }

[JsonConverter(typeof(JsonStringEnumConverter<ObservationKind>))]
public enum ObservationKind
{
    VisibleLine,
    HiddenLine,
    CenterLine,
    CuttingPlaneLine,
    SectionHatch,
    Circle,
    Arc,
    Text,
    Symbol,
    Leader,
    ExtensionLine,
    DimensionLine,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<DrawingViewType>))]
public enum DrawingViewType
{
    Front,
    Top,
    Right,
    Left,
    Rear,
    Bottom,
    Section,
    Detail,
    Auxiliary,
    Isometric,
    Pictorial,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<ProjectionConvention>))]
public enum ProjectionConvention { FirstAngle, ThirdAngle, ReferenceArrow, Mirrored, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<LineMeaning>))]
public enum LineMeaning
{
    VisibleObject,
    HiddenObject,
    Center,
    CuttingPlane,
    SectionHatch,
    Dimension,
    Extension,
    Leader,
    Border,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<DimensionRole>))]
public enum DimensionRole { Driving, Reference, Tolerance, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<HypothesisFeatureKind>))]
public enum HypothesisFeatureKind
{
    Body,
    Plane,
    Edge,
    Face,
    Hole,
    Bore,
    Pocket,
    Slot,
    Step,
    Boss,
    Rib,
    Opening,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<HypothesisProfileKind>))]
public enum HypothesisProfileKind
{
    CenteredRectangle,
    Circle,
    Polygon,
    CompositeCurve,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<CutTermination>))]
public enum CutTermination { ThroughAll, Blind, SteppedOpening, NotApplicable, PendingClarification }

[JsonConverter(typeof(JsonStringEnumConverter<FeaturePlanOperationKind>))]
public enum FeaturePlanOperationKind
{
    ProfileSketch,
    ExtrudeBoss,
    ExtrudeCut,
    Revolve,
    Fillet,
    Chamfer,
    Pattern,
    Shell,
    Loft,
    Sweep,
    Unsupported
}

[JsonConverter(typeof(JsonStringEnumConverter<FeaturePlanPrimitiveKind>))]
public enum FeaturePlanPrimitiveKind { CenteredRectangle, ThreePointRectangle, Circle, Polygon, CompositeCurve }

[JsonConverter(typeof(JsonStringEnumConverter<FeaturePlanCurveKind>))]
public enum FeaturePlanCurveKind { Line, ThreePointArc }

[JsonConverter(typeof(JsonStringEnumConverter<PlanCapabilityStatus>))]
public enum PlanCapabilityStatus { Supported, NeedsCheck, Unsupported }

[JsonConverter(typeof(JsonStringEnumConverter<PlanDirection>))]
public enum PlanDirection { Normal, Reverse, Symmetric, NotApplicable, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<PlanStartCondition>))]
public enum PlanStartCondition { SketchPlane, Offset, NotApplicable, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<PlanTerminationCondition>))]
public enum PlanTerminationCondition { Blind, MidPlane, UpToSurface, ThroughAll, NotApplicable, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<PlanReviewStatus>))]
public enum PlanReviewStatus { Pending, Approved, Rejected }

[JsonConverter(typeof(JsonStringEnumConverter<TraceStage>))]
public enum TraceStage
{
    SourceRegion,
    Observation,
    DimensionObservation,
    Interpretation,
    DimensionBinding,
    HypothesisFeature,
    FeaturePlanOperation,
    GenericDraftOperation,
    ModelingIrOperation,
    SolidworksFeature,
    ReprojectionDifference,
    Failure,
    RepairPatch
}

[JsonConverter(typeof(JsonStringEnumConverter<TraceEdgeType>))]
public enum TraceEdgeType
{
    SourceRegionToObservation,
    ObservationToInterpretation,
    DimensionObservationToDimensionBinding,
    InterpretationToHypothesisFeature,
    HypothesisFeatureToFeaturePlanOperation,
    FeaturePlanOperationToGenericDraftOperation,
    GenericDraftOperationToModelingIrOperation,
    ModelingIrOperationToSolidworksFeature,
    SourceOrModelEntityToReprojectionDifference,
    FailureOrDifferenceToRepairPatch
}

public sealed record ProducerStamp
{
    public string ProducerName { get; init; } = string.Empty;
    public string ProducerVersion { get; init; } = string.Empty;
    public string ConfigurationHash { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record FactProvenance
{
    public FactStatus Status { get; init; } = FactStatus.Unknown;
    public FactStatus? PreviousStatus { get; init; }
    public IReadOnlyList<string> SourceIds { get; init; } = [];
    public string Rationale { get; init; } = string.Empty;
    public string? AssumptionAuthorizationId { get; init; }
}

public sealed record ContractDiagnostic
{
    public string Id { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public ContractDiagnosticSeverity Severity { get; init; }
    public bool Blocking { get; init; }
    public string Message { get; init; } = string.Empty;
    public string DocumentId { get; init; } = string.Empty;
    public string FieldPath { get; init; } = string.Empty;
    public string? EvidenceId { get; init; }
    public IReadOnlyList<string> AffectedIds { get; init; } = [];
}

public sealed record UnresolvedItem
{
    public string Id { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool Blocking { get; init; }
    public IReadOnlyList<string> AffectedIds { get; init; } = [];
    public string Reason { get; init; } = string.Empty;
    public string MinimumQuestion { get; init; } = string.Empty;
    public string SuggestedEvidence { get; init; } = string.Empty;
}

public sealed record ArtifactRecord
{
    public string ArtifactId { get; init; } = string.Empty;
    public ArtifactKind Kind { get; init; }
    public string Uri { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public int? PageNumber { get; init; }
}

public sealed record CoordinateFrame
{
    public string FrameId { get; init; } = string.Empty;
    public CoordinateSpace Space { get; init; }
    public MeasurementUnit Unit { get; init; }
    public string ArtifactId { get; init; } = string.Empty;
    public string OriginDescription { get; init; } = string.Empty;
    public IReadOnlyList<string> AxisLabels { get; init; } = [];
}

public sealed record LocatedPoint2
{
    public string CoordinateFrameId { get; init; } = string.Empty;
    public double X { get; init; }
    public double Y { get; init; }
}

public sealed record LocatedPoint3
{
    public string CoordinateFrameId { get; init; } = string.Empty;
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
}

public sealed record QuantityValue
{
    public double Value { get; init; }
    public MeasurementUnit Unit { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public FactProvenance Fact { get; init; } = new();
}

public sealed record TransformRecord
{
    public string TransformId { get; init; } = string.Empty;
    public string FromFrameId { get; init; } = string.Empty;
    public string ToFrameId { get; init; } = string.Empty;
    public IReadOnlyList<IReadOnlyList<double>> ForwardMatrix { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<double>> InverseMatrix { get; init; } = [];
    public MeasurementUnit InputUnit { get; init; }
    public MeasurementUnit OutputUnit { get; init; }
    public IReadOnlyDictionary<string, double> Parameters { get; init; } = new SortedDictionary<string, double>(StringComparer.Ordinal);
    public double ErrorEstimate { get; init; }
    public MeasurementUnit ErrorUnit { get; init; }
    public string Source { get; init; } = string.Empty;
    public string BeforeArtifactId { get; init; } = string.Empty;
    public string AfterArtifactId { get; init; } = string.Empty;
}

public abstract record DrawingDocumentBase
{
    public string SchemaVersion { get; init; } = DrawingContractSchema.CurrentVersion;
    public string DocumentId { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public string ProducerName { get; init; } = string.Empty;
    public string ProducerVersion { get; init; } = string.Empty;
    public string ConfigurationHash { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DocumentStatus Status { get; init; } = DocumentStatus.Candidate;
    public EvidenceStatus EvidenceStatus { get; init; } = EvidenceStatus.Candidate;
    public FactStatus FactStatus { get; init; } = FactStatus.Unknown;
    public IReadOnlyList<ContractDiagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyList<UnresolvedItem> UnresolvedItems { get; init; } = [];
    public IReadOnlyList<ArtifactRecord> ArtifactManifest { get; init; } = [];
    public IReadOnlyList<CoordinateFrame> CoordinateFrames { get; init; } = [];
    public IReadOnlyList<TransformRecord> Transforms { get; init; } = [];
    public IReadOnlyList<MigrationRecord> MigrationHistory { get; init; } = [];
}

public sealed record MigrationRecord
{
    public string FromVersion { get; init; } = string.Empty;
    public string ToVersion { get; init; } = string.Empty;
    public string MigratorName { get; init; } = string.Empty;
    public DateTimeOffset MigratedAt { get; init; }
    public string Rationale { get; init; } = string.Empty;
}
