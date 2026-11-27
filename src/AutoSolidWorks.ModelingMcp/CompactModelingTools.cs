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
    private static readonly JsonSerializerOptions InputOptions = new(ModelingIrJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static T? Optional<T>(JsonElement? value) where T : class => value is null || value.Value.ValueKind == JsonValueKind.Null
        ? null : value.Value.Deserialize<T>(InputOptions) ?? throw new JsonException("结构化参数为空。");
    private static T Required<T>(JsonElement value) where T : class => Optional<T>(value) ?? throw new JsonException("必须提供结构化参数。");

    [McpServerTool(Name="cad_execution_status",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("按 requestId 查询原生任务状态。pending 或 outcome_unknown 时继续查同一请求。")]
    public static async Task<ExecutionStatusSummary> ExecutionStatus(IModelingExecutor executor, CompactArtifactStore artifacts, string requestId, CancellationToken cancellationToken=default) =>
        CompactResponses.Status(artifacts, await ModelingTools.ExecutionStatus(executor,requestId,cancellationToken));

    [McpServerTool(Name="cad_get_contract",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("按需读取建模合同。常用 draft、assembly、inspection、primitive、dimension_fact、placement_intent。可用 section 指定 JSON Pointer 片段。")]
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
    [Description("按 artifactId 分页读取结果。section 支持 JSON Pointer。")]
    public static object ReadResult(CompactArtifactStore artifacts,string artifactId,string section="",int offset=0,int limit=50) => artifacts.Read(artifactId,section,offset,limit);

    [McpServerTool(Name="cad_create_model_plan",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("编译尺寸文本或结构化草案，返回 plan_id。图纸草案可传 drawingContextId 和 workspaceId。nativeOutputPath 指定 SLDPRT，exportPaths 可另存 STEP 或 STL。")]
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
    [Description("按 planId 生成并保存零件。dryRun 仅校验。background 返回 request_id，后续用 cad_execution_status 查询。")]
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
    [Description("准备图片或 PDF 的原图、局部图及尺寸证据。返回工作区和草案填写提示。")]
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
    [Description("检查本地模型或定位装配配合面、边和基准。可用 cad_read_result 读取完整结果。")]
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

    [McpServerTool(Name="cad_build_assembly",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("按 assembly 合同插入本地零件和子装配体、添加配合、检查干涉并保存 SLDASM。")]
    public static async Task<object> BuildAssembly(IModelingExecutor executor,CompactArtifactStore artifacts,JsonElement plan,string responseDetail="summary",CancellationToken cancellationToken=default)
    {
        CompactResponses.ValidateDetail(responseDetail);
        // The Codex tool bridge can serialize an untyped JSON argument as a string.
        using var document = plan.ValueKind == JsonValueKind.String ? JsonDocument.Parse(plan.GetString()!) : null;
        var assembly = Required<AssemblyPlan>(document?.RootElement ?? plan);
        var result = await ModelingTools.BuildAssembly(executor, assembly, cancellationToken);
        return CompactResponses.Present(artifacts, result, responseDetail);
    }
}
