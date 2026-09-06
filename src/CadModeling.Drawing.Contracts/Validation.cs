using System.Text.RegularExpressions;

namespace CadModeling.Drawing.Contracts;

public sealed record ContractValidationReport(IReadOnlyList<ContractDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(item => item.Severity != ContractDiagnosticSeverity.Error);
}

public sealed class DrawingContractValidator
{
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    public ContractValidationReport Validate(DrawingContractPackage package)
    {
        var diagnostics = new List<ContractDiagnostic>();
        ValidateDocument(package.SourceManifest, diagnostics);
        ValidateDocument(package.Observation, diagnostics);
        ValidateDocument(package.Interpretation, diagnostics);
        ValidateDocument(package.Hypothesis, diagnostics);
        ValidateDocument(package.FeaturePlan, diagnostics);
        ValidateDocument(package.TraceMap, diagnostics);

        var sourceHash = package.SourceManifest.SourceSha256;
        foreach (var document in EnumerateDocuments(package).Skip(1))
        {
            AddIf(document.SourceSha256 != sourceHash, diagnostics, document, "DRW008",
                "source_sha256 must match the source manifest.", "source_sha256", true);
        }

        ValidateSourceManifest(package.SourceManifest, diagnostics);
        ValidateObservation(package.Observation, diagnostics);
        ValidateInterpretation(package.Observation, package.Interpretation, diagnostics);
        ValidateHypothesis(package.Interpretation, package.Hypothesis, diagnostics);
        ValidateFeaturePlan(package.Hypothesis, package.FeaturePlan, diagnostics);
        diagnostics.AddRange(new TraceMapValidator().Validate(package.TraceMap).Diagnostics);
        return new(diagnostics);
    }

    public ContractValidationReport ValidateDocument(DrawingDocumentBase document)
    {
        var diagnostics = new List<ContractDiagnostic>();
        ValidateDocument(document, diagnostics);
        switch (document)
        {
            case DrawingSourceManifest manifest:
                ValidateSourceManifest(manifest, diagnostics);
                break;
            case DrawingObservationDocument observation:
                ValidateObservation(observation, diagnostics);
                break;
            case DrawingInterpretationDocument interpretation:
                ValidateInterpretation(new DrawingObservationDocument(), interpretation, diagnostics);
                break;
            case DrawingHypothesisDocument hypothesis:
                ValidateHypothesis(new DrawingInterpretationDocument(), hypothesis, diagnostics);
                break;
            case FeaturePlanDocument plan:
                ValidateFeaturePlan(new DrawingHypothesisDocument(), plan, diagnostics);
                break;
            case DrawingTraceMap traceMap:
                diagnostics.AddRange(new TraceMapValidator().Validate(traceMap).Diagnostics);
                break;
        }
        return new(diagnostics);
    }

    public ContractValidationReport ValidateInterpretationPair(
        DrawingObservationDocument observation,
        DrawingInterpretationDocument interpretation)
    {
        var diagnostics = new List<ContractDiagnostic>();
        ValidateDocument(observation, diagnostics);
        ValidateObservation(observation, diagnostics);
        ValidateDocument(interpretation, diagnostics);
        AddIf(observation.SourceSha256 != interpretation.SourceSha256, diagnostics, interpretation, "DRW008",
            "source_sha256 must match the observation document.", "source_sha256", true);
        ValidateInterpretation(observation, interpretation, diagnostics);
        return new(diagnostics);
    }

    public ContractValidationReport ValidateHypothesisPair(
        DrawingInterpretationDocument interpretation,
        DrawingHypothesisDocument hypothesis)
    {
        var diagnostics = new List<ContractDiagnostic>();
        ValidateDocument(interpretation, diagnostics);
        ValidateDocument(hypothesis, diagnostics);
        AddIf(interpretation.SourceSha256 != hypothesis.SourceSha256, diagnostics, hypothesis, "DRW008",
            "source_sha256 must match the interpretation document.", "source_sha256", true);
        ValidateHypothesis(interpretation, hypothesis, diagnostics);
        return new(diagnostics);
    }

    private static IEnumerable<DrawingDocumentBase> EnumerateDocuments(DrawingContractPackage package)
    {
        yield return package.SourceManifest;
        yield return package.Observation;
        yield return package.Interpretation;
        yield return package.Hypothesis;
        yield return package.FeaturePlan;
        yield return package.TraceMap;
    }

