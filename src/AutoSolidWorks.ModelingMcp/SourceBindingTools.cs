using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CadModeling.Core;
using CadModeling.Ir;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class SourceBindingTools
{
    [McpServerTool(Name="cad_get_binding_guide",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("读取简短绑定装配卡：必填字段、常用分项键、合法分类、参数路径和锚点归属。避免查询完整草案合同；需要完整类型时取binding_sheet合同。不是图纸识别、不是验收，不启动SolidWorks。")]
    public static object Guide()=>new {
        revision=BindingSheetAssembler.Revision,
        quick_start=QuickStart(),
        workflow=new[]{"识图：查看原图并确定视图、单位、实体/孔槽及基准。",
            "尺寸绑定：填写真实操作、源特征和尺寸。可直接写draft，或用workspace+sheet批量装配；不需要候选核销、双遍扫描、材料探针或独立检查表。",
            "编译：cad_create_model_plan；已有绑定工作区时可用cad_compile_binding_review定位错误。通过后直接使用plan_id。",
            "建模：cad_build_model；返回保存的原生文件和简短状态，不调用独立交付审核。"},
        workflow_revision=SourceWorkflow.DefaultRevision,
        required_draft_fields=new[]{"name","source_text","operations","drawing_context"},
        sheet="groups[]: frame_id, view_id, feature_id, datum, anchors[], annotations[]",
        annotation="id, source_literal, operation_id, text_bounds, evidence, state (default Unresolved), attachment, dimensions[]; optional leader_trace, leader_target_anchor_id, length_unit",
        attachment="target_bounds, trace, explanation, observation_ids; start_anchor_id/end_anchor_id for linear/angle, target_anchor_id for circular/compound. Per-dimension attachment overrides common non-null fields.",
        dimension="id, component_key, parameter_path, optional checks:[{check_id,parameter_path}], operation_id, measurement_id, attachment. Value/unit/quantity come only from the exact source_literal parser.",
        anchor="id, physical_entity_id, kind, bounds, source_path:[{x,y}], state (default Unresolved), candidate_ids and/or observation_ids, evidence; optional construction",
        cross_view="drawing_context.circular_projections explicitly links a reviewed circular anchor to two real parallel silhouette Line anchors in another view of the SAME feature, with axis_direction and source evidence. Lines retain their kind. New measurement IDs cannot hide physical diameter/linear conflicts. See binding-assist reference.",
        outside_arrows="For an actual linear dimension with outside arrows, trace.path retains the longer visible source line; supply both start_arrow_point and end_arrow_point. Extensions end at these arrow tips. Finite projected source planes use construction.kind=LineExtension with one reviewed real Line support; no arbitrary blank contours.",
        model_orientation="Source angles driving frame.x_direction require drawing_context.model_orientation with reviewed unit orthogonal front_right/front_up and direction_bindings [{dimension_id,operation_id,use_end_ray:true}]. Chosen source angle ray must align with positive sketch X. Standard top up is -(front_right cross front_up); first/third angle changes layout only. Signed comparison rejects mirrors. Basis/view semantics remain AI-reviewed; no whole-shape proof.",
        stretched_page="If independent normal-view circular contours are stretched consistently, drawing_context.page_metrics may name 2..8 reviewed normal_circle_anchor_ids on the same page, from different physical circles and at least two views. Each needs a full closed source_path with real ink. The validator derives one page-wide X/Y correction. Never calibrate from the angle arc being tested, accept an oblique ellipse as a normal circle, or use pixels as model dimensions.",
        angle_consumers="Sin/Cos expressions take Degree source facts and return Unitless components for frame.x_direction / frame.normal. Reference these derived facts in all associated hole frames and placement axes. A construction sketch or DependsOn alone does not prove source-driven geometry. Formula resolution on recompilation is not native angle-edit associativity.",
        geometry_selection="Optional cad_bind_source_geometry selects reviewed line/circle proposal IDs and preserves exact source paths; AI still supplies view/feature/physical identity/evidence and explicit review state. Multi-arc proposals require one arc_index; no automatic dimension ownership.",
        coordinates="Unscaled workspace crop pixels for all sheet bounds, trace and source_path; model_direction stays a model vector. No image resizing or automatic nearest-entity assignment.",
        parameter_paths="Dot indices canonical: primitives.0.width_mm, feature.hole_centers.0.xmm, axis_starts_mm.0.x. Explicit [0] syntax is normalized with a receipt; missing or ambiguous slots are rejected.",
        components=new {linear="linear",radius="radius",diameter="bore_diameter",angle="angle",count="count",chamfer="chamfer_distance",counterbore=new[]{"bore_diameter","counterbore_diameter","counterbore_depth"},countersink=new[]{"bore_diameter","countersink_diameter","countersink_angle"},thread=new[]{"thread_major_diameter","thread_pitch","thread_depth"}},
        metadata="Non-numeric parsed tokens retain exact original literals and the supplied annotation evidence. Unparsed suffixes or unbound numeric components fail. Parsing never means semantic review.",
        anchors="Start/end datums may reference another feature in the SAME view. Target anchors, common leader targets and constructed supports remain in the selected feature/view. Same physical_entity_id must preserve kind/feature across views; do not duplicate a boundary as a fake datum.",
        eligible_anchor_evidence="Use actual same-page overlapping inventory candidates whose kind is Geometry or ResidualInk. A VisibleLine hint alone does not establish eligibility. Keep exact source_path/bounds. Do not assign all nearby observation IDs automatically.",
        non_model_categories=new[]{"border","title_block","dimension_graphic","centerline","hatch","note","noise"},
        contracts=new[]{"binding_sheet","binding_batch","draft","drawing_context","geometry_anchor"},
        accuracy="Four-stage defaults retain actual parameter binding, units, source endpoints/anchors and compiler validity. No automatic Reviewed or inferred geometry. Separate interpretation, omission sweeps, material probes and delivery reviews are not required. Explicit legacy revisions keep their original checks.",
        full_schema="cad_get_contract(contract='binding_sheet')",
        native_execution=false
    };

    [McpServerTool(Name="cad_assemble_source_bindings",ReadOnly=false,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("一次提交显式草案和多组来源绑定表，解析原文分项并装配尺寸、特征关键参数和独立验收引用。draft/baseInputId二选一；sheet合同见binding_sheet或简短cad_get_binding_guide。参数[0]规范化为.0并记录。原始草案不可变；装配失败不存新输入。只做数据装配，不生成几何、不复制验收数值、不自动Reviewed、不执行原生；之后仍需原完整编译。")]
    public static object Assemble(CompactArtifactStore artifacts,string workspaceId,JsonElement sheet,JsonElement? draft=null,string? baseInputId=null)
    {
        try
        {
            if((draft is null)==string.IsNullOrWhiteSpace(baseInputId))throw new BindingSheetAssembler.InputError("/draft","Provide exactly one of draft or baseInputId.");
            var input=draft is {} raw?JsonNode.Parse(raw.GetRawText())!:artifacts.LoadInput(baseInputId!,"draft");
            var prepared=BindingSheetAssembler.Prepare(input,sheet,Workspace(artifacts,workspaceId));
            // The existing store performs strict typed normalization. No artifact is
            // stored until all groups and explicit consumers have been assembled.
            var stored=CompactModelInputs.Store(artifacts,"draft",JsonSerializer.SerializeToElement(prepared.Draft,ModelingIrJson.Options),null,null);
            var receipt=JsonSerializer.SerializeToNode(stored,ModelingIrJson.Options)!.AsObject();
            receipt["success"]=true;receipt["binding_sheet_revision"]=BindingSheetAssembler.Revision;
            receipt["workspace_id"]=workspaceId;receipt["base_input_id"]=baseInputId;
            receipt["added_annotations"]=prepared.Annotations;receipt["added_dimensions"]=prepared.Dimensions;receipt["added_anchors"]=prepared.Anchors;
            receipt["normalized_paths"]=JsonSerializer.SerializeToNode(prepared.NormalizedPaths,ModelingIrJson.Options);
            receipt["reverse_sweep_required"]=!SourceWorkflow.IsFourStage(prepared.Draft["drawing_context"]?["workflow_revision"]?.GetValue<string>());return receipt;
        }
        catch(Exception e) when(e is BindingSheetAssembler.InputError or JsonException or ArgumentException or InvalidOperationException)
        {
            return new {success=false,status="assembly_input_invalid",compiled=false,full_drawing_equivalence=false,
                diagnostics=new[]{new {code="BINDING_ASSEMBLY_INPUT",path=e is BindingSheetAssembler.InputError input?input.InputPath:e is JsonException json?json.Path??"/":"/",message=e.Message}},
                input_stored=false,original_input_unchanged=true,native_execution=false};
        }
    }
    private static readonly JsonSerializerOptions Strict=new(ModelingIrJson.Options){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    private static T Decode<T>(JsonElement element)=>element.Deserialize<T>(Strict)??throw new JsonException("输入不能为空。");
    internal static BindingWorkspace Workspace(CompactArtifactStore store,string id)
    {
        var w=store.LoadResult(id).Deserialize<BindingWorkspace>(Strict)??throw new JsonException("工作区为空。");
        BindingWorkspaceImages.Validate(w);return w;
    }
    internal static void Match(DrawingPlanContext c,BindingWorkspace w)
    {
        if(!BindingWorkspaceImages.SamePath(c.SourcePath,w.SourcePath)||SourceWorkflow.Evidence(c) is not {} o ||
            !BindingWorkspaceImages.SamePath(o.InventoryPath,w.InventoryPath)||!string.Equals(o.InventorySha256,w.InventorySha256,StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("草案/上下文与工作区必须来自同一原图和同一库存修订。");
    }
    [McpServerTool(Name="cad_prepare_binding_workspace",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("从已读图库存生成原页、指定裁剪和像素网格，保存可逆坐标框架与哈希。crops 使用原页归一化 bounds；输出裁剪不缩放。读取原图和每个必要局部图后再绑定，不进行OCR、不自动审核。不启动SolidWorks。")]
    public static object Prepare(CompactArtifactStore artifacts,string inventoryPath,string inventorySha256,string outputDirectory,JsonElement? crops=null)
    {
        var w=BindingWorkspaceImages.Prepare(inventoryPath,inventorySha256,outputDirectory,crops is {} c?Decode<BindingCrop[]>(c):[]);
        var receipt=artifacts.SaveResult(w);
        return new {workspace_id=receipt.ArtifactId,workspace_sha256=receipt.Sha256,workspace_path=receipt.Path,revision=w.Revision,
            source_path=w.SourcePath,source_sha256=w.SourceSha256,frames=w.Frames,status="pending_visual_review",semantic_review_complete=false};
    }
    [McpServerTool(Name="cad_add_binding_crops",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("在已有绑定工作区一次追加1..12个原分辨率局部图，复用已有页面和裁剪，不重复读图提取。crops=[{id,page_number,bounds:{left,top,right,bottom}}]使用原页归一化坐标，ID不能重复。用于看清标注引线终点、相邻台阶和跨视图对象；同时裁入文字、完整引线和目标轮廓以保留上下文。返回新workspace_id和新增frames；旧工作区不变。后续绑定使用新workspace_id。无OCR、无自动归属或Reviewed，不启动SolidWorks。")]
    public static object AddCrops(CompactArtifactStore artifacts,string workspaceId,JsonElement crops)
    {
        var w=Workspace(artifacts,workspaceId);
        var requests=Decode<BindingCrop[]>(crops);
        if(requests.Length is <1 or >12)throw new ArgumentException("一次追加1..12个局部图。");
        if(w.Frames.Count+requests.Length>512)throw new ArgumentException("工作区最多512个坐标框架。");
        if(requests.Any(c=>w.Frames.Any(f=>f.Id==c.Id)))throw new ArgumentException("局部图ID已存在，不能覆盖旧坐标框架。");
        var directory=Path.Combine(Path.GetDirectoryName(w.Frames[0].ImagePath)!,"details-"+Guid.NewGuid().ToString("N"));
        var added=BindingWorkspaceImages.Prepare(w.InventoryPath,w.InventorySha256,directory,requests,includeOverview:false);
        var extended=w with{Frames=w.Frames.Concat(added.Frames).ToArray()};
        var receipt=artifacts.SaveResult(extended);
        return new {workspace_id=receipt.ArtifactId,workspace_sha256=receipt.Sha256,workspace_path=receipt.Path,base_workspace_id=workspaceId,
            revision=w.Revision,source_path=w.SourcePath,source_sha256=w.SourceSha256,frames=added.Frames,total_frame_count=extended.Frames.Count,
            reused_frame_count=w.Frames.Count,coordinates="New frames use unscaled crop pixels; source origin and page dimensions are retained. Use the returned workspace_id for binding.",
            status="pending_visual_review",semantic_review_complete=false,native_execution=false};
    }
    [McpServerTool(Name="cad_bind_source_dimensions",ReadOnly=false,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("将AI已判断的标注、锚点和测量批量装配为原合同并存为新输入句柄。batch坐标为frame_id裁剪图原始像素，统一转换原页坐标；共享view_id/feature_id/datum。只追加唯一ID，不猜数值/归属，不自动Reviewed或补扫图记录；派生尺寸和修正仍用原JSON Patch。kind=draft或drawing_context。不启动SolidWorks；存储不是校验通过。合同binding_batch可按需读取。")]
    public static object Bind(CompactArtifactStore artifacts,string baseInputId,string kind,string workspaceId,JsonElement batch)
    {
        if(kind is not("draft" or "drawing_context"))throw new ArgumentException("kind必须是draft或drawing_context。");
        var input=artifacts.LoadInput(baseInputId,kind=="draft"?"draft":"context");
        var w=Workspace(artifacts,workspaceId);
        var counts=ApplyBatch(input,kind,w,Decode<BindingBatch>(batch));
        var result=CompactModelInputs.Store(artifacts,kind,JsonSerializer.SerializeToElement(input,ModelingIrJson.Options),null,null);
        var receipt=JsonSerializer.SerializeToNode(result,ModelingIrJson.Options)!.AsObject();
        receipt["base_input_id"]=baseInputId;receipt["workspace_id"]=workspaceId;receipt["binding_assist_revision"]=BindingWorkspaceImages.Revision;
        receipt["added_annotations"]=counts.Annotations;receipt["added_dimensions"]=counts.Dimensions;receipt["added_anchors"]=counts.Anchors;
        receipt["reverse_sweep_required"]=!SourceWorkflow.IsFourStage((kind=="draft"?input["drawing_context"]:input)?["workflow_revision"]?.GetValue<string>());return receipt;
    }
    internal sealed record BatchCounts(int Annotations,int Dimensions,int Anchors);
    internal static BatchCounts ApplyBatch(JsonNode input,string kind,BindingWorkspace w,BindingBatch b)
    {
        var context=kind=="draft"?input["drawing_context"]:input;
        if(context is not JsonObject node)throw new ArgumentException("输入没有drawing_context。");
        var c=CompactModelInputs.Decode<DrawingPlanContext>(node);Match(c,w);
        if((c.WorkflowRevision != SourceWorkflow.Revision && !SourceWorkflow.IsFourStage(c.WorkflowRevision))||!c.RequireCompleteBindings||!c.RequireGeometryAnchors||!c.RequireDimensionEvidence||
            !SourceWorkflow.IsFourStage(c.WorkflowRevision)&&(!c.RequireInterpretationReview||!c.RequireShapeDecomposition))
            throw new ArgumentException("绑定助手要求当前工作流的真实参数、尺寸证据和几何锚点检查；旧版上下文继续要求原五项检查。");
        var frame=w.Frames.SingleOrDefault(f=>f.Id==b.FrameId)??throw new ArgumentException("frame_id不存在。");
        if(c.Views.Count(v=>v.Id==b.ViewId&&v.PageNumber==frame.PageNumber)!=1)throw new ArgumentException("视图必须唯一并对应裁剪页码。");
        if(c.Features.Count(f=>f.Id==b.FeatureId)!=1)throw new ArgumentException("feature_id必须引用已声明的唯一源特征。");
        if(string.IsNullOrWhiteSpace(b.Datum)||b.Annotations.Count>200||b.Anchors.Count>400||b.Annotations.Sum(a=>a.Dimensions.Count)>500)
            throw new ArgumentException("批次缺少基准或超过标注/锚点/尺寸数量上限。");
        var anchors=b.Anchors.Select(a=>new DrawingGeometryAnchor{Id=a.Id,PhysicalEntityId=a.PhysicalEntityId,FeatureId=b.FeatureId,ViewId=b.ViewId,
            Kind=a.Kind,State=a.State,Bounds=BindingWorkspaceImages.Box(a.Bounds,frame),SourcePath=a.SourcePath.Select(p=>BindingWorkspaceImages.Point(p,frame)).ToArray(),
            Construction=a.Construction,ObservationIds=a.ObservationIds,CandidateIds=a.CandidateIds,Evidence=a.Evidence}).ToArray();
        var annotations=new List<DrawingSourceAnnotation>();var facts=new List<DrawingDimensionFact>();
        foreach(var a in b.Annotations)
        {
            var text=BindingWorkspaceImages.Box(a.TextBounds,frame);
            if(a.RetainedComponents.Any(x=>x.DimensionId is not null))throw new ArgumentException("retained_components只能保存非数值原文，不得隐藏数值绑定。");
            var components=a.Dimensions.Select(d=>new DrawingAnnotationBinding{ComponentKey=d.ComponentKey,DimensionId=d.Id,Evidence=d.AttachmentExplanation}).Concat(a.RetainedComponents).ToArray();
            Unique(components.Select(x=>x.ComponentKey),"标注组件");
            annotations.Add(new(){Id=a.Id,ViewId=b.ViewId,FeatureId=b.FeatureId,OperationId=a.OperationId,TextBounds=text,SourceLiteral=a.SourceLiteral,
                LengthUnit=a.LengthUnit,State=a.State,Evidence=a.Evidence,Components=components,LeaderTrace=BindingWorkspaceImages.Trace(a.LeaderTrace,frame),LeaderTargetAnchorId=a.LeaderTargetAnchorId});
            foreach(var d in a.Dimensions)
            {
                if(!double.IsFinite(d.Value))throw new ArgumentException("标称值必须有限。");
                facts.Add(new(){Id=d.Id,OperationId=d.OperationId??a.OperationId,ParameterPath=d.ParameterPath,Value=d.Value,Unit=d.Unit,
                    Status=DrawingFactStatus.Stated,Critical=true,SourceLiteral=a.SourceLiteral,SourceAnnotationId=a.Id,SourceComponentKey=d.ComponentKey,
                    ViewIds=[b.ViewId],ObservationIds=d.ObservationIds,BindingEvidence=new(){FeatureId=b.FeatureId,ViewId=b.ViewId,MeasurementId=d.MeasurementId??d.Id,
                        QuantityKind=d.QuantityKind,TextBounds=text,TargetBounds=BindingWorkspaceImages.Box(d.TargetBounds,frame),Datum=b.Datum,
                        AttachmentExplanation=d.AttachmentExplanation,StartAnchorId=d.StartAnchorId,EndAnchorId=d.EndAnchorId,TargetAnchorId=d.TargetAnchorId,
                        StartReference=d.StartReference,EndReference=d.EndReference,Trace=BindingWorkspaceImages.Trace(d.Trace,frame)}});
            }
        }
        Unique(c.GeometryAnchors.Select(a=>a.Id).Concat(anchors.Select(a=>a.Id)),"锚点");
        Unique(c.Dimensions.Select(d=>d.Id).Concat(facts.Select(d=>d.Id)),"尺寸");
        Unique((c.AnnotationLedger?.Annotations??[]).Select(a=>a.Id).Concat(annotations.Select(a=>a.Id)),"标注");
        // Match the authoritative validator: start/end datums may belong to another
        // feature in the same view; the measured target and construction supports may not.
        var viewAnchorIds=c.GeometryAnchors.Concat(anchors).Where(a=>a.ViewId==b.ViewId).Select(a=>a.Id).ToHashSet();
        var anchorIds=c.GeometryAnchors.Concat(anchors).Where(a=>a.ViewId==b.ViewId&&a.FeatureId==b.FeatureId).Select(a=>a.Id).ToHashSet();
        foreach(var id in facts.SelectMany(f=>new[]{f.BindingEvidence!.StartAnchorId,f.BindingEvidence.EndAnchorId}).Where(x=>x is not null))
            if(!viewAnchorIds.Contains(id!))throw new ArgumentException("起止锚点缺失或跨视图："+id);
        foreach(var id in facts.Select(f=>f.BindingEvidence!.TargetAnchorId)
            .Concat(annotations.Select(a=>a.LeaderTargetAnchorId)).Concat(anchors.SelectMany(a=>a.Construction?.SupportAnchorIds??[])).Where(x=>x is not null))
            if(!anchorIds.Contains(id!))throw new ArgumentException("锚点引用缺失或跨视图/特征："+id);
        Append(node,"geometry_anchors",anchors);Append(node,"dimensions",facts);
        var ledger=node["annotation_ledger"] as JsonObject;
        if(ledger is null){ledger=new JsonObject{["source_sha256"]=w.SourceSha256,["annotations"]=new JsonArray(),["sweeps"]=new JsonArray()};node["annotation_ledger"]=ledger;}
        if(!string.Equals(ledger["source_sha256"]?.GetValue<string>(),w.SourceSha256,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("账本原图哈希不一致。");
        Append(ledger,"annotations",annotations);
        // Changed evidence cannot inherit a finished reverse sweep. Keep its evidence and ID list for audit.
        foreach(var sweep in (SourceWorkflow.IsFourStage(c.WorkflowRevision)?new JsonArray():ledger["sweeps"] as JsonArray??[]).OfType<JsonObject>())
            if(sweep["pass"]?.GetValue<int>()==2)sweep["state"]="Unresolved";
        return new(annotations.Count,facts.Count,anchors.Length);
    }
    private static void Unique(IEnumerable<string> ids,string label)
    {
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var id in ids)if(string.IsNullOrWhiteSpace(id)||!seen.Add(id))throw new ArgumentException(label+"ID为空或重复："+id);
    }
    private static void Append<T>(JsonObject node,string key,IEnumerable<T> values)
    {
        var list=node[key] as JsonArray;if(list is null){list=new JsonArray();node[key]=list;}
        foreach(var value in values)list.Add(JsonSerializer.SerializeToNode(value,ModelingIrJson.Options));
    }
    [McpServerTool(Name="cad_compile_binding_review",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("调用原cad_create_model_plan全量验证并返回摘要；失败时生成锚点/尺寸局部错误叠图及全部诊断句柄，保留未定位错误。成功直接使用plan_id。只用于已有draftId，工作区必须同源；不自动修改绑定、不启动SolidWorks。")]
    public static object Compile(GenericPlanCompiler compiler,ModelingWorkflow workflow,CompactArtifactStore artifacts,
        string draftId,string workspaceId,string nativeOutputPath,string reviewOutputDirectory)
    {
        var input=artifacts.LoadInput(draftId,"draft");var draft=CompactModelInputs.Decode<GenericModelDraft>(input);
        var w=Workspace(artifacts,workspaceId);Match(draft.DrawingContext??throw new ArgumentException("缺少drawing_context。"),w);
        var raw=CompactModelingTools.CreatePlan(compiler,workflow,artifacts,nativeOutputPath,draftId:draftId,workspaceId:workspaceId,reviewOutputDirectory:reviewOutputDirectory);
        var result=JsonSerializer.SerializeToNode(raw,ModelingIrJson.Options)!.AsObject();
        if(result["binding_review_id"] is null) AttachCompileReview(artifacts,result,draft,workspaceId,reviewOutputDirectory);
        return result;
    }
    internal static void AttachCompileReview(CompactArtifactStore artifacts,JsonObject result,GenericModelDraft draft,string? workspaceId,string? reviewOutputDirectory)
    {
        var context=draft.DrawingContext??throw new ArgumentException("Missing drawing_context.");
        var evidence=SourceWorkflow.Evidence(context)??throw new ArgumentException("Missing source evidence.");
        var w=workspaceId is not null?Workspace(artifacts,workspaceId):BindingWorkspaceImages.Prepare(evidence.InventoryPath,evidence.InventorySha256,
            Path.Combine(Path.GetDirectoryName(evidence.InventoryPath)!,"compile-workspace-"+Guid.NewGuid().ToString("N")),[]);
        Match(context,w);
        var reviewDirectory=reviewOutputDirectory??Path.Combine(Path.GetDirectoryName(evidence.InventoryPath)!,"compile-errors-"+Guid.NewGuid().ToString("N"));
        var complete=artifacts.LoadResult(result["artifact_id"]!.GetValue<string>());
        var errors=(complete["diagnostics"] as JsonArray??[]).Deserialize<ModelingDiagnostic[]>(ModelingIrJson.Options)!
            .Where(x=>x.Severity==DiagnosticSeverity.Error).ToArray();
        var details=BindingErrorImages.Render(w,context,errors,reviewDirectory);
        var receipt=artifacts.SaveResult(details);
        result["binding_review_id"]=receipt.ArtifactId;result["binding_review_path"]=receipt.Path;
        result["binding_review"]=JsonSerializer.SerializeToNode(new {details.Status,details.ErrorCount,details.ImageCount,
            regions=details.Regions.Take(12),regions_truncated=details.Regions.Count>12,region_count=details.Regions.Count,
            complete_regions_section="/regions",complete_diagnostics_section="/diagnostics",semantic_review_complete=false},ModelingIrJson.Options);
    }
    internal static object QuickStart()=>new {
        contracts=new[]{"sketch_frame","primitive","dimension_fact","placement_intent"},
        contract_section_example="cad_get_contract(contract='draft', section='/properties/operations/items')",
        input="cad_read_drawing(inputPath=absolute_path); use review_workspace.drawing_context_input_id and binding_workspace.workspace_id",
        coordinates="Direct draft: original page normalized x/page_width,y/page_height. Sheet: unscaled frame pixels. Never mix the two.",
        avoid_first_compile_errors="Linear/angle traces need model_direction; source anchor bounds must contain their complete source_path; ThreePointArc needs start/end/point_on_arc (xmm,ymm); preview_views uses isometric/front/top/right/left/back, or omit for the four-stage workflow. Correct these in one draft before compile.",
        angular_source="A uniformly stretched drawing needs independently reviewed normal circles in drawing_context.page_metrics before angular arc checks. Bind source angles through Sin/Cos(Degree)->Unitless frame expressions; isolated construction dimensions do not control a solid. Direction-driving angles also require model_orientation with reviewed front basis and signed source-ray bindings; see cad_get_binding_guide.",
        finite_extrusion="Drawing finite extrusions require placement_intent: axis_direction, datum_point_mm, minimum_mm, maximum_mm, tolerance_mm, source_explanation, observation_ids. Bounds are signed millimetres along the normalized axis from datum. Source expectation must be independently read from the drawing, never copied from current extrusion settings; observation_ids must reference registered dimension observations.",
        compile="cad_create_model_plan(draft=..., nativeOutputPath=..., workspaceId=...); errors include binding_review regions; repair only reported fields using immutable input patches.",
        geometry_proposals="cad_find_source_geometry(workspaceId,frameId) proposes all-angle source lines with frame/source coordinates and an overlay. AI must inspect the original image and choose actual endpoints and ownership; candidates stay Unresolved.",
        circular_proposals="cad_find_source_circles(workspaceId,frameId) proposes pixel centers/radii and only ink-supported arc intervals. Concentric candidates remain separate; half-circles are not closed automatically. These are image geometry proposals, not hole identification or source dimensions. AI must still read the annotation and target object.",
        fine_detail="If a leader, surface or dimension endpoint is unclear in the whole-page image, cad_add_binding_crops(workspaceId,crops) appends up to 12 source-resolution crops in one call and returns a new workspace_id. Include the text, full leader and target contour; compare another view when needed. A nearby dimension label alone does not establish the target. Reuse the returned frame_id and original crop pixels for binding.",
        source_binding="Keep actual source endpoints/ink and same-page Geometry/ResidualInk evidence. After changing anchor bounds, recheck overlapping evidence IDs. No auto Reviewed or geometry inference."
    };

}
