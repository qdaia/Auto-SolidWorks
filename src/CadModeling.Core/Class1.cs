using System.Globalization;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CadModeling.Ir;

namespace CadModeling.Core;

public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record ModelingDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? Path = null,
    string? SuggestedAction = null);

public sealed record CompilationResult(
    ModelingPlan? Plan,
    IReadOnlyList<ModelingDiagnostic> Diagnostics)
{
    public bool Success => Plan is not null && Diagnostics.All(x => x.Severity != DiagnosticSeverity.Error);
}

public sealed record ValidationReport(IReadOnlyList<ModelingDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(x => x.Severity != DiagnosticSeverity.Error);
}

public sealed record ExecutorHealth(
    bool Available,
    string Executor,
    string Message,
    string? SolidWorksRevision = null,
    string? ActiveDocument = null)
{
    public bool ServerReached { get; init; }
}

public sealed record ExecutionEvidence(
    string Stage,
    string Message,
    bool Passed,
    IReadOnlyDictionary<string, string>? Data = null,
    string? Code = null,
    ExecutionFailureCategory? Category = null,
    bool Retryable = false,
    string? SuggestedAction = null,
    string? OperationId = null);

[JsonConverter(typeof(JsonStringEnumConverter<ExecutionFailureCategory>))]
public enum ExecutionFailureCategory
{
    Validation,
    ReferenceResolution,
    FeatureCreation,
    GeometryQuality,
    Rebuild,
    Output,
    Preview,
    SolidWorksInterop,
    Environment,
    Transport,
    Unknown
}

public sealed record GeometrySnapshot(
    int SolidBodyCount,
    int FaceCount,
    int EdgeCount,
    double VolumeMm3,
    double SurfaceAreaMm2,
    SpatialPoint CenterOfMassMm,
    BoundingBoxSpec BoundingBoxMm)
{
    public int SurfaceBodyCount { get; init; }
}

public sealed record SpatialPoint(double X, double Y, double Z);

public sealed record StableFeatureReference(
    string OperationId,
    string OperationName,
    string OperationType,
    string SolidWorksName,
    int? SolidWorksFeatureId,
    string? PersistentReferenceBase64,
    IReadOnlyList<string> DependsOn);

public sealed record ExecutionResult(
    bool Success,
    string Status,
    string Message,
    IReadOnlyList<ExecutionEvidence> Evidence,
    string? NativePath = null,
    GeometrySnapshot? Geometry = null,
    IReadOnlyList<StableFeatureReference>? FeatureReferences = null,
    string? PlanFingerprint = null)
{
    public ModelVerificationResult? Verification { get; init; }
    public ModelingRecoveryState? Recovery { get; init; }
    public string? RequestId { get; init; }
    public bool OutcomeUnknown { get; init; }
}

public sealed record ModelingCapabilities(
    string SchemaVersion,
    IReadOnlyList<string> DocumentKinds,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> NaturalLanguageExamples,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> PostflightChecks,
    IReadOnlyDictionary<string, string> RepairGuidance,
    IReadOnlyList<DrawingCapabilityClassification> DrawingCapabilities);

public sealed record DrawingCapabilityClassification(
    string Capability,
    bool Recognized,
    bool Interpretable,
    bool Reasonable,
    bool Plannable,
    bool Executable,
    bool Repairable,
    string Status,
    string Boundary);

public static class ModelingCapabilityCatalog
{
    public static ModelingCapabilities Current { get; } = new(
        ModelingIrSchema.CurrentVersion,
        ["part", "assembly"],
        new[] {
            "ProfileSketch: 平面、圆、多边形、线/弧轮廓、槽、椭圆、样条、点；原生约束、尺寸、偏移和剪裁",
            "草图坐标：标准平面、平面面、任意框架和基准面",
            "ExtrudeBoss/ExtrudeCut：定深（Blind）、两侧对称（MidPlane）、完全贯穿（ThroughAll）、成形到下一面（UpToNext）、成形到曲面（UpToSurface）",
            "检验：特征、尺寸、配置、几何、持久实体引用；源保持的部件编辑",
            "装配：组件，变换，几何配合，干涉和保存状态验证"
        }.Concat(Enum.GetNames<NativeFeatureKind>()).ToArray(),
        ["使用 cad_create_model_plan 与类型化草案配合任何支持的特征组合。",
         "做一个长80宽50厚10毫米的矩形板，中心开一个直径10毫米通孔",
         "80 x 50 x 10 毫米平板，中心直径 10 毫米的通孔",
         "圆柱直径 40 毫米 高度 60 毫米"],
        ["长度默认为毫米，旋转和角度使用度；SetDimension 按原生参数类型推断单位，整数数量保持无单位。",
         "绘制 OCR 返回候选项。该代理在规划前解释视图几何并绑定尺寸；低分辨率 OCR 可能出错。",
         "Tapped 孔使用钻孔与装饰标注；PhysicalThread 使用固定牙型身份的单线公制外螺纹，并核对实体螺旋牙长、螺距与旋向。内螺纹和配合公差等级尚未实现。",
         "原生的SLDPRT、SLDASM和基于模型的SLDDRW/PDF导出功能被支持。导入的尺寸并不保证完全符合一份制造图纸。",
         "自由曲面建模使用提供的轮廓/路径，而不是任意的自动形状重建。",
         "创建成功不代表等同于源图纸。个别变体需要有效的 SolidWorks 几何。"],
        ["原生重建和特征创建", "体的数量，体积，面积，质心和包围盒",
         "请求的尺寸公差", "保存了原生文件和导出", "持久特征引用",
         "最终零件强制保存后重开、重建、Check3实体故障检查和请求的驱动参数读回",
         "装配重开：组件路径、配置、变换、固定状态、配合类型和驱动值；默认拒绝未允许干涉"],
        new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            ["validation"]="修正报告的类型字段并重新编译。",
            ["reference_resolution"]="检查当前实体；使用特征所有权、几何、位置或半径约束查询。",
            ["feature_creation"]="使用操作ID来检查其轮廓、选择和尺寸；修订计划并重建新的输出。",
            ["geometry_quality"]="将实际的体和测量值与所请求的形状进行比较；检查断开或缺失的特征。",
            ["rebuild"]="修复第一个失败的原生特征及其依赖项。",
            ["output"]="选择一个可写且版本化的本地输出路径。",
            ["solid_works_interop"]="检查执行器连接和SolidWorks状态后再尝试。"
        },
        [new("engineering_dimension_candidates",true,true,true,true,true,true,"agent_interpretation",
            "OCR 候选项保留源位置。 图像解释必须确定特征附着关系并解决 尺寸冲突。"),
         new("geometric_feature_planning",true,true,true,true,true,true,"typed_native_features",
            "使用支持的类型化特征和显式几何体创建模型部件和装配。"),
         new("physical_threads_and_gdandt",true,false,false,false,false,false,"limited",
            "装饰螺纹与有限公制实体外螺纹可用；内螺纹、配合公差和一般 GD&T 解释尚未实现。")]);
}

public interface IModelingExecutor
{
    Task<ProjectionCaptureResult> CaptureProjectionAsync(ProjectionCaptureRequest request,CancellationToken cancellationToken=default) =>
        Task.FromResult(new ProjectionCaptureResult(false,"此执行器不支持投影捕捉。"));
    Task<SectionCaptureResult> CaptureSectionAsync(SectionCaptureRequest request,CancellationToken cancellationToken=default) =>
        Task.FromResult(new SectionCaptureResult(false,"该执行器不支持剖面捕捉。"));
    Task<ObservationRenderArtifact> CaptureObservationAsync(ObservationCaptureRequest request,CancellationToken cancellationToken=default) =>
        Task.FromResult(new ObservationRenderArtifact{Success=false,ActualView=request.Observation.RequestedView,
            Message="该执行器不支持活动观察捕捉。"});
    Task<DrawingExportResult> ExportDrawingAsync(DrawingExportRequest request,CancellationToken cancellationToken=default) =>
        Task.FromResult(new DrawingExportResult(false,"该执行器不支持绘图导出。"));
    Task<AssemblyResult> BuildAssemblyAsync(AssemblyPlan plan,CancellationToken cancellationToken=default) =>
        Task.FromResult(new AssemblyResult(false,"该执行器不支持装配体。"));
    Task<ModelInspection> InspectAsync(ModelInspectionRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ModelInspection(false, "该执行器不支持检视。", request.InputPath));
    Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken = default);
    Task<ExecutionResult> ExecuteAsync(ModelingPlan plan, bool dryRun, CancellationToken cancellationToken = default);
}

