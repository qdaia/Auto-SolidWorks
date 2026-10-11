using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;

var output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool condition,string name) { if(!condition) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS: "+name); }
void Reject(Action action,string name)
{
    try { action(); } catch(Exception e) when(e is ArgumentException or InvalidOperationException or IOException or JsonException)
    { Check(true,name); return; } throw new Exception("未拒绝："+name);
}
JsonElement Json(string value)=>JsonSerializer.Deserialize<JsonElement>(value);
JsonObject Node(object value)=>JsonSerializer.SerializeToNode(value,ModelingIrJson.Options)!.AsObject();
var store=new CompactArtifactStore(Path.Combine(output,"store"),"producer-A");
var initial=Json("{\"source_text\":\"测试\",\"operations\":[],\"name\":\"草案\"}");
var first=Node(CompactModelInputs.Store(store,"draft",initial,null,null));
var id=first["input_id"]!.GetValue<string>();
Check(!first["compiled"]!.GetValue<bool>() && first["status"]!.GetValue<string>()=="stored_not_validated","保存不是编译或验收成功");
Check(Node(CompactModelInputs.Store(store,"draft",initial,null,null))["input_id"]!.GetValue<string>()==id,"同一完整输入产生相同句柄");
var edited=Node(CompactModelInputs.Store(store,"draft",null,id,Json("[{\"op\":\"test\",\"path\":\"/name\",\"value\":\"草案\"},{\"op\":\"replace\",\"path\":\"/name\",\"value\":\"修正草案\"},{\"op\":\"add\",\"path\":\"/assumptions\",\"value\":[\"已核对\"]},{\"op\":\"add\",\"path\":\"/assumptions/-\",\"value\":\"原值保留\"}]")));
var editedId=edited["input_id"]!.GetValue<string>();
Check(editedId!=id && store.LoadInput(id,"draft")["name"]!.GetValue<string>()=="草案","增量产生新句柄且保留原输入");
Check(store.LoadInput(editedId,"draft")["assumptions"]!.AsArray().Count==2,"数组追加保留原有记录");
var removed=Node(CompactModelInputs.Store(store,"draft",null,editedId,Json("[{\"op\":\"remove\",\"path\":\"/assumptions/0\"}]")));
Check(store.LoadInput(removed["input_id"]!.GetValue<string>(),"draft")["assumptions"]!.AsArray().Count==1,"数组移除准确定位");
var count=Directory.GetFiles(Path.Combine(output,"store")).Length;
Reject(()=>CompactModelInputs.Store(store,"draft",null,id,Json("[{\"op\":\"replace\",\"path\":\"/name\",\"value\":\"不应保存\"},{\"op\":\"test\",\"path\":\"/source_text\",\"value\":\"错误\"}]")),"失败前置条件拒绝整批修改");
Check(count==Directory.GetFiles(Path.Combine(output,"store")).Length && store.LoadInput(id,"draft")["name"]!.GetValue<string>()=="草案","失败修改不写入新工件且不改原记录");
foreach(var patch in new[]{
    "[{\"op\":\"replace\",\"path\":\"/missing\",\"value\":1}]",
    "[{\"op\":\"add\",\"path\":\"/diameter_typo\",\"value\":1}]",
    "[{\"op\":\"remove\",\"path\":\"/source_text\"}]",
    "[{\"op\":\"copy\",\"path\":\"/name\",\"value\":1}]",
    "[{\"op\":\"replace\",\"path\":\"\",\"value\":{}}]",
    "[{\"op\":\"replace\",\"path\":\"/bad~2\",\"value\":1}]",
    "[{\"op\":\"replace\",\"path\":\"/operations/01\",\"value\":{}}]",
    "[{\"op\":\"replace\",\"path\":\"/operations/-\",\"value\":{}}]",
    "[{\"op\":\"add\",\"path\":\"/operations/99\",\"value\":{}}]",
    "[{\"op\":\"remove\",\"path\":\"/name\",\"value\":1}]",
    "[{\"op\":\"replace\",\"path\":\"/name\"}]",
    "[{\"op\":\"test\",\"path\":\"/name\",\"value\":\"草案\",\"unknown\":1}]",
    "[{\"op\":\"test\",\"op\":\"replace\",\"path\":\"/name\",\"value\":\"草案\"}]",
    "[]", "{}"}) Reject(()=>CompactModelInputs.Store(store,"draft",null,id,Json(patch)),"非法增量拒绝 "+checks.Count);
Reject(()=>CompactModelInputs.Store(store,"draft",Json("{\"source_text\":\"测试\",\"operations\":[],\"unknown\":1}"),null,null),"首次输入未知字段拒绝");
Reject(()=>CompactModelInputs.Store(store,"unknown",initial,null,null),"未知输入类别拒绝");
Reject(()=>CompactModelInputs.Store(store,"draft",initial,id,null),"完整输入与基线同时提供拒绝");
Reject(()=>CompactModelInputs.Store(store,"draft",null,null,null),"缺少输入拒绝");
Reject(()=>CompactModelInputs.Store(store,"draft",initial,null,Json("[]")),"首次输入附带修改拒绝");
Reject(()=>CompactModelInputs.Store(store,"draft",null,id,null),"基线缺少修改拒绝");
Reject(()=>store.LoadInput("../"+id,"draft"),"输入句柄路径穿越拒绝");
Reject(()=>store.LoadInput(store.SaveResult(new{ok=true}).ArtifactId,"draft"),"结果句柄不能代替草案");
Reject(()=>new CompactArtifactStore(Path.Combine(output,"store"),"producer-B").LoadInput(id,"draft"),"生产者身份变化拒绝复用输入");
Check(new CompactArtifactStore(Path.Combine(output,"store"),"producer-A").LoadInput(id,"draft")["name"]!.GetValue<string>()=="草案","服务重启同版本仍可复用输入");
var drawing=Path.Combine(output,"source.png"); File.WriteAllText(drawing,"original");
var sourceModel=Path.Combine(output,"source.SLDPRT"); File.WriteAllText(sourceModel,"source model");
var sourcedDraft=JsonSerializer.SerializeToElement(new GenericModelDraft{SourceText="测试",Operations=[],SourceModelPath=sourceModel},ModelingIrJson.Options);
var sourcedReceipt=Node(CompactModelInputs.Store(store,"draft",sourcedDraft,null,null));
File.AppendAllText(sourceModel,"changed");
Reject(()=>store.LoadInput(sourcedReceipt["input_id"]!.GetValue<string>(),"draft"),"草案来源模型变化拒绝复用");
var checkpoint=Path.Combine(output,"checkpoint.SLDPRT");File.WriteAllText(checkpoint,"saved prefix");
var checkpointManifest=Path.Combine(output,"checkpoint.json");
var checkpointPlan=new RuleBasedTextCompiler().Compile("80 x 50 x 20 mm plate",Path.Combine(output,"never-created.SLDPRT")).Plan!;
File.WriteAllText(checkpointManifest,JsonSerializer.Serialize(new ModelingCheckpointManifest{NativePath=checkpoint,NativeSha256="identity",OriginalPlan=checkpointPlan,FeatureReferences=[],Geometry=new(1,6,12,1,1,new(0,0,0),new(1,1,1))},ModelingIrJson.Options));
var recoveryDraft=JsonSerializer.SerializeToElement(new GenericModelDraft{SourceText="测试",Operations=[],Recovery=new(){ResumeManifestPath=checkpointManifest}},ModelingIrJson.Options);
var recoveryReceipt=Node(CompactModelInputs.Store(store,"draft",recoveryDraft,null,null));
File.AppendAllText(checkpoint,"changed");
Reject(()=>store.LoadInput(recoveryReceipt["input_id"]!.GetValue<string>(),"draft"),"输入恢复前缀变化拒绝复用");
File.WriteAllText(checkpoint,"saved prefix");File.AppendAllText(checkpointManifest," ");
Reject(()=>store.LoadInput(recoveryReceipt["input_id"]!.GetValue<string>(),"draft"),"输入恢复清单变化拒绝复用");
var inventory=Path.Combine(output,"inventory.json");File.WriteAllText(inventory,"original inventory");
var context=JsonSerializer.SerializeToElement(new DrawingPlanContext{SourcePath=drawing,OmissionReview=new(){InventoryPath=inventory,InventorySha256="not-an-acceptance"}},ModelingIrJson.Options);
var contextReceipt=Node(CompactModelInputs.Store(store,"drawing_context",context,null,null));
var contextId=contextReceipt["input_id"]!.GetValue<string>();
Reject(()=>store.LoadInput(contextId,"draft"),"图纸上下文不能代替草案");
File.WriteAllText(drawing,"changed");Reject(()=>store.LoadInput(contextId,"context"),"源图变化拒绝复用图纸上下文");
File.WriteAllText(drawing,"original");File.AppendAllText(inventory,"changed");
Reject(()=>store.LoadInput(contextId,"context"),"遗漏清单变化拒绝复用图纸上下文");
File.WriteAllText(inventory,"original inventory");
Reject(()=>CompactModelInputs.Store(store,"drawing_context",Json("{\"source_path\":\"relative.png\"}"),null,null),"相对来源路径拒绝");
File.AppendAllText(edited["input_path"]!.GetValue<string>()," ");
Reject(()=>store.LoadInput(editedId,"draft"),"输入工件内容改动拒绝");
var read=Node(store.Read(id,"/name"));Check(read["returned"]!.GetValue<int>()>0,"完整输入按现有分页接口读取");
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{success=true,count=checks.Count,checks,native_started=false},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Model input offline checks passed: {checks.Count}");
