using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

if(args.Length!=1)throw new ArgumentException("需要离线证据目录。");
var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);checks.Add(name);Console.WriteLine("PASS: "+name);}
void Reject(Action action,string name){try{action();}catch(Exception e)when(e is ArgumentException or InvalidOperationException){Check(true,name);return;}throw new Exception("FAIL accepted: "+name);}
DesignExpression Lit(double x,DrawingValueUnit unit=DrawingValueUnit.Unitless)=>new(){Kind=DesignExpressionKind.Literal,Literal=new(x,unit)};
DesignExpression Fn(DesignFunction f,params DesignExpression[] a)=>new(){Kind=DesignExpressionKind.Function,Function=f,Arguments=a};
DesignExpression Dim(string n)=>new(){Kind=DesignExpressionKind.Dimension,DimensionName=n};
DesignExpression Var(string n)=>new(){Kind=DesignExpressionKind.Variable,Variable=n};
DesignExpression Op(DesignExpressionKind k,DesignExpression a,DesignExpression b)=>new(){Kind=k,Left=a,Right=b};
var minimal=new DesignIntentSpec{ActiveConfiguration="标准",Configurations=[new(){Name="标准",ReuseExisting=true}]};
CompiledDesignIntent Compile(DesignExpression e)=>DesignIntentContract.Compile(minimal with{GlobalVariables=[new(){Name="测试",Expression=e}]});
foreach(var t in new (DesignFunction F,DesignExpression[] A,double V,DrawingValueUnit U,string Token)[]{
 (DesignFunction.Abs,[Lit(-3,DrawingValueUnit.Millimeter)],3,DrawingValueUnit.Millimeter,"ABS"),
 (DesignFunction.Sqrt,[Lit(81)],9,DrawingValueUnit.Unitless,"SQR"),
 (DesignFunction.Sin,[Lit(30,DrawingValueUnit.Degree)],.5,DrawingValueUnit.Unitless,"SIN"),
 (DesignFunction.Cos,[Lit(60,DrawingValueUnit.Degree)],.5,DrawingValueUnit.Unitless,"COS"),
 (DesignFunction.Tan,[Lit(45,DrawingValueUnit.Degree)],1,DrawingValueUnit.Unitless,"TAN"),
 (DesignFunction.Asin,[Lit(.5)],30,DrawingValueUnit.Degree,"ARCSIN"),
 (DesignFunction.Acos,[Lit(.5)],60,DrawingValueUnit.Degree,"ARCCOS"),
 (DesignFunction.Atan,[Lit(1)],45,DrawingValueUnit.Degree,"ATN"),
 (DesignFunction.Exp,[Lit(1)],2.718281828459045,DrawingValueUnit.Unitless,"EXP"),
 (DesignFunction.Log,[Lit(2.718281828459045)],1,DrawingValueUnit.Unitless,"LOG"),
 (DesignFunction.Int,[Lit(-2.3)],-3,DrawingValueUnit.Unitless,"INT"),
 (DesignFunction.Sign,[Lit(-7,DrawingValueUnit.Degree)],-1,DrawingValueUnit.Unitless,"SGN"),
 (DesignFunction.Min,[Lit(4,DrawingValueUnit.Millimeter),Lit(7,DrawingValueUnit.Millimeter)],4,DrawingValueUnit.Millimeter,"IIF"),
 (DesignFunction.Max,[Lit(4,DrawingValueUnit.Degree),Lit(7,DrawingValueUnit.Degree)],7,DrawingValueUnit.Degree,"IIF"),
 (DesignFunction.Power,[Lit(2),Lit(3)],8,DrawingValueUnit.Unitless," ^ ")})
{
 var c=Compile(Fn(t.F,t.A));var e=c.Equations.Single();
 Check(Math.Abs(e.Value.Value-t.V)<1e-10 && e.Value.Unit==t.U,"独立解析值及单位 "+t.F);
 Check(e.Equation.Contains(t.Token),"白名单渲染 "+t.F);
 Reject(()=>Compile(Fn(t.F)),"拒绝错误参数数量 "+t.F);
}
Check(Compile(new(){Kind=DesignExpressionKind.Negate,Arguments=[Lit(4,DrawingValueUnit.Millimeter)]}).Equations[0].Value==new DesignValue(-4,DrawingValueUnit.Millimeter),"负号保留量纲");
Check(Compile(Fn(DesignFunction.Sign,Lit(0))).Equations[0].Value.Value==0,"零值符号合同");
Check(Compile(Fn(DesignFunction.Sin,Lit(30,DrawingValueUnit.Degree))).Equations[0].Equation.EndsWith("SIN(30)"),"Degrees模式三角函数数值参数不附deg后缀");
Check(Compile(Fn(DesignFunction.Sin,Op(DesignExpressionKind.Add,Lit(10,DrawingValueUnit.Degree),Lit(20,DrawingValueUnit.Degree)))).Equations[0].Equation.EndsWith("SIN((10 + 20))"),"三角函数角度算术参数保留数值模式");
Check(Compile(Lit(30,DrawingValueUnit.Degree)).Equations[0].Equation.EndsWith("30") && Compile(Lit(30,DrawingValueUnit.Degree)).RequiresDegreeEquationUnits,"角度字面量要求Degrees模式并使用数值");
Check(Compile(Fn(DesignFunction.Sign,Lit(-7,DrawingValueUnit.Degree))).Equations[0].Equation.EndsWith("SGN(-7)"),"角度符号函数原生数值参数");
Check(Compile(Fn(DesignFunction.Sign,Lit(-3,DrawingValueUnit.Millimeter))).Equations[0].Equation.EndsWith("SGN(-3mm)"),"长度符号函数保留原生mm参数");
Check(Compile(Fn(DesignFunction.Max,Lit(4,DrawingValueUnit.Degree),Lit(7,DrawingValueUnit.Degree))).RequiresDegreeEquationUnits,"角度比较要求Degrees模式");
var angleReference=minimal with{DimensionInputs=[new(){DimensionName="角度@草图",Value=new(30,DrawingValueUnit.Degree)}],GlobalVariables=[new(){Name="角度",Expression=Dim("角度@草图")}]};
Check(DesignIntentContract.Compile(angleReference).RequiresDegreeEquationUnits,"角度尺寸引用要求Degrees模式");
Check(Compile(Fn(DesignFunction.Int,Lit(-1.2))).Equations[0].Equation.Contains("IIF(-1.2 < INT(-1.2), INT(-1.2) - 1, INT(-1.2))"),"原生负数INT截断通过IIF补偿向下取整");
Check(Compile(Fn(DesignFunction.Power,Lit(-2),Lit(2))).Equations[0].Equation.Contains("((-2) ^ (2))"),"负底数幂保留优先级");
foreach(var e in new[]{Fn(DesignFunction.Sqrt,Lit(-1)),Fn(DesignFunction.Log,Lit(0)),Fn(DesignFunction.Log,Lit(-1)),
 Fn(DesignFunction.Asin,Lit(1.01)),Fn(DesignFunction.Acos,Lit(-1.01)),Fn(DesignFunction.Tan,Lit(90,DrawingValueUnit.Degree)),
 Fn(DesignFunction.Sin,Lit(30)),Fn(DesignFunction.Sin,Lit(1e9,DrawingValueUnit.Degree)),Fn(DesignFunction.Exp,Lit(1000)),
 Fn(DesignFunction.Power,Lit(-2),Lit(.5)),Fn(DesignFunction.Power,Lit(2),Lit(129)),Fn(DesignFunction.Power,Lit(0),Lit(-1)),
 Fn(DesignFunction.Sqrt,Lit(4,DrawingValueUnit.Millimeter)),Fn(DesignFunction.Min,Lit(4),Lit(5,DrawingValueUnit.Degree)),
 Fn(DesignFunction.Power,Lit(2),Lit(2,DrawingValueUnit.Degree)),Lit(double.NaN),Lit(double.PositiveInfinity),
 Fn((DesignFunction)999,Lit(1)),Fn(DesignFunction.Abs,Lit(1)) with{Variable="unused"},Dim("D1@草图") with{Variable="unused"}})
 Reject(()=>Compile(e),"定义域及字段拒绝 "+checks.Count+" "+e.Kind+" "+e.Function);