    private static void ValidateDocument(DrawingDocumentBase document, List<ContractDiagnostic> diagnostics)
    {
        AddIf(!DrawingContractSchema.SupportedVersions.Contains(document.SchemaVersion), diagnostics, document,
            "DRW001", $"Unsupported schema_version '{document.SchemaVersion}'.", "schema_version", true);
        AddIf(string.IsNullOrWhiteSpace(document.DocumentId), diagnostics, document,
            "DRW002", "document_id is required.", "document_id", true);
        AddIf(!Sha256Pattern.IsMatch(document.SourceSha256), diagnostics, document,
            "DRW003", "source_sha256 must be a 64-character hexadecimal SHA-256.", "source_sha256", true);
        AddIf(string.IsNullOrWhiteSpace(document.ProducerName), diagnostics, document,
            "DRW004", "producer_name is required.", "producer_name", true);
        AddIf(string.IsNullOrWhiteSpace(document.ProducerVersion), diagnostics, document,
            "DRW005", "producer_version is required.", "producer_version", true);
        AddIf(!Sha256Pattern.IsMatch(document.ConfigurationHash), diagnostics, document,
            "DRW006", "configuration_hash must be a 64-character hexadecimal SHA-256.", "configuration_hash", true);
        AddIf(document.CreatedAt == default, diagnostics, document,
            "DRW007", "created_at is required.", "created_at", true);

        EnsureUnique(document.ArtifactManifest.Select(item => item.ArtifactId), diagnostics, document,
            "DRW010", "artifact_manifest", "artifact_id");
        EnsureUnique(document.CoordinateFrames.Select(item => item.FrameId), diagnostics, document,
            "DRW011", "coordinate_frames", "frame_id");
        EnsureUnique(document.Transforms.Select(item => item.TransformId), diagnostics, document,
            "DRW012", "transforms", "transform_id");
        EnsureUnique(document.UnresolvedItems.Select(item => item.Id), diagnostics, document,
            "DRW013", "unresolved_items", "id");

        var artifactIds = document.ArtifactManifest.Select(item => item.ArtifactId).ToHashSet(StringComparer.Ordinal);
        var frameIds = document.CoordinateFrames.Select(item => item.FrameId).ToHashSet(StringComparer.Ordinal);
        foreach (var (artifact, index) in document.ArtifactManifest.Select((value, index) => (value, index)))
        {
            AddIf(string.IsNullOrWhiteSpace(artifact.Uri), diagnostics, document, "ART001",
                "Artifact uri is required.", $"artifact_manifest[{index}].uri", true, artifact.ArtifactId);
            AddIf(string.IsNullOrWhiteSpace(artifact.MediaType), diagnostics, document, "ART002",
                "Artifact media_type is required.", $"artifact_manifest[{index}].media_type", true, artifact.ArtifactId);
            AddIf(!Sha256Pattern.IsMatch(artifact.Sha256), diagnostics, document, "ART003",
                "Artifact sha256 must be a 64-character hexadecimal SHA-256.", $"artifact_manifest[{index}].sha256", true, artifact.ArtifactId);
        }
        foreach (var (frame, index) in document.CoordinateFrames.Select((value, index) => (value, index)))
        {
            AddIf(string.IsNullOrWhiteSpace(frame.FrameId), diagnostics, document, "CRD001",
                "Coordinate frame id is required.", $"coordinate_frames[{index}].frame_id", true);
            AddIf(string.IsNullOrWhiteSpace(frame.ArtifactId) || !artifactIds.Contains(frame.ArtifactId), diagnostics, document,
                "CRD002", "Coordinate frame must reference an artifact in artifact_manifest.",
                $"coordinate_frames[{index}].artifact_id", true, frame.ArtifactId);
            AddIf(!UnitMatchesSpace(frame.Space, frame.Unit), diagnostics, document, "CRD003",
                $"Unit '{frame.Unit}' is not valid for coordinate space '{frame.Space}'.",
                $"coordinate_frames[{index}].unit", true, frame.FrameId);
        }

        foreach (var (transform, index) in document.Transforms.Select((value, index) => (value, index)))
        {
            var path = $"transforms[{index}]";
            AddIf(!frameIds.Contains(transform.FromFrameId), diagnostics, document, "TRN001",
                "Transform from_frame_id does not exist.", $"{path}.from_frame_id", true, transform.FromFrameId);
            AddIf(!frameIds.Contains(transform.ToFrameId), diagnostics, document, "TRN002",
                "Transform to_frame_id does not exist.", $"{path}.to_frame_id", true, transform.ToFrameId);
            AddIf(!IsSquareMatrix(transform.ForwardMatrix), diagnostics, document, "TRN003",
                "forward_matrix must be a finite square matrix.", $"{path}.forward_matrix", true, transform.TransformId);
            AddIf(!IsSquareMatrix(transform.InverseMatrix), diagnostics, document, "TRN004",
                "inverse_matrix must be a finite square matrix.", $"{path}.inverse_matrix", true, transform.TransformId);
            AddIf(transform.ForwardMatrix.Count != transform.InverseMatrix.Count, diagnostics, document, "TRN005",
                "Forward and inverse matrices must have the same dimensions.", path, true, transform.TransformId);
            AddIf(string.IsNullOrWhiteSpace(transform.Source), diagnostics, document, "TRN006",
                "Transform source is required.", $"{path}.source", true, transform.TransformId);
            AddIf(!artifactIds.Contains(transform.BeforeArtifactId), diagnostics, document, "TRN007",
                "before_artifact_id does not exist.", $"{path}.before_artifact_id", true, transform.BeforeArtifactId);
            AddIf(!artifactIds.Contains(transform.AfterArtifactId), diagnostics, document, "TRN008",
                "after_artifact_id does not exist.", $"{path}.after_artifact_id", true, transform.AfterArtifactId);
            if (IsSquareMatrix(transform.ForwardMatrix) && IsSquareMatrix(transform.InverseMatrix) &&
                transform.ForwardMatrix.Count == transform.InverseMatrix.Count)
            {
                AddIf(!AreInverse(transform.ForwardMatrix, transform.InverseMatrix, 1e-8), diagnostics, document,
                    "TRN009", "forward_matrix and inverse_matrix do not compose to identity.", path, true, transform.TransformId);
            }
        }

        foreach (var (item, index) in document.UnresolvedItems.Select((value, index) => (value, index)))
        {
            var path = $"unresolved_items[{index}]";
            AddIf(string.IsNullOrWhiteSpace(item.Category), diagnostics, document, "UNR001", "category is required.", $"{path}.category", true, item.Id);
            AddIf(string.IsNullOrWhiteSpace(item.Reason), diagnostics, document, "UNR002", "reason is required.", $"{path}.reason", true, item.Id);
            AddIf(string.IsNullOrWhiteSpace(item.MinimumQuestion), diagnostics, document, "UNR003", "minimum_question is required.", $"{path}.minimum_question", true, item.Id);
            AddIf(string.IsNullOrWhiteSpace(item.SuggestedEvidence), diagnostics, document, "UNR004", "suggested_evidence is required.", $"{path}.suggested_evidence", true, item.Id);
        }
    }

