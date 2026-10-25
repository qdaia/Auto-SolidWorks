using System.Text.Json.Serialization;
using CadModeling.Ir;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<GenericOperationKind>))]
public enum GenericOperationKind
{
    ProfileSketch,
    ExtrudeBoss,
    ExtrudeCut,
    NativeFeature
}

[JsonConverter(typeof(JsonStringEnumConverter<GenericPrimitiveKind>))]
public enum GenericPrimitiveKind
{
    CenteredRectangle,
    ThreePointRectangle,
    Circle,
    Polygon,
    CompositeCurve,
    Ellipse,
    OpenCurve,
    Spline,
    Slot,
    Points
}

[JsonConverter(typeof(JsonStringEnumConverter<GenericCurveKind>))]
public enum GenericCurveKind
{
    Line,
    ThreePointArc
}

/// <summary>
/// Host-model-authored, tool-schema-constrained draft. It is intentionally not executable IR:
/// the compiler normalizes it into Modeling IR and geometry validation and native execution check the resulting operations.
/// </summary>
public sealed record GenericModelDraft
{
    public IReadOnlyList<string>? PreviewViews { get; init; }
    public string? PreviewScopeReason { get; init; }
    public MechanicalRecipeSpec? Recipe { get; init; }
    public ExecutionDeadlineOptions ExecutionDeadline { get; init; } = new();
    public ModelVerificationSpec Verification { get; init; } = new();
    public ModelingRecoveryOptions Recovery { get; init; } = new();
    public DrawingPlanContext? DrawingContext { get; init; }
    public string? SourceModelPath { get; init; }
    public DesignIntentSpec? DesignIntent { get; init; }
    public BoxEdgeHistorySpec? BoxEdgeHistory { get; init; }
    public string Name { get; init; } = "参数化零件";
    public required string SourceText { get; init; }
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public required IReadOnlyList<GenericOperationDraft> Operations { get; init; }
    public BoundingBoxSpec? ExpectedBoundingBoxMm { get; init; }
    public double BoundingBoxToleranceMm { get; init; } = 0.1;
    public int ExpectedSolidBodyCount { get; init; } = 1;
    public int? ExpectedSurfaceBodyCount { get; init; }
    public double? ExpectedVolumeMm3 { get; init; }
    public double VolumeTolerancePercent { get; init; } = 0.5;
    public Vector3? ExpectedCenterOfMassMm { get; init; }
    public double CenterOfMassToleranceMm { get; init; } = 0.1;
}

public sealed record GenericOperationDraft
{
    public DrivenProfileSpec? DrivenProfile { get; init; }
    public ExtrusionPlacementIntent? PlacementIntent { get; init; }
    public string? CenterPositionDimensionName { get; init; }
    public string? CenterPositionDimensionAxis { get; init; }
    public bool RequireEditable { get; init; }
    public bool AutoDimensionPrimitives { get; init; }
    public bool RequireFullyDefined { get; init; }
    public IReadOnlyList<SketchConstraintSpec> Constraints { get; init; } = [];
    public IReadOnlyList<SketchDimensionSpec> Dimensions { get; init; } = [];
    public IReadOnlyList<SketchEditSpec> Edits { get; init; } = [];
    public NativeFeatureOptions? Feature { get; init; }
    public SketchFrame? Frame { get; init; }
    public string? PlaneId { get; init; }
    public bool Merge { get; init; } = true;
    public required GenericOperationKind Type { get; init; }
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    // profile_sketch fields
    public ReferencePlane? Plane { get; init; }
    public GenericFaceReferenceDraft? FaceAttachment { get; init; }
    public IReadOnlyList<GenericPrimitiveDraft> Primitives { get; init; } = [];

    // extrude fields
    public string? SketchId { get; init; }
    public double? DepthMm { get; init; }
    public ExtrudeEndCondition? EndCondition { get; init; }
    public GenericFaceReferenceDraft? EndReference { get; init; }
    public double StartOffsetMm { get; init; }
    public bool ReverseStartOffset { get; init; }
    public bool ReverseDirection { get; init; }
}

public sealed record GenericPrimitiveDraft
{
    public bool Construction { get; init; }
    public bool Closed { get; init; }
    public required GenericPrimitiveKind Type { get; init; }
    public ContourRole Role { get; init; } = ContourRole.Outer;
    public double? CenterXmm { get; init; }
    public double? CenterYmm { get; init; }
    public double? WidthMm { get; init; }
    public double? HeightMm { get; init; }
    public double? DiameterMm { get; init; }
    public IReadOnlyList<ProfilePoint> Points { get; init; } = [];
    public IReadOnlyList<GenericCurveDraft> Curves { get; init; } = [];
}

