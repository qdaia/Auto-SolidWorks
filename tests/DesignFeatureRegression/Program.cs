using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
if(args.Length!=1)throw new ArgumentException("需要离线输出目录。");
var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception("FAIL "+name);checks.Add(name);Console.WriteLine("PASS "+name);}
void Reject(Action action,string name){try{action();}catch(Exception e)when(e is ArgumentException or InvalidOperationException){Check(true,name);return;}throw new Exception("FAIL accepted "+name);}
var child=new DesignConfiguration{Name="无圆角",CreateFromConfiguration="标准",FeatureSuppression=[new(){FeatureName="圆角",Suppressed=true}]};
var spec=new DesignIntentSpec{ActiveConfiguration=child.Name,Configurations=[new(){Name="标准",ReuseExisting=true},child]};
var s=new FeatureSession();var receipt=DesignIntentExecution.Apply(s,spec);
Check(receipt.Configurations.Single(c=>c.Name=="无圆角").Features.Single(f=>f.Name=="圆角").Suppressed,"新配置指定圆角抑制");
Check(receipt.Configurations.Where(c=>c.Name!="无圆角").All(c=>!c.Features.Single(f=>f.Name=="圆角").Suppressed),"其余配置隔离");
Check(receipt.Configurations.All(c=>!c.Features.Single(f=>f.Name=="孔").Suppressed),"未声明特征保留");
Check(s.ActiveConfiguration=="无圆角","最终活动配置");
DesignIntentExecution.VerifySaved(s,spec,receipt);Check(true,"完整特征回执保存重验");
s.OpaqueReencoded=true;DesignIntentExecution.VerifySaved(s,spec,receipt);Check(true,"原生身份等价允许不透明字节重编码");s.OpaqueReencoded=false;
foreach(var corruption in new[]{"state","other-state","identity","missing","extra","empty-id","duplicate","duplicate-id"})
{
 s.Corrupt=corruption;Reject(()=>DesignIntentExecution.VerifySaved(s,spec,receipt),"损坏保存清单拒绝 "+corruption);
 Check(s.ActiveConfiguration=="无圆角","失败保持活动配置 "+corruption);
}
s.Corrupt="";
foreach(var failure in new[]{"port","missing","empty-id","duplicate","duplicate-id"})
{
 var broken=new FeatureSession{Failure=failure};Reject(()=>DesignIntentExecution.Apply(broken,spec),"修改前拒绝 "+failure);
 Check(broken.Mutations==0 && broken.ActiveConfiguration=="原样","预检不创建配置 "+failure);
}
foreach(var failure in new[]{"leak","cascade","identity-after","ignored-write","write"})
{
 var broken=new FeatureSession{Failure=failure};Reject(()=>DesignIntentExecution.Apply(broken,spec),"修改失败被拒绝 "+failure);
 Check(broken.Mutations>0 && broken.ActiveConfiguration=="原样","部分修改不虚称回滚 "+failure);
}
var explicitCascade=spec with{Configurations=[spec.Configurations[0],child with{FeatureSuppression=[..child.FeatureSuppression,new(){FeatureName="孔",Suppressed=true}]}]};
DesignIntentExecution.Apply(new FeatureSession{Failure="cascade"},explicitCascade);Check(true,"显式声明完整连带状态允许通过");
var reverse=spec with{ActiveConfiguration="标准",Configurations=[new(){Name="标准",ReuseExisting=true,FeatureSuppression=[new(){FeatureName="圆角",Suppressed=false}]}]};
var suppressed=new FeatureSession{InitiallySuppressed=true};var unsuppressed=DesignIntentExecution.Apply(suppressed,reverse);
Check(!unsuppressed.Configurations.Single(c=>c.Name=="标准").Features.Single(f=>f.Name=="圆角").Suppressed,"支持显式解除抑制");
Check(unsuppressed.Configurations.Single(c=>c.Name=="原样").Features.Single(f=>f.Name=="圆角").Suppressed,"解除抑制不泄漏");
foreach(var invalid in new[]{child with{FeatureSuppression=[child.FeatureSuppression[0],child.FeatureSuppression[0]]},
 child with{FeatureSuppression=[new(){FeatureName="圆角@零件",Suppressed=true}]},child with{FeatureSuppression=null!}})
 Reject(()=>DesignIntentContract.Compile(spec with{Configurations=[spec.Configurations[0],invalid]}),"非法抑制列表 "+checks.Count);
