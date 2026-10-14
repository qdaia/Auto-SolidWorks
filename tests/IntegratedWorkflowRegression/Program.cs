using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Ingestion;
using CadModeling.Drawing.Providers.Abstractions;

if(args.Length!=3)throw new ArgumentException("Supply unused output directory, ingestion inventory, and previously compiled draft.");
var output=Path.GetFullPath(args[0]);
if(Directory.Exists(output))throw new IOException("Preserve previous evidence; supply a fresh output directory.");
Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS: "+name);}
void Reject(Action action,string name){try{action();}catch(Exception e)when(e is ArgumentException or IOException or InvalidDataException or InvalidOperationException or JsonException){Check(true,name);return;}throw new Exception("Accepted: "+name);}
JsonObject Node(object value)=>JsonSerializer.SerializeToNode(value,ModelingIrJson.Options)!.AsObject();
string Sha(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

var originalPath=Path.GetFullPath(args[1]);
var originalInventorySha=Sha(originalPath);
var original=DrawingOmissionValidation.Load(originalPath,originalInventorySha);
var copyRoot=Path.Combine(output,"copied-source-inventory");Directory.CreateDirectory(copyRoot);
var originalRoot=Path.GetDirectoryName(originalPath)!;
foreach(var file in Directory.GetFiles(originalRoot,"*",SearchOption.AllDirectories))
{
    var destination=Path.Combine(copyRoot,Path.GetRelativePath(originalRoot,file));
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination,false);
}
string? Relocate(string? path)=>path is null?null:Path.Combine(copyRoot,Path.GetRelativePath(originalRoot,path));
var source=Path.Combine(copyRoot,"source-original.png");File.Copy(original.SourcePath,source,false);
var inventory=original with {SourcePath=source,
    Regions=original.Regions.Select(r=>r with{ImagePath=Relocate(r.ImagePath)!}).ToArray(),
    Pages=original.Pages.Select(p=>p with{ObservationPath=Relocate(p.ObservationPath),OverviewPath=Relocate(p.OverviewPath)}).ToArray()};
var path=Path.Combine(copyRoot,"test-inventory.json");
void WriteInventory(DrawingOmissionInventory value,string target)=>File.WriteAllText(target,JsonSerializer.Serialize(value,ModelingIrJson.Options));
WriteInventory(inventory,path);
var store=new CompactArtifactStore(Path.Combine(output,"artifacts"),"integrated-regression");
var prepared=Node(PendingDrawingReview.Prepare(store,path,Sha(path),"source-revision-test"));
var contextId=prepared["drawing_context_input_id"]!.GetValue<string>();
var context=CompactModelInputs.Decode<DrawingPlanContext>(store.LoadInput(contextId,"context"));
Check(context.RequireCompleteBindings&&context.RequireInterpretationReview,"generated pending context retains both strict source gates");
Check(context.Features.Count==0&&context.Dimensions.Count==0&&context.InterpretationReviews.Count==0&&context.OmissionReview!.Candidates.Count==0,"workspace infers no geometry, dimensions, interpretations or accepted candidates");
Check(context.OmissionReview!.Regions.Count==inventory.Regions.Count&&context.OmissionReview.Regions.All(r=>r.State==DrawingReviewState.Unresolved),"all original raw regions remain unresolved");
var receipt=Path.Combine(output,"artifacts",prepared["worklist_artifact_id"]!.GetValue<string>()+".json");
var workspace=JsonNode.Parse(File.ReadAllText(receipt))!["payload"]!;
var ids=workspace["worklists"]!.AsArray().SelectMany(w=>w!["candidates"]!.AsArray()).Select(c=>c!["id"]!.GetValue<string>()).ToArray();
Check(ids.Length==ids.Distinct().Count()&&ids.ToHashSet().SetEquals(inventory.Candidates.Select(c=>c.Id)),"every candidate appears exactly once including geometry and residual ink");
Check(workspace["raw_regions"]!.AsArray().Count==inventory.Regions.Count,"workspace retains all raw source images");
Check(prepared["source_revision_id"]!.GetValue<string>()=="source-revision-test","source revision is returned for task recovery identity");
var repeated=Node(PendingDrawingReview.Prepare(store,path,Sha(path),"source-revision-test"));
Check(repeated["drawing_context_input_id"]!.GetValue<string>()==contextId&&repeated["worklist_artifact_id"]!.GetValue<string>()==prepared["worklist_artifact_id"]!.GetValue<string>(),"identical preparation reuses immutable artifacts");
var validDraft=JsonSerializer.Deserialize<GenericModelDraft>(File.ReadAllText(args[2]),ModelingIrJson.Options)!;
Check(new GenericPlanCompiler().Compile(validDraft).Success,"reference draft remains compilable before pending-context substitution");
var blocked=new GenericPlanCompiler().Compile(validDraft with{DrawingContext=context});
Check(!blocked.Success&&blocked.Diagnostics.Any(d=>d.Code.StartsWith("DRAWING_OMISSION")||d.Code.StartsWith("SOURCE_")),"pending visual review cannot compile as a valid source-backed plan");
Reject(()=>PendingDrawingReview.Prepare(store,path,new string('0',64)),"stale inventory identity rejected");
Reject(()=>PendingDrawingReview.Prepare(store,path,Sha(path),expectedSourcePath:Path.Combine(copyRoot,"different.png")),"ingestion response and inventory source-path mismatch rejected");
Reject(()=>PendingDrawingReview.Prepare(store,path,Sha(path),expectedSourceSha256:new string('0',64)),"ingestion response and inventory source-hash mismatch rejected");
var incomplete=Path.Combine(copyRoot,"incomplete.json");WriteInventory(inventory with{CompleteExtraction=false},incomplete);
Reject(()=>PendingDrawingReview.Prepare(store,incomplete,Sha(incomplete)),"incomplete extraction cannot create full pending worklist");
var duplicate=Path.Combine(copyRoot,"duplicate.json");WriteInventory(inventory with{Candidates=inventory.Candidates.Append(inventory.Candidates[0]).ToArray()},duplicate);
Reject(()=>PendingDrawingReview.Prepare(store,duplicate,Sha(duplicate)),"duplicate candidate IDs rejected through original validator");
var bytes=File.ReadAllBytes(source);File.AppendAllText(source,"changed");
Reject(()=>store.LoadInput(contextId,"context"),"changed source invalidates already prepared context handle");
Reject(()=>PendingDrawingReview.Prepare(store,path,Sha(path)),"changed original image rejected before preparation");
File.WriteAllBytes(source,bytes);
var raw=inventory.Regions[0].ImagePath;bytes=File.ReadAllBytes(raw);File.AppendAllText(raw,"changed");
Reject(()=>PendingDrawingReview.Prepare(store,path,Sha(path)),"changed raw review image rejected through original validator");
File.WriteAllBytes(raw,bytes);

