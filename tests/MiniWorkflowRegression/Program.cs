using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;

var output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool condition,string name) { if(!condition) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS: "+name); }
JsonObject Node(object value)=>JsonSerializer.SerializeToNode(value,ModelingIrJson.Options)!.AsObject();
var source=Path.Combine(output,"source.png"); File.WriteAllText(source,"offline synthetic fixture");
var facts=Enumerable.Range(0,15).Select(i=>new DrawingDimensionFact {
    Id="d"+i,OperationId="o"+i,ParameterPath="depth_mm",Value=999,Status=DrawingFactStatus.Unknown,SourceLiteral="待核对标注",ViewIds=["front"]
}).ToArray();
var draft=new GenericModelDraft { Name="未解决夹具",SourceText="离线测试",Operations=[],
    DrawingContext=new() {SourcePath=source,Dimensions=facts,RequireInterpretationReview=true} };
var failures=new JsonObject { ["success"]=false,["diagnostics"]=new JsonArray {
    Node(new ModelingDiagnostic("DRAWING_BINDING",DiagnosticSeverity.Error,"未知尺寸","drawing_context.dimensions.d0")),
    Node(new ModelingDiagnostic("DRAWING_BINDING",DiagnosticSeverity.Error,"未知尺寸","drawing_context.dimensions.d0")),
    Node(new ModelingDiagnostic("HOLE_DIRECTION_TRACE",DiagnosticSeverity.Info,"方向提示","operations.0")),
    Node(new ModelingDiagnostic("SKETCH_DUPLICATE_DRIVING_DIMENSION",DiagnosticSeverity.Error,"重复驱动","operations.1")) } };
var original=failures.ToJsonString();
var report=PlanningReadiness.Collect(draft,failures);
Check(report["source_gap_count"]!.GetValue<int>()==15,"全部15个来源缺口保留而非只看前12个");
Check(report["error_count"]!.GetValue<int>()==3 && report["error_group_count"]!.GetValue<int>()==2,"重复错误合并并保留原始错误总数");
Check(((JsonArray)report["error_groups"]![0]!["diagnostic_indices"]!).Count==2,"重复诊断索引保留");
Check(report["user_questions_automatic"]!.GetValue<bool>()==false,"来源缺口先交代理复查而不自动问用户");
Check(!report.ToJsonString().Contains("999"),"未知占位数值不作为用户答案返回");
Check(failures.ToJsonString()==original,"报告不改原诊断或成功状态");
var summary=PlanningReadiness.Summarize(report);
Check(summary["source_gaps_truncated"]!.GetValue<bool>() && ((JsonArray)summary["source_gaps"]!).Count==12,"摘要截断显式并保持完整回执入口");
Check(summary["source_gaps_section"]!.GetValue<string>()=="/readiness/source_gaps","来源缺口有完整分页入口");
var repeated=draft with {DrawingContext=draft.DrawingContext! with {Dimensions=[facts[0],facts[0] with {Id="alias",ViewIds=["left"]}]} };
var aliases=PlanningReadiness.Collect(repeated,failures);
Check(aliases["source_gap_count"]!.GetValue<int>()==1 && ((JsonArray)aliases["source_gaps"]![0]!["dimension_ids"]!).Count==2,"同一操作字段合并但保留多个来源ID");
Check(((JsonArray)aliases["source_gaps"]![0]!["view_ids"]!).Count==2,"去重不丢跨视图来源");
var distinct=draft with {DrawingContext=draft.DrawingContext! with {Dimensions=[facts[0],facts[1]]} };
Check(PlanningReadiness.Collect(distinct,failures)["source_gap_count"]!.GetValue<int>()==2,"相同数值不同操作不误合并");
var known=draft with {DrawingContext=draft.DrawingContext! with {Dimensions=[facts[0] with {Status=DrawingFactStatus.Stated}]} };
Check(PlanningReadiness.Collect(known,failures)["source_gap_count"]!.GetValue<int>()==0,"清晰已知尺寸不重复进入提问候选");
var noncritical=draft with {DrawingContext=draft.DrawingContext! with {Dimensions=[facts[0] with {Critical=false}]} };
Check(PlanningReadiness.Collect(noncritical,failures)["source_gap_count"]!.GetValue<int>()==0,"非关键未知不强制当作阻塞用户问题");
var feature=new DrawingFeatureRequirement {Id="hole",SourceLiteral="孔还是实心顶面",ViewIds=["front"],OperationIds=["o0"],
    CriticalParameters=[new(){DimensionId="d0",OperationId="o0",ParameterPath="depth_mm"}]};
var forced=noncritical with {DrawingContext=noncritical.DrawingContext! with {Features=[feature]} };
Check(PlanningReadiness.Collect(forced,failures)["source_gap_count"]!.GetValue<int>()==1,"关键特征引用的未知尺寸仍聚合");
var observed=new SourceInterpretationObservation {ViewId="front",Bounds=new(0,0,.5,.5),Observation="源图反证",Contradicts=true};
var conflict=draft with {DrawingContext=draft.DrawingContext! with {Dimensions=[],Features=[feature],InterpretationReviews=[new(){FeatureId="hole",SelectedHypothesisId="selected",DimensionDatumDerivation="源基准",
    Hypotheses=[new(){Id="selected",Description="已选解释",Observations=[observed]},new(){Id="other",Description="备选解释",Observations=[observed]}]}]} };
Check(PlanningReadiness.Collect(conflict,failures)["source_gap_count"]!.GetValue<int>()==1,"来源反证进入代理复查批次");
var missingReview=conflict with {DrawingContext=conflict.DrawingContext! with {InterpretationReviews=[]}};
Check(PlanningReadiness.Collect(missingReview,failures)["source_gap_count"]!.GetValue<int>()==0,"代理漏写解释合同不伪装成用户缺尺寸");

var store=new CompactArtifactStore(Path.Combine(output,"artifacts"));
var executor=new ForbiddenExecutor();
var workflow=new ModelingWorkflow(new RuleBasedTextCompiler(),new ModelingIrValidator(),executor);
var invalid=Node(CompactModelingTools.CreatePlan(new(),workflow,store,Path.Combine(output,"uncreated.SLDPRT"),draft:JsonSerializer.SerializeToElement(draft,ModelingIrJson.Options)));
Check(!invalid["success"]!.GetValue<bool>() && invalid["plan_id"] is null,"公开编译保持未知关键事实拒绝及无计划句柄");
Check(invalid["readiness"]!["source_gap_count"]!.GetValue<int>()==15,"既有编译调用直接带回集中缺口清单");
var receipt=invalid["artifact_id"]!.GetValue<string>();
var page1=Node(store.Read(receipt,"/readiness/source_gaps",0,10));
var page2=Node(store.Read(receipt,"/readiness/source_gaps",10,10));
Check(page1["next_offset"]!.GetValue<int>()==10 && page2["returned"]!.GetValue<int>()==5 && page2["next_offset"] is null,"全量缺口分页可读且没有静默丢失");
var good=Node(CompactModelingTools.CreatePlan(new(),workflow,store,Path.Combine(output,"plate.SLDPRT"),text:"80 x 50 x 20 mm plate"));
Check(good["success"]!.GetValue<bool>() && good["plan_id"] is not null && !good.ContainsKey("readiness"),"无问题简单件不增加集中提问负担");
var dry=Node(await CompactModelingTools.BuildModel(workflow,new(),store,planId:good["plan_id"]!.GetValue<string>(),dryRun:true));
Check(dry["status"]!.GetValue<string>()=="dry_run_passed" && executor.Calls==0,"计划预演不调用原生执行器");
Check(!Directory.EnumerateFiles(output,"*.SLDPRT",SearchOption.AllDirectories).Any(),"全部专项检查未创建CAD文件");
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new {version="26.10.11mini",count=checks.Count,checks,native_started=false},ModelingIrJson.Options));

sealed class ForbiddenExecutor:IModelingExecutor
{
    public int Calls {get;private set;}
    public Task<ExecutorHealth> HealthAsync(CancellationToken token=default)=>throw new Exception("禁止原生健康检查");
    public Task<ExecutionResult> ExecuteAsync(ModelingPlan plan,bool dryRun,CancellationToken token=default) {Calls++;throw new Exception("禁止原生执行");}
    public Task<ModelInspection> InspectAsync(ModelInspectionRequest request,CancellationToken token=default)=>throw new Exception("禁止原生检查");
}
