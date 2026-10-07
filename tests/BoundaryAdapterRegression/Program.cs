using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

var output=Path.GetFullPath(args.Single());Directory.CreateDirectory(output);var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);checks.Add(name);Console.WriteLine("PASS: "+name);}
void Reject(Action action,string code,string name)
{try{action();}catch(InvalidOperationException e)when(e.Message.StartsWith(code,StringComparison.Ordinal)){Check(true,name);return;}throw new Exception("FAIL accepted: "+name);}
bool Same(string a,string b)=>a==b;
BoundaryCurveBinding Binding(string id,int segments=1)=>new(id,"feature-"+id,["feature-"+id,"sketch-"+id],Enumerable.Range(0,segments).Select(i=>"segment-"+id+"-"+i).ToArray());
BoundaryCurveState Row(BoundaryCurveBinding b,int tangent=0,bool all=false)=>new(b.FeatureReference,BoundaryCurveRepresentation.Feature,null,null,tangent,0,false,tangent==1?1:null,tangent==1?false:null,tangent==1&&all?true:null);
var request=new BoundaryDefinitionRequest([Binding("p0"),Binding("p1")],[Binding("g0"),Binding("g1")],SurfaceEndCondition.None,SurfaceEndCondition.None,true);
BoundaryDefinitionSnapshot State(BoundaryDefinitionRequest r)=>new("boundary",true,[
    new(true,32,r.Guides.Count>0?false:null,r.Profiles.Select((b,i)=>Row(b,BoundarySurfaceContract.ExpectedTangency(r,0,i,r.Profiles.Count),r.Guides.Count==0)).ToArray()),
    new(true,32,null,r.Guides.Select(b=>Row(b)).ToArray())]);
