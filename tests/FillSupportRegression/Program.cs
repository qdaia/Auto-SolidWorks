using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

var output=Path.GetFullPath(args.Single());Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);checks.Add(name);Console.WriteLine("PASS: "+name);}
void Reject(Action run,string code,string name)
{
    try{run();}catch(InvalidOperationException e) when(e.Message.StartsWith(code,StringComparison.Ordinal)){Check(true,name);return;}
    throw new Exception("FAIL accepted: "+name);
}
bool Same(string a,string b)=>a==b;
var face=new FillSupportFace("support","source");
var contact=new FillBoundaryState("contact",EntityKind.Edge,SurfaceContact.Contact,false,[],FillSupportEvidence.NotRequired,null);
var tangent=new FillBoundaryState("tangent",EntityKind.Edge,SurfaceContact.Tangent,true,[face],FillSupportEvidence.UniqueExternalAdjacency,"support");
var curvature=tangent with{Reference="curvature",Contact=SurfaceContact.Curvature};
var snapshot=new FillDefinitionSnapshot("patch",2,true,false,false,false,true,[contact,tangent,curvature]);
var request=new FillBoundaryBinding[]{new("contact",SurfaceContact.Contact,null),new("tangent",SurfaceContact.Tangent,face),new("curvature",SurfaceContact.Curvature,face)};
void Declared(FillDefinitionSnapshot value)=>FillSurfaceContract.VerifyDeclared(value,request,2,true,Same);
void Retained(FillDefinitionSnapshot value)=>FillSurfaceContract.VerifyRetained(snapshot,value,Same);
FillDefinitionSnapshot Change(FillBoundaryState b)=>snapshot with{Boundaries=[contact,tangent,b]};
Declared(snapshot);Check(true,"混合接触、相切和曲率边界与独立源绑定相符");
Retained(snapshot with{Boundaries=[curvature,contact,tangent]});Check(true,"保存后边界重新排序仍按原生身份逐边核对");
var aliased=snapshot with{FeatureReference="PATCH",Boundaries=[contact with{Reference="CONTACT"},tangent with{Reference="TANGENT",SelectedSupportReference="SUPPORT",ExternalAdjacentFaces=[new("SUPPORT","SOURCE")]},curvature with{Reference="CURVATURE",SelectedSupportReference="SUPPORT",ExternalAdjacentFaces=[new("SUPPORT","SOURCE")]}]};
FillSurfaceContract.VerifyRetained(snapshot,aliased,(a,b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase));Check(true,"持久标识使用端口等价判断而非字节相等");
var restored=JsonSerializer.Deserialize<FillDefinitionSnapshot>(JsonSerializer.Serialize(snapshot,ModelingIrJson.Options),ModelingIrJson.Options)!;
Retained(restored);Check(true,"保存绑定与来源特征合同保留完整 JSON 回执");
Reject(()=>Declared(Change(curvature with{Contact=SurfaceContact.Tangent})),"FILL_CONTROL_MISMATCH","G2 不可被 G1 枚举冒充");
Reject(()=>Declared(Change(curvature with{Contact=SurfaceContact.Contact,SupportEvidence=FillSupportEvidence.NotRequired,SelectedSupportReference=null})),"FILL_CONTROL_MISMATCH","G2 不可静默降为接触");
Reject(()=>Declared(Change(curvature with{ExternalAdjacentFaces=[new("wrong","source")],SelectedSupportReference="wrong"})),"FILL_SUPPORT_MISMATCH","相同曲率枚举但支撑面错误仍拒绝");
Reject(()=>Declared(Change(curvature with{ExternalAdjacentFaces=[face with{OwnerReference="foreign"}]})),"FILL_SUPPORT_OWNER","支撑面来自错误特征时拒绝");
Reject(()=>Declared(Change(curvature with{ExternalAdjacentFaces=[face with{OwnerReference="patch"}]})),"FILL_SUPPORT_OWNER","填充结果面不得充当自身支撑证据");
Reject(()=>Declared(Change(curvature with{ExternalAdjacentFaces=[face with{OwnerReference=""}]})),"FILL_SUPPORT_OWNER","缺少来源特征身份时拒绝");
Reject(()=>Declared(Change(curvature with{CompleteAdjacency=false})),"FILL_SUPPORT_UNVERIFIABLE","不完整相邻面库存不能确认支撑");
Reject(()=>Declared(Change(curvature with{SupportEvidence=FillSupportEvidence.Unavailable,SelectedSupportReference=null})),"FILL_SUPPORT_UNVERIFIABLE","API 未提供支撑证据时拒绝");
Reject(()=>Declared(Change(curvature with{ExternalAdjacentFaces=[face,new("other","source")]})),"FILL_SUPPORT_AMBIGUOUS","两侧来源面不得猜测支撑方向");
Reject(()=>Declared(Change(curvature with{ExternalAdjacentFaces=[face,face]})),"FILL_IDENTITY_AMBIGUOUS","重复支撑面身份拒绝");
Reject(()=>Declared(Change(curvature with{ExternalAdjacentFaces=[]})),"FILL_SUPPORT_AMBIGUOUS","零个相邻来源面不能冒充唯一支撑");
Reject(()=>Declared(Change(curvature with{SelectedSupportReference="foreign"})),"FILL_SUPPORT_MISMATCH","选中的支撑不属于完整相邻面库存时拒绝");
Reject(()=>Declared(Change(curvature with{Kind=EntityKind.Feature})),"FILL_SUPPORT_UNVERIFIABLE","草图边界不能承诺 G2 支撑");
Reject(()=>Declared(snapshot with{Boundaries=[contact with{SelectedSupportReference="support"},tangent,curvature]}),"FILL_SUPPORT_INVALID","接触边界不得伪装已绑定 G2 支撑");
Reject(()=>Declared(snapshot with{CompleteInventory=false}),"FILL_DEFINITION_INCOMPLETE","缺少完整原生边界库存时拒绝");
Reject(()=>Declared(snapshot with{Boundaries=[contact,curvature]}),"FILL_BOUNDARY_MISMATCH","遗漏相切边界不能通过部分检查");
Reject(()=>Declared(snapshot with{Boundaries=[contact,tangent,curvature,curvature]}),"FILL_IDENTITY_AMBIGUOUS","重复边界原生身份拒绝");
Reject(()=>Declared(Change(curvature with{Reference="unrelated"})),"FILL_CONTROL_MISMATCH","同控制值的无关边界不能替代目标");
Reject(()=>Declared(Change(curvature with{Contact=(SurfaceContact)99})),"FILL_DEFINITION_INVALID","未知连续性枚举拒绝");
Reject(()=>Declared(snapshot with{Resolution=4}),"FILL_DEFINITION_INCOMPLETE","超出 API 范围的分辨率拒绝");
Reject(()=>Declared(snapshot with{Resolution=1}),"FILL_OPTIONS_MISMATCH","声明分辨率不得在创建后改变");
Reject(()=>Declared(snapshot with{Optimize=false}),"FILL_OPTIONS_MISMATCH","优化开关必须保留");
Reject(()=>Declared(snapshot with{Merge=true}),"FILL_OPTIONS_MISMATCH","创建独立曲面不得悄然合并实体");
Reject(()=>Declared(snapshot with{FormSolid=true}),"FILL_OPTIONS_MISMATCH","创建独立曲面不得悄然形成实体");
Reject(()=>Retained(snapshot with{FeatureReference="other"}),"SAVED_FILL_OPTIONS_MISMATCH","重开后特征身份改变时拒绝");
Reject(()=>Retained(snapshot with{Reverse=true}),"SAVED_FILL_OPTIONS_MISMATCH","保存后反向曲面开关改变时拒绝");
Reject(()=>Retained(Change(curvature with{Contact=SurfaceContact.Tangent})),"SAVED_FILL_CONTROL_MISMATCH","保存后曲率降级被回执捕获");
Reject(()=>Retained(Change(curvature with{ExternalAdjacentFaces=[new("new-face","source")],SelectedSupportReference="new-face"})),"SAVED_FILL_CONTROL_MISMATCH","保存后支撑换面被回执捕获");
Reject(()=>Retained(Change(curvature with{ExternalAdjacentFaces=[face with{OwnerReference="other-source"}]})),"SAVED_FILL_SUPPORT_MISMATCH","保存后来源特征变化被回执捕获");
var twoFace=Change(curvature with{ExternalAdjacentFaces=[face,new("second","source")],SupportEvidence=FillSupportEvidence.NativePerBoundaryDefinition});
Declared(twoFace);Check(true,"具备逐边原生定义证据的端口可处理两侧支撑");
Reject(()=>Declared(Change(curvature with{SupportEvidence=FillSupportEvidence.NotRequired})),"FILL_SUPPORT_UNVERIFIABLE","曲率边界不能删除支撑验收要求");
var initial=snapshot with{Boundaries=[contact,tangent,curvature with{Contact=SurfaceContact.Contact,SupportEvidence=FillSupportEvidence.NotRequired,SelectedSupportReference=null,ExternalAdjacentFaces=[]}]};
var session=new FakeSession(initial,snapshot);
FillSurfaceContract.EnsureControls(session,request,2,true,Same);
Check(session.Sets==1 && session.Commits==1 && session.Captures==2,"连续性修改后提交并重新捕获独立定义");
session=new(initial,snapshot){SetterResult=false};
Reject(()=>FillSurfaceContract.EnsureControls(session,request,2,true,Same),"FILL_CONTROL_UNAVAILABLE","原生 setter 返回 false 不可当成功");
Check(session.Commits==0,"setter 拒绝后不提交修改");
session=new(initial,snapshot){CommitResult=false};
Reject(()=>FillSurfaceContract.EnsureControls(session,request,2,true,Same),"FILL_CONTROL_COMMIT","原生定义提交失败拒绝");
session=new(initial,initial);
Reject(()=>FillSurfaceContract.EnsureControls(session,request,2,true,Same),"FILL_CONTROL_MISMATCH","setter 和提交均返回 true 仍需实际读回 G2");
session=new(initial,snapshot with{FeatureReference="new-patch"});
Reject(()=>FillSurfaceContract.EnsureControls(session,request,2,true,Same),"FILL_FEATURE_CHANGED","设置连续性不得替换目标特征");
session=new(snapshot,snapshot);
FillSurfaceContract.EnsureControls(session,request,2,true,Same);Check(session.Sets==0 && session.Commits==0,"控制正确时保持只读复核");
session=new(initial,snapshot);
Reject(()=>FillSurfaceContract.EnsureControls(session,[new("x",(SurfaceContact)99,null)],2,true,Same),"FILL_REQUEST_INVALID","未知源控制在访问或修改原生端前拒绝");
Check(session.Captures==0 && session.Sets==0,"无效源合同不触发端口操作");
session=new(initial,snapshot);
Reject(()=>FillSurfaceContract.EnsureControls(session,[request[0],request[0]],2,true,Same),"FILL_IDENTITY_AMBIGUOUS","重复源边界在调用端口前拒绝");
Check(session.Captures==0,"重复源合同不访问原生端");
var legacy=snapshot with{Boundaries=[contact with{Kind=EntityKind.Feature}]};
FillSurfaceContract.Validate(legacy,Same);FillSurfaceContract.VerifyRetained(legacy,legacy,Same);Check(true,"既有接触草图回执保持兼容");
Check(typeof(IFillDefinitionSession).Assembly.GetReferencedAssemblies().All(a=>!a.Name!.StartsWith("SolidWorks.Interop")),"核心与模拟测试无 SolidWorks COM 依赖");
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{passed=checks.Count,checks,scope="support_binding_and_retained_definition_core_fake_port",native_acceptance="not_run"},ModelingIrJson.Options));

sealed class FakeSession(FillDefinitionSnapshot initial,FillDefinitionSnapshot final) : IFillDefinitionSession
{
    public bool SetterResult=true,CommitResult=true;
    public int Sets,Commits,Captures;
    public FillDefinitionSnapshot Capture(){Captures++;return Commits>0?final:initial;}
    public bool SetContact(string reference,SurfaceContact contact){Sets++;return SetterResult;}
    public bool Commit(){Commits++;return CommitResult;}
}