    private static void ValidateSourceManifest(DrawingSourceManifest manifest, List<ContractDiagnostic> diagnostics)
    {
        AddIf(string.IsNullOrWhiteSpace(manifest.SourcePath), diagnostics, manifest, "SRC001", "source_path is required.", "source_path", true);
        AddIf(string.IsNullOrWhiteSpace(manifest.FileName), diagnostics, manifest, "SRC002", "file_name is required.", "file_name", true);
        AddIf(string.IsNullOrWhiteSpace(manifest.MediaType), diagnostics, manifest, "SRC003", "media_type is required.", "media_type", true);
        AddIf(manifest.Pages.Count == 0, diagnostics, manifest, "SRC004", "At least one page is required.", "pages", true);
        EnsureUnique(manifest.Pages.Select(item => item.PageId), diagnostics, manifest, "SRC005", "pages", "page_id");
        EnsureUnique(manifest.Pages.Select(item => item.PageNumber.ToString()), diagnostics, manifest, "SRC006", "pages", "page_number");
        foreach (var (page, index) in manifest.Pages.Select((value, index) => (value, index)))
        {
            var path = $"pages[{index}]";
            AddIf(!Sha256Pattern.IsMatch(page.Sha256), diagnostics, manifest, "SRC007", "Page sha256 is invalid.", $"{path}.sha256", true, page.PageId);
            AddIf(page.PageNumber < 1, diagnostics, manifest, "SRC008", "page_number must be positive.", $"{path}.page_number", true, page.PageId);
            AddIf(page.PixelWidth <= 0 || page.PixelHeight <= 0, diagnostics, manifest, "SRC009", "Pixel dimensions must be positive.", path, true, page.PageId);
            AddIf(page.PhysicalWidth <= 0 || page.PhysicalHeight <= 0, diagnostics, manifest, "SRC010", "Physical page dimensions must be positive.", path, true, page.PageId);
            AddIf(page.PhysicalUnit is not (MeasurementUnit.Millimeter or MeasurementUnit.Inch), diagnostics, manifest, "SRC011", "Physical page unit must be millimeter or inch.", $"{path}.physical_unit", true, page.PageId);
            AddIf(page.Dpi <= 0, diagnostics, manifest, "SRC012", "dpi must be positive.", $"{path}.dpi", true, page.PageId);
        }
    }

    private static void ValidateObservation(DrawingObservationDocument document, List<ContractDiagnostic> diagnostics)
    {
        EnsureUnique(document.SourceRegions.Select(item => item.RegionId), diagnostics, document, "OBS001", "source_regions", "region_id");
        EnsureUnique(document.ViewRegions.Select(item => item.ViewRegionId), diagnostics, document, "OBS002", "view_regions", "view_region_id");
        EnsureUnique(document.Observations.Select(item => item.ObservationId), diagnostics, document, "OBS003", "observations", "observation_id");
        EnsureUnique(document.DimensionObservations.Select(item => item.DimensionObservationId), diagnostics, document, "OBS004", "dimension_observations", "dimension_observation_id");
        var regions = document.SourceRegions.Select(item => item.RegionId).ToHashSet(StringComparer.Ordinal);
        var frames = document.CoordinateFrames.ToDictionary(item => item.FrameId, StringComparer.Ordinal);
        foreach (var (region, index) in document.SourceRegions.Select((value, index) => (value, index)))
        {
            AddIf(region.Polygon.Count < 3, diagnostics, document, "OBS005", "Source region polygon requires at least three points.", $"source_regions[{index}].polygon", true, region.RegionId);
            ValidatePoints(region.Polygon, frames, document, diagnostics, $"source_regions[{index}].polygon", region.RegionId,
                allowedSpaces: CoordinateSpaces(CoordinateSpace.SourcePixel, CoordinateSpace.NormalizedPixel, CoordinateSpace.SheetSpace));
        }
        foreach (var (item, index) in document.Observations.Select((value, index) => (value, index)))
        {
            AddIf(!regions.Contains(item.SourceRegionId), diagnostics, document, "OBS006", "Observation source_region_id does not exist.", $"observations[{index}].source_region_id", true, item.ObservationId);
            ValidatePoints(item.Geometry, frames, document, diagnostics, $"observations[{index}].geometry", item.ObservationId,
                allowedSpaces: CoordinateSpaces(CoordinateSpace.SourcePixel, CoordinateSpace.NormalizedPixel, CoordinateSpace.SheetSpace));
            AddIf(item.Confidence is < 0 or > 1, diagnostics, document, "OBS007", "confidence must be in [0, 1].", $"observations[{index}].confidence", true, item.ObservationId);
        }
        foreach (var (item, index) in document.DimensionObservations.Select((value, index) => (value, index)))
        {
            AddIf(!regions.Contains(item.SourceRegionId), diagnostics, document, "OBS008", "Dimension observation source_region_id does not exist.", $"dimension_observations[{index}].source_region_id", true, item.DimensionObservationId);
            AddIf(string.IsNullOrWhiteSpace(item.RawLiteral), diagnostics, document, "OBS009", "OCR dimension candidate must retain raw_literal.", $"dimension_observations[{index}].raw_literal", true, item.DimensionObservationId);
            AddIf(item.Confidence is < 0 or > 1, diagnostics, document, "OBS010", "confidence must be in [0, 1].", $"dimension_observations[{index}].confidence", true, item.DimensionObservationId);
        }
    }