var snapshot=State(request);var port=new FakeBoundary(request,snapshot);
var oldFeature=new BoundaryCreatedFeature("old",0,false,false);
var newFeature=new BoundaryCreatedFeature("new",0,false,false);
var beforeFactory=new BoundaryFeatureInventory(true,[oldFeature]);
var afterFactory=new BoundaryFeatureInventory(true,[oldFeature,newFeature]);
Check(BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory,afterFactory,null,Same)=="new","原生空返回但同步新增唯一有效Boundary时绑定新增身份");
Check(BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory,afterFactory,"new",Same)=="new","正常返回仍与本次新增身份交叉核对");
Reject(()=>BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory,beforeFactory,null,Same),"BOUNDARY_NATIVE_CREATION：","仅有旧Boundary不能冒充工厂结果");
Reject(()=>BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory,afterFactory,"old",Same),"BOUNDARY_NATIVE_CREATION_UNVERIFIABLE","返回旧特征指针拒绝");
Reject(()=>BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory,afterFactory with{Complete=false},null,Same),"BOUNDARY_NATIVE_CREATION_UNVERIFIABLE","不完整工厂后库存拒绝");
Reject(()=>BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory with{Complete=false},afterFactory,null,Same),"BOUNDARY_NATIVE_CREATION_UNVERIFIABLE","不完整工厂前库存拒绝");
Reject(()=>BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory,new(true,[oldFeature,newFeature,new("other",0,false,false)]),null,Same),"BOUNDARY_NATIVE_CREATION_UNVERIFIABLE","两个新增Boundary身份有歧义拒绝");
Reject(()=>BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory,new(true,[oldFeature,newFeature with{ErrorCode=7}]),null,Same),"BOUNDARY_NATIVE_CREATION_UNVERIFIABLE","新增Boundary原生错误拒绝");
Reject(()=>BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory,new(true,[oldFeature,newFeature with{IsWarning=true}]),null,Same),"BOUNDARY_NATIVE_CREATION_UNVERIFIABLE","新增Boundary原生警告拒绝");
Reject(()=>BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory,new(true,[oldFeature,newFeature with{Suppressed=true}]),null,Same),"BOUNDARY_NATIVE_CREATION_UNVERIFIABLE","新增Boundary被抑制拒绝");
Reject(()=>BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory,new(true,[newFeature]),null,Same),"BOUNDARY_NATIVE_CREATION_UNVERIFIABLE","工厂删除已有Boundary拒绝");
Reject(()=>BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory,new(true,[oldFeature,newFeature,newFeature]),null,Same),"BOUNDARY_NATIVE_CREATION_UNVERIFIABLE","重复库存身份拒绝");
Check(BoundarySurfaceContract.ResolveCreatedFeature(beforeFactory,new(true,[oldFeature with{Reference="old-alias"},newFeature]),null,(a,b)=>a==b||new[]{a,b}.Order().SequenceEqual(new[]{"old","old-alias"}))=="new","旧特征原生等价包装变化不能冒充新增特征");
var receipt=BoundarySurfaceContract.Execute(port,request,Same);
Check(port.Creates==1 && port.ClearCount==1 && port.Reads==1,"选择、工厂与保留定义复核使用同一端口");
Check(port.Selected.Select(s=>s.Mark).SequenceEqual([8193,16385,8194,16386]),"2x2 曲线按方向和首尾角色选择");
Check(port.Appends.SequenceEqual([false,true,true,true]),"清空库存后首条替换、其余追加");
Check(port.Parameters==new BoundaryCreationParameters(2,2,2,false,1,false,true,false,true,false,true,false),"创建参数明确为 Boundary 曲面且不形成实体");
Check(port.CurveCalls.Count==4 && port.CurveCalls.All(c=>c.Tangency==0 && c.Draft==0 && c.Length==1 && c.ApplyAll),"每条曲线的声明控制到达工厂");
Check(port.DirectionCalls.SequenceEqual([(0,32,0,false,false),(1,32,0,false,false)]),"两方向全局影响、不裁剪、不闭合参数保留");
BoundaryDefinitionSnapshot Replace(int direction,int index,BoundaryCurveState curve)=>snapshot with{Directions=snapshot.Directions.Select((s,d)=>d==direction?s with{Curves=s.Curves.Select((c,i)=>i==index?curve:c).ToArray()}:s).ToArray()};
var normal=request with{Start=SurfaceEndCondition.NormalToProfile,End=SurfaceEndCondition.NormalToProfile};
var normalSnapshot=State(normal);BoundarySurfaceContract.VerifyDeclared(normalSnapshot,normal,Same);Check(true,"两方向网络保留首尾法向条件而不查询单方向专用控制");
var single=normal with{Guides=[]};var singleSnapshot=State(single);
BoundarySurfaceContract.VerifyDeclared(singleSnapshot,single,Same);Check(true,"单方向法向边界核对统一切向控制而不查询不可用裁剪字段");
var cold=State(single with{Start=SurfaceEndCondition.None,End=SurfaceEndCondition.None});
var fixedHash=new string('a',64);
var copyEvidence=new BoundaryControlCopyEvidence(fixedHash,fixedHash,fixedHash,fixedHash,false,false,false,cold,cold,singleSnapshot);
var observed=BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence,Same);
BoundarySurfaceContract.VerifyDeclared(observed,single,Same);Check(true,"字节相同且身份一致的无参数副本提供实际保存控制，原API的None不冒充源真值");
Reject(()=>BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence with{CopySha256=new string('b',64)},Same),"BOUNDARY_CONTROL_COPY_CHANGED","错误来源文件副本拒绝");
Reject(()=>BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence with{SourceAfterSha256=new string('b',64)},Same),"BOUNDARY_CONTROL_COPY_CHANGED","观察期间来源被改写拒绝");
Reject(()=>BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence with{CopyAfterSha256=new string('b',64)},Same),"BOUNDARY_CONTROL_COPY_CHANGED","副本被保存或改写后拒绝用于只读认证");
Reject(()=>BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence with{SourceSha256="not-a-hash"},Same),"BOUNDARY_CONTROL_COPY_CHANGED","缺失真实文件身份不能认证副本");
Reject(()=>BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence with{SourceWasModified=true},Same),"BOUNDARY_CONTROL_COPY_MUTATION","未保存来源不能借副本获得保存认证");
Reject(()=>BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence with{SourceIsModified=true},Same),"BOUNDARY_CONTROL_COPY_MUTATION","来源在观察期间变为修改状态时拒绝");
Reject(()=>BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence with{ParameterInputOccurred=true},Same),"BOUNDARY_CONTROL_COPY_MUTATION","改过参数的副本不能冒充原保存条件");
Reject(()=>BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence with{CopyAfter=singleSnapshot with{FeatureReference="other"}},Same),"BOUNDARY_CONTROL_COPY_IDENTITY","副本换成另一曲面特征拒绝");
var reordered=singleSnapshot with{Directions=[singleSnapshot.Directions[0] with{Curves=singleSnapshot.Directions[0].Curves.Reverse().ToArray()},singleSnapshot.Directions[1]]};
Reject(()=>BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence with{CopyAfter=reordered},Same),"BOUNDARY_CONTROL_COPY_IDENTITY","副本曲线顺序交换拒绝");
var incomplete=singleSnapshot with{Directions=[singleSnapshot.Directions[0] with{CompleteInventory=false},singleSnapshot.Directions[1]]};
Reject(()=>BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence with{CopyBefore=incomplete},Same),"BOUNDARY_CONTROL_COPY_INVENTORY","编辑前副本库存缺失也必须拒绝");
var changedLength=singleSnapshot with{Directions=[singleSnapshot.Directions[0] with{Curves=[singleSnapshot.Directions[0].Curves[0] with{TangentLength=1.000000001},singleSnapshot.Directions[0].Curves[1]]},singleSnapshot.Directions[1]]};
var actualLength=BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence with{CopyAfter=changedLength},Same);
Reject(()=>BoundarySurfaceContract.VerifyDeclared(actualLength,single,Same),"BOUNDARY_TANGENT_MISMATCH","副本读回保留实际完整精度，不能按显示1.00舍入成声明长度");
var copiedNone=BoundarySurfaceContract.VerifyCopiedSavedControls(copyEvidence with{CopyAfter=cold},Same);
Reject(()=>BoundarySurfaceContract.VerifyDeclared(copiedNone,single,Same),"BOUNDARY_END_CONDITION","副本实际为None时不能按请求补成法向");
BoundarySurfaceContract.VerifyRebuiltSourceInventory(singleSnapshot,cold,Same);
Check(true,"重建库存验证只认证来源身份，不能把API冷读None解释成已认证的端控制");
Reject(()=>BoundarySurfaceContract.VerifyRebuiltSourceInventory(singleSnapshot,reordered,Same),"BOUNDARY_CONTROL_COPY_IDENTITY","重建后轮廓顺序变化拒绝");
Reject(()=>BoundarySurfaceContract.VerifyRebuiltSourceInventory(singleSnapshot,incomplete,Same),"BOUNDARY_CONTROL_COPY_INVENTORY","重建后不完整来源库存拒绝");
var triple=request with{Profiles=[Binding("p0"),Binding("pm"),Binding("p1")]};
port=new(triple,State(triple));BoundarySurfaceContract.Execute(port,triple,Same);
Check(port.Selected.Select(s=>s.Mark).SequenceEqual([8193,24577,16385,8194,16386]),"中间曲线使用角色标记且严格保留源顺序");
port=new(request,snapshot){FailSelect=2};
Reject(()=>BoundarySurfaceContract.Execute(port,request,Same),"BOUNDARY_SELECTION_FAILED","选择失败拒绝创建");Check(port.Creates==0 && port.CurveCalls.Count==0,"选择失败不设置工厂控制");
port=new(request,snapshot){SelectionOverride=[new("unrelated",8193),new("feature-p1",16385),new("feature-g0",8194),new("feature-g1",16386)]};
Reject(()=>BoundarySurfaceContract.Execute(port,request,Same),"BOUNDARY_SELECTION_CONTRACT","相同数量及标记但错误实体拒绝");Check(port.Creates==0,"错误选择身份不调用原生工厂");
port=new(request,snapshot){SelectionOverride=[new("feature-p1",8193),new("feature-p0",16385),new("feature-g0",8194),new("feature-g1",16386)]};
Reject(()=>BoundarySurfaceContract.Execute(port,request,Same),"BOUNDARY_SELECTION_CONTRACT","工厂前拒绝曲线身份顺序交换");
port=new(request,snapshot){SelectionOverride=[]};Reject(()=>BoundarySurfaceContract.Execute(port,request,Same),"BOUNDARY_SELECTION_CONTRACT","选择库存缺失拒绝");
port=new(request,snapshot){SelectionOverride=[new("feature-p0",1),new("feature-p1",2),new("feature-g0",1),new("feature-g1",2)]};
Reject(()=>BoundarySurfaceContract.Execute(port,request,Same),"BOUNDARY_SELECTION_CONTRACT","旧方向标记不能冒充有序 Boundary 标记");
port=new(request,snapshot){FactoryResult=false};Reject(()=>BoundarySurfaceContract.Execute(port,request,Same),"BOUNDARY_NATIVE_CREATION","空工厂结果不替换为 Fill");Check(port.Reads==0,"工厂失败不生成成功定义回执");
void Declared(BoundaryDefinitionSnapshot value)=>BoundarySurfaceContract.VerifyDeclared(value,request,Same);
Reject(()=>Declared(snapshot with{SurfaceBodiesOnly=false}),"BOUNDARY_DEFINITION_UNVERIFIABLE","实体结果不能冒充曲面结果");
Reject(()=>Declared(snapshot with{FeatureReference=""}),"BOUNDARY_DEFINITION_UNVERIFIABLE","结果特征缺少持久身份拒绝");
Reject(()=>Declared(snapshot with{Directions=[snapshot.Directions[0]]}),"BOUNDARY_DEFINITION_UNVERIFIABLE","缺少方向库存拒绝");
Reject(()=>Declared(snapshot with{Directions=[snapshot.Directions[0] with{CompleteInventory=false},snapshot.Directions[1]]}),"BOUNDARY_INVENTORY_MISMATCH","不完整曲线族拒绝");
Reject(()=>Declared(snapshot with{Directions=[snapshot.Directions[0] with{Curves=[snapshot.Directions[0].Curves[0]]},snapshot.Directions[1]]}),"BOUNDARY_INVENTORY_MISMATCH","遗漏一条曲线拒绝");
Reject(()=>Declared(snapshot with{Directions=[snapshot.Directions[0] with{Influence=0},snapshot.Directions[1]]}),"BOUNDARY_DIRECTION_MISMATCH","ToNextCurve 枚举不能冒充 GlobalInfluence");
Reject(()=>Declared(snapshot with{Directions=[snapshot.Directions[0] with{TrimByD1=true},snapshot.Directions[1]]}),"BOUNDARY_DIRECTION_MISMATCH","未请求的方向 1 裁剪拒绝");
Reject(()=>Declared(snapshot with{Directions=[snapshot.Directions[0] with{TrimByD1=null},snapshot.Directions[1]]}),"BOUNDARY_DIRECTION_MISMATCH","可用的裁剪控制缺少观测时拒绝");
Reject(()=>Declared(Replace(0,0,Row(Binding("unrelated")))),"BOUNDARY_CURVE_IDENTITY","创建后的错误曲线身份拒绝");
Reject(()=>Declared(snapshot with{Directions=[snapshot.Directions[0] with{Curves=snapshot.Directions[0].Curves.Reverse().ToArray()},snapshot.Directions[1]]}),"BOUNDARY_CURVE_IDENTITY","边界曲线顺序不能按集合验收");
Reject(()=>Declared(Replace(0,1,snapshot.Directions[0].Curves[0])),"BOUNDARY_IDENTITY_AMBIGUOUS","重复原生曲线身份拒绝");
Reject(()=>Declared(Replace(0,0,Row(request.Profiles[0]) with{Representation=(BoundaryCurveRepresentation)99})),"BOUNDARY_CURVE_IDENTITY","未知原生曲线表示拒绝");
var sketchRows=snapshot with{Directions=snapshot.Directions.Select((d,dir)=>d with{Curves=d.Curves.Select((c,i)=>c with{Representation=BoundaryCurveRepresentation.Sketch,Reference="sketch-"+(dir==0?request.Profiles:request.Guides)[i].SourceId}).ToArray()}).ToArray()};
Declared(sketchRows);BoundarySurfaceContract.VerifyRetained(receipt,sketchRows,Same);Check(true,"完整 Feature 与 Sketch 包装变化仍核对同一来源");
var segment=Row(request.Profiles[0]) with{Representation=BoundaryCurveRepresentation.SingleSketchSegment,Reference="segment-p0-0",OwnerSketchReference="sketch-p0",OwnerSketchSegmentCount=1};
Declared(Replace(0,0,segment));Check(true,"唯一非构造草图段可代表完整单段源曲线");
Reject(()=>Declared(Replace(0,0,segment with{OwnerSketchSegmentCount=2})),"BOUNDARY_CURVE_IDENTITY","多段草图中的单段不可替代整个曲线");
Reject(()=>Declared(Replace(0,0,segment with{OwnerSketchReference="sketch-other"})),"BOUNDARY_CURVE_IDENTITY","同几何但来源草图错误拒绝");
Reject(()=>Declared(Replace(0,0,segment with{Reference="segment-other"})),"BOUNDARY_CURVE_IDENTITY","单段自身身份也必须与源库存一致");
Reject(()=>Declared(Replace(0,0,segment with{OwnerSketchSegmentCount=null})),"BOUNDARY_CURVE_IDENTITY","不能确定草图段库存时拒绝");
Reject(()=>Declared(Replace(0,0,Row(request.Profiles[0]) with{Tangency=1})),"BOUNDARY_END_CONDITION","未请求法向条件不能悄然添加");
Reject(()=>Declared(Replace(0,0,Row(request.Profiles[0]) with{DraftAngle=.2})),"BOUNDARY_DRAFT_MISMATCH","创建后意外拔模拒绝");
Reject(()=>Declared(Replace(0,0,Row(request.Profiles[0]) with{DraftAngle=double.NaN})),"BOUNDARY_DRAFT_MISMATCH","非有限拔模读回拒绝");
Reject(()=>Declared(Replace(0,0,Row(request.Profiles[0]) with{ReverseDraft=true})),"BOUNDARY_DRAFT_MISMATCH","未请求拔模反向拒绝");
Reject(()=>Declared(Replace(0,0,Row(request.Profiles[0]) with{TangentLength=1})),"BOUNDARY_CONTROL_UNAVAILABLE","None 条件不可伪造可用切向长度");
foreach(var value in new double?[]{null,0,-1,.1,double.NaN,double.PositiveInfinity})
{
    var bad=normalSnapshot with{Directions=[normalSnapshot.Directions[0] with{Curves=[normalSnapshot.Directions[0].Curves[0] with{TangentLength=value},normalSnapshot.Directions[0].Curves[1]]},normalSnapshot.Directions[1]]};
    Reject(()=>BoundarySurfaceContract.VerifyDeclared(bad,normal,Same),"BOUNDARY_TANGENT_MISMATCH","法向切向长度不保留拒绝："+value);
}
var reverse=normalSnapshot with{Directions=[normalSnapshot.Directions[0] with{Curves=[normalSnapshot.Directions[0].Curves[0] with{ReverseTangent=true},normalSnapshot.Directions[0].Curves[1]]},normalSnapshot.Directions[1]]};
Reject(()=>BoundarySurfaceContract.VerifyDeclared(reverse,normal,Same),"BOUNDARY_TANGENT_MISMATCH","法向切向方向反转拒绝");
var downgraded=normalSnapshot with{Directions=[snapshot.Directions[0],snapshot.Directions[1]]};
Reject(()=>BoundarySurfaceContract.VerifyDeclared(downgraded,normal,Same),"BOUNDARY_END_CONDITION","法向条件降级为 None 拒绝");
BoundarySurfaceContract.VerifyRetained(receipt,snapshot,Same);Check(true,"保存回执重复验证源绑定和保留控制");
Reject(()=>BoundarySurfaceContract.VerifyRetained(receipt,snapshot with{FeatureReference="new-boundary"},Same),"SAVED_BOUNDARY_IDENTITY","保存后替换结果特征身份拒绝");
var roundtrip=JsonSerializer.Deserialize<BoundaryDefinitionReceipt>(JsonSerializer.Serialize(receipt,ModelingIrJson.Options),ModelingIrJson.Options)!;
BoundarySurfaceContract.VerifyRetained(roundtrip,snapshot,Same);Check(true,"完整来源与控制回执经过 JSON 后仍可复核");
void Invalid(BoundaryDefinitionRequest value,string code,string name)
{var p=new FakeBoundary(value,State(value));Reject(()=>BoundarySurfaceContract.Execute(p,value,Same),code,name);Check(p.ClearCount==0 && p.Creates==0,name+"在调用端口前拒绝");}
Invalid(request with{Profiles=[request.Profiles[0]]},"BOUNDARY_REQUEST_INVALID","方向 1 曲线不足");
Invalid(request with{Start=(SurfaceEndCondition)99},"BOUNDARY_REQUEST_INVALID","未知端条件");
Invalid(request with{Guides=[request.Profiles[0]]},"BOUNDARY_SOURCE_AMBIGUOUS","跨方向重复来源 ID");
Invalid(request with{Guides=[Binding("another") with{FeatureReference="feature-p0",WholeCurveAliases=["feature-p0"]}]},"BOUNDARY_IDENTITY_AMBIGUOUS","不同来源 ID 使用同一原生特征");
Invalid(request with{Guides=[Binding("another") with{WholeCurveAliases=["feature-another","sketch-p0"]}]},"BOUNDARY_IDENTITY_AMBIGUOUS","不同特征共享源草图别名");
Invalid(request with{Profiles=[request.Profiles[0] with{SegmentReferences=["same","same"]},request.Profiles[1]]},"BOUNDARY_IDENTITY_AMBIGUOUS","重复源段库存");
Invalid(request with{Guides=Enumerable.Range(0,129).Select(i=>Binding("limit"+i)).ToArray()},"BOUNDARY_REQUEST_INVALID","方向 2 曲线超过上限");
var large=request with{Profiles=Enumerable.Range(0,128).Select(i=>Binding("p"+i)).ToArray(),Guides=Enumerable.Range(0,128).Select(i=>Binding("g"+i)).ToArray()};
port=new(large,State(large));BoundarySurfaceContract.Execute(port,large,Same);Check(port.Parameters!.ProfileCount==128 && port.Selected.Count==256,"最大声明曲线族不溢出原生 short 参数");
var bowed=JsonSerializer.Deserialize<GenericModelDraft>(File.ReadAllText("plugins/auto-solidworks/skills/auto-solidworks/references/examples/surface-boundary.json"),ModelingIrJson.Options)!;
var compiler=new GenericPlanCompiler();
var compiled=compiler.Compile(bowed,Path.Combine(output,"bowed.SLDPRT"));Check(compiled.Success && compiled.Diagnostics.Any(d=>d.Code=="SURFACE_CORNERS_CHECKED"),"跨平面弯曲 2x2 样例独立编译及四角合同通过");
File.WriteAllText(Path.Combine(output,"bowed-plan.json"),ModelingIrJson.Serialize(compiled.Plan!));
GenericOperationDraft Spatial(string id,params Vector3[] points)=>new(){Id=id,Name="空间曲线_"+id,Type=GenericOperationKind.NativeFeature,Feature=new(){Kind=NativeFeatureKind.SpatialCurve,SpatialCurve=new(){PointsMm=points}}};
var spatial=new GenericModelDraft{Name="空间边界离线样例",SourceText="四条显式毫米空间曲线，四角连接；不代表原生曲面成功。",ExpectedSolidBodyCount=0,ExpectedSurfaceBodyCount=1,Operations=[
    Spatial("p0",new(0,0,0),new(20,5,4),new(40,0,0)),Spatial("p1",new(0,0,20),new(20,8,25),new(40,0,20)),
    Spatial("g0",new(0,0,0),new(-4,3,10),new(0,0,20)),Spatial("g1",new(40,0,0),new(44,3,10),new(40,0,20)),
    new(){Id="patch",Name="空间边界曲面",Type=GenericOperationKind.NativeFeature,DependsOn=["p0","p1","g0","g1"],Feature=new(){Kind=NativeFeatureKind.SurfaceBoundary,ProfileIds=["p0","p1"],GuideIds=["g0","g1"],Surface=new(){RequireBoundaryCornerMatch=true}}}]};
