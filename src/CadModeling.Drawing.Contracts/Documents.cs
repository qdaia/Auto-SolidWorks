using System.Text.Json.Serialization;

namespace CadModeling.Drawing.Contracts;

public sealed record DrawingSourceManifest : DrawingDocumentBase
{
    public string SourcePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public SourceRepresentation Representation { get; init; }
    public IReadOnlyList<SourcePage> Pages { get; init; } = [];
}

public sealed record SourcePage
{
    public string PageId { get; init; } = string.Empty;
    public int PageNumber { get; init; }
    public string ArtifactId { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public double PhysicalWidth { get; init; }
    public double PhysicalHeight { get; init; }
    public MeasurementUnit PhysicalUnit { get; init; }
    public double Dpi { get; init; }
    public SourceRepresentation Representation { get; init; }
}

public sealed record DrawingObservationDocument : DrawingDocumentBase
{
    public IReadOnlyList<SourceRegion> SourceRegions { get; init; } = [];
    public IReadOnlyList<ViewRegionObservation> ViewRegions { get; init; } = [];
    public IReadOnlyList<ObservationEntity> Observations { get; init; } = [];
    public IReadOnlyList<DimensionObservation> DimensionObservations { get; init; } = [];
}

public sealed record SourceRegion
{
    public string RegionId { get; init; } = string.Empty;
    public string ArtifactId { get; init; } = string.Empty;
    public int PageNumber { get; init; }
    public IReadOnlyList<LocatedPoint2> Polygon { get; init; } = [];
    public EvidenceStatus EvidenceStatus { get; init; } = EvidenceStatus.Candidate;
}

public sealed record ViewRegionObservation
{
    public DrawingViewType ViewTypeHint { get; init; } = DrawingViewType.Unknown;
    public string AssignmentBasis { get; init; } = "unassigned";
    public string ViewRegionId { get; init; } = string.Empty;
    public string SourceRegionId { get; init; } = string.Empty;
    public IReadOnlyList<string> ObservationIds { get; init; } = [];
    public EvidenceStatus EvidenceStatus { get; init; } = EvidenceStatus.Candidate;
}

public sealed record ObservationEntity
{
    public string ObservationId { get; init; } = string.Empty;
    public string SourceRegionId { get; init; } = string.Empty;
    public ObservationKind Kind { get; init; }
    public string RawLiteral { get; init; } = string.Empty;
    public IReadOnlyList<LocatedPoint2> Geometry { get; init; } = [];
    public EvidenceStatus EvidenceStatus { get; init; } = EvidenceStatus.Candidate;
    public double Confidence { get; init; }
    public IReadOnlyDictionary<string, string> CandidateProperties { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
}

public sealed record DimensionObservation
{
    public string SourceObservationId { get; init; } = string.Empty;
    public string DimensionKind { get; init; } = "linear";
    public int Count { get; init; } = 1;
    public double? SecondaryValue { get; init; }
    public IReadOnlyList<string> NearbyGeometryObservationIds { get; init; } = [];
    public string BindingStatus { get; init; } = "candidate_requires_geometry_interpretation";
    public string DimensionObservationId { get; init; } = string.Empty;
    public string SourceRegionId { get; init; } = string.Empty;
    public string RawLiteral { get; init; } = string.Empty;
    public double? CandidateNumericValue { get; init; }
    public string CandidateSymbol { get; init; } = string.Empty;
    public MeasurementUnit? CandidateUnit { get; init; }
    public EvidenceStatus EvidenceStatus { get; init; } = EvidenceStatus.Candidate;
    public double Confidence { get; init; }
}

public sealed record DrawingInterpretationDocument : DrawingDocumentBase
{
    public ProjectionConvention ProjectionConvention { get; init; } = ProjectionConvention.Unknown;
    public FactProvenance ProjectionFact { get; init; } = new();
    public MeasurementUnit DrawingUnit { get; init; } = MeasurementUnit.Unitless;
    public FactProvenance UnitFact { get; init; } = new();
    public IReadOnlyList<ViewInterpretation> Views { get; init; } = [];
    public IReadOnlyList<LineInterpretation> LineInterpretations { get; init; } = [];
    public IReadOnlyList<InterpretedFeature> Features { get; init; } = [];
    public IReadOnlyList<DimensionBinding> DimensionBindings { get; init; } = [];
}

public sealed record ViewInterpretation
{
    public string ViewId { get; init; } = string.Empty;
    public string ViewRegionObservationId { get; init; } = string.Empty;
    public DrawingViewType ViewType { get; init; } = DrawingViewType.Unknown;
    public string CoordinateFrameId { get; init; } = string.Empty;
    public EvidenceStatus EvidenceStatus { get; init; } = EvidenceStatus.Candidate;
    public FactProvenance Fact { get; init; } = new();
}

public sealed record LineInterpretation
{
    public string InterpretationId { get; init; } = string.Empty;
    public string ObservationId { get; init; } = string.Empty;
    public LineMeaning Meaning { get; init; } = LineMeaning.Unknown;
    public EvidenceStatus EvidenceStatus { get; init; } = EvidenceStatus.Candidate;
    public FactProvenance Fact { get; init; } = new();
}

public sealed record InterpretedFeature
{
    public string FeatureId { get; init; } = string.Empty;
    public string FeatureType { get; init; } = string.Empty;
    public IReadOnlyList<string> ObservationIds { get; init; } = [];
    public IReadOnlyList<string> ViewIds { get; init; } = [];
    public string TopologyDescription { get; init; } = string.Empty;
    public EvidenceStatus EvidenceStatus { get; init; } = EvidenceStatus.Candidate;
    public FactProvenance Fact { get; init; } = new();
}

public sealed record DimensionBinding
{
    public string BindingId { get; init; } = string.Empty;
    public string DimensionObservationId { get; init; } = string.Empty;
    public string SourceRegionId { get; init; } = string.Empty;
    public string RawLiteral { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public QuantityValue Value { get; init; } = new();
    public DimensionRole Role { get; init; } = DimensionRole.Unknown;
    public string Axis { get; init; } = string.Empty;
    public IReadOnlyList<string> TargetFeatureIds { get; init; } = [];
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
    public EvidenceStatus EvidenceStatus { get; init; } = EvidenceStatus.Candidate;
}

public sealed record DrawingHypothesisDocument : DrawingDocumentBase
{
    public IReadOnlyList<GeometryHypothesis> Hypotheses { get; init; } = [];
}

public sealed record GeometryHypothesis
{
    public string HypothesisId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DocumentStatus Status { get; init; } = DocumentStatus.Candidate;
    public double Confidence { get; init; }
    public IReadOnlyList<string> ExplainsViewIds { get; init; } = [];
    public IReadOnlyList<HypothesisFeature> Features { get; init; } = [];
    public IReadOnlyList<string> ContradictionIds { get; init; } = [];
}

public sealed record HypothesisFeature
{
    public string HypothesisFeatureId { get; init; } = string.Empty;
    public HypothesisFeatureKind Kind { get; init; } = HypothesisFeatureKind.Unknown;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> InterpretationIds { get; init; } = [];
    public IReadOnlyList<string> DimensionBindingIds { get; init; } = [];
    public IReadOnlyList<LocatedPoint3> ReferencePoints { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HypothesisProfileDefinition? BaseProfile { get; init; }
    public CutTermination CutTermination { get; init; } = CutTermination.NotApplicable;
    public string TopologyDescription { get; init; } = string.Empty;
    public EvidenceStatus EvidenceStatus { get; init; } = EvidenceStatus.Candidate;
    public FactProvenance Fact { get; init; } = new();
}

public sealed record HypothesisProfileDefinition
{
    public HypothesisProfileKind ProfileKind { get; init; } = HypothesisProfileKind.Unknown;
    public string ContourRole { get; init; } = string.Empty;
    public bool Closed { get; init; }
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
    public FactProvenance Fact { get; init; } = new();
}

public sealed record FeaturePlanDocument : DrawingDocumentBase
{
    public string PlanId { get; init; } = string.Empty;
    public string SelectedHypothesisId { get; init; } = string.Empty;
    public string CanonicalFrameId { get; init; } = string.Empty;
    public string SolidWorksTransformId { get; init; } = string.Empty;
    public IReadOnlyList<FeaturePlanDatumDefinition> DatumDefinitions { get; init; } = [];
    public IReadOnlyList<FeaturePlanOperationRef> Operations { get; init; } = [];
    public IReadOnlyList<string> IntendedFeatureOrder { get; init; } = [];
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public IReadOnlyList<string> Unknowns { get; init; } = [];
    public IReadOnlyList<FeaturePlanExpectedProjection> ExpectedProjections { get; init; } = [];
    public IReadOnlyList<string> ExpectedTopology { get; init; } = [];
    public IReadOnlyList<string> AcceptanceGates { get; init; } = [];
    public IReadOnlyList<AlternativeFeaturePlanSummary> AlternativePlans { get; init; } = [];
    public string SelectionRationale { get; init; } = string.Empty;
    public string ReviewRequest { get; init; } = string.Empty;
    public PlanReviewStatus ReviewStatus { get; init; } = PlanReviewStatus.Pending;
    public string ReviewAuthorizationId { get; init; } = string.Empty;
    public string CapabilitySnapshotHash { get; init; } = string.Empty;
    public DrawingTraceMap TraceIncrement { get; init; } = new();
}

public sealed record FeaturePlanOperationRef
{
    public string OperationId { get; init; } = string.Empty;
    public string FeatureIntent { get; init; } = string.Empty;
    public FeaturePlanOperationKind OperationKind { get; init; } = FeaturePlanOperationKind.Unsupported;
    public string OperationType { get; init; } = string.Empty; // Legacy task-01 field retained for schema compatibility.
    public string Name { get; init; } = string.Empty;
    public FeaturePlanSupportReference Support { get; init; } = new();
    public IReadOnlyList<FeaturePlanPrimitive> SketchEntities { get; init; } = [];
    public IReadOnlyList<string> Contours { get; init; } = [];
    public IReadOnlyList<string> ConstructionGeometry { get; init; } = [];
    public IReadOnlyList<string> Relations { get; init; } = [];
    public IReadOnlyList<FeaturePlanParameter> Dimensions { get; init; } = [];
    public PlanDirection Direction { get; init; } = PlanDirection.NotApplicable;
    public PlanStartCondition StartCondition { get; init; } = PlanStartCondition.NotApplicable;
    public PlanTerminationCondition TerminationCondition { get; init; } = PlanTerminationCondition.NotApplicable;
    public double StartOffsetMm { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public IReadOnlyList<string> HypothesisFeatureIds { get; init; } = [];
    public string CapabilityKey { get; init; } = string.Empty;
    public PlanCapabilityStatus CapabilityStatus { get; init; } = PlanCapabilityStatus.NeedsCheck;
    public IReadOnlyList<FeaturePlanExpectedProjection> ExpectedViewAppearances { get; init; } = [];
    public string FailureImpact { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockingUnknowns { get; init; } = [];
    public IReadOnlyList<string> DeclaredLowerings { get; init; } = [];
    public DocumentStatus Status { get; init; } = DocumentStatus.Candidate;
    public EvidenceStatus EvidenceStatus { get; init; } = EvidenceStatus.Candidate;
    public FactProvenance Fact { get; init; } = new();
}

public sealed record FeaturePlanDatumDefinition
{
    public string DatumId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Reference { get; init; } = string.Empty;
    public FactProvenance Fact { get; init; } = new();
}

public sealed record FeaturePlanSupportReference
{
    public string Plane { get; init; } = string.Empty;
    public string SupportOperationId { get; init; } = string.Empty;
    public LocatedPoint3? PickPoint { get; init; }
}

public sealed record FeaturePlanParameter
{
    public string ParameterId { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public double Value { get; init; }
    public MeasurementUnit Unit { get; init; } = MeasurementUnit.Unitless;
    public bool Critical { get; init; } = true;
    public FactStatus FactStatus { get; init; } = FactStatus.Unknown;
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
    public string Derivation { get; init; } = string.Empty;
}

public sealed record FeaturePlanPrimitive
{
    public string PrimitiveId { get; init; } = string.Empty;
    public FeaturePlanPrimitiveKind Kind { get; init; }
    public string ContourRole { get; init; } = "outer";
    public IReadOnlyList<FeaturePlanParameter> Parameters { get; init; } = [];
    public IReadOnlyList<LocatedPoint2> Points { get; init; } = [];
    public IReadOnlyList<FeaturePlanCurve> Curves { get; init; } = [];
}

public sealed record FeaturePlanCurve
{
    public FeaturePlanCurveKind Kind { get; init; }
    public LocatedPoint2 Start { get; init; } = new();
    public LocatedPoint2 End { get; init; } = new();
    public LocatedPoint2? PointOnArc { get; init; }
}

public sealed record FeaturePlanExpectedProjection
{
    public string OperationId { get; init; } = string.Empty;
    public string ViewId { get; init; } = string.Empty;
    public string Appearance { get; init; } = string.Empty;
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
}

public sealed record AlternativeFeaturePlanSummary
{
    public string CandidateId { get; init; } = string.Empty;
    public DocumentStatus Status { get; init; } = DocumentStatus.Candidate;
    public string TopologySignature { get; init; } = string.Empty;
    public string RejectionReason { get; init; } = string.Empty;
    public IReadOnlyList<string> MissingCapabilities { get; init; } = [];
}

public sealed record DrawingTraceMap : DrawingDocumentBase
{
    public IReadOnlyList<TraceNode> Nodes { get; init; } = [];
    public IReadOnlyList<TraceEdge> Edges { get; init; } = [];
}

public sealed record TraceNode
{
    public string NodeId { get; init; } = string.Empty;
    public TraceStage Stage { get; init; }
    public string DocumentId { get; init; } = string.Empty;
    public string EntityKind { get; init; } = string.Empty;
    public string ExternalReference { get; init; } = string.Empty;
    public EvidenceStatus Status { get; init; } = EvidenceStatus.Candidate;
}

public sealed record TraceEdge
{
    public string EdgeId { get; init; } = string.Empty;
    public TraceEdgeType EdgeType { get; init; }
    public string FromId { get; init; } = string.Empty;
    public string ToId { get; init; } = string.Empty;
    public ProducerStamp Producer { get; init; } = new();
    public EvidenceStatus Status { get; init; } = EvidenceStatus.Candidate;
    public string Rationale { get; init; } = string.Empty;
}

public sealed record DrawingContractPackage
{
    public DrawingSourceManifest SourceManifest { get; init; } = new();
    public DrawingObservationDocument Observation { get; init; } = new();
    public DrawingInterpretationDocument Interpretation { get; init; } = new();
    public DrawingHypothesisDocument Hypothesis { get; init; } = new();
    public FeaturePlanDocument FeaturePlan { get; init; } = new();
    public DrawingTraceMap TraceMap { get; init; } = new();
}
