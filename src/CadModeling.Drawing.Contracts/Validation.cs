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
        if (package.SourceFacts is not null) ValidateDocument(package.SourceFacts, diagnostics);
        if (package.ViewMap is not null) ValidateDocument(package.ViewMap, diagnostics);
        ValidateDocument(package.Observation, diagnostics);
        ValidateDocument(package.Interpretation, diagnostics);
        ValidateDocument(package.Hypothesis, diagnostics);
        ValidateDocument(package.FeaturePlan, diagnostics);
        ValidateDocument(package.TraceMap, diagnostics);

        var sourceHash = package.SourceManifest.SourceSha256;
        foreach (var document in EnumerateDocuments(package).Skip(1))
        {
            AddIf(document.SourceSha256 != sourceHash, diagnostics, document, "DRW008",
                "source_sha256 必须匹配源manifest。", "source_sha256", true);
        }

        ValidateSourceManifest(package.SourceManifest, diagnostics);
        if (package.SourceFacts is not null) ValidateSourceFacts(package.SourceFacts, diagnostics);
        if (package.ViewMap is not null) ValidateViewMap(package.ViewMap, diagnostics);
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
            case SourceFactsDocument sourceFacts:
                ValidateSourceFacts(sourceFacts, diagnostics);
                break;
            case DrawingViewMapDocument viewMap:
                ValidateViewMap(viewMap, diagnostics);
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
            "source_sha256 必须匹配观察文档。", "source_sha256", true);
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
            "source_sha256 必须匹配解释文档。", "source_sha256", true);
        ValidateHypothesis(interpretation, hypothesis, diagnostics);
        return new(diagnostics);
    }

    private static IEnumerable<DrawingDocumentBase> EnumerateDocuments(DrawingContractPackage package)
    {
        yield return package.SourceManifest;
        if (package.SourceFacts is not null) yield return package.SourceFacts;
        if (package.ViewMap is not null) yield return package.ViewMap;
        yield return package.Observation;
        yield return package.Interpretation;
        yield return package.Hypothesis;
        yield return package.FeaturePlan;
        yield return package.TraceMap;
    }

    private static void ValidateSourceFacts(SourceFactsDocument document, List<ContractDiagnostic> diagnostics)
    {
        foreach (var issue in SourceFactRevisions.ValidateShape(document))
        {
            diagnostics.Add(new()
            {
                Id = $"{document.DocumentId}:{issue.Code}:{diagnostics.Count + 1}",
                Code = issue.Code,
                Severity = ContractDiagnosticSeverity.Error,
                Blocking = true,
                Message = issue.Message,
                DocumentId = document.DocumentId,
                FieldPath = "facts",
                AffectedIds = issue.FactIds
            });
        }
        AddIf(string.IsNullOrWhiteSpace(document.RevisionId) || document.RevisionId != SourceFactRevisions.RevisionId(document),
            diagnostics, document, "SRC_REVISION_HASH",
            "revision_id 必须匹配确定性源事实的内容指纹。", "revision_id", true);
    }

    private static void ValidateViewMap(DrawingViewMapDocument document, List<ContractDiagnostic> diagnostics)
    {
        EnsureUnique(document.Views.Select(item => item.ViewId), diagnostics, document,
            "VIEW_DUPLICATE", "views", "view_id");
        AddIf(document.DrawingUnit != MeasurementUnit.Millimeter, diagnostics, document,
            "VIEW_UNIT", "视图映射 drawing_unit 在 1 阶段必须以毫米为单位。", "drawing_unit", true);
        AddIf(document.Status == DocumentStatus.Valid && document.ProjectionConvention == ProjectionConvention.Unknown,
            diagnostics, document, "VIEW_PROJECTION_UNKNOWN",
            "有效的视图映射不能具有未知的投影惯例。", "projection_convention", true);
        AddIf(document.ProjectionConvention == ProjectionConvention.Mirrored && document.Status != DocumentStatus.Conflict,
            diagnostics, document, "VIEW_MIRROR_STATE",
            "镜像投影证据必须保持为冲突，而非有效的投影惯例。", "projection_convention", true);

        foreach (var view in document.Views)
        {
            var field = $"views[{view.ViewId}]";
            var sourceFrame = document.CoordinateFrames.FirstOrDefault(item => item.FrameId == view.SourceFrameId);
            var localFrame = document.CoordinateFrames.FirstOrDefault(item => item.FrameId == view.ViewMillimeterFrameId);
            var modelFrame = document.CoordinateFrames.FirstOrDefault(item => item.FrameId == view.ModelFrameId);
            var sourceToView = document.Transforms.FirstOrDefault(item => item.TransformId == view.SourceToViewTransformId);
            var viewToModel = document.Transforms.FirstOrDefault(item => item.TransformId == view.ViewToModelTransformId);

            AddIf(string.IsNullOrWhiteSpace(view.ViewId) || view.PageNumber < 1 || string.IsNullOrWhiteSpace(view.SourceRegionId),
                diagnostics, document, "VIEW_SOURCE", "视图需要 id、正向页面和源区域。", field, true);
            AddIf(sourceFrame is null, diagnostics, document, "VIEW_FRAME",
                "视图源框架不存在。", field + ".source_frame_id", true);
            AddIf(localFrame is null, diagnostics, document, "VIEW_FRAME",
                "视图毫米框架不存在。", field + ".view_millimeter_frame_id", true);
            AddIf(modelFrame is null, diagnostics, document, "VIEW_FRAME",
                "模型框架不存在。", field + ".model_frame_id", true);
            if (localFrame is not null)
                AddIf(localFrame.Space != CoordinateSpace.ViewLocal || localFrame.Unit != MeasurementUnit.Millimeter,
                    diagnostics, document, "VIEW_UNIT", "视图局部坐标系必须使用毫米为单位。", field + ".view_millimeter_frame_id", true);
            if (modelFrame is not null)
                AddIf(modelFrame.Space != CoordinateSpace.SolidworksModel || modelFrame.Unit != MeasurementUnit.Millimeter,
                    diagnostics, document, "VIEW_UNIT", "模型框架必须是 SolidWorks 毫米。", field + ".model_frame_id", true);
            AddIf(sourceToView is null || sourceToView.FromFrameId != view.SourceFrameId || sourceToView.ToFrameId != view.ViewMillimeterFrameId,
                diagnostics, document, "VIEW_SOURCE_TRANSFORM",
                "源到视图的变换缺失或连接了错误的框架。", field + ".source_to_view_transform_id", true);
            AddIf(viewToModel is null || viewToModel.FromFrameId != view.ViewMillimeterFrameId || viewToModel.ToFrameId != view.ModelFrameId,
                diagnostics, document, "VIEW_MODEL_TRANSFORM",
                "视图到模型的变换缺失或连接了错误的框架。", field + ".view_to_model_transform_id", true);
            AddIf(!double.IsFinite(view.PositionUncertaintyMm) || view.PositionUncertaintyMm < 0,
                diagnostics, document, "VIEW_UNCERTAINTY",
                "视图位置不确定性必须是有限且非负的。", field + ".position_uncertainty_mm", true);
            if (view.Status == ViewMapStatus.Resolved)
            {
                var hasScale = sourceToView is not null &&
                    sourceToView.Parameters.TryGetValue("model_mm_per_sheet_mm", out var scale) &&
                    double.IsFinite(scale) && scale > 0;
                AddIf(!hasScale, diagnostics, document, "VIEW_SCALE_UNRESOLVED",
                    "一个已解决的视图必须包含有限且正的显式绘图比例校准。", field + ".source_to_view_transform_id", true);
                AddIf(view.Fact.Status is not (FactStatus.Stated or FactStatus.Derived) || view.Fact.SourceIds.Count < 2,
                    diagnostics, document, "VIEW_CALIBRATION_EVIDENCE",
                    "视图解析必须保留独立的缩放和模型锚点证据。", field + ".fact", true);
            }
            else if (document.Status == DocumentStatus.Valid)
                AddIf(true, diagnostics, document, "VIEW_VALID_UNRESOLVED",
                    "有效的视图映射文档中不能包含候选项、冲突或不可验证的视图。", field + ".status", true);
        }
    }

    private static void ValidateDocument(DrawingDocumentBase document, List<ContractDiagnostic> diagnostics)
    {
        AddIf(!DrawingContractSchema.SupportedVersions.Contains(document.SchemaVersion), diagnostics, document,
            "DRW001", $"不支持 schema_version '{document.SchemaVersion}'。", "schema_version", true);
        AddIf(string.IsNullOrWhiteSpace(document.DocumentId), diagnostics, document,
            "DRW002", "需要 document_id。", "document_id", true);
        AddIf(!Sha256Pattern.IsMatch(document.SourceSha256), diagnostics, document,
            "DRW003", "source_sha256 必须是一个 64 个字符的十六进制 SHA-256。", "source_sha256", true);
        AddIf(string.IsNullOrWhiteSpace(document.ProducerName), diagnostics, document,
            "DRW004", "需要 producer_name。", "producer_name", true);
        AddIf(string.IsNullOrWhiteSpace(document.ProducerVersion), diagnostics, document,
            "DRW005", "需要 producer_version。", "producer_version", true);
        AddIf(!Sha256Pattern.IsMatch(document.ConfigurationHash), diagnostics, document,
            "DRW006", "configuration_hash 必须是一个 64 个字符的十六进制 SHA-256。", "configuration_hash", true);
        AddIf(document.CreatedAt == default, diagnostics, document,
            "DRW007", "需要 created_at。", "created_at", true);

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
                "URI 特征 是 必需 的。", $"artifact_manifest[{index}].uri", true, artifact.ArtifactId);
            AddIf(string.IsNullOrWhiteSpace(artifact.MediaType), diagnostics, document, "ART002",
                "需要 Artifact media_type。", $"artifact_manifest[{index}].media_type", true, artifact.ArtifactId);
            AddIf(!Sha256Pattern.IsMatch(artifact.Sha256), diagnostics, document, "ART003",
                "工件 sha256 必须是包含 64 个十六进制字符的 SHA-256。", $"artifact_manifest[{index}].sha256", true, artifact.ArtifactId);
        }
        foreach (var (frame, index) in document.CoordinateFrames.Select((value, index) => (value, index)))
        {
            AddIf(string.IsNullOrWhiteSpace(frame.FrameId), diagnostics, document, "CRD001",
                "坐标系ID是必需的。", $"coordinate_frames[{index}].frame_id", true);
            AddIf(string.IsNullOrWhiteSpace(frame.ArtifactId) || !artifactIds.Contains(frame.ArtifactId), diagnostics, document,
                "CRD002", "坐标系必须参考artifact_manifest中的一个实体。",
                $"coordinate_frames[{index}].artifact_id", true, frame.ArtifactId);
            AddIf(!UnitMatchesSpace(frame.Space, frame.Unit), diagnostics, document, "CRD003",
                $"单位 '{frame.Unit}' 不适用于坐标空间 '{frame.Space}'。",
                $"coordinate_frames[{index}].unit", true, frame.FrameId);
        }

        foreach (var (transform, index) in document.Transforms.Select((value, index) => (value, index)))
        {
            var path = $"transforms[{index}]";
            AddIf(!frameIds.Contains(transform.FromFrameId), diagnostics, document, "TRN001",
                "变换中的 from_frame_id 不存在。", $"{path}.from_frame_id", true, transform.FromFrameId);
            AddIf(!frameIds.Contains(transform.ToFrameId), diagnostics, document, "TRN002",
                "变换中的 to_frame_id 不存在。", $"{path}.to_frame_id", true, transform.ToFrameId);
            AddIf(!IsSquareMatrix(transform.ForwardMatrix), diagnostics, document, "TRN003",
                "forward_matrix 必须是一个有限的方矩阵。", $"{path}.forward_matrix", true, transform.TransformId);
            AddIf(!IsSquareMatrix(transform.InverseMatrix), diagnostics, document, "TRN004",
                "inverse_matrix 必须是一个有限的方矩阵。", $"{path}.inverse_matrix", true, transform.TransformId);
            AddIf(transform.ForwardMatrix.Count != transform.InverseMatrix.Count, diagnostics, document, "TRN005",
                "前矩阵和逆矩阵必须具有相同的维度。", path, true, transform.TransformId);
            AddIf(string.IsNullOrWhiteSpace(transform.Source), diagnostics, document, "TRN006",
                "源变换 required.", $"{path}.source", true, transform.TransformId);
            AddIf(!artifactIds.Contains(transform.BeforeArtifactId), diagnostics, document, "TRN007",
                "before_artifact_id 不存在。", $"{path}.before_artifact_id", true, transform.BeforeArtifactId);
            AddIf(!artifactIds.Contains(transform.AfterArtifactId), diagnostics, document, "TRN008",
                "after_artifact_id 不存在。", $"{path}.after_artifact_id", true, transform.AfterArtifactId);
            if (IsSquareMatrix(transform.ForwardMatrix) && IsSquareMatrix(transform.InverseMatrix) &&
                transform.ForwardMatrix.Count == transform.InverseMatrix.Count)
            {
                AddIf(!AreInverse(transform.ForwardMatrix, transform.InverseMatrix, 1e-8), diagnostics, document,
                    "TRN009", "forward_matrix 和 inverse_matrix 不构成 identity。", path, true, transform.TransformId);
            }
        }

        foreach (var (item, index) in document.UnresolvedItems.Select((value, index) => (value, index)))
        {
            var path = $"unresolved_items[{index}]";
            AddIf(string.IsNullOrWhiteSpace(item.Category), diagnostics, document, "UNR001", "类别是必需的。", $"{path}.category", true, item.Id);
            AddIf(string.IsNullOrWhiteSpace(item.Reason), diagnostics, document, "UNR002", "原因 required。", $"{path}.reason", true, item.Id);
            AddIf(string.IsNullOrWhiteSpace(item.MinimumQuestion), diagnostics, document, "UNR003", "需要 minimum_question。", $"{path}.minimum_question", true, item.Id);
            AddIf(string.IsNullOrWhiteSpace(item.SuggestedEvidence), diagnostics, document, "UNR004", "需要 suggested_evidence。", $"{path}.suggested_evidence", true, item.Id);
        }
    }

    private static void ValidateSourceManifest(DrawingSourceManifest manifest, List<ContractDiagnostic> diagnostics)
    {
        AddIf(string.IsNullOrWhiteSpace(manifest.SourcePath), diagnostics, manifest, "SRC001", "需要 source_path。", "source_path", true);
        AddIf(string.IsNullOrWhiteSpace(manifest.FileName), diagnostics, manifest, "SRC002", "需要 file_name。", "file_name", true);
        AddIf(string.IsNullOrWhiteSpace(manifest.MediaType), diagnostics, manifest, "SRC003", "需要 media_type。", "media_type", true);
        AddIf(manifest.Pages.Count == 0, diagnostics, manifest, "SRC004", "至少需要一页。", "pages", true);
        EnsureUnique(manifest.Pages.Select(item => item.PageId), diagnostics, manifest, "SRC005", "pages", "page_id");
        EnsureUnique(manifest.Pages.Select(item => item.PageNumber.ToString()), diagnostics, manifest, "SRC006", "pages", "page_number");
        foreach (var (page, index) in manifest.Pages.Select((value, index) => (value, index)))
        {
            var path = $"pages[{index}]";
            AddIf(!Sha256Pattern.IsMatch(page.Sha256), diagnostics, manifest, "SRC007", "页面 256 无效。", $"{path}.sha256", true, page.PageId);
            AddIf(page.PageNumber < 1, diagnostics, manifest, "SRC008", "page_number 必须是正数。", $"{path}.page_number", true, page.PageId);
            AddIf(page.PixelWidth <= 0 || page.PixelHeight <= 0, diagnostics, manifest, "SRC009", "像素尺寸必须为正数。", path, true, page.PageId);
            AddIf(page.PhysicalWidth <= 0 || page.PhysicalHeight <= 0, diagnostics, manifest, "SRC010", "物理页面尺寸必须为正数。", path, true, page.PageId);
            AddIf(page.PhysicalUnit is not (MeasurementUnit.Millimeter or MeasurementUnit.Inch), diagnostics, manifest, "SRC011", "物理页面单位必须是毫米或英寸。", $"{path}.physical_unit", true, page.PageId);
            AddIf(page.Dpi <= 0, diagnostics, manifest, "SRC012", "dpi 必须为正数。", $"{path}.dpi", true, page.PageId);
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
            AddIf(region.Polygon.Count < 3, diagnostics, document, "OBS005", "源区域多边形需要至少三个点。", $"source_regions[{index}].polygon", true, region.RegionId);
            ValidatePoints(region.Polygon, frames, document, diagnostics, $"source_regions[{index}].polygon", region.RegionId,
                allowedSpaces: CoordinateSpaces(CoordinateSpace.SourcePixel, CoordinateSpace.NormalizedPixel, CoordinateSpace.SheetSpace));
        }
        foreach (var (item, index) in document.Observations.Select((value, index) => (value, index)))
        {
            AddIf(!regions.Contains(item.SourceRegionId), diagnostics, document, "OBS006", "观察到的 source_region_id 不存在。", $"observations[{index}].source_region_id", true, item.ObservationId);
            ValidatePoints(item.Geometry, frames, document, diagnostics, $"observations[{index}].geometry", item.ObservationId,
                allowedSpaces: CoordinateSpaces(CoordinateSpace.SourcePixel, CoordinateSpace.NormalizedPixel, CoordinateSpace.SheetSpace));
            AddIf(item.Confidence is < 0 or > 1, diagnostics, document, "OBS007", "置信度必须在 [0, 1] 范围内。", $"observations[{index}].confidence", true, item.ObservationId);
        }
        foreach (var (item, index) in document.DimensionObservations.Select((value, index) => (value, index)))
        {
            AddIf(!regions.Contains(item.SourceRegionId), diagnostics, document, "OBS008", "测量尺寸 source_region_id 不存在。", $"dimension_observations[{index}].source_region_id", true, item.DimensionObservationId);
            AddIf(string.IsNullOrWhiteSpace(item.RawLiteral), diagnostics, document, "OBS009", "OCR 尺寸候选项必须保留 raw_literal。", $"dimension_observations[{index}].raw_literal", true, item.DimensionObservationId);
            AddIf(item.Confidence is < 0 or > 1, diagnostics, document, "OBS010", "置信度必须在 [0, 1] 范围内。", $"dimension_observations[{index}].confidence", true, item.DimensionObservationId);
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
            RequirePending(document, diagnostics, "INT005", "未知的投影约定要求 pending_clarification。", "projection_convention");
        if (document.DrawingUnit == MeasurementUnit.Unitless)
            RequirePending(document, diagnostics, "INT006", "未知的图纸单位要求 pending_clarification。", "drawing_unit");
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
            AddIf(!viewRegions.Contains(view.ViewRegionObservationId), diagnostics, document, "INT007", "视图未引用观察到的视图区域。", $"views[{index}].view_region_observation_id", true, view.ViewId);
            AddIf(!frames.Contains(view.CoordinateFrameId), diagnostics, document, "INT008", "视图 coordinate_frame_id 在此文档中不存在。", $"views[{index}].coordinate_frame_id", true, view.ViewId);
            if (view.ViewType == DrawingViewType.Unknown)
                RequirePending(document, diagnostics, "INT009", "未知的视图类型要求 pending_clarification。", $"views[{index}].view_type", view.ViewId);
            ValidateFact(view.Fact, document, diagnostics, $"views[{index}].fact", view.ViewId);
        }
        foreach (var (line, index) in document.LineInterpretations.Select((value, index) => (value, index)))
        {
            AddIf(!observations.Contains(line.ObservationId), diagnostics, document, "INT010", "线诠释不参考观察结果。", $"line_interpretations[{index}].observation_id", true, line.InterpretationId);
            ValidateFact(line.Fact, document, diagnostics, $"line_interpretations[{index}].fact", line.InterpretationId);
        }
        foreach (var (feature, index) in document.Features.Select((value, index) => (value, index)))
        {
            AddIf(feature.ObservationIds.Any(id => !observations.Contains(id)), diagnostics, document, "INT011", "特征引用一个缺失的观察。", $"features[{index}].observation_ids", true, feature.FeatureId);
            ValidateFact(feature.Fact, document, diagnostics, $"features[{index}].fact", feature.FeatureId);
        }
        foreach (var (binding, index) in document.DimensionBindings.Select((value, index) => (value, index)))
        {
            var path = $"dimension_bindings[{index}]";
            var found = dimensionObservations.TryGetValue(binding.DimensionObservationId, out var observed);
            AddIf(!found, diagnostics, document, "DIM001", "标注绑定必须引用标注观察。", $"{path}.dimension_observation_id", true, binding.BindingId, binding.DimensionObservationId);
            AddIf(!regions.Contains(binding.SourceRegionId), diagnostics, document, "DIM002", "约束尺寸必须引用源区域。", $"{path}.source_region_id", true, binding.BindingId);
            AddIf(string.IsNullOrWhiteSpace(binding.RawLiteral), diagnostics, document, "DIM003", "边界尺寸必须保留原特征的原数值。", $"{path}.raw_literal", true, binding.BindingId);
            AddIf(binding.Value.Unit == MeasurementUnit.Unitless, diagnostics, document, "DIM004", "边界尺寸需要显式的单位。", $"{path}.value.unit", true, binding.BindingId);
            AddIf(string.IsNullOrWhiteSpace(binding.Symbol), diagnostics, document, "DIM005", "边界尺寸需要显式的符号表示，如果没有图形符号，则使用 'linear'。", $"{path}.symbol", true, binding.BindingId);
            AddIf(binding.Role == DimensionRole.Driving && binding.TargetFeatureIds.Count == 0, diagnostics, document,
                "DIM006", "驱动尺寸必须绑定到至少一个解释特征。", $"{path}.target_feature_ids", true, binding.BindingId);
            AddIf(binding.Role == DimensionRole.Driving && binding.EvidenceIds.Count == 0, diagnostics, document,
                "DIM007", "驱动尺寸必须包括 evidence_ids。", $"{path}.evidence_ids", true, binding.BindingId);
            AddIf(binding.TargetFeatureIds.Any(id => !features.Contains(id)), diagnostics, document, "DIM008", "尺寸绑定针对一个缺失的特征进行目标。", $"{path}.target_feature_ids", true, binding.BindingId);
            if (!string.IsNullOrWhiteSpace(binding.SourceFactId) && binding.Decision == DimensionBindingDecision.Bound)
            {
                AddIf(string.IsNullOrWhiteSpace(binding.SourceRevisionId), diagnostics, document, "DIM012",
                    "一个绑定的源事实必须保留用于附着的源事实修订版本。", $"{path}.source_revision_id", true, binding.BindingId);
                AddIf(!Sha256Pattern.IsMatch(binding.SourceFactFingerprint), diagnostics, document, "DIM013",
                    "一个绑定源事实必须保留一个SHA-256附着指纹。", $"{path}.source_fact_fingerprint", true, binding.BindingId);
                AddIf(string.IsNullOrWhiteSpace(binding.OperationId) || string.IsNullOrWhiteSpace(binding.ParameterPath), diagnostics, document, "DIM014",
                    "一个有边界源事实需要一个显式的类型化平面操作和参数路径。", path, true, binding.BindingId);
                AddIf(!binding.Candidates.Any(candidate => candidate.HasAttachmentEvidence && candidate.OperationId == binding.OperationId && candidate.ParameterPath == binding.ParameterPath),
                    diagnostics, document, "DIM015", "一个被绑定的源事实必须由具有真实联系证据的候选项支持。", $"{path}.candidates", true, binding.BindingId);
            }
            if (found && observed is not null)
            {
                AddIf(binding.SourceRegionId != observed.SourceRegionId, diagnostics, document, "DIM009", "源区域绑定与其观察不同。", $"{path}.source_region_id", true, binding.BindingId, observed.DimensionObservationId);
                AddIf(binding.RawLiteral != observed.RawLiteral, diagnostics, document, "DIM010", "绑定原始字面值与观察到的值不同。", $"{path}.raw_literal", true, binding.BindingId, observed.DimensionObservationId);
                AddIf(observed.CandidateUnit.HasValue && binding.Value.Unit != observed.CandidateUnit, diagnostics, document, "DIM011", "绑定单元与观察到的候选单元发生冲突。", $"{path}.value.unit", true, binding.BindingId, observed.DimensionObservationId);
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
            AddIf(hypothesis.Confidence is < 0 or > 1, diagnostics, document, "HYP002", "置信度必须在 [0, 1] 范围内。", $"hypotheses[{hypothesisIndex}].confidence", true, hypothesis.HypothesisId);
            foreach (var (feature, featureIndex) in hypothesis.Features.Select((value, index) => (value, index)))
            {
                featureIds.Add(feature.HypothesisFeatureId);
                var path = $"hypotheses[{hypothesisIndex}].features[{featureIndex}]";
                AddIf(feature.InterpretationIds.Any(id => !interpretationIds.Contains(id)), diagnostics, document, "HYP003", "假设特征引用了一个缺失的解释。", $"{path}.interpretation_ids", true, feature.HypothesisFeatureId);
                AddIf(feature.DimensionBindingIds.Any(id => !bindingIds.Contains(id)), diagnostics, document, "HYP004", "特征假设引用了一个缺失的维度绑定。", $"{path}.dimension_binding_ids", true, feature.HypothesisFeatureId);
                ValidatePoints((IEnumerable<LocatedPoint3>)feature.ReferencePoints, frames, document, diagnostics, $"{path}.reference_points", feature.HypothesisFeatureId,
                    allowedSpaces: CoordinateSpaces(CoordinateSpace.ObjectXyz));
                if (feature.CutTermination == CutTermination.PendingClarification || feature.Kind == HypothesisFeatureKind.Unknown)
                    RequirePending(document, diagnostics, "HYP005", "关键拓扑不确定性要求 pending_clarification。", path, feature.HypothesisFeatureId);
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
            "PLN005", "一个包含操作的 FeaturePlan 需要至少一个接受门。", "acceptance_gates", true,
            string.IsNullOrWhiteSpace(document.PlanId) ? document.DocumentId : document.PlanId);
        var hypothesisIds = hypothesis.Hypotheses.Select(item => item.HypothesisId).ToHashSet(StringComparer.Ordinal);
        var featureIds = hypothesis.Hypotheses.SelectMany(item => item.Features).Select(item => item.HypothesisFeatureId).ToHashSet(StringComparer.Ordinal);
        AddIf(!string.IsNullOrWhiteSpace(document.SelectedHypothesisId) && !hypothesisIds.Contains(document.SelectedHypothesisId), diagnostics, document,
            "PLN002", "selected_hypothesis_id 不存在。", "selected_hypothesis_id", true, document.SelectedHypothesisId);
        var previous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (operation, index) in document.Operations.Select((value, index) => (value, index)))
        {
            var path = $"operations[{index}]";
            AddIf(operation.DependsOn.Any(id => !previous.Contains(id)), diagnostics, document, "PLN003", "操作依赖必须指向之前的操作。", $"{path}.depends_on", true, operation.OperationId);
            AddIf(operation.HypothesisFeatureIds.Any(id => !featureIds.Contains(id)), diagnostics, document, "PLN004", "操作引用了一个缺失的假设特征。", $"{path}.hypothesis_feature_ids", true, operation.OperationId);
            ValidateFact(operation.Fact, document, diagnostics, $"{path}.fact", operation.OperationId);
            previous.Add(operation.OperationId);
        }
    }

    private static void ValidateFact(FactProvenance fact, DrawingDocumentBase document, List<ContractDiagnostic> diagnostics, string path, string evidenceId)
    {
        AddIf(fact.Status == FactStatus.Stated && fact.SourceIds.Count == 0, diagnostics, document, "FCT001", "一个明示的事实需要 source_ids。", $"{path}.source_ids", true, evidenceId);
        AddIf(fact.Status == FactStatus.Derived && (fact.SourceIds.Count == 0 || string.IsNullOrWhiteSpace(fact.Rationale)), diagnostics, document,
            "FCT002", "推导出的事实需要 source_ids 和推导理由。", path, true, evidenceId);
        AddIf(fact.Status == FactStatus.Assumed && string.IsNullOrWhiteSpace(fact.AssumptionAuthorizationId), diagnostics, document,
            "FCT003", "一个假设的事实需要一个明确的assumption_authorization_id；未知不能自动提升为事实。", $"{path}.assumption_authorization_id", true, evidenceId);
        AddIf(fact.PreviousStatus == FactStatus.Unknown && fact.Status == FactStatus.Assumed && string.IsNullOrWhiteSpace(fact.AssumptionAuthorizationId), diagnostics, document,
            "FCT004", "未知被升级为默认值而没有明确授权。", path, true, evidenceId);
        AddIf(fact.Status == FactStatus.Unknown && fact.SourceIds.Count > 0, diagnostics, document,
            "FCT005", "未知事实不得声称具有确认的源ID。", $"{path}.source_ids", true, evidenceId);
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
            AddIf(string.IsNullOrWhiteSpace(point.CoordinateFrameId), diagnostics, document, "CRD010", "裸坐标被禁止；需要 coordinate_frame_id。", $"{path}[{index}].coordinate_frame_id", true, evidenceId);
            AddIf(!found, diagnostics, document, "CRD011", "坐标系不存在。", $"{path}[{index}].coordinate_frame_id", true, evidenceId);
            AddIf(found && frame is not null && !allowedSpaces.Contains(frame.Space), diagnostics, document, "CRD012", "坐标系在此字段上无效的空间。", $"{path}[{index}].coordinate_frame_id", true, evidenceId);
            AddIf(!double.IsFinite(point.X) || !double.IsFinite(point.Y), diagnostics, document, "CRD013", "坐标必须是有限的。", $"{path}[{index}]", true, evidenceId);
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
            AddIf(string.IsNullOrWhiteSpace(point.CoordinateFrameId), diagnostics, document, "CRD010", "裸坐标被禁止；需要 coordinate_frame_id。", $"{path}[{index}].coordinate_frame_id", true, evidenceId);
            AddIf(!found, diagnostics, document, "CRD011", "坐标系不存在。", $"{path}[{index}].coordinate_frame_id", true, evidenceId);
            AddIf(found && frame is not null && !allowedSpaces.Contains(frame.Space), diagnostics, document, "CRD012", "坐标系在此字段上无效的空间。", $"{path}[{index}].coordinate_frame_id", true, evidenceId);
            AddIf(!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z), diagnostics, document, "CRD013", "坐标必须是有限的。", $"{path}[{index}]", true, evidenceId);
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
            AddIf(string.IsNullOrWhiteSpace(id), diagnostics, document, code, $"{field}是必需的。", $"{path}[{index}].{field}", true);
            AddIf(!string.IsNullOrWhiteSpace(id) && !seen.Add(id), diagnostics, document, code, $"重复的 {field}：{id}。", $"{path}[{index}].{field}", true, id);
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
            DrawingContractValidator.AddIf(string.IsNullOrWhiteSpace(node.NodeId), diagnostics, map, "TRC001", "节点ID需要被指定。", $"nodes[{index}].node_id", true);
            DrawingContractValidator.AddIf(!string.IsNullOrWhiteSpace(node.NodeId) && !nodes.TryAdd(node.NodeId, node), diagnostics, map, "TRC002", $"复制具有标识符{node.NodeId}的路径节点。", $"nodes[{index}].node_id", true, node.NodeId);
            DrawingContractValidator.AddIf(string.IsNullOrWhiteSpace(node.DocumentId), diagnostics, map, "TRC003", "需要追踪节点 document_id。", $"nodes[{index}].document_id", true, node.NodeId);
        }

        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (edge, index) in map.Edges.Select((value, index) => (value, index)))
        {
            var path = $"edges[{index}]";
            DrawingContractValidator.AddIf(string.IsNullOrWhiteSpace(edge.EdgeId), diagnostics, map, "TRC004", "需要指定边的边 id。", $"{path}.edge_id", true);
            DrawingContractValidator.AddIf(!string.IsNullOrWhiteSpace(edge.EdgeId) && !edgeIds.Add(edge.EdgeId), diagnostics, map, "TRC005", $"复制具有标识符{edge.EdgeId}的边线边缘。", $"{path}.edge_id", true, edge.EdgeId);
            var hasFrom = nodes.TryGetValue(edge.FromId, out var from);
            var hasTo = nodes.TryGetValue(edge.ToId, out var to);
            DrawingContractValidator.AddIf(!hasFrom, diagnostics, map, "TRC006", "边缘 from_id 被遗弃。", $"{path}.from_id", true, edge.EdgeId, edge.FromId);
            DrawingContractValidator.AddIf(!hasTo, diagnostics, map, "TRC007", "边缘 to_id  orphaned。", $"{path}.to_id", true, edge.EdgeId, edge.ToId);
            DrawingContractValidator.AddIf(string.IsNullOrWhiteSpace(edge.Rationale), diagnostics, map, "TRC008", "需要追踪边的 依据说明（rationale）。", $"{path}.rationale", true, edge.EdgeId);
            if (hasFrom && hasTo && from is not null && to is not null)
            {
                var allowed = Allowed[edge.EdgeType];
                DrawingContractValidator.AddIf(!allowed.From.Contains(from.Stage) || !allowed.To.Contains(to.Stage), diagnostics, map,
                    "TRC009", $"{edge.EdgeType}的阶段依赖不合法：{from.Stage}->{to.Stage}。", path, true, edge.EdgeId, from.NodeId, to.NodeId);
                DrawingContractValidator.AddIf(edge.Status == EvidenceStatus.Confirmed &&
                    (from.Status != EvidenceStatus.Confirmed || to.Status != EvidenceStatus.Confirmed), diagnostics, map,
                    "TRC010", "无法确认端点之一的边线特征，因为其任一端点仍为候选项/冲突/无法读取。", $"{path}.status", true, edge.EdgeId, from.NodeId, to.NodeId);
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