var spatialCompiled=compiler.Compile(spatial,Path.Combine(output,"spatial.SLDPRT"));Check(spatialCompiled.Success && spatialCompiled.Diagnostics.Any(d=>d.Code=="SURFACE_CORNERS_CHECKED"),"真实三维点的空间曲线样例通过四角合同");
File.WriteAllText(Path.Combine(output,"spatial-draft.json"),JsonSerializer.Serialize(spatial,ModelingIrJson.Options));File.WriteAllText(Path.Combine(output,"spatial-plan.json"),ModelingIrJson.Serialize(spatialCompiled.Plan!));
var changed=spatial.Operations.ToArray();changed[2]=Spatial("g0",new(100,0,0),new(-4,3,10),new(0,0,20));
var gap=compiler.Compile(spatial with{Operations=changed},Path.Combine(output,"gap.SLDPRT"));Check(!gap.Success && gap.Diagnostics.Any(d=>d.Code=="SURFACE_CORNER_GAP"),"错位空间角点拒绝且不自动吸附");
changed=spatial.Operations.ToArray();changed[0]=Spatial("p0",new(0,0,0),new(20,5,4),new(0,0,0));
var degenerate=compiler.Compile(spatial with{Operations=changed},Path.Combine(output,"degenerate.SLDPRT"));Check(!degenerate.Success,"重合端点空间曲线不能通过四角合同");
var tampered=spatialCompiled.Plan!.Operations.ToArray();tampered[^1]=tampered[^1] with{DependsOn=["p0","p1","g0"]};
var missing=new ModelingIrValidator().Validate(spatialCompiled.Plan with{Operations=tampered});
Check(!missing.IsValid && missing.Diagnostics.Any(d=>d.Code=="SURFACE_DEPENDENCY"),"编译后 IR 缺少曲线依赖时拒绝");
Check(!Directory.EnumerateFiles(output,"*.SLDPRT").Any(),"样例仅写计划 JSON，未生成原生零件");
Check(typeof(IBoundarySurfaceSession).Assembly.GetReferencedAssemblies().All(a=>!a.Name!.StartsWith("SolidWorks.Interop")),"创建核心与模拟端口没有 COM 依赖");
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{passed=checks.Count,checks,scope="boundary_factory_selection_and_saved_controls_core_fake_port_spatial_fixtures",native_acceptance="not_run"},ModelingIrJson.Options));