Reject(()=>DesignIntentContract.Compile(spec with{RequireFullyDefinedSketches=["圆角"]}),"完全定义草图不能被抑制");
var dim=new DesignDimensionValue{DimensionName="D1@圆角",Value=new(1,DrawingValueUnit.Millimeter)};
Reject(()=>DesignIntentContract.Compile(spec with{DimensionInputs=[dim]}),"尺寸输入来源不能被抑制");
Reject(()=>DesignIntentContract.Compile(spec with{Configurations=[spec.Configurations[0],child with{Dimensions=[dim]}]}),"尺寸写入目标不能被抑制");
Reject(()=>DesignIntentContract.Compile(spec with{Equations=[new(){DimensionName=dim.DimensionName,ExpectedValue=dim.Value,Expression=new(){Kind=DesignExpressionKind.Literal,Literal=dim.Value}}]}),"方程目标不能被抑制");
var plan=new RuleBasedTextCompiler().Compile("80 x 50 x 10 mm plate").Plan! with{DesignIntent=spec};
var roundtrip=ModelingIrJson.Deserialize(ModelingIrJson.Serialize(plan));
Check(roundtrip.DesignIntent!.Configurations[1].FeatureSuppression[0].Suppressed,"抑制字段JSON往返");
Check(ModelingRecovery.TypedPlanFingerprint(plan)!=ModelingRecovery.TypedPlanFingerprint(plan with{DesignIntent=reverse}),"抑制改变计划身份");
Check(ModelingRecovery.PrefixFingerprint(plan,1)!=ModelingRecovery.PrefixFingerprint(plan with{DesignIntent=reverse},1),"抑制改变检查点身份");
var stripped=receipt with{Configurations=receipt.Configurations.Select(c=>c with{Features=[]}).ToArray()};
Reject(()=>DesignIntentExecution.VerifySaved(s,spec,stripped),"删除特征回执不能绕过保存核对");
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{status="pass",passed=checks.Count,native_acceptance="not_run",checks},new JsonSerializerOptions{WriteIndented=true}));

sealed class FeatureSession:IDesignIntentSession
{
 readonly Dictionary<string,Dictionary<string,bool>> states=new(){["标准"]=new(){["圆角"]=false,["孔"]=false},["原样"]=new(){["圆角"]=false,["孔"]=false}};
 public bool InitiallySuppressed{init{if(value)foreach(var state in states.Values)state["圆角"]=true;}}
 public string ActiveConfiguration{get;private set;}="原样";
 public string Failure{get;init;}="";public string Corrupt{get;set;}="";public bool OpaqueReencoded{get;set;}
 public int Mutations{get;private set;}
 public IReadOnlyList<string> ConfigurationNames()=>states.Keys.ToArray();
 public void EnsureEditable(){}public void SelectConfiguration(string name)=>ActiveConfiguration=name;
 public void CreateConfiguration(string name,string source){Mutations++;states.Add(name,new(states[source]));}
 public IReadOnlyList<DesignEquationState> ReadEquations()=>[];
 public void WriteEquation(CompiledDesignEquation e)=>throw new InvalidOperationException();
 public void EvaluateAndRebuild(){}public DesignDimensionState ReadDimension(string name)=>throw new InvalidOperationException();
 public void WriteDimension(string name,double value)=>throw new InvalidOperationException();
 public void RequireFullyDefinedSketch(string name){}public void RequireGeometry(GeometryQualitySpec g){}
 public void RequireFeatureSuppressionAuthoring(){if(Failure=="port")throw new InvalidOperationException();}
 public IReadOnlyList<DesignFeatureState> ReadFeatureInventory()
 {
  var mode=Corrupt.Length>0?Corrupt:Failure;
  var result=states[ActiveConfiguration].Select(p=>new DesignFeatureState(p.Key,"id:"+p.Key+(OpaqueReencoded?":v2":""),p.Value)).ToList();
  if(mode=="state")result[0]=result[0] with{Suppressed=!result[0].Suppressed};
  if(mode=="other-state")result[1]=result[1] with{Suppressed=!result[1].Suppressed};
  if(mode=="identity" || mode=="identity-after" && Mutations>0)result[0]=result[0] with{PersistentReference="replaced"};
  if(mode=="empty-id")result[0]=result[0] with{PersistentReference=""};
  if(mode=="missing")result.RemoveAt(0);
  if(mode=="extra")result.Add(new("多余","extra",false));
  if(mode=="duplicate")result.Add(result[0]);
  if(mode=="duplicate-id")result[1]=result[1] with{PersistentReference=result[0].PersistentReference};
  return result;
 }
 public bool SameFeatureIdentity(string a,string b)=>a.Replace(":v2","")==b.Replace(":v2","");
 public void WriteFeatureSuppression(string name,bool value)
 {
  Mutations++;if(Failure=="write")throw new InvalidOperationException();if(Failure=="ignored-write")return;
  if(Failure=="leak")foreach(var state in states.Values)state[name]=value;else states[ActiveConfiguration][name]=value;
  if(Failure=="cascade")states[ActiveConfiguration]["孔"]=value;
 }
}