public sealed record GenericCurveDraft
{
    public required GenericCurveKind Type { get; init; }
    public required ProfilePoint Start { get; init; }
    public required ProfilePoint End { get; init; }
    public ProfilePoint? PointOnArc { get; init; }
}

public sealed record GenericFaceReferenceDraft
{
    public required string SupportOperationId { get; init; }
    public required double PickXmm { get; init; }
    public required double PickYmm { get; init; }
    public required double PickZmm { get; init; }
}

public sealed partial class GenericPlanCompiler
{
    public CompilationResult Compile(
        GenericModelDraft draft,
        string? nativeOutputPath = null,
        IReadOnlyList<string>? exportPaths = null,
        bool overwriteAllowed = false)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("plan.compile");
        if(draft.Recipe is not null)
        {
            var expanded=MechanicalRecipes.Expand(draft);
            if(expanded.Diagnostics.Any(d=>d.Severity==DiagnosticSeverity.Error))return new(null,expanded.Diagnostics);
            draft=expanded.Draft!;
        }
        var diagnostics = new List<ModelingDiagnostic>();
        if (draft.DrawingContext is { } annotationContext)
            diagnostics.AddRange(SourceAnnotationValidation.ValidateOperations(annotationContext, draft.Operations));
        if(draft.DrawingContext is { } materialContext && SourceWorkflow.RequiresSourceReview(materialContext.WorkflowRevision))
        {
            var generated=SourceMaterialCoverage.Generate(materialContext);
            diagnostics.AddRange(generated.Diagnostics);
            // Keep conflicting caller values visible: the source validator rejects them rather than overwriting them.
            draft=draft with{Verification=draft.Verification with{MaterialRegions=draft.Verification.MaterialRegions.Concat(
                generated.Checks.Where(c=>!draft.Verification.MaterialRegions.Any(existing=>existing.Id==c.Id))).ToArray()}};
        }
        diagnostics.AddRange(ModelingPreflight.Validate(draft));
        diagnostics.AddRange(DrawingPlanValidation.Validate(draft));
        if (string.IsNullOrWhiteSpace(draft.Name))
            diagnostics.Add(Error("GPC001", "一个通用模型草案需要一个名称。", "draft.name"));
        if (string.IsNullOrWhiteSpace(draft.SourceText))
            diagnostics.Add(Error("GPC002", "一个通用模型草案需要 source_text 以实现可追溯性。", "draft.source_text"));
        if (draft.Operations.Count == 0 && (draft.DesignIntent is null || string.IsNullOrWhiteSpace(draft.SourceModelPath)))
            diagnostics.Add(Error("GPC003", "一个通用的模型草案至少需要一个操作。", "draft.operations"));

        var operations = new List<ModelingOperation>();
        for (var index = 0; index < draft.Operations.Count; index++)
        {
            var source = draft.Operations[index];
            if (string.IsNullOrWhiteSpace(source.Name))
                source = source with { Name = GeneratedChineseText.FeatureName(source, index + 1) };
            var path = $"draft.operations[{index}]";
            var operation = CompileOperation(source, path, diagnostics);
            if (operation is not null) operations.Add(operation);
        }