public sealed class ModelingIrValidator
{
    public ValidationReport Validate(ModelingPlan plan, bool forExecution = false)
    {
        var diagnostics = new List<ModelingDiagnostic>();
        diagnostics.AddRange(DrawingPlanValidation.ValidateCompiled(plan));
        diagnostics.AddRange(ModelVerification.Validate(plan.Verification, plan.DrawingContext));
        diagnostics.AddRange(ModelingRecovery.Validate(plan));
        diagnostics.AddRange(DesignIntentContract.Validate(plan.DesignIntent));
        try { BoxEdgeHistoryContract.Validate(plan); }
        catch (ArgumentException ex) { diagnostics.Add(new("SEMANTIC_HISTORY_CONTRACT",DiagnosticSeverity.Error,ex.Message,"box_edge_history")); }
        ErrorIf(plan.ExecutionDeadline.DeadlineMilliseconds is <1 or >3_600_000||plan.ExecutionDeadline.PauseAcknowledgementMilliseconds is <1 or >5000,"IR_DEADLINE","执行截止时间必须为 1..3600000 毫秒；暂停确认必须为 1..5000 毫秒。","execution_deadline");
        ErrorIf(!ModelingIrSchema.IsSupported(plan.SchemaVersion), "IR001",
            $"不支持 schema_version：{plan.SchemaVersion}。支持的版本：{string.Join(", ", ModelingIrSchema.SupportedVersions)}。", "schema_version");
        ErrorIf(string.IsNullOrWhiteSpace(plan.PlanId), "IR002", "需要 plan_id。", "plan_id");
        ErrorIf(string.IsNullOrWhiteSpace(plan.Name), "IR003", "名称是必填项。", "name");
        ErrorIf(plan.DocumentKind != DocumentKind.Part, "IR004", "MVP 执行器目前仅支持零件文档。", "document_kind");
        ErrorIf(plan.Operations.Count == 0 && (plan.DesignIntent is null || string.IsNullOrWhiteSpace(plan.SourceModelPath)), "IR005", "至少需要进行一个建模操作，或在源模型副本上声明参数设计意图。", "operations");

        var seen = new Dictionary<string, ModelingOperation>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < plan.Operations.Count; index++)
        {
            var operation = plan.Operations[index];
            var path = $"operations[{index}]";
            ErrorIf(string.IsNullOrWhiteSpace(operation.Id), "IR010", "操作 ID 是必需的。", $"{path}.id");
            ErrorIf(!seen.TryAdd(operation.Id, operation), "IR011", $"操作重复，操作ID为{operation.Id}。", $"{path}.id");
            ErrorIf(string.IsNullOrWhiteSpace(operation.Name), "IR012", "操作名称是必需的。", $"{path}.name");

            foreach (var dependency in operation.DependsOn)
            {
                ErrorIf(!seen.ContainsKey(dependency), "IR013",
                    $"依赖项 '{dependency}' 必须引用之前的操作。", $"{path}.depends_on");
            }

            switch (operation)
            {
                case ProfileSketchOperation sketch:
                    if (sketch.Frame is { } frame) NativeFeatureValidation.ValidateFrame(frame, path, diagnostics);
                    ValidateSketch(sketch, path, diagnostics);
                    break;
                case ExtrudeBossOperation extrude:
                    ValidateExtrude(extrude.SketchId, extrude.DepthMm, extrude.EndCondition,
                        extrude.EndReference, extrude.StartOffsetMm, extrude.ReverseStartOffset,
                        operation, path, seen, diagnostics);
                    break;
                case ExtrudeCutOperation cut:
                    ValidateExtrude(cut.SketchId, cut.DepthMm, cut.EndCondition,
                        cut.EndReference, cut.StartOffsetMm, cut.ReverseStartOffset,
                        operation, path, seen, diagnostics);
                    break;
                case NativeFeatureOperation native:
                    NativeFeatureValidation.Validate(native, path, diagnostics);
                    SurfaceFeatureValidation.ValidateReferences(native, plan, index, path, diagnostics);
                    AdvancedFeatureValidation.ValidateReferences(native, plan, index, path, diagnostics);
                    break;
                default:
                    diagnostics.Add(new("IR099", DiagnosticSeverity.Error,
                        $"不支持的操作 CLR 类型 '{operation.GetType().Name}'。", path));
                    break;
            }
        }

        var nativePath = plan.Output.NativePath;
        if (plan.SourceModelPath is { } source)
        {
            ErrorIf(!Path.IsPathFullyQualified(source) || !File.Exists(source), "SOURCE_MODEL", "源模型必须是一个现有的绝对文件路径。", "source_model_path");
            ErrorIf(!source.EndsWith(".sldprt",StringComparison.OrdinalIgnoreCase), "SOURCE_FORMAT", "源编辑目前需要使用原生的SLDPRT文件。", "source_model_path");
            ErrorIf(Path.IsPathFullyQualified(source) && nativePath is not null && Path.IsPathFullyQualified(nativePath) && Path.GetFullPath(source).Equals(Path.GetFullPath(nativePath), StringComparison.OrdinalIgnoreCase),
                "SOURCE_MODEL_OUTPUT", "编辑使用了不同的输出版本；源路径和输出路径必须不同。", "output.native_path");
        }
        if (forExecution && string.IsNullOrWhiteSpace(nativePath))
            diagnostics.Add(new("OUT001", DiagnosticSeverity.Error, "需要一个 native_path 才能执行。", "output.native_path"));