    private static void ValidateInterpretation(
        DrawingObservationDocument observation,
        DrawingInterpretationDocument document,
        List<ContractDiagnostic> diagnostics)
    {
        EnsureUnique(document.Views.Select(item => item.ViewId), diagnostics, document, "INT001", "views", "view_id");
        EnsureUnique(document.LineInterpretations.Select(item => item.InterpretationId), diagnostics, document, "INT002", "line_interpretations", "interpretation_id");
        EnsureUnique(document.Features.Select(item => item.FeatureId), diagnostics, document, "INT003", "features", "feature_id");
        EnsureUnique(document.DimensionBindings.Select(item => item.BindingId), diagnostics, document, "INT004", "dimension_bindings", "binding_id");
        if (document.ProjectionConvention == ProjectionConvention.Unknown)
            RequirePending(document, diagnostics, "INT005", "Unknown projection convention requires pending_clarification.", "projection_convention");
        if (document.DrawingUnit == MeasurementUnit.Unitless)
            RequirePending(document, diagnostics, "INT006", "Unknown drawing unit requires pending_clarification.", "drawing_unit");
        ValidateFact(document.ProjectionFact, document, diagnostics, "projection_fact", "projection_convention");
        ValidateFact(document.UnitFact, document, diagnostics, "unit_fact", "drawing_unit");

        var viewRegions = observation.ViewRegions.Select(item => item.ViewRegionId).ToHashSet(StringComparer.Ordinal);
        var observations = observation.Observations.Select(item => item.ObservationId).ToHashSet(StringComparer.Ordinal);
        var dimensionObservations = observation.DimensionObservations.ToDictionary(item => item.DimensionObservationId, StringComparer.Ordinal);
        var regions = observation.SourceRegions.Select(item => item.RegionId).ToHashSet(StringComparer.Ordinal);
        var features = document.Features.Select(item => item.FeatureId).ToHashSet(StringComparer.Ordinal);
        var frames = document.CoordinateFrames.Select(item => item.FrameId).ToHashSet(StringComparer.Ordinal);

        foreach (var (view, index) in document.Views.Select((value, index) => (value, index)))
        {
            AddIf(!viewRegions.Contains(view.ViewRegionObservationId), diagnostics, document, "INT007", "View does not reference an observed view region.", $"views[{index}].view_region_observation_id", true, view.ViewId);
            AddIf(!frames.Contains(view.CoordinateFrameId), diagnostics, document, "INT008", "View coordinate_frame_id does not exist in this document.", $"views[{index}].coordinate_frame_id", true, view.ViewId);
            if (view.ViewType == DrawingViewType.Unknown)
                RequirePending(document, diagnostics, "INT009", "Unknown view type requires pending_clarification.", $"views[{index}].view_type", view.ViewId);
            ValidateFact(view.Fact, document, diagnostics, $"views[{index}].fact", view.ViewId);
        }
        foreach (var (line, index) in document.LineInterpretations.Select((value, index) => (value, index)))
        {
            AddIf(!observations.Contains(line.ObservationId), diagnostics, document, "INT010", "Line interpretation does not reference an observation.", $"line_interpretations[{index}].observation_id", true, line.InterpretationId);
            ValidateFact(line.Fact, document, diagnostics, $"line_interpretations[{index}].fact", line.InterpretationId);
        }
        foreach (var (feature, index) in document.Features.Select((value, index) => (value, index)))
        {
            AddIf(feature.ObservationIds.Any(id => !observations.Contains(id)), diagnostics, document, "INT011", "Feature references a missing observation.", $"features[{index}].observation_ids", true, feature.FeatureId);
            ValidateFact(feature.Fact, document, diagnostics, $"features[{index}].fact", feature.FeatureId);
        }
        foreach (var (binding, index) in document.DimensionBindings.Select((value, index) => (value, index)))
        {
            var path = $"dimension_bindings[{index}]";
            var found = dimensionObservations.TryGetValue(binding.DimensionObservationId, out var observed);
            AddIf(!found, diagnostics, document, "DIM001", "Dimension binding must reference a dimension observation.", $"{path}.dimension_observation_id", true, binding.BindingId, binding.DimensionObservationId);
            AddIf(!regions.Contains(binding.SourceRegionId), diagnostics, document, "DIM002", "Dimension binding must reference a source region.", $"{path}.source_region_id", true, binding.BindingId);
            AddIf(string.IsNullOrWhiteSpace(binding.RawLiteral), diagnostics, document, "DIM003", "Bound dimension must retain the original literal.", $"{path}.raw_literal", true, binding.BindingId);
            AddIf(binding.Value.Unit == MeasurementUnit.Unitless, diagnostics, document, "DIM004", "Bound dimension requires an explicit unit.", $"{path}.value.unit", true, binding.BindingId);
            AddIf(string.IsNullOrWhiteSpace(binding.Symbol), diagnostics, document, "DIM005", "Bound dimension requires an explicit symbol, using 'linear' when no glyph is present.", $"{path}.symbol", true, binding.BindingId);
            AddIf(binding.Role == DimensionRole.Driving && binding.TargetFeatureIds.Count == 0, diagnostics, document,
                "DIM006", "Driving dimension must bind to at least one interpreted feature.", $"{path}.target_feature_ids", true, binding.BindingId);
            AddIf(binding.Role == DimensionRole.Driving && binding.EvidenceIds.Count == 0, diagnostics, document,
                "DIM007", "Driving dimension must include evidence_ids.", $"{path}.evidence_ids", true, binding.BindingId);
            AddIf(binding.TargetFeatureIds.Any(id => !features.Contains(id)), diagnostics, document, "DIM008", "Dimension binding targets a missing feature.", $"{path}.target_feature_ids", true, binding.BindingId);
            if (found && observed is not null)
            {
                AddIf(binding.SourceRegionId != observed.SourceRegionId, diagnostics, document, "DIM009", "Binding source region differs from its observation.", $"{path}.source_region_id", true, binding.BindingId, observed.DimensionObservationId);
                AddIf(binding.RawLiteral != observed.RawLiteral, diagnostics, document, "DIM010", "Binding raw literal differs from its observation.", $"{path}.raw_literal", true, binding.BindingId, observed.DimensionObservationId);
                AddIf(observed.CandidateUnit.HasValue && binding.Value.Unit != observed.CandidateUnit, diagnostics, document, "DIM011", "Binding unit conflicts with the observed candidate unit.", $"{path}.value.unit", true, binding.BindingId, observed.DimensionObservationId);
            }
            ValidateFact(binding.Value.Fact, document, diagnostics, $"{path}.value.fact", binding.BindingId);
        }
    }