sealed class FakeBoundary(BoundaryDefinitionRequest request,BoundaryDefinitionSnapshot state) : IBoundarySurfaceSession
{
    public int ClearCount,Creates,Reads,FailSelect=-1;
    public bool FactoryResult=true;
    public BoundaryCreationParameters? Parameters;
    public List<BoundarySelection> Selected=[];
    public List<bool> Appends=[];
    public IReadOnlyList<BoundarySelection>? SelectionOverride;
    public List<(int Direction,int Index,int Tangency,double Draft,double Length,bool ApplyAll)> CurveCalls=[];
    public List<(int Direction,int Influence,int Trim,bool Closed,bool Split)> DirectionCalls=[];
    public void ClearSelection(){ClearCount++;Selected.Clear();}
    public bool Select(string id,int mark,bool append)
    {if(Selected.Count==FailSelect)return false;Appends.Add(append);Selected.Add(new(request.Profiles.Concat(request.Guides).Single(c=>c.SourceId==id).FeatureReference,mark));return true;}
    public IReadOnlyList<BoundarySelection> CaptureSelection()=>SelectionOverride??Selected;
    public void SetCurveData(short d,short i,short tangent,double draft,double length,bool all)=>CurveCalls.Add((d,i,tangent,draft,length,all));
    public void SetDirectionData(short d,int influence,short trim,bool closed,bool split)=>DirectionCalls.Add((d,influence,trim,closed,split));
    public bool Create(BoundaryCreationParameters p){Creates++;Parameters=p;return FactoryResult;}
    public BoundaryDefinitionSnapshot CaptureDefinition(){Reads++;return state;}
}