        if (!string.IsNullOrWhiteSpace(nativePath))
        {
            ErrorIf(!Path.IsPathFullyQualified(nativePath), "OUT002", "native_path 必须是绝对值。", "output.native_path");
            ErrorIf(!nativePath.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase), "OUT003",
                "零件执行器需要一个 .SLDPRT native_path。", "output.native_path");
            ErrorIf(File.Exists(nativePath) && !plan.Output.OverwriteAllowed, "OUT004",
                "原生输出已经存在，且 overwrite_allowed 为 false。", "output.native_path",
                "请选择一个版本路径，或者明确授权覆盖。");
        }

        for (var index = 0; index < plan.Output.ExportPaths.Count; index++)
        {
            var exportPath = plan.Output.ExportPaths[index];
            var path = $"output.export_paths[{index}]";
            ErrorIf(!Path.IsPathFullyQualified(exportPath), "OUT011", "导出路径必须是绝对路径。", path);
            var extension = Path.GetExtension(exportPath);
            ErrorIf(!new[] { ".step", ".stp", ".stl" }.Contains(extension, StringComparer.OrdinalIgnoreCase),
                "OUT012", "支持的导出扩展名是 .STEP、.STP 和 .STL。", path);
            ErrorIf(File.Exists(exportPath) && !plan.Output.OverwriteAllowed, "OUT013",
                "导出输出已经存在，且 overwrite_allowed 为 false。", path,
                "请选择一个版本路径，或者明确授权覆盖。");
        }

        ValidateAcceptance(plan.Acceptance, diagnostics);

        return new(diagnostics);

        void ErrorIf(bool condition, string code, string message, string path, string? action = null)
        {
            if (condition) diagnostics.Add(new(code, DiagnosticSeverity.Error, message, path, action));
        }
    }

    private static void ValidateAcceptance(AcceptanceSpec acceptance, List<ModelingDiagnostic> diagnostics)
    {
        if (acceptance.ExpectedBoundingBoxMm is { } bounds &&
            (!double.IsFinite(bounds.X) || bounds.X<0 || !double.IsFinite(bounds.Y) || bounds.Y<0 || !double.IsFinite(bounds.Z) || bounds.Z<0))
            diagnostics.Add(new("ACC001", DiagnosticSeverity.Error,
                "预期的包围盒尺寸必须是有限且非负的。", "acceptance.expected_bounding_box_mm"));
        if (!double.IsFinite(acceptance.BoundingBoxToleranceMm) || acceptance.BoundingBoxToleranceMm < 0)
            diagnostics.Add(new("ACC002", DiagnosticSeverity.Error,
                "包围盒的容差必须是有限且非负的。", "acceptance.bounding_box_tolerance_mm"));
        if (acceptance.Geometry.ExpectedSolidBodyCount < 0 || acceptance.Geometry.ExpectedSurfaceBodyCount < 0 ||
            acceptance.Geometry.ExpectedSolidBodyCount == 0 && !(acceptance.Geometry.ExpectedSurfaceBodyCount > 0))
            diagnostics.Add(new("ACC010", DiagnosticSeverity.Error,
                "预期计数必须是非负的；一个只包含曲面的模型需要一个正的预期曲面体计数。", "acceptance.geometry.expected_solid_body_count"));
        if (acceptance.Geometry.ExpectedVolumeMm3 is { } volume && !IsFinitePositive(volume))
            diagnostics.Add(new("ACC011", DiagnosticSeverity.Error,
                "预期体积必须指定为有限且正数。", "acceptance.geometry.expected_volume_mm3"));
        if (!double.IsFinite(acceptance.Geometry.VolumeTolerancePercent) || acceptance.Geometry.VolumeTolerancePercent < 0)
            diagnostics.Add(new("ACC012", DiagnosticSeverity.Error,
                "体积容差百分比必须是有限且非负的。", "acceptance.geometry.volume_tolerance_percent"));

        static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0;
        if(acceptance.Geometry.ExpectedCenterOfMassMm is { } center && !NativeFeatureValidation.Finite(center) ||
            !double.IsFinite(acceptance.Geometry.CenterOfMassToleranceMm) || acceptance.Geometry.CenterOfMassToleranceMm<0)
            diagnostics.Add(new("ACC013",DiagnosticSeverity.Error,"质心期望和公差必须是有限的；公差不能是负数。","acceptance.geometry"));
    }

    private static void ValidateSketch(ProfileSketchOperation sketch, string path, List<ModelingDiagnostic> diagnostics)
    {
        if(sketch.AutoDimensionPrimitives && (sketch.Edits.Count>0 || sketch.Constraints.Count>0 || sketch.Dimensions.Count>0
            || sketch.Primitives.Any(p=>p is not (CenteredRectangleProfile or CircleProfile))))
            diagnostics.Add(new("IR_AUTO_DIMENSION",DiagnosticSeverity.Error,"自动驱动尺寸只接受无额外编辑/约束的矩形和圆；复杂草图请显式声明。",path));
        if (sketch.Primitives.Count == 0)
            diagnostics.Add(new("IR020", DiagnosticSeverity.Error, "需要至少一个原始图形特征的轮廓草图。", $"{path}.primitives"));
        if (!sketch.Primitives.Any(x => x.Role == ContourRole.Outer))
            diagnostics.Add(new("IR021", DiagnosticSeverity.Error, "轮廓草图至少需要一个外轮廓。", $"{path}.primitives"));

        if (sketch.FaceAttachment is { } attachment)
        {
            if (string.IsNullOrWhiteSpace(attachment.SupportOperationId))
                diagnostics.Add(new("IR040", DiagnosticSeverity.Error,
                    "一个面附件需要先前的支持操作 id。", $"{path}.face_attachment.support_operation_id"));
            else if (!sketch.DependsOn.Contains(attachment.SupportOperationId, StringComparer.OrdinalIgnoreCase))
                diagnostics.Add(new("IR041", DiagnosticSeverity.Error,
                    "一个附着于面的草图必须列出其支撑操作在 depends_on。", $"{path}.depends_on"));

            if (!double.IsFinite(attachment.PickXmm) || !double.IsFinite(attachment.PickYmm) || !double.IsFinite(attachment.PickZmm))
                diagnostics.Add(new("IR042", DiagnosticSeverity.Error,
                    "面附着点的坐标必须是有限毫米值。", $"{path}.face_attachment"));
        }

        for (var index = 0; index < sketch.Primitives.Count; index++)
        {
            var primitive = sketch.Primitives[index];
            var primitivePath = $"{path}.primitives[{index}]";
            if (!double.IsFinite(primitive.CenterXmm) || !double.IsFinite(primitive.CenterYmm))
                diagnostics.Add(new("IR022", DiagnosticSeverity.Error, "轮廓中心坐标必须是有限的。", primitivePath));
            switch (primitive)
            {
                case EllipseProfile e when !double.IsFinite(e.MajorRadiusMm) || !double.IsFinite(e.MinorRadiusMm) || e.MajorRadiusMm <= 0 || e.MinorRadiusMm <= 0:
                    diagnostics.Add(new("IR050", DiagnosticSeverity.Error, "椭圆的半径必须为正数。", primitivePath)); break;
                case OpenCurveProfile open:
                    if (open.Curves.Count == 0) diagnostics.Add(new("IR051", DiagnosticSeverity.Error, "开放曲线必须包含段落。", primitivePath));
                    foreach (var curve in open.Curves)
                        if (!double.IsFinite(curve.Start.Xmm) || !double.IsFinite(curve.Start.Ymm) || !double.IsFinite(curve.End.Xmm) || !double.IsFinite(curve.End.Ymm))
                            diagnostics.Add(new("IR052", DiagnosticSeverity.Error, "曲线点必须是有限的。", primitivePath));
                    break;
                case SplineProfile spline:
                    if (spline.Points.Count < 2 || spline.Points.Any(p => !double.IsFinite(p.Xmm) || !double.IsFinite(p.Ymm)))
                        diagnostics.Add(new("IR053", DiagnosticSeverity.Error, "样条需要至少两个有限点。", primitivePath));
                    break;
                case SketchPointsProfile pointSet:
                    if(pointSet.Points.Count==0 || pointSet.Points.Any(p=>!double.IsFinite(p.Xmm)||!double.IsFinite(p.Ymm)))
                        diagnostics.Add(new("IR054",DiagnosticSeverity.Error,"草图点集需要有限的点。",primitivePath));
                    break;
                case CenteredRectangleProfile rectangle when rectangle.WidthMm <= 0 || rectangle.HeightMm <= 0 ||
                                                               !double.IsFinite(rectangle.WidthMm) || !double.IsFinite(rectangle.HeightMm):
                    diagnostics.Add(new("IR023", DiagnosticSeverity.Error, "矩形尺寸必须是有限且正数。", primitivePath));
                    break;
                case CircleProfile circle when circle.DiameterMm <= 0 || !double.IsFinite(circle.DiameterMm):
                    diagnostics.Add(new("IR024", DiagnosticSeverity.Error, "圆的直径必须是有限且正数。", primitivePath));
                    break;
                case ThreePointRectangleProfile rectangle:
                    var points = new[] { rectangle.Corner1, rectangle.Corner2, rectangle.Corner3 };
                    if (points.Any(point => !double.IsFinite(point.Xmm) || !double.IsFinite(point.Ymm)))
                    {
                        diagnostics.Add(new("IR028", DiagnosticSeverity.Error, "三点矩形坐标必须是有限的。", primitivePath));
                        break;
                    }
                    var cross = (rectangle.Corner2.Xmm - rectangle.Corner1.Xmm) * (rectangle.Corner3.Ymm - rectangle.Corner2.Ymm) -
                                (rectangle.Corner2.Ymm - rectangle.Corner1.Ymm) * (rectangle.Corner3.Xmm - rectangle.Corner2.Xmm);
                    if (Math.Abs(cross) < 1e-9)
                        diagnostics.Add(new("IR029", DiagnosticSeverity.Error, "三点矩形角必须定义一个非零面积。", primitivePath));
                    break;
                case PolygonProfile polygon:
                    if (polygon.Points.Count < 3)
                    {
                        diagnostics.Add(new("IR025", DiagnosticSeverity.Error, "至少需要三个点，多边形才能存在。", primitivePath));
                        break;
                    }
                    if (polygon.Points.Any(point => !double.IsFinite(point.Xmm) || !double.IsFinite(point.Ymm)))
                        diagnostics.Add(new("IR026", DiagnosticSeverity.Error, "多边形点坐标必须是有限的。", primitivePath));
                    var twiceArea = polygon.Points.Select((point, pointIndex) =>
                    {
                        var next = polygon.Points[(pointIndex + 1) % polygon.Points.Count];
                        return point.Xmm * next.Ymm - next.Xmm * point.Ymm;
                    }).Sum();
                    if (Math.Abs(twiceArea) < 1e-9)
                        diagnostics.Add(new("IR027", DiagnosticSeverity.Error, "多边形面积必须非零。", primitivePath));
                    break;
                case CompositeCurveProfile composite:
                    ValidateCompositeCurve(composite, primitivePath, diagnostics);
                    break;
            }
        }
    }

    private static void ValidateCompositeCurve(
        CompositeCurveProfile composite,
        string path,
        List<ModelingDiagnostic> diagnostics)
    {
        if (composite.Curves.Count < 3)
        {
            diagnostics.Add(new("IR050", DiagnosticSeverity.Error,
                "至少需要三个连续的段构成复合曲线轮廓。", $"{path}.curves"));
            return;
        }

        for (var index = 0; index < composite.Curves.Count; index++)
        {
            var curve = composite.Curves[index];
            var curvePath = $"{path}.curves[{index}]";
            if (!IsFinite(curve.Start) || !IsFinite(curve.End))
                diagnostics.Add(new("IR051", DiagnosticSeverity.Error,
                    "曲线组合的端点必须是有限的。", curvePath));
            if (SamePoint(curve.Start, curve.End))
                diagnostics.Add(new("IR052", DiagnosticSeverity.Error,
                    "复合曲线段不能有重合的端点。", curvePath));
            if (curve is ThreePointArcProfileCurve arc)
            {
                if (!IsFinite(arc.PointOnArc))
                    diagnostics.Add(new("IR053", DiagnosticSeverity.Error,
                        "三点弧的三点必须是有限的。", curvePath));
                else if (AreCollinear(arc.Start, arc.PointOnArc, arc.End))
                    diagnostics.Add(new("IR054", DiagnosticSeverity.Error,
                        "三点圆弧需要圆弧上的一个非共线点。", curvePath));
            }

            var next = composite.Curves[(index + 1) % composite.Curves.Count];
            if (!SamePoint(curve.End, next.Start))
                diagnostics.Add(new("IR055", DiagnosticSeverity.Error,
                    "复合曲线段必须按一个封闭、端点连接的轮廓的顺序排列。", curvePath));
        }

        static bool IsFinite(ProfilePoint point) => double.IsFinite(point.Xmm) && double.IsFinite(point.Ymm);
        static bool SamePoint(ProfilePoint first, ProfilePoint second) =>
            Math.Abs(first.Xmm - second.Xmm) <= 1e-7 && Math.Abs(first.Ymm - second.Ymm) <= 1e-7;
        static bool AreCollinear(ProfilePoint first, ProfilePoint second, ProfilePoint third) =>
            Math.Abs((second.Xmm - first.Xmm) * (third.Ymm - first.Ymm) -
                     (second.Ymm - first.Ymm) * (third.Xmm - first.Xmm)) <= 1e-9;
    }

    private static void ValidateExtrude(
        string sketchId,
        double depthMm,
        ExtrudeEndCondition endCondition,
        PlanarFaceReference? endReference,
        double startOffsetMm,
        bool reverseStartOffset,
        ModelingOperation operation,
        string path,
        IReadOnlyDictionary<string, ModelingOperation> seen,
        List<ModelingDiagnostic> diagnostics)
    {
        if (endCondition is ExtrudeEndCondition.Blind or ExtrudeEndCondition.MidPlane &&
            (depthMm <= 0 || !double.IsFinite(depthMm)))
            diagnostics.Add(new("IR030", DiagnosticSeverity.Error,
                "盲面和中平面拉伸深度必须是有限的正毫米值。", $"{path}.depth_mm"));
        if (endCondition == ExtrudeEndCondition.UpToSurface &&
            (!double.IsFinite(depthMm) || depthMm < 0))
            diagnostics.Add(new("IR030", DiagnosticSeverity.Error,
                "曲面至特征的退避深度必须是有限且非负的。", $"{path}.depth_mm"));
        if (endCondition is not (ExtrudeEndCondition.Blind or ExtrudeEndCondition.MidPlane or ExtrudeEndCondition.UpToSurface or ExtrudeEndCondition.ThroughAll or ExtrudeEndCondition.UpToNext))
            diagnostics.Add(new("IR033", DiagnosticSeverity.Error,
                "不支持的拉伸端条件。", $"{path}.end_condition"));
        if (endCondition == ExtrudeEndCondition.UpToSurface && endReference is null)
            diagnostics.Add(new("IR034", DiagnosticSeverity.Error,
                "到曲面的拉伸要求有一个 end_reference。", $"{path}.end_reference"));
        if (endCondition != ExtrudeEndCondition.UpToSurface && endReference is not null)
            diagnostics.Add(new("IR035", DiagnosticSeverity.Error,
                "end_reference 只有在 UpToSurface extrude 的情况下才有效。", $"{path}.end_reference"));
        if (!double.IsFinite(startOffsetMm) || startOffsetMm < 0)
            diagnostics.Add(new("IR036", DiagnosticSeverity.Error,
                "起始偏移必须是一个有限的非负毫米值。", $"{path}.start_offset_mm"));
        if (startOffsetMm == 0 && reverseStartOffset)
            diagnostics.Add(new("IR037", DiagnosticSeverity.Error,
                "reverse_start_offset 需要一个正数的 start_offset_mm。", $"{path}.reverse_start_offset"));
        if (endReference is { } reference)
        {
            if (string.IsNullOrWhiteSpace(reference.SupportOperationId) ||
                !seen.ContainsKey(reference.SupportOperationId))
                diagnostics.Add(new("IR043", DiagnosticSeverity.Error,
                    "参考面必须是先前的操作。", $"{path}.end_reference.support_operation_id"));
            else if (!operation.DependsOn.Contains(reference.SupportOperationId, StringComparer.OrdinalIgnoreCase))
                diagnostics.Add(new("IR044", DiagnosticSeverity.Error,
                    "曲面到特征的拉伸必须列出其端参考支撑在depends_on。", $"{path}.depends_on"));
            if (!double.IsFinite(reference.PickXmm) || !double.IsFinite(reference.PickYmm) || !double.IsFinite(reference.PickZmm))
                diagnostics.Add(new("IR045", DiagnosticSeverity.Error,
                    "端参考拾取坐标必须是有限毫米值。", $"{path}.end_reference"));
        }
        if (!seen.TryGetValue(sketchId, out var sketchOp) || sketchOp is not ProfileSketchOperation)
            diagnostics.Add(new("IR031", DiagnosticSeverity.Error,
                $"sketch_id '{sketchId}' 必须引用之前的 profile_sketch 操作。", $"{path}.sketch_id"));
        if (!operation.DependsOn.Contains(sketchId, StringComparer.OrdinalIgnoreCase))
            diagnostics.Add(new("IR032", DiagnosticSeverity.Error,
                "挤出操作必须在depends_on中列出sketch_id。", $"{path}.depends_on"));
    }
}