    private static void ValidateHypothesis(
        DrawingInterpretationDocument interpretation,
        DrawingHypothesisDocument document,
        List<ContractDiagnostic> diagnostics)
    {
        EnsureUnique(document.Hypotheses.Select(item => item.HypothesisId), diagnostics, document, "HYP001", "hypotheses", "hypothesis_id");
        var featureIds = new List<string>();
        var interpretationIds = interpretation.LineInterpretations.Select(item => item.InterpretationId)
            .Concat(interpretation.Features.Select(item => item.FeatureId)).ToHashSet(StringComparer.Ordinal);
        var bindingIds = interpretation.DimensionBindings.Select(item => item.BindingId).ToHashSet(StringComparer.Ordinal);
        var frames = document.CoordinateFrames.ToDictionary(item => item.FrameId, StringComparer.Ordinal);
        foreach (var (hypothesis, hypothesisIndex) in document.Hypotheses.Select((value, index) => (value, index)))
        {
            AddIf(hypothesis.Confidence is < 0 or > 1, diagnostics, document, "HYP002", "confidence must be in [0, 1].", $"hypotheses[{hypothesisIndex}].confidence", true, hypothesis.HypothesisId);
            foreach (var (feature, featureIndex) in hypothesis.Features.Select((value, index) => (value, index)))
            {
                featureIds.Add(feature.HypothesisFeatureId);
                var path = $"hypotheses[{hypothesisIndex}].features[{featureIndex}]";
                AddIf(feature.InterpretationIds.Any(id => !interpretationIds.Contains(id)), diagnostics, document, "HYP003", "Hypothesis feature references a missing interpretation.", $"{path}.interpretation_ids", true, feature.HypothesisFeatureId);
                AddIf(feature.DimensionBindingIds.Any(id => !bindingIds.Contains(id)), diagnostics, document, "HYP004", "Hypothesis feature references a missing dimension binding.", $"{path}.dimension_binding_ids", true, feature.HypothesisFeatureId);
                ValidatePoints((IEnumerable<LocatedPoint3>)feature.ReferencePoints, frames, document, diagnostics, $"{path}.reference_points", feature.HypothesisFeatureId,
                    allowedSpaces: CoordinateSpaces(CoordinateSpace.ObjectXyz));
                if (feature.CutTermination == CutTermination.PendingClarification || feature.Kind == HypothesisFeatureKind.Unknown)
                    RequirePending(document, diagnostics, "HYP005", "Critical topology uncertainty requires pending_clarification.", path, feature.HypothesisFeatureId);
                ValidateFact(feature.Fact, document, diagnostics, $"{path}.fact", feature.HypothesisFeatureId);
            }
        }
        EnsureUnique(featureIds, diagnostics, document, "HYP006", "hypotheses[].features", "hypothesis_feature_id");
    }