var expansion=Lit(1);for(int i=0;i<15;i++)expansion=Fn(DesignFunction.Min,expansion,Lit(2));
Reject(()=>Compile(expansion),"IIF 展开长度有界");
var deep=Lit(1);for(int i=0;i<34;i++)deep=Fn(DesignFunction.Abs,deep);
Reject(()=>Compile(deep),"函数深度上限");
DesignExpression Tree(int n)=>n==0?Lit(1):Op(DesignExpressionKind.Add,Tree(n-1),Tree(n-1));
Reject(()=>Compile(Tree(8)),"表达式节点上限");

var spec=minimal with{
 ActiveConfiguration="加大",
 DimensionInputs=[new(){DimensionName="输入@草图",Value=new(10,DrawingValueUnit.Millimeter)}],
 GlobalVariables=[new(){Name="B",Expression=Op(DesignExpressionKind.Multiply,Var("A"),Lit(2))},new(){Name="A",Expression=Dim("输入@草图")}],
 Equations=[new(){DimensionName="输出@草图",Expression=Op(DesignExpressionKind.Add,Var("B"),Lit(5,DrawingValueUnit.Millimeter)),ExpectedValue=new(25,DrawingValueUnit.Millimeter)}],
 Configurations=[minimal.Configurations[0],new(){Name="加大",CreateFromConfiguration="标准",
  GlobalVariables=[new(){Name="B",Expression=Op(DesignExpressionKind.Multiply,Var("A"),Lit(3)),ExpectedValue=new(30,DrawingValueUnit.Millimeter)}],
  EquationValues=[new(){DimensionName="输出@草图",Value=new(35,DrawingValueUnit.Millimeter)}]}]
};
var compiled=DesignIntentContract.Compile(spec);
Check(compiled.Equations.Select(e=>e.Target).SequenceEqual(new[]{"A","B","输出@草图"}),"联合依赖按引用顺序排序");
Check(compiled.ForConfiguration("加大").Last().Value.Value==35 && compiled.ForConfiguration("标准").Last().Value.Value==25,"每配置独立期望值");
Check(compiled.RequiresAutomaticSolveOrder,"尺寸引用及配置覆盖要求自动求解顺序");
var mixed=spec with{GlobalVariables=[new(){Name="B",Expression=Dim("输出@草图")} ]};
Reject(()=>DesignIntentContract.Compile(mixed),"变量尺寸混合循环拒绝");
Reject(()=>DesignIntentContract.Compile(spec with{DimensionInputs=[]}),"未知尺寸引用拒绝");
Reject(()=>DesignIntentContract.Compile(spec with{DimensionInputs=[spec.DimensionInputs[0],spec.DimensionInputs[0]]}),"重复尺寸输入拒绝");
Reject(()=>DesignIntentContract.Compile(spec with{DimensionInputs=[new(){DimensionName="输出@草图",Value=new(25,DrawingValueUnit.Millimeter)}]}),"控制目标不能声明为外部输入");
var child=spec.Configurations[1];
void RejectChild(DesignConfiguration c,string name)=>Reject(()=>DesignIntentContract.Compile(spec with{Configurations=[spec.Configurations[0],c]}),name);
RejectChild(child with{GlobalVariables=[child.GlobalVariables[0] with{Name="b"}]},"大小写不匹配覆盖拒绝");
RejectChild(child with{GlobalVariables=[child.GlobalVariables[0] with{ExpectedValue=new(31,DrawingValueUnit.Millimeter)}]},"错误配置变量预期拒绝");
RejectChild(child with{EquationValues=[]},"缺少不同配置方程期望拒绝");
RejectChild(child with{EquationValues=[child.EquationValues[0] with{DimensionName="未知@草图"}]},"未知配置方程目标拒绝");
RejectChild(child with{DimensionInputValues=[new(){DimensionName="未知@草图",Value=new(1,DrawingValueUnit.Millimeter)}]},"未知输入覆盖拒绝");
RejectChild(child with{Dimensions=[new(){DimensionName="输入@草图",Value=new(10,DrawingValueUnit.Degree)}]},"输入写覆盖量纲冲突拒绝");
var chain=minimal with{GlobalVariables=[new(){Name="读后续",Expression=Dim("下游@草图")}],Equations=[
 new(){DimensionName="下游@草图",Expression=Dim("上游@草图"),ExpectedValue=new(12,DrawingValueUnit.Millimeter)},
 new(){DimensionName="上游@草图",Expression=Lit(12,DrawingValueUnit.Millimeter),ExpectedValue=new(12,DrawingValueUnit.Millimeter)}]};