public sealed partial class RuleBasedTextCompiler
{
    public CompilationResult Compile(string text, string? nativeOutputPath = null)
    {
        var diagnostics = new List<ModelingDiagnostic>();
        if (string.IsNullOrWhiteSpace(text))
            return new(null, [new("NL001", DiagnosticSeverity.Error, "自然语言输入为空。", "text")]);

        var originalNormalized=Normalize(text);
        var normalized = NormalizeQuantities(originalNormalized, diagnostics);
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) return new(null, diagnostics);
        var assumptions = new List<string>();
        if (!Regex.IsMatch(originalNormalized,QuantityUnit,RegexOptions.IgnoreCase))
            assumptions.Add("A1. 未声明单位的长度按毫米解释。");

        ModelingPlan? plan = ForkBracketIntentRegex().IsMatch(normalized)
            ? TryCompileForkBracket(normalized, text, nativeOutputPath, assumptions, diagnostics)
            : LChannelIntentRegex().IsMatch(normalized)
                ? TryCompileLChannel(normalized, text, nativeOutputPath, assumptions, diagnostics)
                : TryCompilePlate(normalized, text, nativeOutputPath, assumptions, diagnostics)
                  ?? TryCompileCylinder(normalized, text, nativeOutputPath, assumptions, diagnostics);

        if (plan is null)
        {
            diagnostics.Add(new("NL100", DiagnosticSeverity.Error,
                "MVP 编译器无法识别具有完整尺寸的受支持部件。", "text",
                "使用 '80 x 50 x 10 mm 块' 或 '直径 40 mm, 高度 60 mm 圆柱体'。"));
            return new(null, diagnostics);
        }