    private static void ValidateFeaturePlan(
        DrawingHypothesisDocument hypothesis,
        FeaturePlanDocument document,
        List<ContractDiagnostic> diagnostics)
    {
        EnsureUnique(document.Operations.Select(item => item.OperationId), diagnostics, document, "PLN001", "operations", "operation_id");
        AddIf(document.Operations.Count > 0 && document.AcceptanceGates.Count == 0, diagnostics, document,
            "PLN005", "A FeaturePlan with operations requires at least one acceptance gate.", "acceptance_gates", true,
            string.IsNullOrWhiteSpace(document.PlanId) ? document.DocumentId : document.PlanId);
        var hypothesisIds = hypothesis.Hypotheses.Select(item => item.HypothesisId).ToHashSet(StringComparer.Ordinal);
        var featureIds = hypothesis.Hypotheses.SelectMany(item => item.Features).Select(item => item.HypothesisFeatureId).ToHashSet(StringComparer.Ordinal);
        AddIf(!string.IsNullOrWhiteSpace(document.SelectedHypothesisId) && !hypothesisIds.Contains(document.SelectedHypothesisId), diagnostics, document,
            "PLN002", "selected_hypothesis_id does not exist.", "selected_hypothesis_id", true, document.SelectedHypothesisId);
        var previous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (operation, index) in document.Operations.Select((value, index) => (value, index)))
        {
            var path = $"operations[{index}]";
            AddIf(operation.DependsOn.Any(id => !previous.Contains(id)), diagnostics, document, "PLN003", "Operation dependencies must refer to earlier operations.", $"{path}.depends_on", true, operation.OperationId);
            AddIf(operation.HypothesisFeatureIds.Any(id => !featureIds.Contains(id)), diagnostics, document, "PLN004", "Operation references a missing hypothesis feature.", $"{path}.hypothesis_feature_ids", true, operation.OperationId);
            ValidateFact(operation.Fact, document, diagnostics, $"{path}.fact", operation.OperationId);
            previous.Add(operation.OperationId);
        }
    }

    private static void ValidateFact(FactProvenance fact, DrawingDocumentBase document, List<ContractDiagnostic> diagnostics, string path, string evidenceId)
    {
        AddIf(fact.Status == FactStatus.Stated && fact.SourceIds.Count == 0, diagnostics, document, "FCT001", "A stated fact requires source_ids.", $"{path}.source_ids", true, evidenceId);
        AddIf(fact.Status == FactStatus.Derived && (fact.SourceIds.Count == 0 || string.IsNullOrWhiteSpace(fact.Rationale)), diagnostics, document,
            "FCT002", "A derived fact requires source_ids and a derivation rationale.", path, true, evidenceId);
        AddIf(fact.Status == FactStatus.Assumed && string.IsNullOrWhiteSpace(fact.AssumptionAuthorizationId), diagnostics, document,
            "FCT003", "An assumed fact requires an explicit assumption_authorization_id; unknown cannot be promoted automatically.", $"{path}.assumption_authorization_id", true, evidenceId);
        AddIf(fact.PreviousStatus == FactStatus.Unknown && fact.Status == FactStatus.Assumed && string.IsNullOrWhiteSpace(fact.AssumptionAuthorizationId), diagnostics, document,
            "FCT004", "Unknown was upgraded to assumed without explicit authorization.", path, true, evidenceId);
        AddIf(fact.Status == FactStatus.Unknown && fact.SourceIds.Count > 0, diagnostics, document,
            "FCT005", "Unknown facts may not claim confirming source ids.", $"{path}.source_ids", true, evidenceId);
    }

    private static void ValidatePoints(
        IEnumerable<LocatedPoint2> points,
        IReadOnlyDictionary<string, CoordinateFrame> frames,
        DrawingDocumentBase document,
        List<ContractDiagnostic> diagnostics,
        string path,
        string evidenceId,
        IReadOnlySet<CoordinateSpace> allowedSpaces)
    {
        foreach (var (point, index) in points.Select((value, index) => (value, index)))
        {
            var found = frames.TryGetValue(point.CoordinateFrameId, out var frame);
            AddIf(string.IsNullOrWhiteSpace(point.CoordinateFrameId), diagnostics, document, "CRD010", "Naked coordinate is forbidden; coordinate_frame_id is required.", $"{path}[{index}].coordinate_frame_id", true, evidenceId);
            AddIf(!found, diagnostics, document, "CRD011", "Coordinate frame does not exist.", $"{path}[{index}].coordinate_frame_id", true, evidenceId);
            AddIf(found && frame is not null && !allowedSpaces.Contains(frame.Space), diagnostics, document, "CRD012", "Coordinate frame has an invalid space for this field.", $"{path}[{index}].coordinate_frame_id", true, evidenceId);
            AddIf(!double.IsFinite(point.X) || !double.IsFinite(point.Y), diagnostics, document, "CRD013", "Coordinates must be finite.", $"{path}[{index}]", true, evidenceId);
        }
    }

    private static void ValidatePoints(
        IEnumerable<LocatedPoint3> points,
        IReadOnlyDictionary<string, CoordinateFrame> frames,
        DrawingDocumentBase document,
        List<ContractDiagnostic> diagnostics,
        string path,
        string evidenceId,
        IReadOnlySet<CoordinateSpace> allowedSpaces)
    {
        foreach (var (point, index) in points.Select((value, index) => (value, index)))
        {
            var found = frames.TryGetValue(point.CoordinateFrameId, out var frame);
            AddIf(string.IsNullOrWhiteSpace(point.CoordinateFrameId), diagnostics, document, "CRD010", "Naked coordinate is forbidden; coordinate_frame_id is required.", $"{path}[{index}].coordinate_frame_id", true, evidenceId);
            AddIf(!found, diagnostics, document, "CRD011", "Coordinate frame does not exist.", $"{path}[{index}].coordinate_frame_id", true, evidenceId);
            AddIf(found && frame is not null && !allowedSpaces.Contains(frame.Space), diagnostics, document, "CRD012", "Coordinate frame has an invalid space for this field.", $"{path}[{index}].coordinate_frame_id", true, evidenceId);
            AddIf(!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z), diagnostics, document, "CRD013", "Coordinates must be finite.", $"{path}[{index}]", true, evidenceId);
        }
    }

    private static bool UnitMatchesSpace(CoordinateSpace space, MeasurementUnit unit) => space switch
    {
        CoordinateSpace.SourcePixel or CoordinateSpace.NormalizedPixel => unit == MeasurementUnit.Pixel,
        CoordinateSpace.SheetSpace => unit is MeasurementUnit.PdfPoint or MeasurementUnit.Millimeter or MeasurementUnit.Inch,
        CoordinateSpace.ViewLocal or CoordinateSpace.ObjectXyz => unit is MeasurementUnit.Millimeter or MeasurementUnit.Inch,
        CoordinateSpace.SolidworksModel => unit == MeasurementUnit.Meter,
        _ => false
    };

    private static IReadOnlySet<CoordinateSpace> CoordinateSpaces(params CoordinateSpace[] spaces) =>
        new HashSet<CoordinateSpace>(spaces);

    private static bool IsSquareMatrix(IReadOnlyList<IReadOnlyList<double>> matrix) =>
        matrix.Count is > 1 and <= 4 && matrix.All(row => row.Count == matrix.Count && row.All(double.IsFinite));

    private static bool AreInverse(IReadOnlyList<IReadOnlyList<double>> left, IReadOnlyList<IReadOnlyList<double>> right, double tolerance)
    {
        for (var row = 0; row < left.Count; row++)
        for (var column = 0; column < left.Count; column++)
        {
            var sum = 0d;
            for (var index = 0; index < left.Count; index++)
                sum += left[row][index] * right[index][column];
            var expected = row == column ? 1d : 0d;
            if (Math.Abs(sum - expected) > tolerance) return false;
        }
        return true;
    }

    private static void RequirePending(DrawingDocumentBase document, List<ContractDiagnostic> diagnostics, string code, string message, string path, string? evidenceId = null) =>
        AddIf(document.Status != DocumentStatus.PendingClarification, diagnostics, document, code, message, path, true, evidenceId);

    private static void EnsureUnique(
        IEnumerable<string> ids,
        List<ContractDiagnostic> diagnostics,
        DrawingDocumentBase document,
        string code,
        string path,
        string field)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var id in ids)
        {
            AddIf(string.IsNullOrWhiteSpace(id), diagnostics, document, code, $"{field} is required.", $"{path}[{index}].{field}", true);
            AddIf(!string.IsNullOrWhiteSpace(id) && !seen.Add(id), diagnostics, document, code, $"Duplicate {field} '{id}'.", $"{path}[{index}].{field}", true, id);
            index++;
        }
    }

    internal static void AddIf(
        bool condition,
        List<ContractDiagnostic> diagnostics,
        DrawingDocumentBase document,
        string code,
        string message,
        string path,
        bool blocking,
        string? evidenceId = null,
        params string[] affectedIds)
    {
        if (!condition) return;
        diagnostics.Add(new ContractDiagnostic
        {
            Id = $"{document.DocumentId}:{code}:{diagnostics.Count + 1}",
            Code = code,
            Severity = ContractDiagnosticSeverity.Error,
            Blocking = blocking,
            Message = message,
            DocumentId = document.DocumentId,
            FieldPath = path,
            EvidenceId = evidenceId,
            AffectedIds = affectedIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray()
        });
    }
}

