using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;
using CadModeling.Drawing.Providers.Abstractions;

var output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool condition,string name) { if(!condition) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS: "+name); }
void Reject(Action action,string name) { try { action(); } catch(Exception e) when(e is ArgumentException or InvalidOperationException or IOException or JsonException) { Check(true,name); return; } throw new Exception("未拒绝："+name); }
JsonObject Node(object value)=>JsonSerializer.SerializeToNode(value,ModelingIrJson.Options)!.AsObject();
var store=new CompactArtifactStore(Path.Combine(output,"store"),"producer-A");
var compiler=new RuleBasedTextCompiler(); var validator=new ModelingIrValidator(); var fake=new FakeExecutor();
var workflow=new ModelingWorkflow(compiler,validator,fake);
var original=compiler.Compile("80 x 50 x 20 mm plate",Path.Combine(output,"uncreated.SLDPRT"));
Check(original.Success,"基线文本计划可编译"); var plan=original.Plan!;
var saved=store.SavePlan(plan);
Check(store.LoadPlan(saved.ArtifactId).PlanId==plan.PlanId,"同一句柄保留请求计划身份");
Check(ModelingIrJson.Serialize(store.LoadPlan(saved.ArtifactId))==ModelingIrJson.Serialize(plan),"保存与加载保留完整 IR");
Check(store.SavePlan(plan).ArtifactId==saved.ArtifactId,"相同计划不覆盖已有内容");
Check(new CompactArtifactStore(Path.Combine(output,"store"),"producer-A").LoadPlan(saved.ArtifactId).Output.NativePath==plan.Output.NativePath,"新服务实例可读取同版本计划");
Reject(()=>new CompactArtifactStore(Path.Combine(output,"store"),"producer-B").LoadPlan(saved.ArtifactId),"编译器身份变化拒绝执行");
Reject(()=>store.LoadPlan("../"+saved.ArtifactId),"句柄路径穿越被拒绝");
Reject(()=>store.LoadPlan("plan_"+new string('a',64)),"不存在的句柄被拒绝");
var resultReceipt=store.SaveResult(new {success=false,message="失败"});
Reject(()=>store.LoadPlan(resultReceipt.ArtifactId),"结果句柄不能代替计划");
var source=Path.Combine(output,"source.SLDPRT"); File.WriteAllText(source,"original");
var sourceReceipt=store.SavePlan(plan with{SourceModelPath=source}); File.WriteAllText(source,"changed");
Reject(()=>store.LoadPlan(sourceReceipt.ArtifactId),"源模型变化被拒绝");
var checkpointPath=Path.Combine(output,"checkpoint.SLDPRT"); File.WriteAllText(checkpointPath,"saved prefix");
var manifestPath=Path.Combine(output,"checkpoint.json");
File.WriteAllText(manifestPath,JsonSerializer.Serialize(new ModelingCheckpointManifest{NativePath=checkpointPath,NativeSha256="hash",OriginalPlan=plan,FeatureReferences=[],Geometry=new(1,6,12,1,1,new(0,0,0),new(1,1,1))},ModelingIrJson.Options));
var checkpointReceipt=store.SavePlan(plan with{Recovery=plan.Recovery with{ResumeManifestPath=manifestPath}});
File.WriteAllText(checkpointPath,"changed prefix");
Reject(()=>store.LoadPlan(checkpointReceipt.ArtifactId),"恢复原生前缀变化被拒绝");
File.WriteAllText(checkpointPath,"saved prefix");
File.AppendAllText(manifestPath," ");
Reject(()=>store.LoadPlan(checkpointReceipt.ArtifactId),"恢复清单变化被拒绝");
var drawing=Path.Combine(output,"drawing.png"); File.WriteAllText(drawing,"original drawing");
var drawingReceipt=store.SavePlan(plan with{DrawingContext=new(){SourcePath=drawing}}); File.WriteAllText(drawing,"changed drawing");
Reject(()=>store.LoadPlan(drawingReceipt.ArtifactId),"源图纸变化被拒绝");
var tampered=store.SavePlan(plan with{Name="防篡改计划"}); File.AppendAllText(tampered.Path," ");
Reject(()=>store.LoadPlan(tampered.ArtifactId),"计划文件改动被拒绝");
Reject(()=>store.SavePlan(plan with{Name="防篡改计划"}),"同名已变化结果禁止覆盖");
var changedOutput=store.SavePlan(plan with{Output=plan.Output with{NativePath=Path.Combine(output,"other.SLDPRT")}});
Check(changedOutput.ArtifactId!=saved.ArtifactId,"输出范围纳入内容身份");
var changedCheck=store.SavePlan(plan with{Verification=new(){RequireWholeModelInventory=true}});
Check(changedCheck.ArtifactId!=saved.ArtifactId,"验证要求纳入内容身份");

// Public wrappers exercise the original compiler, validator and unchanged workflow without COM.
var publicStore=new CompactArtifactStore(Path.Combine(output,"public-store"));
var compiled=Node(CompactModelingTools.CreatePlan(new(),workflow,publicStore,Path.Combine(output,"public.SLDPRT"),text:"80 x 50 x 20 mm plate"));
Check(compiled["success"]!.GetValue<bool>() && compiled["plan_id"] is not null,"公开编译返回可执行句柄");
Check(!compiled.ContainsKey("plan")&&!compiled.ContainsKey("ir_json"),"默认编译不重复发送计划与 IR");
var id=compiled["plan_id"]!.GetValue<string>();
var dry=Node(await CompactModelingTools.BuildModel(workflow,validator,publicStore,planId:id,dryRun:true));
Check(dry["status"]!.GetValue<string>()=="dry_run_passed" && fake.Executions==0,"句柄 dry-run 不调用执行器");
var full=Node(CompactModelingTools.CreatePlan(new(),workflow,publicStore,Path.Combine(output,"full.SLDPRT"),text:"80 x 50 x 20 mm plate",responseDetail:"full"));
Check(full["plan"] is JsonObject && full["ir_json"] is not null,"full 模式兼容完整旧返回");
var legacy=Node(await CompactModelingTools.BuildModel(workflow,validator,publicStore,irJson:full["ir_json"]!.GetValue<string>(),dryRun:true,responseDetail:"full"));
Check(legacy["status"]!.GetValue<string>()=="dry_run_passed","旧 irJson 调用仍可用");
Reject(()=>CompactModelingTools.BuildModel(workflow,validator,publicStore,planId:id,irJson:full["ir_json"]!.GetValue<string>(),dryRun:true).GetAwaiter().GetResult(),"双输入拒绝");
Reject(()=>CompactModelingTools.BuildModel(workflow,validator,publicStore,dryRun:true).GetAwaiter().GetResult(),"缺少计划拒绝");
Reject(()=>CompactModelingTools.BuildModel(workflow,validator,publicStore,planId:id,responseDetail:"unknown").GetAwaiter().GetResult(),"无效返回模式在执行前拒绝");
Check(fake.Executions==0,"输入拒绝未触发执行器");
var invalid=Node(CompactModelingTools.CreatePlan(new(),workflow,publicStore,Path.Combine(output,"invalid.SLDPRT"),text:"80 x 50 x 20 mm plate unknown unresolved geometry"));
Check(!invalid["success"]!.GetValue<bool>() && invalid["plan_id"] is null && invalid["diagnostics_count"]!.GetValue<int>()>0,"失败编译没有可执行句柄并保留诊断");
var invalidPlan=publicStore.SavePlan(plan with{Operations=[]});
var rejected=Node(await CompactModelingTools.BuildModel(workflow,validator,publicStore,planId:invalidPlan.ArtifactId,dryRun:true));
Check(!rejected["success"]!.GetValue<bool>() && fake.Executions==0,"句柄执行仍经过原始 IR 验证");
Check(Node(CompactModelingTools.GetContract("draft")).ToJsonString().Contains("design_intent"),"按需 draft 合同保留高级字段");
Check(Node(CompactModelingTools.GetContract("inspection")).ToJsonString().Contains("semantic_key"),"按需 inspection 合同保留身份要求");
Reject(()=>CompactModelingTools.GetContract("unknown"),"未知合同拒绝");
using(var unknownDraft=JsonDocument.Parse("{\"name\":\"测试\",\"source_text\":\"测试\",\"operations\":[],\"diameter_typo\":8}"))
    Reject(()=>CompactModelingTools.CreatePlan(new(),workflow,publicStore,Path.Combine(output,"typo.SLDPRT"),draft:unknownDraft.RootElement),"原始 JSON 参数中的未知字段拒绝而不静默忽略");

var failures=Enumerable.Range(0,25).Select(i=>new ExecutionEvidence("feature","失败",false,Code:"FEATURE_ERROR",OperationId:"op"+i)).ToArray();
var failed=new ExecutionResult(false,"outcome_unknown","执行状态未知",failures){RequestId="req-42",OutcomeUnknown=true,
    Verification=new([new("dimension",false,"mismatch","尺寸不符")]),Recovery=new("checkpoint.json","checkpoint.SLDPRT",["base"],["tail"],"tail",true,"可恢复")};
var summary=Node(CompactResponses.Present(store,failed,"summary"));
Check(!summary["success"]!.GetValue<bool>() && summary["outcome_unknown"]!.GetValue<bool>() && summary["request_id"]!.GetValue<string>()=="req-42","失败与未知状态保留请求 ID");
Check(summary["evidence_actionable_count"]!.GetValue<int>()==25 && summary["evidence_truncated"]!.GetValue<bool>(),"截断明确报告失败总数");
Check(summary["verification"]!["failed_count"]!.GetValue<int>()==1 && summary["verification"]!["failed_checks"]![0]!["id"]!.GetValue<string>()=="dimension","失败检查保留 ID");
Check(summary["recovery"]!["can_resume"]!.GetValue<bool>() && summary["recovery"]!["manifest_path"] is not null,"恢复身份和边界保留");
var sid=summary["artifact_id"]!.GetValue<string>();
var page1=Node(store.Read(sid,"/evidence",0,10)); var page2=Node(store.Read(sid,"/evidence",10,10)); var page3=Node(store.Read(sid,"/evidence",20,10));
Check(page1["next_offset"]!.GetValue<int>()==10 && page2["returned"]!.GetValue<int>()==10 && page3["returned"]!.GetValue<int>()==5 && page3["next_offset"] is null,"分页完整覆盖所有失败证据");
Check(Node(store.Read(sid))["items"] is JsonArray,"顶层读取列出字段与容器入口");
Reject(()=>store.Read(sid,"/missing"),"未知结果字段拒绝");
Reject(()=>store.Read(sid,"/bad~2escape"),"非法 JSON Pointer 拒绝");
Reject(()=>store.Read(sid,limit:201),"读取条数预算强制生效");
Reject(()=>store.Read(sid,offset:-1),"负分页偏移拒绝");
var escaped=store.SaveResult(new Dictionary<string,object>{{"a/b~c",new[]{1,2}}});
Check(Node(store.Read(escaped.ArtifactId,"/a~1b~0c"))["total"]!.GetValue<int>()==2,"JSON Pointer 转义正确");
var bulky=store.SaveResult(new{items=Enumerable.Range(0,10).Select(i=>new{label=i,data=new string('x',10000)}).ToArray()});
var bounded=Node(store.Read(bulky.ArtifactId,"/items",limit:10));
Check(bounded["returned"]!.GetValue<int>()==2 && bounded["next_offset"]!.GetValue<int>()==2,"分页字符预算产生正确续读偏移");
var huge=store.SaveResult(new{items=new[]{new{data=new string('x',30000)}}});
Reject(()=>store.Read(huge.ArtifactId,"/items"),"超大单项要求更细路径");
Check(Node(store.Read(huge.ArtifactId,"/items/0"))["items"] is JsonArray,"超大对象仍可读取字段入口");
var textPage=Node(store.Read(huge.ArtifactId,"/items/0/data"));
Check(textPage["returned"]!.GetValue<int>()==24000 && textPage["next_offset"]!.GetValue<int>()==24000,"长字符串按字符预算分块读取");
Check(Node(store.Read(huge.ArtifactId,"/items/0/data",offset:24000))["text"]!.GetValue<string>().Length==6000,"长字符串剩余内容可读取");
var unicode=store.SaveResult(new{text=new string('x',23999)+"😀"+new string('y',5)});
var unicodePage=Node(store.Read(unicode.ArtifactId,"/text"));
Check(unicodePage["next_offset"]!.GetValue<int>()==23999 && Node(store.Read(unicode.ArtifactId,"/text",offset:23999))["text"]!.GetValue<string>().StartsWith("😀"),"字符串分块不损坏 Unicode 代理对");
Reject(()=>store.Read(unicode.ArtifactId,"/text",offset:24000),"不允许从 Unicode 代理对内部读取");
var status=CompactResponses.Status(store,new(Execution:failed,RequestId:"req-42",Pending:true,DeadlineExceeded:true,ServerReached:true));
Check(status.Pending && status.OutcomeUnknown && status.DeadlineExceeded && status.Success==false && status.Status=="pending","状态 DTO 保留 pending/unknown/deadline/failure");
var unavailable=CompactResponses.Status(store,new(Error:"状态不可用",ErrorCode:"TRANSPORT"));
Check(unavailable.Success is null && unavailable.Status=="error","不可用状态不伪造成功");
Check(CompactResponses.Status(store,new(Execution:new(true,"completed","早期回执",[]),OutcomeUnknown:true)).Success is null,"状态未知时旧成功回执不表示完成");
var blocked=Path.Combine(output,"not-a-directory");File.WriteAllText(blocked,"block");var blockedStore=new CompactArtifactStore(blocked,"producer-A");
var writeFailure=Node(CompactResponses.Present(blockedStore,failed,"summary"));
Check(writeFailure["artifact_id"] is null && writeFailure["artifact_error"] is not null && writeFailure["request_id"]!.GetValue<string>()=="req-42","结果保存失败不能掩盖未知原生结果");
var incomplete=new ModelInspection(false,"采集不完整",source){CaptureComplete=false,ModelReopened=false,RebuildSucceeded=false,OutcomeUnknown=true,RequestId="inspect-42"};
var inspected=Node(await CompactModelingTools.InspectModel(new FakeExecutor{Inspection=incomplete},store,source));
Check(!inspected["capture_complete"]!.GetValue<bool>() && !inspected["model_reopened"]!.GetValue<bool>() && inspected["outcome_unknown"]!.GetValue<bool>(),"检查摘要保留采集与重开失败");
var ingestion=new FakeIngestion{Result=new(){Outcome=IngestionOutcome.Rejected,OmissionStatus="requires_visual_review",OmissionCandidateCount=8,RequiredReviewRegionCount=4}};
var read=Node(await CompactModelingTools.ReadDrawing(ingestion,store,drawing));
Check(read["outcome"]!.GetValue<string>()=="Rejected" && read["omission_candidate_count"]!.GetValue<int>()==8 && !read["full_drawing_equivalence"]!.GetValue<bool>(),"读图摘要保留拒绝和遗漏复查状态");
Check(!Directory.EnumerateFiles(output,"public.SLDPRT").Any(),"离线调用没有生成原生模型");
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{success=true,count=checks.Count,checks,native_started=false},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Token economy offline checks passed: {checks.Count}");

sealed class FakeExecutor : IModelingExecutor
{
    public int Executions {get;private set;}
    public ModelInspection Inspection {get;init;}=new(false,"模拟不可用","mock");
    public Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken=default)=>throw new Exception("禁止启动执行器");
    public Task<ExecutionResult> ExecuteAsync(ModelingPlan plan,bool dryRun,CancellationToken cancellationToken=default)
    { Executions++;throw new Exception("禁止原生执行"); }
    public Task<ModelInspection> InspectAsync(ModelInspectionRequest request,CancellationToken cancellationToken=default)=>Task.FromResult(Inspection);
}
sealed class FakeIngestion : IEngineeringDrawingIngestionService
{
    public DrawingIngestionResponse Result {get;init;}=new();
    public Task<DrawingIngestionResponse> IngestAsync(DrawingIngestionRequest request,CancellationToken cancellationToken=default)=>Task.FromResult(Result);
}
