using System.ComponentModel;
using System.IO;
using System.Reflection;
using CadModeling.Core;
using CadModeling.Ir;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Ingestion;
using CadModeling.Drawing.Providers.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<RuleBasedTextCompiler>();
builder.Services.AddSingleton<GenericPlanCompiler>();
builder.Services.AddSingleton<ModelingIrValidator>();
builder.Services.AddSingleton<ArtifactCacheStore>();
builder.Services.AddSingleton<IModelingExecutor>(_ => new AutoStartingNamedPipeModelingExecutor(new()
{
    PipeName = Environment.GetEnvironmentVariable("CAD_SOLIDWORKS_PIPE") ?? "auto-solidworks-modeling",
    ExecutablePath = Environment.GetEnvironmentVariable("CAD_SOLIDWORKS_EXECUTOR"),
    LogDirectory = Environment.GetEnvironmentVariable("CAD_EXECUTOR_LOG_DIR")
}));
builder.Services.AddSingleton<ModelingWorkflow>();
builder.Services.AddSingleton<Stage4SavedModelAcceptanceWorkflow>();
builder.Services.AddSingleton<IPdfNativeObservationProvider, PdfPigNativeObservationProvider>();
builder.Services.AddSingleton<IPdfRasterizationProvider, PdftoppmRasterizationProvider>();
builder.Services.AddSingleton<IRasterInputProvider, WpfRasterInputProvider>();
builder.Services.AddSingleton<IRasterPreprocessor, EngineeringRasterPreprocessor>();
builder.Services.AddSingleton<ILayoutObservationProvider, PageLayoutObservationProvider>();
builder.Services.AddSingleton<ITextObservationProvider, LocalTextObservationProvider>();
builder.Services.AddSingleton<IPrimitiveObservationProvider, DeterministicPrimitiveObservationProvider>();
builder.Services.AddSingleton<IObservationFusionService, ObservationFusionService>();
builder.Services.AddSingleton<IEngineeringDrawingIngestionService, EngineeringDrawingIngestionService>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<ModelingTools>(ModelingIrJson.Options).WithTools<GordonSurfaceTools>(ModelingIrJson.Options)
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
    {
        CallToolResult result;
        try { result = await next(context, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ModelContextProtocol.McpProtocolException) { throw; }
        catch (Exception exception)
        {
            var detail = System.Text.RegularExpressions.Regex.IsMatch(exception.Message,"[\\u4e00-\\u9fff]")
                ? exception.Message : "请检查请求参数和本地运行环境。";
            return new CallToolResult { IsError=true,Content=[new TextContentBlock { Text=$"调用工具“{context.Params?.Name}”失败：{detail}" }] };
        }
        // The SDK deliberately hides ordinary exceptions behind a generic message.
        // Localize that wrapper without changing isError, structured data or protocol errors.
        if (result.IsError == true)
            foreach (var text in result.Content.OfType<TextContentBlock>())
                if (text.Text.StartsWith("An error occurred invoking '", StringComparison.Ordinal))
                    text.Text = $"调用工具“{context.Params?.Name}”失败。请检查请求参数；详细原因见本地诊断日志。";
        return result;
    }));
await builder.Build().RunAsync();