Check(DesignIntentContract.Compile(chain).Equations.Select(e=>e.Target).SequenceEqual(new[]{"上游@草图","下游@草图","读后续"}),"尺寸到尺寸到变量联合拓扑排序");

var session=new FixtureSession();var receipt=DesignIntentExecution.Apply(session,spec);
Check(receipt.Configurations.Single(c=>c.Name=="标准").Dimensions.Single(d=>d.Name=="输出@草图").SystemValue==.025,"独立模拟求解标准配置25mm");
Check(receipt.Configurations.Single(c=>c.Name=="加大").Dimensions.Single(d=>d.Name=="输出@草图").SystemValue==.035,"独立模拟求解加大配置35mm");
Check(receipt.Configurations.Single(c=>c.Name=="原样").Equations.Any(e=>e.Equation=="\"保留\" = 7"),"无关方程保留");
Check(receipt.Configurations.All(c=>!c.Dimensions.Single(d=>d.Name=="输入@草图").Driving),"只读参考尺寸可作为输入");
Check(session.ActiveConfiguration=="加大","完成后保留活动配置");
DesignIntentExecution.VerifySaved(session,spec,receipt);Check(true,"保存回执重新求解验证");
foreach(var failure in new[]{"raw","equation","dimension","scope","status","disabled","input"})
{
 session.Corrupt=failure;
 Reject(()=>DesignIntentExecution.VerifySaved(session,spec,receipt),"保存损坏拒绝 "+failure);
 Check(session.ActiveConfiguration=="加大","保存失败恢复活动配置 "+failure);
}
session.Corrupt="";
foreach(var failure in new[]{"solve-order","config-port","input"})
{
 var broken=new FixtureSession{Fail=failure};Reject(()=>DesignIntentExecution.Apply(broken,spec),"预检失败 "+failure);
 Check(broken.Mutations==0 && broken.ActiveConfiguration=="原样","首次修改前拒绝且恢复配置 "+failure);
}
var inherited=spec with{Configurations=[spec.Configurations[0],child with{DimensionInputValues=[new(){DimensionName="输入@草图",Value=new(11,DrawingValueUnit.Millimeter)}],
 GlobalVariables=[child.GlobalVariables[0] with{ExpectedValue=new(33,DrawingValueUnit.Millimeter)}],EquationValues=[child.EquationValues[0] with{Value=new(38,DrawingValueUnit.Millimeter)}]}]};