public sealed class TraceMapValidator
{
    private static readonly IReadOnlyDictionary<TraceEdgeType, (IReadOnlySet<TraceStage> From, IReadOnlySet<TraceStage> To)> Allowed =
        new Dictionary<TraceEdgeType, (IReadOnlySet<TraceStage>, IReadOnlySet<TraceStage>)>
        {
            [TraceEdgeType.SourceRegionToObservation] = (Set(TraceStage.SourceRegion), Set(TraceStage.Observation, TraceStage.DimensionObservation)),
            [TraceEdgeType.ObservationToInterpretation] = (Set(TraceStage.Observation), Set(TraceStage.Interpretation)),
            [TraceEdgeType.DimensionObservationToDimensionBinding] = (Set(TraceStage.DimensionObservation), Set(TraceStage.DimensionBinding)),
            [TraceEdgeType.InterpretationToHypothesisFeature] = (Set(TraceStage.Interpretation, TraceStage.DimensionBinding), Set(TraceStage.HypothesisFeature)),
            [TraceEdgeType.HypothesisFeatureToFeaturePlanOperation] = (Set(TraceStage.HypothesisFeature), Set(TraceStage.FeaturePlanOperation)),
            [TraceEdgeType.FeaturePlanOperationToGenericDraftOperation] = (Set(TraceStage.FeaturePlanOperation), Set(TraceStage.GenericDraftOperation)),
            [TraceEdgeType.GenericDraftOperationToModelingIrOperation] = (Set(TraceStage.GenericDraftOperation), Set(TraceStage.ModelingIrOperation)),
            [TraceEdgeType.ModelingIrOperationToSolidworksFeature] = (Set(TraceStage.ModelingIrOperation), Set(TraceStage.SolidworksFeature)),
            [TraceEdgeType.SourceOrModelEntityToReprojectionDifference] = (Set(TraceStage.SourceRegion, TraceStage.Observation, TraceStage.DimensionObservation, TraceStage.Interpretation, TraceStage.DimensionBinding, TraceStage.HypothesisFeature, TraceStage.FeaturePlanOperation, TraceStage.GenericDraftOperation, TraceStage.ModelingIrOperation, TraceStage.SolidworksFeature), Set(TraceStage.ReprojectionDifference)),
            [TraceEdgeType.FailureOrDifferenceToRepairPatch] = (Set(TraceStage.Failure, TraceStage.ReprojectionDifference), Set(TraceStage.RepairPatch))
        };