[McpServerToolType]
public sealed class ModelingTools
{
    [McpServerTool(Name="cad_execution_status",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("超时、暂停或连接中断后，按已签发的 request_id 查询执行器状态和回执；此操作不启动 SolidWorks。处于 pending 或 outcome_unknown 状态时，不得重新提交原生修改。")]
    public static Task<ExecutorServiceResponse> ExecutionStatus(IModelingExecutor executor,string requestId,CancellationToken cancellationToken=default)=>
        executor is IExecutionControl control?control.GetExecutionStatusAsync(requestId,cancellationToken):Task.FromResult(new ExecutorServiceResponse(Error:"执行器未提供请求状态与暂停控制接口。"));
    [McpServerTool(Name="cad_pause_execution",ReadOnly=false,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("请求已签发的 request_id 在下一处安全的原生执行边界暂停；此操作不启动 SolidWorks。正在执行的同步 COM 调用可能继续运行，重试前必须查询状态。")]
    public static Task<ExecutorServiceResponse> PauseExecution(IModelingExecutor executor,string requestId,CancellationToken cancellationToken=default)=>
        executor is IExecutionControl control?control.PauseExecutionAsync(requestId,cancellationToken):Task.FromResult(new ExecutorServiceResponse(Error:"执行器未提供请求状态与暂停控制接口。"));
    [McpServerTool(Name="cad_review_drawing_coverage",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("读取源图纸生成的遗漏清单和原始复查裁剪图，或据此检查 drawing_context。返回尚未核对的注释、几何及残留笔画，缺失的区域复查、数量不匹配和已声明的跨视图冲突。此操作不启动 SolidWorks；通过只表示复查记录完整，不证明图纸已被完整理解。使用 offset/limit 分别分页读取候选项、区域和问题，并依据各自返回的总数继续读取；可用 pageNumber 限定页码。")]
    public static object ReviewDrawingCoverage(string inventoryPath, DrawingPlanContext? drawingContext=null,
        int? pageNumber=null, int offset=0, int limit=50, bool onlyUnresolved=true)
    {
        if(offset<0 || limit is <1 or >200) throw new ArgumentException("offset 必须非负，limit 必须在 1..200 范围内。");
        inventoryPath=AbsolutePath(inventoryPath);
        var inventory=DrawingOmissionValidation.Load(inventoryPath);
        if(pageNumber is { } page && (page<1 || page>inventory.TotalPages)) throw new ArgumentException("pageNumber 超出源图纸页码范围。");
        DrawingOmissionResult? review=null;
        if(drawingContext is not null)
        {
            if(drawingContext.OmissionReview is { } supplied && !string.Equals(AbsolutePath(supplied.InventoryPath),inventoryPath,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("drawingContext 引用了另一份遗漏清单。");
            review=DrawingOmissionValidation.Evaluate(drawingContext);
        }
        var issues=review?.Issues.Where(i=>pageNumber is null || i.PageNumber is null || i.PageNumber==pageNumber).ToArray()??[];
        var unresolvedCandidates=issues.Where(i=>i.CandidateId is not null).Select(i=>i.CandidateId!).ToHashSet(StringComparer.Ordinal);
        var unresolvedRegions=issues.Where(i=>i.RegionId is not null).Select(i=>i.RegionId!).ToHashSet(StringComparer.Ordinal);
        // Global integrity failures must not make the UI hide all candidates as if they were accounted for.
        var canFilter=review is not null && !issues.Any(i=>i.CandidateId is null && i.RegionId is null);
        var candidates=inventory.Candidates.Where(c=>pageNumber is null || c.PageNumber==pageNumber)
            .Where(c=>!onlyUnresolved || !canFilter || unresolvedCandidates.Contains(c.Id)).ToArray();
        var regions=inventory.Regions.Where(r=>pageNumber is null || r.PageNumber==pageNumber)
            .Where(r=>!onlyUnresolved || !canFilter || unresolvedRegions.Contains(r.Id)).ToArray();
        return new {
            inventory_path=inventoryPath,inventory_sha256=DrawingPlanValidation.FileHash(inventoryPath),
            status=review?.Status??"requires_visual_review",full_drawing_equivalence=false,
            scope="仅核对候选项和已声明的复查记录。必须检查原始图像；检测结果和复查声明都可能遗漏特征。",
            source_path=inventory.SourcePath,total_pages=inventory.TotalPages,complete_extraction=inventory.CompleteExtraction,
            limitations=inventory.Limitations,pages=inventory.Pages.Where(p=>pageNumber is null||p.PageNumber==pageNumber).ToArray(),
            total_candidates=candidates.Length,total_regions=regions.Length,total_issues=issues.Length,
            accounted_candidates=review?.AccountedCandidates,reviewed_regions=review?.ReviewedRegions,
            offset,limit,candidates=candidates.Skip(offset).Take(limit).ToArray(),regions=regions.Skip(offset).Take(limit).ToArray(),issues=issues.Skip(offset).Take(limit).ToArray(),
            next_offset=offset+limit<Math.Max(candidates.Length,Math.Max(regions.Length,issues.Length))?(int?)(offset+limit):null
        };
    }
    [McpServerTool(Name="cad_export_drawing",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("将已保存的原生零件或装配体导出为 SLDDRW/PDF；支持配置图幅、第一角／第三角标准视图和分页参数表。重开后检查原生模型／零部件尺寸及每一页参数表。未放置尺寸时明确失败。保留源文件，输出路径必须尚不存在；此导出不认证制造信息完整性。")]
    public static Task<DrawingExportResult> ExportDrawing(IModelingExecutor executor,string inputPath,string nativePath,string pdfPath,string? templatePath=null,DrawingLayoutOptions? layout=null,CancellationToken cancellationToken=default) =>
        executor.ExportDrawingAsync(new(inputPath,nativePath,pdfPath,templatePath,layout),cancellationToken);
    [McpServerTool(Name="cad_list_weldment_profiles",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("查找本地原生 SLDLFP 焊件型材，默认搜索 ProgramData/SOLIDWORKS。创建结构构件前，使用 cad_inspect_model 读取所选型材的可用配置。")]
    public static object WeldmentProfiles(string? directory=null)
    {
        var root=directory is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"SOLIDWORKS") : AbsolutePath(directory);
        var profiles=Directory.Exists(root)?Directory.EnumerateFiles(root,"*.sldlfp",new EnumerationOptions { RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint })
            .OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).Take(1000).Select(p=>new { path=p,name=Path.GetFileNameWithoutExtension(p),library=Path.GetRelativePath(root,Path.GetDirectoryName(p)!) }).ToArray():[];
        return new { directory=root,profiles,maximum_results=1000 };
    }
    [McpServerTool(Name="cad_build_assembly",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("根据已有零部件、明确的毫米位移与欧拉角旋转、几何配合和干涉检查创建本地原生 SolidWorks 装配体。自动在同目录生成同名的 SLDDRW/PDF，采用第一角 A3 视图。装配体和工程图读回及 PDF 导出均通过才成功；工程图失败时保留已保存的装配体并报告失败。即使 overwrite_allowed 为 true，也在建模前拒绝已存在的工程图／PDF 路径。保留源零部件。")]
    public static Task<AssemblyResult> BuildAssembly(IModelingExecutor executor,AssemblyPlan plan,CancellationToken cancellationToken=default)
    {
        AssemblyPlanValidation.Validate(plan);
        return executor.BuildAssemblyAsync(plan,cancellationToken);
    }
    [McpServerTool(Name = "cad_inspect_model", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("读取本地模型的特征、原生尺寸、几何和可选面／边线查询，包括解析球面的半径及球心。可选验证检查圆柱壁、包围盒、原生尺寸，以及剪裁面样本和接缝位置／切平面连续性。接缝检查可显式要求 G2 法曲率采样；不支持或不完整的测量不能通过，有限采样不保证全局连续性。球面检查需要 radius_mm 和 center_mm。对尚未打开的原生文件以只读方式打开；STEP 通过独立工作副本导入。可能启动 SolidWorks。")]
    public static Task<ModelInspection> InspectModel(IModelingExecutor executor,string inputPath,
        EntityQuery[]? queries=null,ModelVerificationSpec? verification=null,CancellationToken cancellationToken=default,
        GeometryRef[]? geometryReferences=null,MeasurementQuery[]? measurements=null,string? documentRevision=null,
        string? sourceRevisionId=null,ConnectivityInspectionQuery[]? connectivityQueries=null,NativeEditabilityProbeSpec[]? editabilityProbes=null)
    {
        var errors=ModelVerification.Validate((verification??new()) with { Bindings=[] },null).ToArray();
        // Standalone inspection reuses expected measurements; source binding was checked during compilation.
        if(errors.Length>0)
            throw new ArgumentException(string.Join(" ",errors.Select(e=>e.Message)));
        return executor.InspectAsync(new(Path.GetFullPath(inputPath),queries,verification,geometryReferences,measurements,
            documentRevision,sourceRevisionId,connectivityQueries,editabilityProbes),cancellationToken);
    }

    [McpServerTool(Name="cad_capture_projection",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("从已保存的原生零件在临时工程图中提取 T09 可见／隐藏投影图元；需要已锁定的源 SHA 和视图坐标系。提取快照用于与源图纸比较，不代表图纸等价性验收通过。")]
    public static Task<ProjectionCaptureResult> CaptureProjection(IModelingExecutor executor,ProjectionCaptureRequest request,CancellationToken cancellationToken=default)=>
        executor.CaptureProjectionAsync(request,cancellationToken);

    [McpServerTool(Name="cad_capture_section",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("按已锁定的剖面定义从已保存的原生零件提取 T10 有界剖面轮廓。不支持或不完整的提取结果不能认证剖面等价性。")]
    public static Task<SectionCaptureResult> CaptureSection(IModelingExecutor executor,SectionCaptureRequest request,CancellationToken cancellationToken=default)=>
        executor.CaptureSectionAsync(request,cancellationToken);

    [McpServerTool(Name = "cad_get_capabilities", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("列出本地零件／装配体建模操作、原生特征类型、单位、格式、工作流程及当前能力限制。")]
    public static object Capabilities() => new
    {
        server_version = typeof(ModelingTools).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        mode = "modeling_only",
        complex_surface_modeling = GordonSurfaceTools.Capability(),
        default_unit = "mm",
        default_language = GeneratedChineseText.Language,
        localization = PluginCapabilityManifest.Current.GetProperty("localization"),
        engineering_reliability = PluginCapabilityManifest.Current.GetProperty("engineering_reliability"),
        design_intent_authoring = PluginCapabilityManifest.Current.GetProperty("design_intent_authoring"),
        offline_modeling_boundaries = PluginCapabilityManifest.Current.GetProperty("offline_modeling_boundaries"),
        semantic_topology = PluginCapabilityManifest.Current.GetProperty("semantic_topology"),
        fill_surface_contract = PluginCapabilityManifest.Current.GetProperty("fill_surface_contract"),
        boundary_surface_contract = PluginCapabilityManifest.Current.GetProperty("boundary_surface_contract"),
        physical_thread_contract = PluginCapabilityManifest.Current.GetProperty("physical_thread_contract"),
        concentric_tree_mobility = PluginCapabilityManifest.Current.GetProperty("concentric_tree_mobility"),
        current_native_acceptance = PluginCapabilityManifest.Current.GetProperty("current_native_acceptance"),
        native_evidence_policy = PluginCapabilityManifest.Current.GetProperty("native_evidence_policy"),
        local_integration = PluginCapabilityManifest.Current.GetProperty("local_integration"),
        text_input = PluginCapabilityManifest.Current.GetProperty("text_input"),
        execution_deadline = PluginCapabilityManifest.Current.GetProperty("execution_deadline"),
        input_formats = new[] { "text", "pdf", "png", "jpg", "jpeg", "tif", "tiff" },
        output_formats = new[] { "sldprt", "sldasm", "step", "stp", "stl", "slddrw", "pdf" },
        drawing_export = PluginCapabilityManifest.Current.GetProperty("drawing_export"),
        schema_version=ModelingIrSchema.CurrentVersion,
        operations = ModelingCapabilityCatalog.Current.Operations,
        native_feature_kinds=Enum.GetNames<NativeFeatureKind>(),
        surface_modeling = PluginCapabilityManifest.Current.GetProperty("surface_modeling"),
        sketch_primitive_kinds=Enum.GetNames<GenericPrimitiveKind>(),
        assembly_mate_kinds=Enum.GetNames<AssemblyMateKind>(),
        source_requirements = new { primary_interpreter="agent_vision",ocr="auxiliary_candidates",drawing_feature_coverage=true,critical_dimension_binding=true,compiled_binding_integrity=true },
        drawing_omission = new { independent_source_inventory=true,raw_overlapping_tiles=true,residual_ink_candidates=true,
            complete_plan_requires_review=true,compile_and_build_recheck=true,declared_cross_view_checks=true,
            full_drawing_equivalence=false,automatic_semantic_recall_certification=false,review_tool="cad_review_drawing_coverage" },
        measured_verification = new[] { "cylinder_diameter_axis_position_axial_extent_count", "interior_vs_exterior_cylinder", "native_dimensions", "model_bounding_box", "saved_native_readback", "trimmed_plane_cylinder_cone_samples", "trimmed_sphere_radius_center_outward_normal", "outward_normal_and_cone_half_angle", "boundary_clearance_samples", "unique_sampled_face_area" },
        local_geometry = PluginCapabilityManifest.Current.GetProperty("local_geometry"),
        entity_selection = new { feature_body_scope="优先生成面，其次受影响面；禁止用范围外的体替代。", failure_diagnostics="候选／匹配数量、选择范围及最多五个最近候选点。", sphere_radius_filter=true },
        modeling_upgrade = PluginCapabilityManifest.Current.GetProperty("modeling_upgrade"),
        complex_modeling = PluginCapabilityManifest.Current.GetProperty("complex_modeling"),
        local_recovery = new { enabled_by_default=true,mode="verified_prefix_and_suffix_replay",checkpoint_file_hash=true,source_requirements_preserved=true },
        natural_language_examples = ModelingCapabilityCatalog.Current.NaturalLanguageExamples.Skip(1).ToArray(),
        workflow = "提供图纸时先读取图纸，再创建类型化模型计划，随后建模并保存。",
        limitations = ModelingCapabilityCatalog.Current.Limitations
    };

    [McpServerTool(Name = "cad_read_drawing", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("读取本地工程图片或 PDF，生成页面图像、文本、图元观测，以及包含重叠原始复查裁剪图的独立遗漏清单。创建完整图纸计划前，使用 cad_review_drawing_coverage 核对候选项和未解释笔画。此操作不启动 SolidWorks。")]
    public static Task<DrawingIngestionResponse> ReadDrawing(
        IEngineeringDrawingIngestionService service,
        [Description("图片或 PDF 的绝对路径。")] string inputPath,
        [Description("提取页面图像的目录，默认为 LocalAppData/AutoSolidWorks/drawings。")] string? artifactRoot = null,
        int[]? pageNumbers = null, int renderDpi = 300,
        [Description("可选的已解释视图／注释区域，使用页面归一化 [0,1] 坐标。侧向尺寸文字应设置 OCR 旋转角；区域标签描述几何，不能由 OCR 文本直接推断。")] DrawingViewRegionHint[]? viewHints = null,
        CancellationToken cancellationToken = default) => service.IngestAsync(new()
        {
            InputPath = Path.GetFullPath(inputPath),
            ArtifactRoot = Path.GetFullPath(artifactRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoSolidWorks", "drawings")),
            PageNumbers = pageNumbers ?? [],
            ViewHints = viewHints ?? [],
            RenderDpi = renderDpi,
            PreprocessProfile = "engineering-default-v1",
            ProviderProfile = "offline-default-v1"
        }, cancellationToken);

    [McpServerTool(Name = "cad_create_model_plan", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("将类型化几何草案或受支持的简单尺寸描述编译成模型计划。draft 可声明 design_intent：方程、全局变量、配置变量、固定函数、尺寸引用及指定配置驱动尺寸；此版本该创作适配器仅编译及离线测试，原生验收未执行。尺寸默认毫米；draft 与 text 必须且只能提供一个。返回供 cad_build_model 使用的 ir_json。名称默认中文；显式名称保留。")]
    public static object CreatePlan(
        GenericPlanCompiler compiler, ModelingWorkflow workflow,
        [Description(".SLDPRT 输出文件的绝对路径。")] string nativeOutputPath,
        GenericModelDraft? draft = null, string? text = null,
        [Description("可选的 STEP/STP/STL 导出绝对路径。")] string[]? exportPaths = null,
        bool overwriteAllowed = false)
    {
        if ((draft is null) == string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("draft 与 text 必须且只能提供一个。");
        var output = AbsolutePath(nativeOutputPath);
        var exports = exportPaths?.Select(AbsolutePath).ToArray();
        var compilation = draft is not null
            ? compiler.Compile(draft, output, exports, overwriteAllowed)
            : workflow.Compile(text!, output);
        if (draft is null && compilation.Plan is not null)
        {
            compilation = compilation with { Plan = compilation.Plan with {
                Output = compilation.Plan.Output with {
                    ExportPaths = exports ?? [], OverwriteAllowed = overwriteAllowed
                }
            }};
        }
        return new {
            success = compilation.Success,
            drawing_review_status = draft?.DrawingContext is null ? "not_applicable" : !compilation.Success ? "incomplete" :
                draft.DrawingContext.OmissionReview is null ? "partial_unreviewed" : "declared_review_complete",
            full_drawing_equivalence = false,
            diagnostics = compilation.Diagnostics,
            plan = compilation.Plan,
            ir_json = compilation.Plan is null ? null : ModelingIrJson.Serialize(compilation.Plan)
        };
    }

    [McpServerTool(Name = "cad_build_model", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("在 SolidWorks 中执行已编译计划并保存输出，默认立即建模。dryRun 可选，仅检查几何计划，不连接 SolidWorks。")]
    public static Task<ExecutionResult> BuildModel(ModelingWorkflow workflow, ModelingIrValidator validator,
        [Description("cad_create_model_plan 返回的 ir_json。")] string irJson,
        bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var plan = ModelingIrJson.Deserialize(irJson);
        if (!dryRun) return workflow.ExecuteAsync(plan, false, cancellationToken);
        var validation = validator.Validate(plan, forExecution: true);
        return Task.FromResult(new ExecutionResult(validation.IsValid,
            validation.IsValid ? "dry_run_passed" : "invalid_model",
            validation.IsValid ? $"已检查{plan.Operations.Count}项建模操作；未创建 SolidWorks 文档。" : "请修正无效的模型参数。",
            validation.Diagnostics.Select(d => new ExecutionEvidence("model_parameters", d.Message,
                d.Severity != DiagnosticSeverity.Error, Code: d.Code, SuggestedAction: d.SuggestedAction)).ToArray()));
    }

    [McpServerTool(Name = "cad_executor_health", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("启动或检查本地 SolidWorks 执行器及连接；仅用于建模或故障排查，可能启动 SolidWorks。")]
    public static Task<ExecutorHealth> Health(ModelingWorkflow workflow, CancellationToken cancellationToken) =>
        workflow.HealthAsync(cancellationToken);

    [McpServerTool(Name="cad_verify_revolved_family",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("将 T17 有限旋转类轮廓与实际已保存并重开的原生模型核对。检查绑定独立地将每个源台阶事实映射到必要的原生直径／轴向长度尺寸；探测请求不能自行定义必要尺寸集合。T08/T09 输入必须由对应生成器产生。")]
    public static Task<Stage4SavedModelAcceptance<RevolvedFamilyObservation>> VerifyRevolvedFamily(
        Stage4SavedModelAcceptanceWorkflow workflow,string nativePath,RevolvedFamilyProfile profile,RevolvedFamilyInspectionBinding binding,Stage4EvidenceBundle evidence,CancellationToken cancellationToken=default)=>
        workflow.VerifyRevolvedAsync(AbsolutePath(nativePath),profile,binding,evidence,cancellationToken);

    [McpServerTool(Name="cad_verify_hole_group",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("将 T18 有限孔组与实际已保存并重开的原生模型核对，逐一匹配测得的孔实例与原生阵列。解析锥形沉孔需要完整剪裁圆锥边界；装饰螺纹需要原生规格、尺寸、深度／模式和入口绑定。缺失或有歧义的采集结果保持不可验证。需要逐孔 T08 和已锁定的 T09 证据；不认证实体螺旋螺纹。")]
    public static Task<Stage4SavedModelAcceptance<HoleGroupObservation>> VerifyHoleGroup(
        Stage4SavedModelAcceptanceWorkflow workflow,string nativePath,HoleGroupProfile profile,HoleGroupInspectionBinding binding,Stage4EvidenceBundle evidence,CancellationToken cancellationToken=default)=>
        workflow.VerifyHoleGroupAsync(AbsolutePath(nativePath),profile,binding,evidence,cancellationToken);

    [McpServerTool(Name="cad_verify_edge_treatment",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("将 T19 轮廓圆弧／实体圆角／倒角与实际已保存并重开的原生模型核对。读取命名原生尺寸和 T06 几何引用；修复候选必须提供完整 T14 尝试／执行／覆盖／检查包，以及由最终验收重新计算的 T13 差异。")]
    public static Task<Stage4SavedModelAcceptance<EdgeTreatmentObservation>> VerifyEdgeTreatment(
        Stage4SavedModelAcceptanceWorkflow workflow,string nativePath,EdgeTreatmentIntent intent,EdgeTreatmentInspectionBinding binding,Stage4RepairEvidenceBundle? repair=null,CancellationToken cancellationToken=default)=>
        workflow.VerifyEdgeTreatmentAsync(AbsolutePath(nativePath),intent,binding,repair,cancellationToken);

    private static string AbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("请使用绝对输出路径。");
        return Path.GetFullPath(path);
    }
}
