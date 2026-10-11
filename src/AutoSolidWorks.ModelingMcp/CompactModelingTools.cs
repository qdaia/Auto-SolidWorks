using System.ComponentModel;
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
    [Description("按需读取完整 JSON 合同。contract 支持 draft、inspection、drawing_context、assembly、drawing_layout。常用几何字段也见技能参考文件；此工具不启动 SolidWorks。")]
    public static JsonNode GetContract(string contract) => ModelingIrJson.Options.GetJsonSchemaAsNode(contract switch {
        "draft" => typeof(GenericModelDraft), "inspection" => typeof(ModelInspectionRequest),
        "drawing_context" => typeof(DrawingPlanContext), "assembly" => typeof(AssemblyPlan),
        "drawing_layout" => typeof(DrawingLayoutOptions), _ => throw new ArgumentException("未知合同名称。") });

    [McpServerTool(Name="cad_read_result",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("分页读取本地计划／完整结果句柄。section 使用 JSON Pointer（如 /evidence、/verification/checks、/pages/0/observation/observations）；省略时列顶层字段及容器入口。每页 1..200 项，内容字符预算 24000；字符串按字符偏移分块。按 next_offset 续读。校验哈希，不启动 SolidWorks。")]
    public static object ReadResult(CompactArtifactStore artifacts,string artifactId,string section="",int offset=0,int limit=50) => artifacts.Read(artifactId,section,offset,limit);

    [McpServerTool(Name="cad_create_model_plan",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("编译 draft、draftId 或受支持的简单 text（三者择一）；drawingContextId 可引用独立保存的图纸上下文，默认保存本地不可变计划并返回 plan_id、摘要与诊断，不回传完整 IR。draft 使用现有类型化合同（cad_get_contract 或技能参考）。responseDetail=full 兼容返回 plan/ir_json。名称默认中文，单位默认毫米。")]
    public static object CreatePlan(GenericPlanCompiler compiler,ModelingWorkflow workflow,CompactArtifactStore artifacts,
        string nativeOutputPath,JsonElement? draft=null,string? text=null,string[]? exportPaths=null,bool overwriteAllowed=false,string responseDetail="summary",
        string? draftId=null,string? drawingContextId=null)
    {
        CompactResponses.ValidateDetail(responseDetail);
        if ((draft is null ? 0 : 1) + (text is null ? 0 : 1) + (draftId is null ? 0 : 1) != 1)
            throw new ArgumentException("draft、text、draftId 必须且只能提供一个。");
        var modelDraft = draftId is null ? Optional<GenericModelDraft>(draft)
            : CompactModelInputs.Decode<GenericModelDraft>(artifacts.LoadInput(draftId,"draft"));
        if (drawingContextId is not null)
        {
            if (modelDraft is null || modelDraft.DrawingContext is not null)
                throw new ArgumentException("drawingContextId 仅用于尚未内嵌 drawing_context 的草案。");
            modelDraft = modelDraft with { DrawingContext = CompactModelInputs.Decode<DrawingPlanContext>(artifacts.LoadInput(drawingContextId,"context")) };
        }
        var compiled=ModelingTools.CreatePlan(compiler,workflow,nativeOutputPath,modelDraft,text,exportPaths,overwriteAllowed);
        var node=JsonSerializer.SerializeToNode(compiled,ModelingIrJson.Options)!.AsObject();
        ArtifactReceipt? plan=null;
        if(node["success"]!.GetValue<bool>() && node["ir_json"] is { } ir)
            plan=artifacts.SavePlan(ModelingIrJson.Deserialize(ir.GetValue<string>()));
        var result=artifacts.SaveResult(compiled);
        if(responseDetail=="summary") { node.Remove("plan"); node.Remove("ir_json"); node=CompactResponses.Summarize(node); }
        node["plan_id"]=plan?.ArtifactId; node["plan_sha256"]=plan?.Sha256; node["plan_path"]=plan?.Path;
        node["artifact_id"]=result.ArtifactId; node["artifact_sha256"]=result.Sha256; node["artifact_path"]=result.Path;
        node["response_detail"]=responseDetail;
        if(plan is not null) { var model=artifacts.LoadPlan(plan.ArtifactId); node["operation_count"]=model.Operations.Count; node["native_output_path"]=model.Output.NativePath; }
        return node;
    }

    [McpServerTool(Name="cad_build_model",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("执行 planId 或旧版 irJson（二者择一），默认立即建模。planId 校验内容、编译器和来源／恢复文件身份；仍执行全部原有验收。dryRun=true 只检查计划，不连接 SolidWorks。默认摘要，responseDetail=full 返回完整证据。")]
    public static async Task<object> BuildModel(ModelingWorkflow workflow,ModelingIrValidator validator,CompactArtifactStore artifacts,
        string? irJson=null,string? planId=null,bool dryRun=false,string responseDetail="summary",CancellationToken cancellationToken=default)
    {
        CompactResponses.ValidateDetail(responseDetail);
        if(string.IsNullOrWhiteSpace(irJson)==string.IsNullOrWhiteSpace(planId)) throw new ArgumentException("planId 与 irJson 必须且只能提供一个。");
        var ir=string.IsNullOrWhiteSpace(planId) ? irJson! : ModelingIrJson.Serialize(artifacts.LoadPlan(planId),false);
        return CompactResponses.Present(artifacts,await ModelingTools.BuildModel(workflow,validator,ir,dryRun,cancellationToken),responseDetail);
    }

    [McpServerTool(Name="cad_read_drawing",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("读取工程图片／PDF，默认返回页数、源身份、遗漏清单、页面文件路径、诊断及完整结果句柄；原始观测用 cad_read_result 按页读取。必须实际检查所有源页和非空细节并复查遗漏；摘要不表示理解完整。不启动 SolidWorks。responseDetail=full 保留完整旧返回。")]
    public static async Task<object> ReadDrawing(IEngineeringDrawingIngestionService service,CompactArtifactStore artifacts,string inputPath,
        string? artifactRoot=null,int[]? pageNumbers=null,int renderDpi=300,JsonElement? viewHints=null,string responseDetail="summary",CancellationToken cancellationToken=default)
    {
        CompactResponses.ValidateDetail(responseDetail);
        return CompactResponses.Present(artifacts,await ModelingTools.ReadDrawing(service,inputPath,artifactRoot,pageNumbers,renderDpi,Optional<DrawingViewRegionHint[]>(viewHints),cancellationToken),responseDetail);
    }

    [McpServerTool(Name="cad_inspect_model",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("检查本地模型与可选几何／尺寸要求，默认返回几何、完整性／重开状态、失败检查及详细结果句柄；面边、尺寸、特征用 cad_read_result 分页读取。参数遵循 inspection 合同，可按需 cad_get_contract。可能启动 SolidWorks。responseDetail=full 保留完整结果。")]
    public static async Task<object> InspectModel(IModelingExecutor executor,CompactArtifactStore artifacts,string inputPath,
        JsonElement? queries=null,JsonElement? verification=null,JsonElement? geometryReferences=null,JsonElement? measurements=null,
        string? documentRevision=null,string? sourceRevisionId=null,JsonElement? connectivityQueries=null,JsonElement? editabilityProbes=null,
        string responseDetail="summary",CancellationToken cancellationToken=default)
    {
        CompactResponses.ValidateDetail(responseDetail);
        return CompactResponses.Present(artifacts,await ModelingTools.InspectModel(executor,inputPath,Optional<EntityQuery[]>(queries),Optional<ModelVerificationSpec>(verification),cancellationToken,
            Optional<GeometryRef[]>(geometryReferences),Optional<MeasurementQuery[]>(measurements),documentRevision,sourceRevisionId,
            Optional<ConnectivityInspectionQuery[]>(connectivityQueries),Optional<NativeEditabilityProbeSpec[]>(editabilityProbes)),responseDetail);
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
        complete["tool_profile"]=Environment.GetEnvironmentVariable("AUTO_SOLIDWORKS_TOOL_PROFILE") ?? "compact";
        var receipt=artifacts.SaveResult(complete);
        if(responseDetail=="full") return complete;
        var summary=CompactResponses.Summarize(complete);
        foreach(var key in new[]{"operations","native_feature_kinds","input_formats","output_formats","limitations"}) summary[key]=complete[key]?.DeepClone();
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