        var plan = new ModelingPlan
        {
            PlanId = $"plan_{Guid.NewGuid():N}",
            Name = draft.Name.Trim(),
            SourceText = draft.SourceText.Trim(),
            SourceModelPath = draft.SourceModelPath,
            DesignIntent = draft.DesignIntent,
            BoxEdgeHistory = draft.BoxEdgeHistory,
            DrawingContext = draft.DrawingContext,
            Verification = draft.Verification,
            Recovery = SourceWorkflow.IsFourStage(draft.DrawingContext?.WorkflowRevision) && draft.Recovery.Directory is null && draft.Recovery.ResumeManifestPath is null
                ? draft.Recovery with {Enabled=false,AutoSelectCheckpoint=false} : draft.Recovery,
            ExecutionDeadline = draft.ExecutionDeadline,
            Assumptions = draft.Assumptions,
            Operations = operations,
            Output = new()
            {
                PreviewViews=draft.PreviewViews??(SourceWorkflow.IsFourStage(draft.DrawingContext?.WorkflowRevision)?[]:["isometric","front","top","right","left"]),
                PreviewScopeReason=draft.PreviewScopeReason,
                NativePath = nativeOutputPath,
                ExportPaths = exportPaths ?? [],
                OverwriteAllowed = overwriteAllowed
            },
            Acceptance = new()
            {
                ExpectedFeatures = operations.Where(item => item is not NativeFeatureOperation n ||
                    n.Options.Kind is not (NativeFeatureKind.SetDimension or NativeFeatureKind.Suppress or NativeFeatureKind.Restore or NativeFeatureKind.Flatten))
                    .Select(item => item.Name).ToArray(),
                ExpectedBoundingBoxMm = draft.ExpectedBoundingBoxMm,
                BoundingBoxToleranceMm = draft.BoundingBoxToleranceMm,
                Geometry = new()
                {
                    ExpectedSolidBodyCount = draft.ExpectedSolidBodyCount,
                    ExpectedSurfaceBodyCount = draft.ExpectedSurfaceBodyCount,
                    RequirePositiveVolume = draft.ExpectedSolidBodyCount > 0,
                    ExpectedVolumeMm3 = draft.ExpectedVolumeMm3,
                    ExpectedCenterOfMassMm=draft.ExpectedCenterOfMassMm,
                    CenterOfMassToleranceMm=draft.CenterOfMassToleranceMm,
                    VolumeTolerancePercent = draft.VolumeTolerancePercent
                }
            }
        };