var inheritance=new FixtureSession();Reject(()=>DesignIntentExecution.Apply(inheritance,inherited),"新配置来源输入不符拒绝");
Check(inheritance.Mutations==0,"继承错误在创建配置前拒绝");
var badAngle=minimal with{GlobalVariables=[new(){Name="角度函数",Expression=Fn(DesignFunction.Sin,Lit(30,DrawingValueUnit.Degree))}]};
var angle=new FixtureSession{Fail="degrees"};Reject(()=>DesignIntentExecution.Apply(angle,badAngle),"非度数模式预检拒绝");Check(angle.Mutations==0,"角度模式拒绝无修改");
var leakage=new FixtureSession{Fail="leak"};Reject(()=>DesignIntentExecution.Apply(leakage,spec),"配置变量泄漏拒绝");
var partial=new FixtureSession{Fail="scoped-write"};Reject(()=>DesignIntentExecution.Apply(partial,spec),"部分写入失败拒绝");
Check(partial.Mutations>0 && partial.ActiveConfiguration=="原样" && partial.ConfigurationNames().Contains("加大"),"部分失败不虚称回滚");
var referenceWrite=spec with{Configurations=[spec.Configurations[0] with{Dimensions=[new(){DimensionName="输入@草图",Value=new(10,DrawingValueUnit.Millimeter)}]},child]};
var reference=new FixtureSession();Reject(()=>DesignIntentExecution.Apply(reference,referenceWrite),"参考尺寸不能作为写目标");Check(reference.Mutations==0,"只读写目标无修改拒绝");

var plan=new RuleBasedTextCompiler().Compile("80 x 50 x 10 mm plate").Plan! with{DesignIntent=spec};
var roundtrip=ModelingIrJson.Deserialize(ModelingIrJson.Serialize(plan));
Check(DesignIntentContract.Compile(roundtrip.DesignIntent!).ForConfiguration("加大").Last().Value.Value==35,"新增配置字段与尺寸引用JSON往返");
var finalInspection=new ModelInspection(true,"离线最终活动配置夹具",""){Features=[new("草图","ProfileFeature",false,null,
 [new("输出@草图",.035,""){Driving=true,ParameterType="swDimensionParamTypeDoubleLinear"}])]};
var savedPlan=new ModelingPlan{PlanId="scoped-final",Name="配置最终值",DesignIntent=spec,Operations=[]};
SavedModelRequirements.Validate(savedPlan,finalInspection);Check(true,"最终保存接受活动配置35mm");
Reject(()=>SavedModelRequirements.Validate(savedPlan,finalInspection with{Features=[finalInspection.Features![0] with{
 Dimensions=[finalInspection.Features[0].Dimensions[0] with{SystemValue=.025}]}]}),"基准25mm不能替代活动配置35mm");