        plan = ApplyTextRequirements(normalized, plan, diagnostics);
        diagnostics.AddRange(new ModelingIrValidator().Validate(plan).Diagnostics);
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) return new(null, diagnostics);
        diagnostics.Add(new("NL000", DiagnosticSeverity.Info,
            "自然语言已编译为确定的建模计划；执行前请核对建模假设。"));
        return new(plan, diagnostics);
    }

    private static ModelingPlan? TryCompileForkBracket(
        string normalized,
        string sourceText,
        string? output,
        IReadOnlyList<string> assumptions,
        List<ModelingDiagnostic> diagnostics)
    {
        var baseDimensions = ForkBaseDimensionsRegex().Match(normalized);
        var mountingDiameter = ForkMountingHoleDiameterRegex().Match(normalized);
        var mountingSpacing = ForkMountingHoleSpacingRegex().Match(normalized);
        var earOuterWidth = ForkEarOuterWidthRegex().Match(normalized);
        var earGap = ForkEarGapRegex().Match(normalized);
        var pinDiameter = ForkPinHoleDiameterRegex().Match(normalized);
        var pinHeight = ForkPinCenterHeightRegex().Match(normalized);
        var headRadius = ForkHeadRadiusRegex().Match(normalized);
        var pinToEarEnd = ForkPinToEarEndRegex().Match(normalized);
        var stepToBaseEnd = ForkStepToBaseEndRegex().Match(normalized);
        var earEndHeight = ForkEarEndHeightRegex().Match(normalized);

        if (!baseDimensions.Success || !mountingDiameter.Success || !mountingSpacing.Success ||
            !earOuterWidth.Success || !earGap.Success || !pinDiameter.Success || !pinHeight.Success ||
            !headRadius.Success || !pinToEarEnd.Success || !stepToBaseEnd.Success || !earEndHeight.Success)
        {
            diagnostics.Add(new("NL110", DiagnosticSeverity.Error,
                "识别到叉架部件的特征，但缺少一个或多个关键尺寸。", "text",
                "指定基础尺寸 W×D×T，安装孔直径和间距，耳外宽度和内间隙，销孔直径，销中心高度，头部半径，销到耳端的距离，台阶到基础端的距离，耳端高度。"));
            return null;
        }

        var baseWidth = Number(baseDimensions, "w");
        var baseDepth = Number(baseDimensions, "d");
        var baseThickness = Number(baseDimensions, "t");
        var mountDiameter = Number(mountingDiameter, "v");
        var mountSpacing = Number(mountingSpacing, "v");
        var outerWidth = Number(earOuterWidth, "v");
        var innerGap = Number(earGap, "v");
        var pinHoleDiameter = Number(pinDiameter, "v");
        var pinCenterFromBottom = Number(pinHeight, "v");
        var radius = Number(headRadius, "v");
        var earEndFromPin = Number(pinToEarEnd, "v");
        var stepFromBaseEnd = Number(stepToBaseEnd, "v");
        var endHeightFromBottom = Number(earEndHeight, "v");
        var stepFromPin = baseDepth - stepFromBaseEnd;

        var dimensions = new[]
        {
            baseWidth, baseDepth, baseThickness, mountDiameter, mountSpacing, outerWidth, innerGap,
            pinHoleDiameter, pinCenterFromBottom, radius, earEndFromPin, stepFromBaseEnd, endHeightFromBottom
        };
        if (dimensions.Any(value => value <= 0 || !double.IsFinite(value)) ||
            innerGap >= outerWidth || mountSpacing >= baseWidth || mountDiameter >= baseDepth ||
            pinHoleDiameter >= radius * 2 || stepFromPin <= 0 || stepFromPin >= earEndFromPin ||
            earEndFromPin > baseDepth || endHeightFromBottom <= baseThickness ||
            pinCenterFromBottom - radius <= baseThickness)
        {
            diagnostics.Add(new("NL111", DiagnosticSeverity.Error,
                "叉架的尺寸在几何上是不一致的。", "text",
                "检查孔径、耳槽/外宽、安装孔间距、台阶位置、端面高度以及高于底面的R头间隙。"));
            return null;
        }

        var explicitAssumptions = assumptions.ToList();
        explicitAssumptions.Add(FormattableString.Invariant($"A2. 两个 Ø {mountDiameter} mm 的底座安装孔被放置在 {baseDepth} mm 的深度中心线上，因为它们的纵向位置是未指定的。"));
        explicitAssumptions.Add(FormattableString.Invariant($"A3. 两个耳孔是同心的，直径为{pinHoleDiameter}mm，通过切口去除；未推断出额外的同心凸台。"));
        explicitAssumptions.Add(FormattableString.Invariant($"A4. 未指定的边线仍然尖锐；R{radius}mm的侧轮廓直接创建了垫块头部。"));
        explicitAssumptions.Add("A5. 右平面耳轮廓是一个封闭的构造顺序轮廓：6 mm垂直线，下斜线，R头半圆，上斜线，末端垂直，然后是底部闭合线。");

        var baseTop = baseThickness / 2d;
        var pinZ = pinCenterFromBottom - baseThickness / 2d;
        var endTop = endHeightFromBottom - baseThickness / 2d;
        // Right-plane sketch coordinates are mirrored in the SolidWorks standard-right view.
        // Keep the round head at the drawing's left and the dimensioned ear end at its right.
        var pinU = baseDepth;
        var slotMinY = pinU - earEndFromPin - 1d;
        var slotMaxY = pinU + radius + 1d;
        var slotMinZ = baseTop;
        var slotMaxZ = pinZ + radius + 1d;

        var baseSketch = new ProfileSketchOperation
        {
            Id = "sketch_base",
            Name = "基础轮廓草图",
            Plane = ReferencePlane.Top,
            Primitives =
            [
                new CenteredRectangleProfile
                {
                    Role = ContourRole.Outer,
                    CenterXmm = 0d,
                    CenterYmm = baseDepth / 2d,
                    WidthMm = baseWidth,
                    HeightMm = baseDepth
                }
            ]
        };
        var baseBoss = new ExtrudeBossOperation
        {
            Id = "boss_base", Name = "基础凸台", DependsOn = [baseSketch.Id], SketchId = baseSketch.Id,
            DepthMm = baseThickness, EndCondition = ExtrudeEndCondition.MidPlane
        };
        var footAtStep = new ProfilePoint(pinU - stepFromPin, baseTop);
        var topOfSixMillimeterVertical = new ProfilePoint(pinU - stepFromPin, baseTop + 6d);
        var lowerSemiCircleEnd = new ProfilePoint(pinU, pinZ - radius);
        var upperSemiCircleEnd = new ProfilePoint(pinU, pinZ + radius);
        var leftArcPoint = new ProfilePoint(pinU + radius, pinZ);
        var topAtEarEnd = new ProfilePoint(pinU - earEndFromPin, endTop);
        var footAtEarEnd = new ProfilePoint(pinU - earEndFromPin, baseTop);
        var earProfileSketch = new ProfileSketchOperation
        {
            Id = "sketch_lug_right_plane", Name = "耳板侧面轮廓草图", Plane = ReferencePlane.Right,
            DependsOn = [baseBoss.Id],
            Primitives =
            [
                new CompositeCurveProfile
                {
                    Role = ContourRole.Outer,
                    Curves =
                    [
                        new LineProfileCurve { Start = footAtStep, End = topOfSixMillimeterVertical },
                        new LineProfileCurve { Start = topOfSixMillimeterVertical, End = lowerSemiCircleEnd },
                        new ThreePointArcProfileCurve { Start = lowerSemiCircleEnd, PointOnArc = leftArcPoint, End = upperSemiCircleEnd },
                        new LineProfileCurve { Start = upperSemiCircleEnd, End = topAtEarEnd },
                        new LineProfileCurve { Start = topAtEarEnd, End = footAtEarEnd },
                        new LineProfileCurve { Start = footAtEarEnd, End = footAtStep }
                    ]
                }
            ]
        };
        var earProfileBoss = new ExtrudeBossOperation
        {
            Id = "boss_lug_ears", Name = "双耳凸台", DependsOn = [earProfileSketch.Id, baseBoss.Id],
            SketchId = earProfileSketch.Id, DepthMm = outerWidth, EndCondition = ExtrudeEndCondition.MidPlane
        };
        var slotSketch = new ProfileSketchOperation
        {
            Id = "sketch_center_slot", Name = "中心槽轮廓草图", Plane = ReferencePlane.Right,
            DependsOn = [earProfileBoss.Id],
            Primitives =
            [
                new CenteredRectangleProfile
                {
                    CenterXmm = (slotMinY + slotMaxY) / 2d,
                    CenterYmm = (slotMinZ + slotMaxZ) / 2d,
                    WidthMm = slotMaxY - slotMinY,
                    HeightMm = slotMaxZ - slotMinZ
                }
            ]
        };
        var slotCut = new ExtrudeCutOperation
        {
            Id = "cut_center_slot", Name = "中心槽切除", DependsOn = [slotSketch.Id, earProfileBoss.Id],
            SketchId = slotSketch.Id, DepthMm = innerGap, EndCondition = ExtrudeEndCondition.MidPlane
        };
        var pinSketch = new ProfileSketchOperation
        {
            Id = "sketch_pin_holes", Name = "销孔轮廓草图", Plane = ReferencePlane.Right,
            DependsOn = [slotCut.Id],
            Primitives = [new CircleProfile { DiameterMm = pinHoleDiameter, CenterXmm = pinU, CenterYmm = pinZ }]
        };
        var pinCut = new ExtrudeCutOperation
        {
            Id = "cut_pin_holes", Name = "销孔切除", DependsOn = [pinSketch.Id, slotCut.Id],
            SketchId = pinSketch.Id, DepthMm = outerWidth + 4d, EndCondition = ExtrudeEndCondition.MidPlane
        };
        var mountingSketch = new ProfileSketchOperation
        {
            Id = "sketch_mounting_holes", Name = "安装孔轮廓草图", Plane = ReferencePlane.Top,
            DependsOn = [pinCut.Id],
            Primitives =
            [
                new CircleProfile { DiameterMm = mountDiameter, CenterXmm = -mountSpacing / 2d, CenterYmm = baseDepth / 2d },
                new CircleProfile { DiameterMm = mountDiameter, CenterXmm = mountSpacing / 2d, CenterYmm = baseDepth / 2d }
            ]
        };
        var mountingCut = new ExtrudeCutOperation
        {
            Id = "cut_mounting_holes", Name = "安装孔切除", DependsOn = [mountingSketch.Id, pinCut.Id],
            SketchId = mountingSketch.Id, DepthMm = baseThickness + 4d, EndCondition = ExtrudeEndCondition.MidPlane
        };

        ModelingOperation[] operations =
        [
            baseSketch, baseBoss, earProfileSketch, earProfileBoss,
            slotSketch, slotCut, pinSketch, pinCut, mountingSketch, mountingCut
        ];
        var plan = NewPlan("fork_bracket", sourceText, output, explicitAssumptions, operations,
            new(baseWidth, pinCenterFromBottom + radius, baseDepth + radius));
        return output is null
            ? plan
            : plan with { Output = plan.Output with { ExportPaths = [Path.ChangeExtension(output, ".step")] } };
    }

    private static ModelingPlan? TryCompilePlate(
        string normalized,
        string sourceText,
        string? output,
        IReadOnlyList<string> assumptions,
        List<ModelingDiagnostic> diagnostics)
    {
        var labelled = ChinesePlateRegex().Match(normalized);
        var triplet = DimensionTripletRegex().Match(normalized);
        if (!labelled.Success && !triplet.Success)
            return null;

        var match = labelled.Success ? labelled : triplet;
        var width = Number(match, "x");
        var height = Number(match, "y");
        var depth = Number(match, "z");
        if (width <= 0 || height <= 0 || depth <= 0) return null;

        var profiles = new List<ProfilePrimitive>
        {
            new CenteredRectangleProfile { Role = ContourRole.Outer, WidthMm = width, HeightMm = height }
        };

        if (HoleIntentRegex().IsMatch(normalized))
        {
            var centered = HasHoleCenterIntent(normalized);
            var through = ThroughHoleIntentRegex().IsMatch(normalized);
            var blind = BlindHoleIntentRegex().IsMatch(normalized);
            var diameterMatch = HoleDiameterRegex().Match(normalized);

            if (blind && through)
                diagnostics.Add(new("NL023", DiagnosticSeverity.Error,
                    "同一个孔不能同时声明盲孔和通孔。", "text"));
            else if (!through && !blind)
                diagnostics.Add(new("NL022", DiagnosticSeverity.Error,
                    "孔深度不明确。请指定通孔或盲孔深度。", "text",
                    "当前受支持的流程可明确指定“中心直径10毫米通孔”。"));
            if (!centered)
                diagnostics.Add(new("NL021", DiagnosticSeverity.Error,
                    "需要指定孔的位置；编译器不会默默地将未指定的孔放置在中心。", "text",
                    "指定孔位于中心，或者在延长 IR 后提供 X/Y 坐标。"));
            if (!diameterMatch.Success)
                diagnostics.Add(new("NL024", DiagnosticSeverity.Error,
                    "需要指定孔的直径。", "text", "请输入孔的直径（毫米）。"));
            else if (centered && through && !blind)
            {
                var holeDiameter = Number(diameterMatch, "d");
                if (holeDiameter >= Math.Min(width, height))
                    diagnostics.Add(new("NL020", DiagnosticSeverity.Error,
                        "请求的孔直径不大于板的包络面。", "text"));
                else
                    profiles.Add(new CircleProfile { Role = ContourRole.Inner, DiameterMm = holeDiameter });
            }
        }

        var sketch = new ProfileSketchOperation
        {
            Id = "sketch_base",
            Name = "基础轮廓草图",
            Plane = ReferencePlane.Front,
            Primitives = profiles,
            AutoDimensionPrimitives = true,
            RequireFullyDefined = true
        };
        var extrude = new ExtrudeBossOperation
        {
            Id = "extrude_base",
            Name = "基础拉伸",
            DependsOn = [sketch.Id],
            SketchId = sketch.Id,
            DepthMm = depth
        };
        return NewPlan("rectangular_plate", sourceText, output, assumptions, [sketch, extrude],
            new(width, height, depth), ProfileAreaMm2(sketch.Primitives) * depth);
    }

    private static ModelingPlan? TryCompileLChannel(
        string normalized,
        string sourceText,
        string? output,
        IReadOnlyList<string> assumptions,
        List<ModelingDiagnostic> diagnostics)
    {
        var envelope = LChannelEnvelopeRegex().Match(normalized);
        var left = LChannelLeftSectionRegex().Match(normalized);
        var transition = LChannelTransitionRegex().Match(normalized);
        var right = LChannelRightSectionRegex().Match(normalized);
        if (!envelope.Success || !left.Success || !transition.Success || !right.Success)
        {
            diagnostics.Add(new("NL120", DiagnosticSeverity.Error,
                "变量 L 形通道意图被识别，但其标准尺寸不完整。", "text",
                "指定外轮廓的尺寸 L×W×H，左端的长度和矩形开口 W×H，过渡长度，以及右端的长度和矩形开口 W×H。"));
            return null;
        }

        var length = Number(envelope, "l");
        var width = Number(envelope, "w");
        var height = Number(envelope, "h");
        var leftLength = Number(left, "len");
        var leftOpenA = Number(left, "a");
        var leftOpenB = Number(left, "b");
        var transitionLength = Number(transition, "len");
        var rightLength = Number(right, "len");
        var rightOpenA = Number(right, "a");
        var rightOpenB = Number(right, "b");

        var values = new[]
        {
            length, width, height, leftLength, leftOpenA, leftOpenB,
            transitionLength, rightLength, rightOpenA, rightOpenB
        };
        const double tolerance = 1e-6;
        if (values.Any(value => value <= 0 || !double.IsFinite(value)) ||
            Math.Abs(leftLength + transitionLength + rightLength - length) > tolerance ||
            leftOpenA >= width || rightOpenA >= width ||
            leftOpenB >= height || rightOpenB >= height)
        {
            diagnostics.Add(new("NL121", DiagnosticSeverity.Error,
                "变量 L 形通道的尺寸在几何上不一致。", "text",
                "要求正数尺寸，开口宽度小于包络宽度，开口高度小于包络高度，且段长之和等于总长度。"));
            return null;
        }

        var leftBaseThickness = height - leftOpenB;
        var rightBaseThickness = height - rightOpenB;
        var leftWallThickness = width - leftOpenA;
        var rightWallThickness = width - rightOpenA;
        var x0 = -length / 2d;
        var x1 = x0 + leftLength;
        var x2 = x1 + transitionLength;
        var x3 = length / 2d;

        static CompositeCurveProfile ClosedLineContour(params ProfilePoint[] points) => new()
        {
            Role = ContourRole.Outer,
            Curves = points.Select((point, index) => (ProfileCurve)new LineProfileCurve
            {
                Start = point,
                End = points[(index + 1) % points.Length]
            }).ToArray()
        };

        var baseSketch = new ProfileSketchOperation
        {
            Id = "sketch_variable_base_xz", Name = "变截面底座轮廓草图", Plane = ReferencePlane.Front,
            Primitives =
            [
                ClosedLineContour(
                    new(x0, 0d), new(x3, 0d), new(x3, rightBaseThickness),
                    new(x2, rightBaseThickness), new(x1, leftBaseThickness), new(x0, leftBaseThickness))
            ]
        };
        var baseBoss = new ExtrudeBossOperation
        {
            Id = "boss_variable_base", Name = "变截面底座凸台", DependsOn = [baseSketch.Id],
            SketchId = baseSketch.Id, DepthMm = width, EndCondition = ExtrudeEndCondition.MidPlane
        };

        var wallOuterY = -width / 2d;
        var wallSketch = new ProfileSketchOperation
        {
            Id = "sketch_variable_wall_xy", Name = "变截面侧壁轮廓草图", Plane = ReferencePlane.Top,
            DependsOn = [baseBoss.Id],
            Primitives =
            [
                ClosedLineContour(
                    new(x0, wallOuterY), new(x3, wallOuterY), new(x3, wallOuterY + rightWallThickness),
                    new(x2, wallOuterY + rightWallThickness), new(x1, wallOuterY + leftWallThickness),
                    new(x0, wallOuterY + leftWallThickness))
            ]
        };
        var wallBoss = new ExtrudeBossOperation
        {
            Id = "boss_variable_wall", Name = "变截面侧壁凸台",
            DependsOn = [wallSketch.Id, baseBoss.Id], SketchId = wallSketch.Id,
            DepthMm = height, EndCondition = ExtrudeEndCondition.Blind, ReverseDirection = true
        };

        var transitionOpeningVolume = transitionLength / 6d *
            (2d * leftOpenA * leftOpenB + leftOpenA * rightOpenB +
             rightOpenA * leftOpenB + 2d * rightOpenA * rightOpenB);
        var removedVolume = leftLength * leftOpenA * leftOpenB +
                            transitionOpeningVolume +
                            rightLength * rightOpenA * rightOpenB;
        var expectedVolume = length * width * height - removedVolume;
        var explicitAssumptions = assumptions.ToList();
        explicitAssumptions.Add("A2. 矩形开口保持锚定在一个共同的外角；模型方向可以旋转或镜像，而不会改变几何形状。");
        explicitAssumptions.Add("A3. 开口宽度和高度在过渡部分线性变化，因此两个L腿的厚度可以各自独立变化；所有未指定的边线断裂保持锐利。");

        diagnostics.Add(new("NL122", DiagnosticSeverity.Info,
            FormattableString.Invariant(
                $"变量 L 形截面从左基准点： x=0 开口{leftOpenA}×{leftOpenB}； x={leftLength}过渡开始； x={leftLength + transitionLength}开口{rightOpenA}×{rightOpenB}； x={length}结束。 左侧墙/底厚度为{leftWallThickness}/{leftBaseThickness}；右侧墙/底厚度为{rightWallThickness}/{rightBaseThickness}。"),
            "text"));

        ModelingOperation[] operations = [baseSketch, baseBoss, wallSketch, wallBoss];
        var plan = NewPlan("variable_l_channel", sourceText, output, explicitAssumptions, operations,
            // This template sketches length × height on Front and extrudes width normal to it.
            // SolidWorks model-space bounding axes are therefore X=length, Y=height, Z=width.
            new(length, height, width), expectedVolume);
        return output is null
            ? plan
            : plan with { Output = plan.Output with { ExportPaths = [Path.ChangeExtension(output, ".step")] } };
    }

    private static ModelingPlan? TryCompileCylinder(
        string normalized,
        string sourceText,
        string? output,
        IReadOnlyList<string> assumptions,
        List<ModelingDiagnostic> diagnostics)
    {
        var match = CylinderRegex().Match(normalized);
        if (!match.Success) return null;
        var diameter = Number(match, "d");
        var height = Number(match, "h");
        if (diameter <= 0 || height <= 0) return null;

        var sketch = new ProfileSketchOperation
        {
            Id = "sketch_base",
            Name = "基础轮廓草图",
            Plane = ReferencePlane.Front,
            Primitives = [new CircleProfile { Role = ContourRole.Outer, DiameterMm = diameter }],
            AutoDimensionPrimitives = true,
            RequireFullyDefined = true
        };
        var extrude = new ExtrudeBossOperation
        {
            Id = "extrude_base",
            Name = "基础拉伸",
            DependsOn = [sketch.Id],
            SketchId = sketch.Id,
            DepthMm = height
        };
        return NewPlan("cylinder", sourceText, output, assumptions, [sketch, extrude],
            new(diameter, diameter, height), ProfileAreaMm2(sketch.Primitives) * height);
    }

    private static ModelingPlan NewPlan(
        string name,
        string source,
        string? output,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<ModelingOperation> operations,
        BoundingBoxSpec bounds,
        double? expectedVolumeMm3 = null) => new()
    {
        PlanId = $"plan_{Guid.NewGuid():N}",
        Name = GeneratedChineseText.ModelName(name),
        SourceText = source,
        Assumptions = assumptions,
        Operations = operations,
        Output = new() { NativePath = output, OverwriteAllowed = false },
        Acceptance = new()
        {
            ExpectedFeatures = operations.Select(x => x.Name).ToArray(),
            ExpectedBoundingBoxMm = bounds,
            Geometry = new() { ExpectedVolumeMm3 = expectedVolumeMm3 }
        }
    };

    private static double ProfileAreaMm2(IEnumerable<ProfilePrimitive> primitives) => primitives.Sum(primitive =>
    {
        var unsignedArea = primitive switch
        {
            CenteredRectangleProfile rectangle => rectangle.WidthMm * rectangle.HeightMm,
            CircleProfile circle => Math.PI * Math.Pow(circle.DiameterMm / 2d, 2d),
            PolygonProfile polygon => Math.Abs(polygon.Points.Select((point, index) =>
            {
                var next = polygon.Points[(index + 1) % polygon.Points.Count];
                return point.Xmm * next.Ymm - next.Xmm * point.Ymm;
            }).Sum()) / 2d,
            _ => 0d
        };
        return primitive.Role == ContourRole.Inner ? -unsignedArea : unsignedArea;
    });

    private static double Number(Match match, string group) =>
        double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static string Normalize(string text) =>
        text.Trim().ToLowerInvariant().Replace('×', 'x').Replace('＊', 'x').Replace(',', ' ');

    [GeneratedRegex(@"(?:mm|毫米|millimet(?:er|re)s?)", RegexOptions.IgnoreCase)]
    private static partial Regex MentionsUnitRegex();

    [GeneratedRegex(@"(?:双耳|叉形|叉耳|clevis|fork[\s-]*bracket)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkBracketIntentRegex();

    [GeneratedRegex(@"(?:变截面\s*l\s*形(?:开口|角形)?槽|变截面\s*(?:角槽|角形槽)|variable[\s-]*section.*l[\s-]*(?:channel|槽))", RegexOptions.IgnoreCase)]
    private static partial Regex LChannelIntentRegex();

    [GeneratedRegex(@"(?:外包络|外形|envelope)\s*(?<l>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<w>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<h>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase)]
    private static partial Regex LChannelEnvelopeRegex();

    [GeneratedRegex(@"(?:左段|起始段)(?:长度)?\s*(?<len>\d+(?:\.\d+)?)\s*(?:mm|毫米)?.{0,32}?(?:(?:内部)?(?:方形|矩形)?开口)\s*(?<a>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<b>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase)]
    private static partial Regex LChannelLeftSectionRegex();

    [GeneratedRegex(@"过渡段(?:长度)?\s*(?<len>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase)]
    private static partial Regex LChannelTransitionRegex();

    [GeneratedRegex(@"(?:右段|末段)(?:长度)?\s*(?<len>\d+(?:\.\d+)?)\s*(?:mm|毫米)?.{0,32}?(?:(?:内部)?(?:方形|矩形)?开口)\s*(?<a>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<b>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase)]
    private static partial Regex LChannelRightSectionRegex();

    [GeneratedRegex(@"(?:底座|base)\s*(?:尺寸)?\s*(?<w>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<d>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<t>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase)]
    private static partial Regex ForkBaseDimensionsRegex();

    [GeneratedRegex(@"(?:安装孔|底座孔).{0,20}?(?:直径|[ø⌀])\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkMountingHoleDiameterRegex();

    [GeneratedRegex(@"(?:安装孔|底座孔).{0,30}?(?:中心距|孔距)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkMountingHoleSpacingRegex();

    [GeneratedRegex(@"(?:双耳外宽|耳板外宽|耳外宽)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkEarOuterWidthRegex();

    [GeneratedRegex(@"(?:内间距|内距|槽宽)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkEarGapRegex();

    [GeneratedRegex(@"(?:耳孔|销孔).{0,12}?(?:直径|[ø⌀])\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkPinHoleDiameterRegex();

    [GeneratedRegex(@"(?:孔心距底面|销孔中心距底面)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkPinCenterHeightRegex();

    [GeneratedRegex(@"(?:圆头\s*)?r\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkHeadRadiusRegex();

    [GeneratedRegex(@"(?:孔心到耳片右端|孔心到右端|孔心距耳片右端)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkPinToEarEndRegex();

    [GeneratedRegex(@"(?:台阶距底座右端|台阶到底座右端|台阶距右端)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkStepToBaseEndRegex();

    [GeneratedRegex(@"(?:右端高度|耳片右端高度|末端高度)\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ForkEarEndHeightRegex();

    [GeneratedRegex(@"(?:长(?:度)?\s*)?(?<x>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*(?:宽(?:度)?\s*)(?<y>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*(?:厚(?:度)?|高(?:度)?)\s*(?<z>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChinesePlateRegex();

    [GeneratedRegex(@"(?<x>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<y>\d+(?:\.\d+)?)\s*(?:mm|毫米)?\s*x\s*(?<z>\d+(?:\.\d+)?)\s*(?:mm|毫米)?(?:\s*(?:plate|板|块|rectangular))?", RegexOptions.IgnoreCase)]
    private static partial Regex DimensionTripletRegex();

    [GeneratedRegex(@"(?:孔|hole)", RegexOptions.IgnoreCase)]
    private static partial Regex HoleIntentRegex();

    [GeneratedRegex(@"(?:中心|中央|center(?:ed)?)", RegexOptions.IgnoreCase)]
    private static partial Regex CenterIntentRegex();

    [GeneratedRegex(@"(?:通孔|through(?:[\s-]*hole)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ThroughHoleIntentRegex();

    [GeneratedRegex(@"(?:盲孔|blind(?:[\s-]*hole)?)", RegexOptions.IgnoreCase)]
    private static partial Regex BlindHoleIntentRegex();

    [GeneratedRegex(@"(?:直径|diameter|[ø⌀])\s*(?<d>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase)]
    private static partial Regex HoleDiameterRegex();

    [GeneratedRegex(@"(?:圆柱|cylinder).{0,20}?(?:直径|diameter|[ø⌀])\s*(?<d>\d+(?:\.\d+)?)\s*(?:mm|毫米)?.{0,20}?(?:高(?:度)?|height|长(?:度)?)\s*(?<h>\d+(?:\.\d+)?)|(?:直径|diameter|[ø⌀])\s*(?<d>\d+(?:\.\d+)?)\s*(?:mm|毫米)?.{0,20}?(?:高(?:度)?|height|长(?:度)?)\s*(?<h>\d+(?:\.\d+)?)\s*(?:mm|毫米)?.{0,12}?(?:圆柱|cylinder)", RegexOptions.IgnoreCase)]
    private static partial Regex CylinderRegex();
}

public sealed class ModelingWorkflow(
    RuleBasedTextCompiler compiler,
    ModelingIrValidator validator,
    IModelingExecutor executor)
{
    public CompilationResult Compile(string text, string? nativeOutputPath = null) => compiler.Compile(text, nativeOutputPath);
    public ValidationReport Validate(ModelingPlan plan, bool forExecution = false) => validator.Validate(plan, forExecution);
    public Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken = default) => executor.HealthAsync(cancellationToken);

    public async Task<ExecutionResult> ExecuteAsync(ModelingPlan plan, bool dryRun, CancellationToken cancellationToken = default)
    {
        var report = validator.Validate(plan, forExecution: true);
        if (!report.IsValid)
            return new(false, "rejected", "建模 IR 验证失败。",
                report.Diagnostics.Select(x => new ExecutionEvidence(
                    "validation", $"{x.Code}: {x.Message}", false, Code: x.Code,
                    Category: ExecutionFailureCategory.Validation,
                    SuggestedAction: x.SuggestedAction)).ToArray(),
                PlanFingerprint: ModelingPlanIdentity.Fingerprint(plan));
        return await executor.ExecuteAsync(plan, dryRun, cancellationToken);
    }
}

public sealed class MockModelingExecutor : IModelingExecutor
{
    public Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ExecutorHealth(true, "mock", "模拟执行器已可用。"));

    public Task<ExecutionResult> ExecuteAsync(ModelingPlan plan, bool dryRun, CancellationToken cancellationToken = default)
    {
        var evidence = plan.Operations.Select(x =>
            new ExecutionEvidence("operation", $"已验证 {x.Id}（{x.GetType().Name}）。", true,
                new Dictionary<string, string> { ["name"] = x.Name })).ToArray();
        return Task.FromResult(new ExecutionResult(true, dryRun ? "dry_run" : "mocked", "IR 被模拟执行器接受。",
            evidence, plan.Output.NativePath, PlanFingerprint: ModelingPlanIdentity.Fingerprint(plan)));
    }
}

public static class ModelingPlanIdentity
{
    private static readonly JsonSerializerOptions CanonicalOptions=CreateCanonicalOptions();

    public static string Fingerprint(ModelingPlan plan)
    {
        // JSON clients can represent an integral -0 as 0. They are the same
        // geometric value; source strings and all nonzero values stay exact.
        var canonical = JsonSerializer.Serialize(plan,CanonicalOptions);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static JsonSerializerOptions CreateCanonicalOptions()
    {
        var options=new JsonSerializerOptions(ModelingIrJson.Options){WriteIndented=false};
        options.Converters.Add(new CanonicalDoubleConverter());
        return options;
    }

    private sealed class CanonicalDoubleConverter:JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader,Type typeToConvert,JsonSerializerOptions options)=>reader.GetDouble();
        public override void Write(Utf8JsonWriter writer,double value,JsonSerializerOptions options)=>writer.WriteNumberValue(value==0d?0d:value);
    }
}

public sealed record ExecutorServiceRequest(string Action, ModelingPlan? Plan = null, bool DryRun = false, ModelInspectionRequest? Inspection = null,AssemblyPlan? Assembly = null,DrawingExportRequest? Drawing = null,ObservationCaptureRequest? Observation = null,string? RequestId = null,ProjectionCaptureRequest? Projection = null,SectionCaptureRequest? Section = null,int? DeadlineMilliseconds = null);
public sealed record ExecutorServiceResponse(ExecutorHealth? Health = null, ExecutionResult? Execution = null, string? Error = null, ModelInspection? Inspection = null,AssemblyResult? Assembly = null,DrawingExportResult? Drawing = null,ObservationRenderArtifact? Observation = null,string? RequestId = null,bool Pending = false,bool PauseAccepted = false,ProjectionCaptureResult? Projection = null,SectionCaptureResult? Section = null,string? ErrorCode = null,bool OutcomeUnknown = false,bool DeadlineExceeded = false,bool ServerReached = false);

public interface IExecutionControl
{
    Task<ExecutorServiceResponse> GetExecutionStatusAsync(string requestId,CancellationToken cancellationToken=default);
    Task<ExecutorServiceResponse> PauseExecutionAsync(string requestId,CancellationToken cancellationToken=default);
}

public sealed class NamedPipeModelingExecutor(
    string pipeName = "cad-modeling-solidworks",
    int connectTimeoutMilliseconds = 5000,
    int responseTimeoutMilliseconds = 300_000) : IModelingExecutor, IExecutionControl
{
    public async Task<ProjectionCaptureResult> CaptureProjectionAsync(ProjectionCaptureRequest request,CancellationToken cancellationToken=default)
    {
        var response=await SendAsync(new("projection",Projection:request),cancellationToken);
        return (response.Projection??new(false,response.Error??"没有投影响应。")) with{RequestId=response.RequestId,OutcomeUnknown=response.OutcomeUnknown};
    }
    public async Task<SectionCaptureResult> CaptureSectionAsync(SectionCaptureRequest request,CancellationToken cancellationToken=default)
    {
        var response=await SendAsync(new("section",Section:request),cancellationToken);
        return (response.Section??new(false,response.Error??"没有剖面响应。")) with{RequestId=response.RequestId,OutcomeUnknown=response.OutcomeUnknown};
    }
    public async Task<ObservationRenderArtifact> CaptureObservationAsync(ObservationCaptureRequest request,CancellationToken cancellationToken=default)
    {
        var response=await SendAsync(new("observe",Observation:request),cancellationToken);
        return (response.Observation??new ObservationRenderArtifact{Success=false,ActualView=request.Observation.RequestedView,Message=response.Error??"没有观察到响应。"}) with{RequestId=response.RequestId,OutcomeUnknown=response.OutcomeUnknown};
    }
    public async Task<DrawingExportResult> ExportDrawingAsync(DrawingExportRequest request,CancellationToken cancellationToken=default)
    {
        var response=await SendAsync(new("drawing",Drawing:request),cancellationToken);
        return (response.Drawing??new(false,response.Error??"没有绘图响应。")) with{RequestId=response.RequestId,OutcomeUnknown=response.OutcomeUnknown};
    }
    public async Task<AssemblyResult> BuildAssemblyAsync(AssemblyPlan plan,CancellationToken cancellationToken=default)
    {
        var response=await SendAsync(new("assembly",Assembly:plan),cancellationToken);
        return (response.Assembly??new(false,response.Error??"没有装配响应。")) with{RequestId=response.RequestId,OutcomeUnknown=response.OutcomeUnknown};
    }
    public async Task<ModelInspection> InspectAsync(ModelInspectionRequest request, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new("inspect", Inspection: request), cancellationToken);
        return (response.Inspection ?? new(false, response.Error ?? "没有检查响应。", request.InputPath)) with{RequestId=response.RequestId,OutcomeUnknown=response.OutcomeUnknown};
    }
    public async Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new("health"), cancellationToken);
        return (response.Health ?? new(false, "named-pipe", response.Error ?? "执行器返回无健康结果。")) with{ServerReached=response.ServerReached};
    }

    public async Task<ExecutionResult> ExecuteAsync(ModelingPlan plan, bool dryRun, CancellationToken cancellationToken = default)
    {
        if(cancellationToken.IsCancellationRequested)return new(false,"cancelled","取消了未提交原生请求。",[]);
        var requestId=Guid.NewGuid().ToString("N");
        if(plan.ExecutionDeadline.DeadlineMilliseconds is <1 or >3_600_000||plan.ExecutionDeadline.PauseAcknowledgementMilliseconds is <1 or >5000)
            return new(false,"rejected","执行截止时间必须为 1..3600000 毫秒，并且暂停确认必须为 1..5000 毫秒。",[]);
        var pending=SendAsync(new("execute",plan,dryRun,RequestId:requestId,DeadlineMilliseconds:plan.ExecutionDeadline.DeadlineMilliseconds),CancellationToken.None);
        var cancelled=Task.Delay(Timeout.Infinite,cancellationToken);
        var completed=await Task.WhenAny(pending,cancelled);
        if(completed==pending)
        {
            var response=await pending;
            if(response.Execution is not null)return response.Execution with{RequestId=requestId};
            if(!response.OutcomeUnknown)return new(false,"failed",response.Error??"执行器返回无执行结果。",[]){RequestId=requestId};
        }
        using var controlDeadline=new CancellationTokenSource(plan.ExecutionDeadline.PauseAcknowledgementMilliseconds);
        var pause=await PauseExecutionAsync(requestId,controlDeadline.Token);
        _=pending.ContinueWith(static task=>{ _=task.Exception; },TaskContinuationOptions.OnlyOnFaulted);
        var message=pause.PauseAccepted
            ? "暂停请求已送达执行器。正在进行的COM调用将在安全边界处完成；查询此请求ID以获取持久结果。"
            : pause.Error??"客户端在执行者确认暂停请求之前取消了。";
        return new(false,completed==pending?"deadline_exceeded":"pause_requested",message,
            [new("pause_request",message,pause.PauseAccepted,new Dictionary<string,string>{{"request_id",requestId}},
                pause.PauseAccepted?"EXECUTION_PAUSE_REQUESTED":"EXECUTION_PAUSE_NOT_CONFIRMED")]){RequestId=requestId,OutcomeUnknown=true};
    }

    public async Task<ExecutorServiceResponse> PauseExecutionAsync(string requestId,CancellationToken cancellationToken=default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        // The execute connection may still be completing registration; retry briefly on a separate
        // control connection rather than treating an early race as a successful pause.
        ExecutorServiceResponse? last=null;
        for(var attempt=0;attempt<10;attempt++)
        {
            if(cancellationToken.IsCancellationRequested)return last??new(Error:"暂停确认截止时间已过。",RequestId:requestId,OutcomeUnknown:true);
            last=await SendAsync(new("pause",RequestId:requestId),cancellationToken);
            if(last.PauseAccepted||last.Execution is not null)return last;
            if(attempt<9)try{await Task.Delay(25,cancellationToken);}catch(OperationCanceledException){return last;}
        }
        return last??new(Error:"暂停控制请求没有返回响应。",RequestId:requestId);
    }

    public Task<ExecutorServiceResponse> GetExecutionStatusAsync(string requestId,CancellationToken cancellationToken=default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return SendAsync(new("execution_status",RequestId:requestId),cancellationToken);
    }

    private async Task<ExecutorServiceResponse> SendAsync(ExecutorServiceRequest request, CancellationToken cancellationToken)
    {
        var budget=request.DeadlineMilliseconds??(request.Action is "pause" or "execution_status"?1000:request.Action=="health"?Math.Min(responseTimeoutMilliseconds,5000):responseTimeoutMilliseconds);
        if(budget is <1 or >3_600_000)return new(Error:"无效的执行器截止时间。",ErrorCode:"EXECUTOR_DEADLINE_INVALID");
        request=request with{RequestId=request.RequestId??Guid.NewGuid().ToString("N"),DeadlineMilliseconds=budget};
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        var sent=false;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(connectTimeoutMilliseconds, deadline.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            sent=true;
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, ModelingIrJson.Options).AsMemory(),deadline.Token);
            var line = await reader.ReadLineAsync(deadline.Token);
            if (line is null) return new(Error: "执行器关闭了管道而没有回应。",RequestId:request.RequestId,OutcomeUnknown:true);
            return (JsonSerializer.Deserialize<ExecutorServiceResponse>(line, ModelingIrJson.Options)
                   ?? new(Error: "执行器响应为空。")) with{ServerReached=true};
        }
        catch (TimeoutException)
        {
            return new(Error: $"连接到 SolidWorks 执行器管道 '{pipeName}' 时超时。先启动执行器服务。");
        }
        catch (OperationCanceledException)
        {
            var code=cancellationToken.IsCancellationRequested?"EXECUTOR_CANCELLED":"EXECUTOR_DEADLINE";
            return new(Error:$"{code}: 请求{request.RequestId}停止等待，已过{budget}毫秒。提交的同步 COM 调用可能仍处于活动状态；在重试前请先查询其请求 ID。",RequestId:request.RequestId,ErrorCode:code,OutcomeUnknown:sent,DeadlineExceeded:!cancellationToken.IsCancellationRequested);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new(Error: $"无法与SolidWorks执行器通信：{ex.Message}",RequestId:request.RequestId,OutcomeUnknown:sent);
        }
    }
}

public sealed record LocalExecutorLaunchOptions
{
    public int ResponseTimeoutMilliseconds { get; init; } = 300_000;
    public string PipeName { get; init; } = "cad-modeling-solidworks";
    public string? ExecutablePath { get; init; }
    public string? LogDirectory { get; init; }
    public int StartupTimeoutMilliseconds { get; init; } = 20000;
}

public sealed class AutoStartingNamedPipeModelingExecutor : IModelingExecutor, IExecutionControl, IDisposable
{
    // Control requests only use the pipe. They never run a health COM call or launch SolidWorks.
    public Task<ExecutorServiceResponse> GetExecutionStatusAsync(string requestId,CancellationToken cancellationToken=default)=>_client.GetExecutionStatusAsync(requestId,cancellationToken);
    public Task<ExecutorServiceResponse> PauseExecutionAsync(string requestId,CancellationToken cancellationToken=default)=>_client.PauseExecutionAsync(requestId,cancellationToken);
    public async Task<ProjectionCaptureResult> CaptureProjectionAsync(ProjectionCaptureRequest request,CancellationToken cancellationToken=default)
    {
        var health=await HealthAsync(cancellationToken);
        return health.Available?await _client.CaptureProjectionAsync(request,cancellationToken):new(false,health.Message);
    }
    public async Task<SectionCaptureResult> CaptureSectionAsync(SectionCaptureRequest request,CancellationToken cancellationToken=default)
    {
        var health=await HealthAsync(cancellationToken);
        return health.Available?await _client.CaptureSectionAsync(request,cancellationToken):new(false,health.Message);
    }
    public async Task<ObservationRenderArtifact> CaptureObservationAsync(ObservationCaptureRequest request,CancellationToken cancellationToken=default)
    {
        var health=await HealthAsync(cancellationToken);
        return health.Available?await _client.CaptureObservationAsync(request,cancellationToken):new ObservationRenderArtifact{Success=false,ActualView=request.Observation.RequestedView,Message=health.Message};
    }
    public async Task<DrawingExportResult> ExportDrawingAsync(DrawingExportRequest request,CancellationToken cancellationToken=default)
    {
        var health=await HealthAsync(cancellationToken);
        return health.Available?await _client.ExportDrawingAsync(request,cancellationToken):new(false,health.Message);
    }
    public async Task<AssemblyResult> BuildAssemblyAsync(AssemblyPlan plan,CancellationToken cancellationToken=default)
    {
        var health=await HealthAsync(cancellationToken);
        return health.Available?await _client.BuildAssemblyAsync(plan,cancellationToken):new(false,health.Message);
    }
    public async Task<ModelInspection> InspectAsync(ModelInspectionRequest request, CancellationToken cancellationToken = default)
    {
        var health = await HealthAsync(cancellationToken);
        return health.Available ? await _client.InspectAsync(request, cancellationToken) : new(false, health.Message, request.InputPath);
    }
    private readonly LocalExecutorLaunchOptions _options;
    private readonly NamedPipeModelingExecutor _client;
    private readonly NamedPipeModelingExecutor _fastProbe;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _logGate = new();
    private Process? _ownedProcess;
    private StreamWriter? _logWriter;
    private bool _disposed;

    public AutoStartingNamedPipeModelingExecutor(LocalExecutorLaunchOptions options)
    {
        _options = options;
        _client = new(options.PipeName,responseTimeoutMilliseconds:options.ResponseTimeoutMilliseconds);
        _fastProbe = new(options.PipeName, connectTimeoutMilliseconds: 250);
    }

    public async Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _fastProbe.HealthAsync(cancellationToken);
        if (existing.ServerReached) return existing;
        if(existing.Message.StartsWith("EXECUTOR_BUSY",StringComparison.Ordinal)||existing.Message.StartsWith("EXECUTOR_DEADLINE",StringComparison.Ordinal)||existing.Message.StartsWith("EXECUTOR_CANCELLED",StringComparison.Ordinal))return existing;

        var launchError = await EnsureServiceStartedAsync(cancellationToken);
        if (launchError is not null)
            return new(false, "solidworks-com-service", launchError);

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(_options.StartupTimeoutMilliseconds);
        ExecutorHealth last = existing;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_ownedProcess is { HasExited: true })
                return new(false, "solidworks-com-service",
                    $"SolidWorks 执行器在启动时退出，代码为{_ownedProcess.ExitCode}。查看 '{CurrentLogPath()}'。");

            last = await _fastProbe.HealthAsync(cancellationToken);
            if (last.Available) return last;
            await Task.Delay(150, cancellationToken);
        }

        return new(false, "solidworks-com-service",
            $"SolidWorks 执行器在{_options.StartupTimeoutMilliseconds}毫秒内未准备好。" +
            $"最后结果：{last.Message}请参见 '{CurrentLogPath()}'。");
    }

    public async Task<ExecutionResult> ExecuteAsync(
        ModelingPlan plan,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var health = await HealthAsync(cancellationToken);
        if (!health.Available)
            return new(false, "executor_unavailable", health.Message,
                [new("executor", health.Message, false)]);
        var result = await _client.ExecuteAsync(plan, dryRun, cancellationToken);
        var healthData = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(health.SolidWorksRevision))
            healthData["solidworks_revision"] = health.SolidWorksRevision;
        if (!string.IsNullOrWhiteSpace(health.ActiveDocument))
            healthData["active_document"] = health.ActiveDocument;
        return result with
        {
            Evidence =
            [
                new ExecutionEvidence("executor_health", health.Message, true,
                    healthData.Count == 0 ? null : healthData),
                .. result.Evidence
            ]
        };
    }

    private async Task<string?> EnsureServiceStartedAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) return "执行器客户端已经被dispose。";
            if (_ownedProcess is { HasExited: false }) return null;

            var existing = await _fastProbe.HealthAsync(cancellationToken);
            if (existing.ServerReached) return existing.Available?null:existing.Message;

            var executable = ResolveExecutorPath(_options.ExecutablePath);
            if (executable is null)
                return "无法找到 CadModeling.Executor.SolidWorks.exe。将 CAD_SOLIDWORKS_EXECUTOR 设置为其绝对路径。";

            var logDirectory = Path.GetFullPath(_options.LogDirectory ??
                Path.Combine(Path.GetTempPath(), "SolidWorksAgent", "logs"));
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, $"executor-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            _logWriter = new StreamWriter(logPath, append: true, new UTF8Encoding(false)) { AutoFlush = true };

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("serve");
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(_options.PipeName);
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            _ownedProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _ownedProcess.OutputDataReceived += (_, eventArgs) => WriteLog("stdout", eventArgs.Data);
            _ownedProcess.ErrorDataReceived += (_, eventArgs) => WriteLog("stderr", eventArgs.Data);
            if (!_ownedProcess.Start()) return $"无法启动 SolidWorks 执行器：{executable}。";
            _ownedProcess.BeginOutputReadLine();
            _ownedProcess.BeginErrorReadLine();
            WriteLog("host", $"启动执行进程 pid={_ownedProcess.Id}, 管道={_options.PipeName}。");
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"无法启动SolidWorks执行器：{ex.Message}";
        }
        finally
        {
            _startGate.Release();
        }
    }

    public static string? ResolveExecutorPath(string? configuredPath = null)
    {
        configuredPath ??= Environment.GetEnvironmentVariable("CAD_SOLIDWORKS_EXECUTOR");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));
            return File.Exists(full) ? full : null;
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "CadModeling.Executor.SolidWorks.exe")
        };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "src", "CadModeling.Executor.SolidWorks", "bin", "Debug", "net9.0-windows", "CadModeling.Executor.SolidWorks.exe"));
            candidates.Add(Path.Combine(directory.FullName, "src", "CadModeling.Executor.SolidWorks", "bin", "Release", "net9.0-windows", "CadModeling.Executor.SolidWorks.exe"));
        }
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private string CurrentLogPath() => _logWriter is null
        ? _options.LogDirectory ?? Path.Combine(Path.GetTempPath(), "SolidWorksAgent", "logs")
        : ((FileStream)_logWriter.BaseStream).Name;

    private void WriteLog(string source, string? message)
    {
        if (message is null) return;
        lock (_logGate)
            _logWriter?.WriteLine($"{DateTimeOffset.Now:O} [{source}] {message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_ownedProcess is { HasExited: false })
            {
                // Parent monitoring requests cooperative shutdown. A synchronous COM
                // call must finish and persist its receipt before its executor exits.
                // Disposing the client never kills an in-flight native request.
                _ownedProcess.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            _ownedProcess?.Dispose();
            _logWriter?.Dispose();
            _startGate.Dispose();
        }
    }
}