        // Source review and typed-IR diagnostics are independent. Collect both even when a draft
        // operation failed conversion; no partial projection is exposed as an executable plan.
        if(plan.DrawingContext is { } sourceContext && Path.IsPathFullyQualified(sourceContext.SourcePath) && File.Exists(sourceContext.SourcePath))
        {
            try { plan = plan with { DrawingSourceSha256 = DrawingPlanValidation.FileHash(sourceContext.SourcePath) }; }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException)
            { diagnostics.Add(Error("DRAWING_BINDING", error.Message, "drawing_context.source_path")); }
        }
        if(plan.Recovery.AutoSelectCheckpoint && plan.Recovery.ResumeManifestPath is null)
        {
            try
            {
                var selection = ModelingRecovery.SelectCompatibleCheckpoint(plan);
                foreach(var candidate in selection.Candidates)
                    diagnostics.Add(new("RECOVERY_CANDIDATE",DiagnosticSeverity.Info,candidate.Path+": "+candidate.Reason,"recovery"));
                if(selection.ManifestPath is { } selected)
                {
                    plan = plan with {Recovery=plan.Recovery with {ResumeManifestPath=selected}};
                    diagnostics.Add(new("RECOVERY_AUTO_SELECTED",DiagnosticSeverity.Info,"复用最长的兼容快照："+selected,"recovery.resume_manifest_path"));
                }
                else diagnostics.Add(new("RECOVERY_CLEAN_BUILD",DiagnosticSeverity.Info,"当前任务目录没有兼容检查点，将从头建立；未截断或改写旧快照。","recovery"));
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException)
            { diagnostics.Add(Error("RECOVERY_INVALID",error.Message,"recovery")); }
        }
        var validator = new ModelingIrValidator();
        diagnostics.AddRange(validator.ValidateDraftProjection(plan).Diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new(null, diagnostics.Distinct().ToArray());

        if(plan.DrawingContext is { } context)
        {
            plan = plan with { DrawingBindingDigest = DrawingPlanValidation.Digest(plan) };
        }
        var validation = validator.Validate(plan, forExecution: false);
        diagnostics.AddRange(validation.Diagnostics);
        if (diagnostics.All(item => item.Severity != DiagnosticSeverity.Error))
            diagnostics.Add(new("GPC000", DiagnosticSeverity.Info,
                "类型化草案已编译为可执行建模计划。"));
        return new(plan, diagnostics.Distinct().ToArray());
    }

    private static ModelingOperation? CompileOperation(
        GenericOperationDraft source,
        string path,
        List<ModelingDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(source.Id))
            diagnostics.Add(Error("GPC010", "操作 ID 是必需的。", $"{path}.id"));
        if (string.IsNullOrWhiteSpace(source.Name))
            diagnostics.Add(Error("GPC011", "操作名称是必需的。", $"{path}.name"));

        return source.Type switch
        {
            GenericOperationKind.ProfileSketch => CompileSketch(source, path, diagnostics),
            GenericOperationKind.ExtrudeBoss => CompileExtrude(source, path, diagnostics, isBoss: true),
            GenericOperationKind.ExtrudeCut => CompileExtrude(source, path, diagnostics, isBoss: false),
            GenericOperationKind.NativeFeature when source.Feature is not null => CompileNativeFeature(source),
            _ => RejectOperation(source, path, diagnostics)
        };
    }

    private static ModelingOperation? CompileSketch(
        GenericOperationDraft source,
        string path,
        List<ModelingDiagnostic> diagnostics)
    {
        if(source.DrivenProfile is not null)
        {
            try { source=DrivenProfiles.Expand(source); }
            catch(ArgumentException error) { diagnostics.Add(Error("DRIVEN_PROFILE_INVALID",error.Message,path+".driven_profile"));return null; }
        }
        if (source.Plane is null && source.Frame is null && source.PlaneId is null)
            diagnostics.Add(Error("GPC020", "轮廓草图需要平面。", $"{path}.plane"));
        if (source.Primitives.Count == 0)
            diagnostics.Add(Error("GPC021", "一个轮廓草图至少需要一个基本形状。", $"{path}.primitives"));

        var primitives = new List<ProfilePrimitive>();
        for (var index = 0; index < source.Primitives.Count; index++)
        {
            var primitive = CompilePrimitive(source.Primitives[index], $"{path}.primitives[{index}]", diagnostics);
            if (primitive is not null) primitives.Add(primitive);
        }

        if ((source.Plane is null && source.Frame is null && source.PlaneId is null) || primitives.Count != source.Primitives.Count)
            return null;

        var dependencies = source.DependsOn.ToList();
        if (source.PlaneId is not null) AddDependency(dependencies, source.PlaneId);
        PlanarFaceAttachment? attachment = null;
        if (source.FaceAttachment is { } face)
        {
            AddDependency(dependencies, face.SupportOperationId);
            attachment = new()
            {
                SupportOperationId = face.SupportOperationId,
                PickXmm = face.PickXmm,
                PickYmm = face.PickYmm,
                PickZmm = face.PickZmm
            };
        }

        return new ProfileSketchOperation
        {
            AutoDimensionPrimitives = source.AutoDimensionPrimitives,
            CenterPositionDimensionName=source.CenterPositionDimensionName,
            CenterPositionDimensionAxis=source.CenterPositionDimensionAxis,
            RequireEditable = source.RequireEditable,
            RequireFullyDefined = source.RequireFullyDefined,
            Constraints = source.Constraints,
            Dimensions = source.Dimensions,
            Edits = source.Edits,
            Id = source.Id,
            Name = source.Name,
            DependsOn = dependencies,
            Plane = source.Plane ?? ReferencePlane.Front,
            Frame = source.Frame,
            PlaneId = source.PlaneId,
            FaceAttachment = attachment,
            Primitives = primitives
        };
    }

    private static ModelingOperation? CompileExtrude(
        GenericOperationDraft source,
        string path,
        List<ModelingDiagnostic> diagnostics,
        bool isBoss)
    {
        if (string.IsNullOrWhiteSpace(source.SketchId))
            diagnostics.Add(Error("GPC030", "挤出需要 sketch_id。", $"{path}.sketch_id"));
        if (source.EndCondition is null)
            diagnostics.Add(Error("GPC031", "挤出需要显式的 end_condition 特征。", $"{path}.end_condition"));
        if (source.DepthMm is null && source.EndCondition is ExtrudeEndCondition.ThroughAll or ExtrudeEndCondition.UpToNext or ExtrudeEndCondition.UpToSurface)
            source = source with { DepthMm = 0 };
        if (source.DepthMm is null)
            diagnostics.Add(Error("GPC032", "Blind/MidPlane 挤出需要显式的正 depth_mm；贯穿/到下一面/到曲面不需要制造深度。", $"{path}.depth_mm"));
        if (source.DepthMm is { } depth && (!double.IsFinite(depth) || depth < 0))
            diagnostics.Add(Error("GPC032", "挤出深度必须为有限且非负的毫米值。", $"{path}.depth_mm"));
        if (string.IsNullOrWhiteSpace(source.SketchId) || source.EndCondition is null || source.DepthMm is null)
            return null;

        var dependencies = source.DependsOn.ToList();
        AddDependency(dependencies, source.SketchId);
        PlanarFaceReference? endReference = null;
        if (source.EndReference is { } face)
        {
            AddDependency(dependencies, face.SupportOperationId);
            endReference = new()
            {
                SupportOperationId = face.SupportOperationId,
                PickXmm = face.PickXmm,
                PickYmm = face.PickYmm,
                PickZmm = face.PickZmm
            };
        }

        return isBoss
            ? new ExtrudeBossOperation
            {
                Merge = source.Merge,
                Id = source.Id,
                Name = source.Name,
                DependsOn = dependencies,
                SketchId = source.SketchId,
                DepthMm = source.DepthMm.Value,
                EndCondition = source.EndCondition.Value,
                EndReference = endReference,
                StartOffsetMm = source.StartOffsetMm,
                ReverseStartOffset = source.ReverseStartOffset,
                ReverseDirection = source.ReverseDirection
            }
            : new ExtrudeCutOperation
            {
                Id = source.Id,
                Name = source.Name,
                DependsOn = dependencies,
                SketchId = source.SketchId,
                DepthMm = source.DepthMm.Value,
                EndCondition = source.EndCondition.Value,
                EndReference = endReference,
                StartOffsetMm = source.StartOffsetMm,
                ReverseStartOffset = source.ReverseStartOffset,
                ReverseDirection = source.ReverseDirection
            };
    }

    private static ProfilePrimitive? CompilePrimitive(
        GenericPrimitiveDraft source,
        string path,
        List<ModelingDiagnostic> diagnostics)
    {
        switch (source.Type)
        {
            case GenericPrimitiveKind.Slot:
                if (source.Points.Count != 2 || source.WidthMm is not > 0)
                { diagnostics.Add(Error("SLOT_PARAMETERS", "槽需要两个弧心点，并且要求 width_mm 为正数。", path)); return null; }
                var start = source.Points[0]; var end = source.Points[1]; var radius = source.WidthMm.Value / 2;
                var dx = end.Xmm - start.Xmm; var dy = end.Ymm - start.Ymm; var length = Math.Sqrt(dx*dx+dy*dy);
                if (length < 1e-9) { diagnostics.Add(Error("SLOT_LENGTH", "槽中心必须不同。",path)); return null; }
                ProfilePoint Shift(ProfilePoint p, double u, double v) => new(p.Xmm+(dx*u-dy*v)/length, p.Ymm+(dy*u+dx*v)/length);
                return new CompositeCurveProfile { Role=source.Role, Curves=[
                    new LineProfileCurve { Start=Shift(start,0,radius),End=Shift(end,0,radius) },
                    new ThreePointArcProfileCurve { Start=Shift(end,0,radius),PointOnArc=Shift(end,radius,0),End=Shift(end,0,-radius) },
                    new LineProfileCurve { Start=Shift(end,0,-radius),End=Shift(start,0,-radius) },
                    new ThreePointArcProfileCurve { Start=Shift(start,0,-radius),PointOnArc=Shift(start,-radius,0),End=Shift(start,0,radius) }
                ] };
            case GenericPrimitiveKind.Ellipse:
                if (!RequireCenter(source, path, diagnostics) || source.WidthMm is null || source.HeightMm is null)
                {
                    diagnostics.Add(Error("GPC050", "椭圆需要显式的中心点，width_mm和height_mm。", path));
                    return null;
                }
                return new EllipseProfile { CenterXmm = source.CenterXmm!.Value, CenterYmm = source.CenterYmm!.Value,
                    MajorRadiusMm = source.WidthMm.Value / 2, MinorRadiusMm = source.HeightMm.Value / 2, Role = source.Role };
            case GenericPrimitiveKind.Spline:
                return new SplineProfile { Role = source.Role, Points = source.Points, Closed = source.Closed };
            case GenericPrimitiveKind.Points:
                return new SketchPointsProfile { Role=source.Role, Points=source.Points };
            case GenericPrimitiveKind.OpenCurve:
                if (source.Curves.Any(c => c.Type == GenericCurveKind.ThreePointArc && c.PointOnArc is null))
                {
                    diagnostics.Add(Error("GPC051", "每个弧都需要 point_on_arc。", path));
                    return null;
                }
                return new OpenCurveProfile { Role = source.Role, Construction = source.Construction,
                    Curves = source.Curves.Select(c => c.Type == GenericCurveKind.Line
                        ? (ProfileCurve)new LineProfileCurve { Start = c.Start, End = c.End }
                        : new ThreePointArcProfileCurve { Start = c.Start, End = c.End, PointOnArc = c.PointOnArc! }).ToArray() };
            case GenericPrimitiveKind.CenteredRectangle:
                if (!RequireCenter(source, path, diagnostics) || source.WidthMm is null || source.HeightMm is null)
                {
                    if (source.WidthMm is null) diagnostics.Add(Error("GPC040", "中心的矩形需要 width_mm 。", $"{path}.width_mm"));
                    if (source.HeightMm is null) diagnostics.Add(Error("GPC041", "中心的矩形需要 height_mm。", $"{path}.height_mm"));
                    return null;
                }
                return new CenteredRectangleProfile
                {
                    Role = source.Role,
                    CenterXmm = source.CenterXmm!.Value,
                    CenterYmm = source.CenterYmm!.Value,
                    WidthMm = source.WidthMm.Value,
                    HeightMm = source.HeightMm.Value
                };

            case GenericPrimitiveKind.ThreePointRectangle:
                if (source.Points.Count != 3)
                {
                    diagnostics.Add(Error("GPC042", "三点矩形需要恰好三个有序点。", $"{path}.points"));
                    return null;
                }
                return new ThreePointRectangleProfile
                {
                    Role = source.Role,
                    Corner1 = source.Points[0],
                    Corner2 = source.Points[1],
                    Corner3 = source.Points[2]
                };

            case GenericPrimitiveKind.Circle:
                if (!RequireCenter(source, path, diagnostics) || source.DiameterMm is null)
                {
                    if (source.DiameterMm is null) diagnostics.Add(Error("GPC043", "一个圆需要 diameter_mm。", $"{path}.diameter_mm"));
                    return null;
                }
                return new CircleProfile
                {
                    Role = source.Role,
                    CenterXmm = source.CenterXmm!.Value,
                    CenterYmm = source.CenterYmm!.Value,
                    DiameterMm = source.DiameterMm.Value
                };

            case GenericPrimitiveKind.Polygon:
                if (source.Points.Count < 3)
                {
                    diagnostics.Add(Error("GPC044", "一个多边形至少需要三个有序的点。", $"{path}.points"));
                    return null;
                }
                return new PolygonProfile { Role = source.Role, Points = source.Points };

            case GenericPrimitiveKind.CompositeCurve:
                if (source.Curves.Count < 3)
                {
                    diagnostics.Add(Error("GPC045", "一个复合曲线至少需要三个有序的曲线。", $"{path}.curves"));
                    return null;
                }
                var curves = new List<ProfileCurve>();
                for (var index = 0; index < source.Curves.Count; index++)
                {
                    var curve = source.Curves[index];
                    var curvePath = $"{path}.curves[{index}]";
                    if (curve.Type == GenericCurveKind.ThreePointArc && curve.PointOnArc is null)
                    {
                        diagnostics.Add(Error("GPC046", "三点圆弧需要 point_on_arc。", $"{curvePath}.point_on_arc"));
                        continue;
                    }
                    curves.Add(curve.Type == GenericCurveKind.Line
                        ? new LineProfileCurve { Start = curve.Start, End = curve.End }
                        : new ThreePointArcProfileCurve
                        {
                            Start = curve.Start,
                            PointOnArc = curve.PointOnArc!,
                            End = curve.End
                        });
                }
                return curves.Count == source.Curves.Count
                    ? new CompositeCurveProfile { Role = source.Role, Curves = curves }
                    : null;

            default:
                diagnostics.Add(Error("GPC049", $"不支持的原始类型 '{source.Type}'。", $"{path}.type"));
                return null;
        }
    }

    private static bool RequireCenter(
        GenericPrimitiveDraft source,
        string path,
        List<ModelingDiagnostic> diagnostics)
    {
        var valid = true;
        if (source.CenterXmm is null)
        {
            diagnostics.Add(Error("GPC047", "这个原始需要显式 center_xmm。", $"{path}.center_xmm"));
            valid = false;
        }
        if (source.CenterYmm is null)
        {
            diagnostics.Add(Error("GPC048", "这个原始需要显式 center_ymm。", $"{path}.center_ymm"));
            valid = false;
        }
        return valid;
    }

    private static ModelingOperation? RejectOperation(
        GenericOperationDraft source,
        string path,
        List<ModelingDiagnostic> diagnostics)
    {
        diagnostics.Add(Error("GPC019", $"不支持的操作类型 '{source.Type}' 。", $"{path}.type"));
        return null;
    }

    private static void AddDependency(List<string> dependencies, string id)
    {
        if (!dependencies.Contains(id, StringComparer.OrdinalIgnoreCase)) dependencies.Add(id);
    }

    private static ModelingDiagnostic Error(string code, string message, string path) =>
        new(code, DiagnosticSeverity.Error, message, path,
            "修正结构草案并重新编译；不要跳过建模 IR。");
}