foreach(var changed in new[]{spec with{DimensionInputs=[spec.DimensionInputs[0] with{Value=new(11,DrawingValueUnit.Millimeter)}]},
 spec with{Configurations=[spec.Configurations[0],child with{GlobalVariables=[child.GlobalVariables[0] with{Expression=Lit(30,DrawingValueUnit.Millimeter)}]}]},
 spec with{GlobalVariables=[new(){Name="函数",Expression=Fn(DesignFunction.Abs,Lit(-1))}]} })
{
 var next=plan with{DesignIntent=changed};
 Check(ModelingRecovery.TypedPlanFingerprint(plan)!=ModelingRecovery.TypedPlanFingerprint(next),"新字段改变计划身份 "+checks.Count);
 Check(ModelingRecovery.PrefixFingerprint(plan,1)!=ModelingRecovery.PrefixFingerprint(next,1),"新字段使检查点失效 "+checks.Count);
}
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{status="pass",passed=checks.Count,native_acceptance="not_run",evidence="pure_core_and_independent_fixture_port",checks},new JsonSerializerOptions{WriteIndented=true}));

// This fixture computes the two known equations from its independent input state.
// It deliberately does not reuse CompiledDesignEquation.Value as a readback oracle.
sealed class FixtureSession:IDesignIntentSession
{
 readonly Dictionary<string,Dictionary<string,string>> equations=new(){["标准"]=new(){["保留"]="\"保留\" = 7"},["原样"]=new(){["保留"]="\"保留\" = 7"}};
 readonly Dictionary<string,double> values=new(){["标准"]=.001,["原样"]=.001};
 public string ActiveConfiguration{get;private set;}="原样";
 public int Mutations{get;private set;}
 public string Fail{get;init;}="";public string Corrupt{get;set;}="";
 public IReadOnlyList<string> ConfigurationNames()=>equations.Keys.ToArray();
 public void EnsureEditable(){}
 public void SelectConfiguration(string name){if(!equations.ContainsKey(name))throw new InvalidOperationException();ActiveConfiguration=name;}
 public void CreateConfiguration(string name,string source){Mutations++;equations.Add(name,new(equations[source]));values.Add(name,values[source]);ActiveConfiguration=name;}
 public void RequireDegreeEquationUnits(){if(Fail=="degrees")throw new InvalidOperationException("degree mode");}
 public void RequireAutomaticSolveOrder(){if(Fail=="solve-order")throw new InvalidOperationException("solve order");}
 public void RequireConfigurationEquationAuthoring(){if(Fail=="config-port")throw new InvalidOperationException("port unavailable");}
 public void WriteEquation(CompiledDesignEquation e){Mutations++;foreach(var map in equations.Values)map[e.Target]=e.Equation;}
 public void WriteConfigurationEquation(CompiledDesignEquation e,string config){if(Fail=="scoped-write")throw new InvalidOperationException("write failed");Mutations++;if(Fail=="leak")foreach(var map in equations.Values)map[e.Target]=e.Equation;else equations[config][e.Target]=e.Equation;}
 public void EvaluateAndRebuild(){if(equations[ActiveConfiguration].ContainsKey("输出@草图"))values[ActiveConfiguration]=equations[ActiveConfiguration]["B"].Contains("* 3")?.035:.025;}
 public IReadOnlyList<DesignEquationState> ReadEquations()=>equations[ActiveConfiguration].Select(e=>new DesignEquationState(
  Corrupt=="equation" && e.Key=="B"?e.Value+" + 1mm":e.Value,!e.Key.Contains('@'),Corrupt=="disabled",Corrupt!="scope",
  (e.Key=="保留"?7:e.Key=="A"?10:e.Key=="B"?(e.Value.Contains("* 3")?30:20):values[ActiveConfiguration]*1000)+(Corrupt=="raw"?1:0),Corrupt!="status")).ToArray();
 public DesignDimensionState ReadDimension(string name)=>new(name,NativeDimensionParameterKind.Length,
  name=="输入@草图"?(Fail=="input"||Corrupt=="input"?.011:.010):values[ActiveConfiguration]+(Corrupt=="dimension"?.001:0),name!="输入@草图");
 public void WriteDimension(string name,double value){Mutations++;throw new InvalidOperationException("fixture input is reference-only");}
 public void RequireFullyDefinedSketch(string name){}
 public void RequireGeometry(GeometryQualitySpec g){}
}