var ingested=new DrawingIngestionResponse{Outcome=IngestionOutcome.Succeeded,OmissionInventoryPath=path,OmissionInventorySha256=Sha(path),SourceFactsRevisionId="source-revision-test",
    SourceManifest=new(){SourcePath=source,SourceSha256=inventory.SourceSha256}};
var service=new FakeIngestion{Result=ingested};
var read=Node(await CompactModelingTools.ReadDrawing(service,store,source));
Check(service.Calls==1&&read["review_workspace"]?["drawing_context_input_id"] is not null,"one default read call returns usable pending-context and worklist handles");
Check(read["outcome"]!.GetValue<string>()=="Succeeded"&&read["review_workspace_status"]!.GetValue<string>()=="pending_independent_visual_review","ingestion success remains distinct from pending interpretation");
var optOut=Node(await CompactModelingTools.ReadDrawing(service,store,source,prepareReview:false));
Check(!optOut.ContainsKey("review_workspace")&&!optOut.ContainsKey("review_workspace_status"),"explicit opt-out retains extraction-only response shape");
var full=Node(await CompactModelingTools.ReadDrawing(service,store,source,responseDetail:"full"));
Check(full.ContainsKey("pages")&&full.ContainsKey("source_manifest")&&full.ContainsKey("review_workspace"),"full response preserves extraction evidence alongside pending workspace");
var bad=Node(await CompactModelingTools.ReadDrawing(new FakeIngestion{Result=ingested with{OmissionInventorySha256=new string('0',64)}},store,source));
Check(bad["outcome"]!.GetValue<string>()=="Succeeded"&&bad["review_workspace_status"]!.GetValue<string>()=="blocked"&&bad["artifact_id"] is not null&&bad["review_workspace"] is null,"failed preparation retains original ingestion receipt without offering a context handle");
var rejected=Node(await CompactModelingTools.ReadDrawing(new FakeIngestion{Result=ingested with{Outcome=IngestionOutcome.Rejected}},store,source));
Check(rejected["outcome"]!.GetValue<string>()=="Rejected"&&rejected["review_workspace_status"]!.GetValue<string>()=="not_prepared","rejected ingestion is never converted to prepared source");

var diagnosticList=new JsonArray(Enumerable.Range(0,20).Select(i=>(JsonNode?)new JsonObject{["code"]="TRACE_"+i,["severity"]=0,["message"]="information"}).ToArray());
diagnosticList.Add(new JsonObject{["code"]="SOURCE_FATAL",["severity"]=2,["message"]="must fix"});
diagnosticList.Add(new JsonObject{["code"]="WARNING",["severity"]=1,["message"]="review risk"});
var summary=CompactResponses.Summarize(new JsonObject{["success"]=false,["diagnostics"]=diagnosticList});
Check(summary["diagnostics"]![0]!["code"]!.GetValue<string>()=="SOURCE_FATAL"&&summary["diagnostics"]![1]!["code"]!.GetValue<string>()=="WARNING","errors and warnings precede direction traces within bounded summary");
Check(summary["diagnostics_count"]!.GetValue<int>()==22&&summary["diagnostics_actionable_count"]!.GetValue<int>()==22&&summary["diagnostics_truncated"]!.GetValue<bool>()&&summary["success"]!.GetValue<bool>()==false,"priority does not erase counts, truncation or failed status");
Check(summary["diagnostics"]![2]!["code"]!.GetValue<string>()=="TRACE_0","same-severity ordering remains stable");
var stringSeverity=CompactResponses.Summarize(new JsonObject{["diagnostics"]=new JsonArray(new JsonObject{["code"]="info",["severity"]="Info"},new JsonObject{["code"]="error",["severity"]="Error"})});
Check(stringSeverity["diagnostics"]![0]!["code"]!.GetValue<string>()=="error","string and numeric severity encodings both prioritize actual errors");
Check(Sha(originalPath)==originalInventorySha&&Sha(original.SourcePath)==original.SourceSha256.ToLowerInvariant(),"original inventory and image remain unchanged");
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{success=true,count=checks.Count,checks,candidate_count=ids.Length,native_execution=false},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Integrated workflow checks passed: "+checks.Count);

sealed class FakeIngestion:IEngineeringDrawingIngestionService
{
    public required DrawingIngestionResponse Result{get;init;}
    public int Calls{get;private set;}
    public Task<DrawingIngestionResponse> IngestAsync(DrawingIngestionRequest request,CancellationToken cancellationToken=default){Calls++;return Task.FromResult(Result);}
}