    public ContractValidationReport Validate(DrawingTraceMap map)
    {
        var diagnostics = new List<ContractDiagnostic>();
        var nodes = new Dictionary<string, TraceNode>(StringComparer.Ordinal);
        foreach (var (node, index) in map.Nodes.Select((value, index) => (value, index)))
        {
            DrawingContractValidator.AddIf(string.IsNullOrWhiteSpace(node.NodeId), diagnostics, map, "TRC001", "Trace node id is required.", $"nodes[{index}].node_id", true);
            DrawingContractValidator.AddIf(!string.IsNullOrWhiteSpace(node.NodeId) && !nodes.TryAdd(node.NodeId, node), diagnostics, map, "TRC002", $"Duplicate trace node id '{node.NodeId}'.", $"nodes[{index}].node_id", true, node.NodeId);
            DrawingContractValidator.AddIf(string.IsNullOrWhiteSpace(node.DocumentId), diagnostics, map, "TRC003", "Trace node document_id is required.", $"nodes[{index}].document_id", true, node.NodeId);
        }

        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (edge, index) in map.Edges.Select((value, index) => (value, index)))
        {
            var path = $"edges[{index}]";
            DrawingContractValidator.AddIf(string.IsNullOrWhiteSpace(edge.EdgeId), diagnostics, map, "TRC004", "Trace edge id is required.", $"{path}.edge_id", true);
            DrawingContractValidator.AddIf(!string.IsNullOrWhiteSpace(edge.EdgeId) && !edgeIds.Add(edge.EdgeId), diagnostics, map, "TRC005", $"Duplicate trace edge id '{edge.EdgeId}'.", $"{path}.edge_id", true, edge.EdgeId);
            var hasFrom = nodes.TryGetValue(edge.FromId, out var from);
            var hasTo = nodes.TryGetValue(edge.ToId, out var to);
            DrawingContractValidator.AddIf(!hasFrom, diagnostics, map, "TRC006", "Trace edge from_id is orphaned.", $"{path}.from_id", true, edge.EdgeId, edge.FromId);
            DrawingContractValidator.AddIf(!hasTo, diagnostics, map, "TRC007", "Trace edge to_id is orphaned.", $"{path}.to_id", true, edge.EdgeId, edge.ToId);
            DrawingContractValidator.AddIf(string.IsNullOrWhiteSpace(edge.Rationale), diagnostics, map, "TRC008", "Trace edge rationale is required.", $"{path}.rationale", true, edge.EdgeId);
            if (hasFrom && hasTo && from is not null && to is not null)
            {
                var allowed = Allowed[edge.EdgeType];
                DrawingContractValidator.AddIf(!allowed.From.Contains(from.Stage) || !allowed.To.Contains(to.Stage), diagnostics, map,
                    "TRC009", $"Illegal stage dependency for {edge.EdgeType}: {from.Stage} -> {to.Stage}.", path, true, edge.EdgeId, from.NodeId, to.NodeId);
                DrawingContractValidator.AddIf(edge.Status == EvidenceStatus.Confirmed &&
                    (from.Status != EvidenceStatus.Confirmed || to.Status != EvidenceStatus.Confirmed), diagnostics, map,
                    "TRC010", "A trace edge cannot be confirmed while either endpoint remains candidate/conflict/unreadable.", $"{path}.status", true, edge.EdgeId, from.NodeId, to.NodeId);
            }
        }
        return new(diagnostics);
    }

    public IReadOnlyList<TraceNode> TraceUpstream(DrawingTraceMap map, string startNodeId) => Traverse(map, startNodeId, upstream: true);
    public IReadOnlyList<TraceNode> TraceDownstream(DrawingTraceMap map, string startNodeId) => Traverse(map, startNodeId, upstream: false);

    private static IReadOnlyList<TraceNode> Traverse(DrawingTraceMap map, string startNodeId, bool upstream)
    {
        var nodes = map.Nodes.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        if (!nodes.ContainsKey(startNodeId)) return [];
        var edges = upstream
            ? map.Edges.GroupBy(item => item.ToId).ToDictionary(group => group.Key, group => group.Select(item => item.FromId).ToArray(), StringComparer.Ordinal)
            : map.Edges.GroupBy(item => item.FromId).ToDictionary(group => group.Key, group => group.Select(item => item.ToId).ToArray(), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { startNodeId };
        var queue = new Queue<string>();
        queue.Enqueue(startNodeId);
        var result = new List<TraceNode>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!edges.TryGetValue(current, out var adjacent)) continue;
            foreach (var id in adjacent.OrderBy(item => item, StringComparer.Ordinal))
            {
                if (!visited.Add(id) || !nodes.TryGetValue(id, out var node)) continue;
                result.Add(node);
                queue.Enqueue(id);
            }
        }
        return result;
    }

    private static IReadOnlySet<TraceStage> Set(params TraceStage[] stages) => new HashSet<TraceStage>(stages);
}
