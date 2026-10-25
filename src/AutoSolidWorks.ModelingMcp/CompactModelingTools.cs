using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using CadModeling.Core;
using CadModeling.Ir;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Ingestion;
using CadModeling.Drawing.Providers.Abstractions;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class CompactModelingTools
{
    [McpServerTool(Name="cad_parse_source_callout",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("拆分AI已从原图抄录的整条标注，返回数量/孔径/深度/沉孔/锪孔/螺纹/PCD和公差原文跨度及未解析尾部。不是OCR，不猜漏字，不自动绑定或标记已审。用组件键填 annotation_ledger；Complete仅代表有限语法覆盖，不认证制造语义。")]
    public static object ParseSourceCallout(string sourceLiteral, DrawingValueUnit lengthUnit = DrawingValueUnit.Millimeter) => HoleCalloutParser.Parse(sourceLiteral, lengthUnit);
    [McpServerTool(Name="cad_store_model_input",ReadOnly=false,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("完整保存 drawing_context 或 draft，返回不可变 input_id；修正时使用 baseInputId 与 edits（add/replace/remove/test、JSON Pointer）仅发送增量。input 与 baseInputId 二选一。严格校验字段并绑定来源／恢复身份，保存不表示图纸复查或编译通过，不启动 SolidWorks。句柄可用于编译、遗漏复查及 cad_read_result。")]
    public static object StoreModelInput(CompactArtifactStore artifacts,string kind,JsonElement? input=null,string? baseInputId=null,JsonElement? edits=null) =>
        CompactModelInputs.Store(artifacts,kind,input,baseInputId,edits);
    private static readonly JsonSerializerOptions InputOptions = new(ModelingIrJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static T? Optional<T>(JsonElement? value) where T : class => value is null || value.Value.ValueKind == JsonValueKind.Null
        ? null : value.Value.Deserialize<T>(InputOptions) ?? throw new JsonException("结构化参数为空。");
    private static T Required<T>(JsonElement value) where T : class => Optional<T>(value) ?? throw new JsonException("必须提供结构化参数。");

    [McpServerTool(Name="cad_execution_status",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("按 requestId 查询执行状态；返回小型状态与完整回执句柄。不启动 SolidWorks。pending/outcome_unknown 时不得重放修改；详细回执用 cad_read_result 读取。")]
    public static async Task<ExecutionStatusSummary> ExecutionStatus(IModelingExecutor executor, CompactArtifactStore artifacts, string requestId, CancellationToken cancellationToken=default) =>
        CompactResponses.Status(artifacts, await ModelingTools.ExecutionStatus(executor,requestId,cancellationToken));

    [McpServerTool(Name="cad_pause_execution",ReadOnly=false,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("按 requestId 请求下一安全边界暂停，返回状态摘要与回执句柄。同步 COM 可能仍在运行，后续查询状态。")]
    public static async Task<ExecutionStatusSummary> PauseExecution(IModelingExecutor executor, CompactArtifactStore artifacts, string requestId, CancellationToken cancellationToken=default) =>
        CompactResponses.Status(artifacts, await ModelingTools.PauseExecution(executor,requestId,cancellationToken));

    [McpServerTool(Name="cad_get_contract",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("优先读取小合同 sketch_frame、primitive、dimension_fact、placement_intent；section 可用 JSON Pointer 截取自包含合同（保留可达引用）。不熟悉入口先用 cad_get_binding_guide，避免重复读取巨大 draft。省略 section 保持完整合同兼容。contract 支持 draft、inspection、drawing_context、shape_decomposition、dimension_expression、geometry_anchor、source_projection、assembly、drawing_layout。shape_decomposition 用于逐视图轮廓、材料/空缺与轴向周向分层。draft 数值字段另支持 {$dimension:尺寸ID,unit:单位枚举}；派生 expression 由服务器求值。此工具不启动 SolidWorks。")]
    public static JsonNode GetContract(string contract, string section="") => FocusedContract.Select(ModelingIrJson.Options.GetJsonSchemaAsNode(contract switch {
        "operation" => typeof(GenericOperationDraft), "primitive" => typeof(GenericPrimitiveDraft),
        "sketch_frame" => typeof(SketchFrame), "dimension_fact" => typeof(DrawingDimensionFact),
        "binding_sheet" => typeof(BindingSheet), "binding_batch" => typeof(BindingBatch), "binding_crops" => typeof(BindingCrop[]),
        "draft" => typeof(GenericModelDraft), "inspection" => typeof(ModelInspectionRequest),
        "annotation_ledger" => typeof(DrawingAnnotationLedger), "drawing_context" => typeof(DrawingPlanContext), "shape_decomposition" => typeof(DrawingShapeDecomposition), "dimension_expression" => typeof(DimensionExpression),
        "geometry_anchor" => typeof(DrawingGeometryAnchor), "assembly" => typeof(AssemblyPlan),
        "driven_profile" => typeof(DrivenProfileSpec), "placement_intent" => typeof(ExtrusionPlacementIntent), "visual_review" => typeof(DeclaredVisualReview),
        "source_projection" => typeof(SourceProjectionComparisonRequest),
        "drawing_layout" => typeof(DrawingLayoutOptions), _ => throw new ArgumentException("未知合同名称。") }), section);

    [McpServerTool(Name="cad_read_result",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("分页读取本地计划／完整结果句柄。section 使用 JSON Pointer（如 /evidence、/verification/checks、/pages/0/observation/observations）；省略时列顶层字段及容器入口。每页 1..200 项，内容字符预算 24000；字符串按字符偏移分块。按 next_offset 续读。校验哈希，不启动 SolidWorks。")]
    public static object ReadResult(CompactArtifactStore artifacts,string artifactId,string section="",int offset=0,int limit=50) => artifacts.Read(artifactId,section,offset,limit);

    [McpServerTool(Name="cad_create_model_plan",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("编译 draft、draftId 或简单 text（三者择一）；drawingContextId 复用图纸上下文。默认返回 plan_id 与摘要，不回传完整 IR。失败时自动生成来源错误局部叠图和完整诊断句柄；workspaceId可复用识图工作区，readiness集中列来源缺口与修复项，先复查原图再集中询问仍阻塞的关键事实；完整清单用结果句柄分页读取。原尺寸绑定与验收门禁保留。responseDetail=full 兼容完整返回。名称默认中文、单位毫米。")]
    public static object CreatePlan(GenericPlanCompiler compiler,ModelingWorkflow workflow,CompactArtifactStore artifacts,
        string nativeOutputPath,JsonElement? draft=null,string? text=null,string[]? exportPaths=null,bool overwriteAllowed=false,string responseDetail="summary",
        string? draftId=null,string? drawingContextId=null,string? workspaceId=null,string? reviewOutputDirectory=null)
    {
        CompactResponses.ValidateDetail(responseDetail);
        if ((draft is null ? 0 : 1) + (text is null ? 0 : 1) + (draftId is null ? 0 : 1) != 1)
            throw new ArgumentException("draft、text、draftId 必须且只能提供一个。");
        var draftNode = draftId is not null ? artifacts.LoadInput(draftId,"draft")
            : draft is { } rawDraft ? JsonNode.Parse(rawDraft.GetRawText()) : null;
        if (drawingContextId is not null)
        {
            if (draftNode is not JsonObject || draftNode["drawing_context"] is not null)
                throw new ArgumentException("drawingContextId 仅用于尚未内嵌 drawing_context 的草案。");
            draftNode["drawing_context"] = artifacts.LoadInput(drawingContextId,"context");
        }
        var prepared = draftNode is null ? null : SourceWorkflowInputs.Prepare(draftNode);
        var modelDraft = prepared is null ? null : CompactModelInputs.Decode<GenericModelDraft>(prepared.Input);
        if (workspaceId is not null)
        {
            var workspace = SourceBindingTools.Workspace(artifacts,workspaceId);
            SourceBindingTools.Match(modelDraft?.DrawingContext ?? throw new ArgumentException("workspaceId requires drawing_context."),workspace);
        }
        var compiled=ModelingTools.CreatePlan(compiler,workflow,nativeOutputPath,modelDraft,text,exportPaths,overwriteAllowed);
        var node=JsonSerializer.SerializeToNode(compiled,ModelingIrJson.Options)!.AsObject();
        if (prepared is not null) node["source_workflow"] = prepared.Adoption.DeepClone();
        if(prepared is not null && modelDraft?.DrawingContext is not null)
        {
            try
            {
                var inputReceipt=artifacts.SaveInput("draft",prepared.Input,modelDraft.SourceModelPath,modelDraft.DrawingContext,modelDraft.Recovery);
                node["prepared_draft_id"]=inputReceipt.ArtifactId;
                node["prepared_draft_sha256"]=inputReceipt.Sha256;
            }
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
            { node["prepared_draft_storage_error"]=error.Message; } // Keep compilation diagnostics even for missing/invalid source paths.
        }
        JsonObject? readiness=null;
        if(modelDraft?.DrawingContext is not null || node["success"]?.GetValue<bool>()!=true)
        {
            readiness=PlanningReadiness.Collect(modelDraft,node);
            node["readiness"]=readiness;
        }
        ArtifactReceipt? plan=null;
        if(node["success"]!.GetValue<bool>() && node["ir_json"] is { } ir)
            plan=artifacts.SavePlan(ModelingIrJson.Deserialize(ir.GetValue<string>()));
        var result=artifacts.SaveResult(node);
        if(responseDetail=="summary") { node.Remove("plan"); node.Remove("ir_json"); node=CompactResponses.Summarize(node); }
        if(readiness is not null && responseDetail=="summary") node["readiness"]=PlanningReadiness.Summarize(readiness);
        if(prepared is not null && responseDetail=="summary") node["source_workflow"]=SourceWorkflowInputs.Summarize(prepared.Adoption);
        node["plan_id"]=plan?.ArtifactId; node["plan_sha256"]=plan?.Sha256; node["plan_path"]=plan?.Path;
        node["artifact_id"]=result.ArtifactId; node["artifact_sha256"]=result.Sha256; node["artifact_path"]=result.Path;
        node["response_detail"]=responseDetail;
        if(plan is not null) { var model=artifacts.LoadPlan(plan.ArtifactId); node["operation_count"]=model.Operations.Count; node["native_output_path"]=model.Output.NativePath; }
        if (modelDraft?.DrawingContext is not null && node["success"]?.GetValue<bool>() != true)
        {
            try { SourceBindingTools.AttachCompileReview(artifacts,node,modelDraft,workspaceId,reviewOutputDirectory); }
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
            { node["binding_review_error"] = error.Message; } // Never hide original compiler errors.
        }
        return node;
    }

    [McpServerTool(Name="cad_build_model",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("执行 planId 或旧版 irJson（二者择一）。background=true 返回 request_id，随后用 cad_execution_status 查询；同一时间只允许一项原生任务，pending/outcome_unknown 不得重放。可在执行期间分析后续特征。四步流程编译通过即执行并保存，无需独立交付审核。显式旧版阶段计划仍保留原有语义。planId 校验内容、编译器和来源／恢复身份。dryRun=true 仅同步检查计划，不连接 SolidWorks。")]
    public static async Task<object> BuildModel(ModelingWorkflow workflow,ModelingIrValidator validator,CompactArtifactStore artifacts,
        string? irJson=null,string? planId=null,bool dryRun=false,string responseDetail="summary",CancellationToken cancellationToken=default,bool background=false)
    {
        CompactResponses.ValidateDetail(responseDetail);
        if(string.IsNullOrWhiteSpace(irJson)==string.IsNullOrWhiteSpace(planId)) throw new ArgumentException("planId 与 irJson 必须且只能提供一个。");
        var ir=string.IsNullOrWhiteSpace(planId) ? irJson! : ModelingIrJson.Serialize(artifacts.LoadPlan(planId),false);
        if(background)
        {
            if(dryRun) throw new ArgumentException("background 只用于原生执行；dryRun 使用同步检查。");
            var plan=ModelingIrJson.Deserialize(ir);
            var requestId=Guid.NewGuid().ToString("N");
            // A failed local journal write prevents dispatch; a failed result write never retries mutation.
            artifacts.SaveResult(new{request_id=requestId,plan_id=planId,plan_fingerprint=ModelingPlanIdentity.Fingerprint(plan),
                native_output_path=plan.Output.NativePath,state="dispatch_intent",background=true});
            var started=await workflow.StartExecutionAsync(plan,requestId,cancellationToken);
            return CompactResponses.Status(artifacts,started);
        }
        return CompactResponses.Present(artifacts,await ModelingTools.BuildModel(workflow,validator,ir,dryRun,cancellationToken),responseDetail);
    }

    [McpServerTool(Name="cad_read_drawing",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("四步流程的识图入口：准备原图、必要局部图和尺寸绑定用的像素/几何证据，不运行OCR。AI读图后直接填写视图、特征、尺寸和实际参数绑定。默认prepareReview=true返回source-four-stage-2上下文及原像素网格工作区；bindingCrops可一并指定局部图，prepareBindingWorkspace=false可关闭图像准备；不要求候选核销、双遍账本、形状分区探针或独立交付审核。不启动SolidWorks。")]
    public static async Task<object> ReadDrawing(IEngineeringDrawingIngestionService service,CompactArtifactStore artifacts,string inputPath,
        string? artifactRoot=null,int[]? pageNumbers=null,int renderDpi=300,JsonElement? viewHints=null,string responseDetail="summary",CancellationToken cancellationToken=default,
        bool prepareReview=true,bool prepareBindingWorkspace=true,JsonElement? bindingCrops=null)
    {
        CompactResponses.ValidateDetail(responseDetail);
        var result = await ModelingTools.ReadDrawing(service,inputPath,artifactRoot,pageNumbers,renderDpi,Optional<DrawingViewRegionHint[]>(viewHints),cancellationToken);
        var presented = CompactResponses.Present(artifacts,result,responseDetail);
        if (!prepareReview) return presented;
        var node = JsonSerializer.SerializeToNode(presented,ModelingIrJson.Options)!.AsObject();
        if (result.Outcome == IngestionOutcome.Rejected || result.OmissionInventoryPath is null || result.OmissionInventorySha256 is null)
        {
            node["review_workspace_status"] = "not_prepared";
            return node;
        }
        try
        {
            node["review_workspace"] = JsonSerializer.SerializeToNode(PendingDrawingReview.Prepare(artifacts,
                result.OmissionInventoryPath,result.OmissionInventorySha256,result.SourceFactsRevisionId,
                result.SourceManifest?.SourcePath,result.SourceManifest?.SourceSha256),ModelingIrJson.Options);
            node["review_workspace_status"] = "pending_recognition_and_binding";
            node["authoring_guide"] = JsonSerializer.SerializeToNode(SourceBindingTools.QuickStart(),ModelingIrJson.Options);
            if (prepareBindingWorkspace)
            {
                try { node["binding_workspace"] = JsonSerializer.SerializeToNode(SourceBindingTools.Prepare(artifacts,
                    result.OmissionInventoryPath,result.OmissionInventorySha256,
                    Path.Combine(Path.GetDirectoryName(result.OmissionInventoryPath)!,"binding-"+Guid.NewGuid().ToString("N")),bindingCrops),ModelingIrJson.Options); }
                catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
                { node["binding_workspace_error"] = error.Message; }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        {
            // Retain ingestion's original outcome/receipt; no silent semantic acceptance or native replay.
            node["review_workspace_status"] = "blocked";
            node["review_workspace_error"] = "待审工作清单未建立：" + error.Message;
        }
        return node;
    }

    [McpServerTool(Name="cad_inspect_model",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("检查本地模型与可选几何／尺寸要求，默认返回几何、完整性／重开状态、失败检查及详细结果句柄；面边、尺寸、特征用 cad_read_result 分页读取。参数遵循 inspection 合同，可按需 cad_get_contract。可能启动 SolidWorks。responseDetail=full 保留完整结果。")]
    public static async Task<object> InspectModel(IModelingExecutor executor,CompactArtifactStore artifacts,string inputPath,
        JsonElement? queries=null,JsonElement? verification=null,JsonElement? geometryReferences=null,JsonElement? measurements=null,
        string? documentRevision=null,string? sourceRevisionId=null,JsonElement? connectivityQueries=null,JsonElement? editabilityProbes=null,
        string responseDetail="summary",CancellationToken cancellationToken=default,bool requireEditableSketches=false)
    {
        CompactResponses.ValidateDetail(responseDetail);
        return CompactResponses.Present(artifacts,await ModelingTools.InspectModel(executor,inputPath,Optional<EntityQuery[]>(queries),Optional<ModelVerificationSpec>(verification),cancellationToken,
            Optional<GeometryRef[]>(geometryReferences),Optional<MeasurementQuery[]>(measurements),documentRevision,sourceRevisionId,
            Optional<ConnectivityInspectionQuery[]>(connectivityQueries),Optional<NativeEditabilityProbeSpec[]>(editabilityProbes),requireEditableSketches),responseDetail);
    }

    [McpServerTool(Name="cad_review_drawing_coverage",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("核对源遗漏清单与 drawingContext，按页及 offset/limit 读取未解决候选／区域／问题。候选核对不认证完整图纸理解；必须看原图。")]
    public static object ReviewDrawingCoverage(CompactArtifactStore artifacts,string inventoryPath,JsonElement? drawingContext=null,int? pageNumber=null,int offset=0,int limit=50,bool onlyUnresolved=true,string? drawingContextId=null)
    {
        if (drawingContext is not null && drawingContextId is not null) throw new ArgumentException("drawingContext 与 drawingContextId 不能同时提供。");
        var context = drawingContextId is null ? Optional<DrawingPlanContext>(drawingContext)
            : CompactModelInputs.Decode<DrawingPlanContext>(artifacts.LoadInput(drawingContextId,"context"));
        return ModelingTools.ReviewDrawingCoverage(inventoryPath,context,pageNumber,offset,limit,onlyUnresolved);
    }

    [McpServerTool(Name="cad_build_assembly",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("按 assembly 合同创建原生装配体及第一角 SLDDRW/PDF；保留源组件，装配与工程图均通过才成功。默认摘要和完整结果句柄，responseDetail=full 返回完整结果。")]
    public static async Task<object> BuildAssembly(IModelingExecutor executor,CompactArtifactStore artifacts,JsonElement plan,string responseDetail="summary",CancellationToken cancellationToken=default)
    { CompactResponses.ValidateDetail(responseDetail); return CompactResponses.Present(artifacts,await ModelingTools.BuildAssembly(executor,Required<AssemblyPlan>(plan),cancellationToken),responseDetail); }

    [McpServerTool(Name="cad_export_drawing",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("将已保存模型导出到未使用的 SLDDRW/PDF 路径，保存重开并核对尺寸／参数表；没有尺寸明确失败。默认摘要和结果句柄，responseDetail=full 返回完整结果。")]
    public static async Task<object> ExportDrawing(IModelingExecutor executor,CompactArtifactStore artifacts,string inputPath,string nativePath,string pdfPath,
        string? templatePath=null,JsonElement? layout=null,string responseDetail="summary",CancellationToken cancellationToken=default)
    { CompactResponses.ValidateDetail(responseDetail); return CompactResponses.Present(artifacts,await ModelingTools.ExportDrawing(executor,inputPath,nativePath,pdfPath,templatePath,Optional<DrawingLayoutOptions>(layout),cancellationToken),responseDetail); }

    [McpServerTool(Name="cad_get_capabilities",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("读取当前版本、操作类型、工具配置和能力限制；默认摘要与完整能力结果句柄，responseDetail=full 返回详细历史／当前验收边界。不启动 SolidWorks。")]
    public static object Capabilities(CompactArtifactStore artifacts,string responseDetail="summary")
    {
        CompactResponses.ValidateDetail(responseDetail);
        var complete=JsonSerializer.SerializeToNode(ModelingTools.Capabilities(),ModelingIrJson.Options)!.AsObject();
        complete["binding_assist"]=JsonSerializer.SerializeToNode(new {revision=BindingWorkspaceImages.Revision,
            sheet_revision=BindingSheetAssembler.Revision,atomic_sheet_assembly=true,concise_authoring_guide=true,focused_contracts=true,automatic_binding_workspace=true,automatic_compile_error_images=true,
            circular_projection_revision=DrawingCircularProjectionValidation.Revision,explicit_outside_arrow_tips=true,bounded_source_line_extension=true,physical_measurement_identity_checks=true,coordinate_workspace=true,batch_source_binding=true,source_error_overlays=true,incremental_detail_crops=true,detail_crop_batch_limit=12,automatic_visual_review=false,
            existing_binding_and_native_checks_preserved=true,default_workflow=SourceWorkflow.DefaultRevision,end_to_end_speedup="not_established",reference="skills/auto-solidworks/references/binding-assist.md"},ModelingIrJson.Options);
        complete["source_geometry_proposals"]=JsonSerializer.SerializeToNode(new {revision=InkLineDetector.Revision,tool="cad_find_source_geometry",all_angle_straight_lines=true,
            selection_tool="cad_bind_source_geometry",selection_revision=SourceGeometryBindingTools.Revision,circle_tool="cad_find_source_circles",circle_revision=InkCircleDetector.Revision,source_supported_circular_arcs=true,fragmented_circle_proposals=true,circle_engine="OpenCV 4.13.0 EdgeDrawing/EDCircles + source-ink fitting",
            uses_source_ink_mask=true,semantic_review_automatic=false,line_coordinates_only=true,general_accuracy_or_speed_leadership="not_established"},ModelingIrJson.Options);
        complete["tool_profile"]=Environment.GetEnvironmentVariable("AUTO_SOLIDWORKS_TOOL_PROFILE") ?? "compact";
        var receipt=artifacts.SaveResult(complete);
        if(responseDetail=="full") return complete;
        var summary=CompactResponses.Summarize(complete);
        foreach(var key in new[]{"operations","native_feature_kinds","input_formats","output_formats","limitations","report_issue_hardening","integrated_workflow","mini_workflow","current_native_evidence","source_requirements","iterative_delivery","source_workflow","binding_assist","source_geometry_proposals"}) summary[key]=complete[key]?.DeepClone();
        summary["artifact_id"]=receipt.ArtifactId; summary["artifact_sha256"]=receipt.Sha256; summary["artifact_path"]=receipt.Path;
        summary["advanced_tools"]=new JsonArray("cad_capture_projection","cad_capture_section","cad_verify_revolved_family","cad_verify_hole_group","cad_verify_edge_treatment");
        summary["advanced_profile_instruction"]="设置 AUTO_SOLIDWORKS_TOOL_PROFILE=full 后启动新 MCP 会话，保留全部专用验收工具。";
        return summary;
    }

    [McpServerTool(Name="cad_list_weldment_profiles",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("查询本机 SLDLFP 焊件型材；原生配置需 cad_inspect_model 检查。")]
    public static object WeldmentProfiles(string? directory=null) => ModelingTools.WeldmentProfiles(directory);

    [McpServerTool(Name="cad_executor_health",ReadOnly=false,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("诊断本地执行器和 SolidWorks 连接，可能启动 SolidWorks；不作为每次建模前置步骤。")]
    public static Task<ExecutorHealth> Health(ModelingWorkflow workflow,CancellationToken cancellationToken=default) => ModelingTools.Health(workflow,cancellationToken);
}
