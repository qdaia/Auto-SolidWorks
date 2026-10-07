using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

if (args.Length != 1) throw new ArgumentException("提供新的离线报告目录。");
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("保留旧证据，请使用新目录。");
Directory.CreateDirectory(output);
var checks = new List<string>();
AssemblyMotion T(double x, double y, double z) => new() { Kind = AssemblyMotionKind.Translation, Direction = new(x,y,z) };
AssemblyMotion R(double x, double y, double z, Vector3? point = null) => new() { Kind = AssemblyMotionKind.Rotation, Direction = new(x,y,z), PointMm = point ?? new(0,0,0) };
AssemblyMotion S(double pitch, Vector3? point = null) => new() { Kind = AssemblyMotionKind.Screw, Direction = new(0,0,1), PointMm = point ?? new(0,0,0), PitchMmPerRadian = pitch };
AssemblyPlan Plan(params AssemblyMotion[] expected) => new() { Name = "机构离线合同", NativePath = Path.Combine(output,"uncreated.SLDASM"),
    Components = [new() { Id = "base", Path = "never-opened.SLDPRT", Fixed = true }, new() { Id = "moving", Path = "never-opened.SLDPRT" }],
    Mobility = new() { Requirements = [new() { ComponentId = "moving", RelativeToComponentId = "base", SourceLiteral = "源设计要求的相对运动", ExpectedBasis = expected }] } };
AssemblyMobilityObservation Observation(params AssemblyMotion[] observed) => new() {
    ComponentId = "moving", RelativeToComponentId = "base", ComponentIdentity = "instance:moving", ReferenceIdentity = "instance:base", Configuration = "默认",
    EvidenceSource = "offline-independent-fixture", State = AssemblyMobilityEvidenceState.Complete, InAssemblyCoordinates = true,
    RegularConfiguration = true, DegreesOfFreedom = observed.Length, Basis = observed };
FakeSession Session(params AssemblyMotion[] observed) => new(Observation(observed));
void Pass(string label, Action action) { action(); checks.Add(label); Console.WriteLine("PASS: " + label); }
void Reject(string label, Action action)
{
    try { action(); } catch (ArgumentException ex) when (ex.Message.Contains("ASSEMBLY_MOBILITY", StringComparison.Ordinal))
    { checks.Add(label); Console.WriteLine("PASS: rejected " + label); return; }
    throw new Exception("接受了非法／不匹配观测：" + label);
}
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
Pass("固定相对运动为零", () => Check(AssemblyMobilityContract.Verify(Plan(), Session()).Single().DegreesOfFreedom == 0,"zero"));
Pass("轴向移动副", () => AssemblyMobilityContract.Verify(Plan(T(0,0,1)), Session(T(0,0,4))));
Pass("反向轴是同一自由运动", () => AssemblyMobilityContract.Verify(Plan(T(0,0,1)), Session(T(0,0,-1))));
Pass("旋转副轴线上移动取点", () => AssemblyMobilityContract.Verify(Plan(R(0,0,1,new(20,10,0))), Session(R(0,0,-1,new(20,10,500)))));
Pass("圆柱副保留独立旋转和平移", () => Check(AssemblyMobilityContract.Verify(Plan(R(0,0,1),T(0,0,1)), Session(T(0,0,-2),R(0,0,4))).Single().DegreesOfFreedom == 2,"cylindrical"));
Pass("相同圆柱运动子空间可以使用螺旋混合基", () => AssemblyMobilityContract.Verify(Plan(R(0,0,1),T(0,0,1)), Session(S(2),T(0,0,1))));
Pass("球副的三个独立旋转", () => AssemblyMobilityContract.Verify(Plan(R(1,0,0),R(0,1,0),R(0,0,1)), Session(R(1,1,0),R(0,1,1),R(1,0,1))));
Pass("平面副三个自由度", () => AssemblyMobilityContract.Verify(Plan(T(1,0,0),T(0,1,0),R(0,0,1)), Session(R(0,0,-1),T(1,1,0),T(1,-1,0))));
Pass("完全自由的六维刚体运动", () => Check(AssemblyMobilityContract.Verify(Plan(T(1,0,0),T(0,1,0),T(0,0,1),R(1,0,0),R(0,1,0),R(0,0,1)),
    Session(R(1,1,0,new(20,30,40)),R(0,1,1,new(20,30,40)),R(1,0,1,new(20,30,40)),T(1,1,0),T(0,1,1),T(1,0,1))).Single().DegreesOfFreedom == 6,"free"));
Pass("明确螺旋副导程", () => AssemblyMobilityContract.Verify(Plan(S(2)), Session(S(2))));
Pass("负导程有独立意图", () => AssemblyMobilityContract.Verify(Plan(S(-2)), Session(S(-2))));
Pass("评估点平移不改变同一轴的比较", () => { var p = Plan(R(0,0,1,new(20,10,0)));
    p = p with { Mobility = new() { Requirements = [p.Mobility!.Requirements[0] with { EvaluationPointMm = new(100,70,-20) }] } };
    AssemblyMobilityContract.Verify(p, Session(R(0,0,1,new(20,10,70)))); });
Pass("容差内的小平面偏差", () => AssemblyMobilityContract.Verify(Plan(T(1,0,0),T(0,1,0)),Session(T(1,0,.4e-6),T(0,1,.4e-6))));
Reject("临界容差使用任意基不能弱化", () => AssemblyMobilityContract.Verify(Plan(T(1,0,0),T(0,1,0)),Session(T(1,0,.8e-6),T(0,1,.8e-6))));
Reject("旋转运动基后临界容差结论相同", () => AssemblyMobilityContract.Verify(Plan(T(1,0,0),T(0,1,0)),Session(T(1,1,1.6e-6),T(1,-1,0))));
Reject("相同数量但平移方向错误", () => AssemblyMobilityContract.Verify(Plan(T(0,0,1)), Session(T(1,0,0))));
Reject("相同数量但运动类型错误", () => AssemblyMobilityContract.Verify(Plan(R(0,0,1)), Session(T(0,0,1))));
Reject("平行但旋转轴偏移", () => AssemblyMobilityContract.Verify(Plan(R(0,0,1)), Session(R(0,0,1,new(10,0,0)))));
Reject("螺旋副变成独立圆柱副", () => AssemblyMobilityContract.Verify(Plan(S(2)), Session(R(0,0,1),T(0,0,1))));
Reject("螺旋导程错误", () => AssemblyMobilityContract.Verify(Plan(S(2)), Session(S(3))));
Reject("螺旋旋向错误", () => AssemblyMobilityContract.Verify(Plan(S(2)), Session(S(-2))));
Reject("多余轴向自由度", () => AssemblyMobilityContract.Verify(Plan(R(0,0,1)), Session(R(0,0,1),T(0,0,1))));
Reject("缺少轴向自由度", () => AssemblyMobilityContract.Verify(Plan(R(0,0,1),T(0,0,1)), Session(R(0,0,1))));
Reject("观测返回数量不能代替完整运动基", () => AssemblyMobilityContract.Verify(Plan(T(0,0,1)), new FakeSession(Observation(T(0,0,1)) with { DegreesOfFreedom = 2 })));
Reject("缺失观测不能当成固定", () => AssemblyMobilityContract.Verify(Plan(), new FakeSession(Observation() with { State = AssemblyMobilityEvidenceState.Unavailable })));
Reject("不完整观测不能通过", () => AssemblyMobilityContract.Verify(Plan(T(0,0,1)), new FakeSession(Observation(T(0,0,1)) with { State = AssemblyMobilityEvidenceState.Incomplete })));
Reject("未记录自由度数量", () => AssemblyMobilityContract.Verify(Plan(), new FakeSession(Observation() with { DegreesOfFreedom = null })));
Reject("组件局部坐标不能冒充装配坐标", () => AssemblyMobilityContract.Verify(Plan(T(0,0,1)), new FakeSession(Observation(T(0,0,1)) with { InAssemblyCoordinates = false })));
Reject("奇异位形不能证明一般机构自由度", () => AssemblyMobilityContract.Verify(Plan(T(0,0,1)), new FakeSession(Observation(T(0,0,1)) with { RegularConfiguration = false })));
Reject("错误组件归属", () => AssemblyMobilityContract.Verify(Plan(), new FakeSession(Observation() with { ComponentId = "other" })));
Reject("错误参考组件", () => AssemblyMobilityContract.Verify(Plan(), new FakeSession(Observation() with { RelativeToComponentId = "other" })));
Reject("陈旧组件持久身份", () => AssemblyMobilityContract.Verify(Plan(), new FakeSession(Observation() with { ComponentIdentity = "old" })));
Reject("陈旧参考持久身份", () => AssemblyMobilityContract.Verify(Plan(), new FakeSession(Observation() with { ReferenceIdentity = "old" })));
Reject("错误配置", () => AssemblyMobilityContract.Verify(Plan(), new FakeSession(Observation() with { Configuration = "其他" })));
Reject("没有来源的观测", () => AssemblyMobilityContract.Verify(Plan(), new FakeSession(Observation() with { EvidenceSource = "" })));
Reject("零运动方向", () => AssemblyMobilityContract.Validate(Plan(T(0,0,0))));
Reject("非有限方向", () => AssemblyMobilityContract.Validate(Plan(T(double.NaN,0,1))));
Reject("方向范数溢出不能成为 NaN 通过", () => AssemblyMobilityContract.Verify(Plan(T(1,0,0)), Session(T(double.MaxValue,double.MaxValue,0))));
Reject("相关运动不能按条目计算数量", () => AssemblyMobilityContract.Validate(Plan(T(0,0,1),T(0,0,-2))));
Reject("病态运动基", () => AssemblyMobilityContract.Validate(Plan(T(1,0,0),T(1,1e-12,0))));
Reject("观测相关运动基", () => AssemblyMobilityContract.Verify(Plan(T(1,0,0),T(0,1,0)), Session(T(1,0,0),T(2,0,0))));
Reject("未声明旋转轴位置", () => AssemblyMobilityContract.Validate(Plan(R(0,0,1) with { PointMm = null })));
Reject("被忽略的平移轴中心", () => AssemblyMobilityContract.Validate(Plan(T(0,0,1) with { PointMm = new(0,0,0) })));
Reject("被忽略的平移导程", () => AssemblyMobilityContract.Validate(Plan(T(0,0,1) with { PitchMmPerRadian = 1 })));
Reject("纯旋转被忽略的导程", () => AssemblyMobilityContract.Validate(Plan(R(0,0,1) with { PitchMmPerRadian = 1 })));
Reject("螺旋导程缺失", () => AssemblyMobilityContract.Validate(Plan(S(1) with { PitchMmPerRadian = null })));
Reject("零导程应明确声明旋转", () => AssemblyMobilityContract.Validate(Plan(S(0))));
Reject("非有限导程", () => AssemblyMobilityContract.Validate(Plan(S(double.PositiveInfinity))));
Reject("非有限旋转中心", () => AssemblyMobilityContract.Validate(Plan(R(0,0,1,new(double.NaN,0,0)))));
Reject("未知运动类型", () => AssemblyMobilityContract.Validate(Plan(T(0,0,1) with { Kind = (AssemblyMotionKind)999 })));
Reject("不能超过六维", () => AssemblyMobilityContract.Validate(Plan(T(1,0,0),T(0,1,0),T(0,0,1),R(1,0,0),R(0,1,0),R(0,0,1),S(1))));
void InvalidRequirement(string label, Func<AssemblyMobilityRequirement,AssemblyMobilityRequirement> change) => Reject(label, () => {
    var p = Plan(T(0,0,1)); AssemblyMobilityContract.Validate(p with { Mobility = new() { Requirements = [change(p.Mobility!.Requirements[0])] } }); });
InvalidRequirement("没有源意图", r => r with { SourceLiteral = "" });
InvalidRequirement("未知组件", r => r with { ComponentId = "unknown" });
InvalidRequirement("不能相对于自己", r => r with { RelativeToComponentId = "moving" });
InvalidRequirement("无效特征长度", r => r with { CharacteristicLengthMm = 0 });
InvalidRequirement("非有限评估点", r => r with { EvaluationPointMm = new(double.PositiveInfinity,0,0) });
InvalidRequirement("容差不能放宽到隐藏额外运动", r => r with { SubspaceTolerance = .5 });
Reject("完全约束要求和活动机构矛盾", () => AssemblyMobilityContract.Validate(Plan(T(0,0,1)) with { RequireFullyConstrainedComponents = true }));
Reject("两个固定组件不能相对移动", () => { var p = Plan(T(0,0,1)); AssemblyMobilityContract.Validate(p with { Components = p.Components.Select(c => c with { Fixed = true }).ToArray() }); });
Reject("空合同不能绕过验证", () => AssemblyMobilityContract.Validate(Plan() with { Mobility = new() }));
Reject("重复组件需求", () => { var p = Plan(); AssemblyMobilityContract.Validate(p with { Mobility = new() { Requirements = [p.Mobility!.Requirements[0],p.Mobility.Requirements[0]] } }); });
Pass("移动参考组件的相对运动可由完整端口观测", () => { var p = Plan(T(0,0,1)); AssemblyMobilityContract.Verify(p with { Components = p.Components.Select(c => c with { Fixed = false }).ToArray() }, Session(T(0,0,1))); });
Reject("重复实例身份", () => { var s = Session(); s.Identities["base"] = s.Identities["moving"]; AssemblyMobilityContract.Verify(Plan(),s); });
Reject("检查期间配置变更", () => { var s = Session(); s.AfterInspect = () => s.Configuration = "变更配置"; AssemblyMobilityContract.Verify(Plan(),s); });
Reject("检查期间组件身份变更", () => { var s = Session(); s.AfterInspect = () => s.Identities["moving"] = "replaced"; AssemblyMobilityContract.Verify(Plan(),s); });
var created = AssemblyMobilityContract.Verify(Plan(R(0,0,1),T(0,0,1)),Session(R(0,0,1),T(0,0,1)));
Pass("保存重开后的完整运动子空间", () => AssemblyMobilityContract.VerifySaved(Plan(R(0,0,1),T(0,0,1)),Session(S(4),T(0,0,1)),created));
Reject("保存重开丢失运动", () => AssemblyMobilityContract.VerifySaved(Plan(R(0,0,1),T(0,0,1)),Session(R(0,0,1)),created));
Reject("保存前证据缺失", () => AssemblyMobilityContract.VerifySaved(Plan(R(0,0,1),T(0,0,1)),Session(R(0,0,1),T(0,0,1)),[]));
Reject("保存重开被替换实例", () => { var s = Session(R(0,0,1),T(0,0,1)); s.Identities["moving"] = "new"; s.Observation = s.Observation with { ComponentIdentity = "new" };
    AssemblyMobilityContract.VerifySaved(Plan(R(0,0,1),T(0,0,1)),s,created); });
Reject("保存前证据不能伪造自由度数量", () => AssemblyMobilityContract.VerifySaved(Plan(R(0,0,1),T(0,0,1)),Session(R(0,0,1),T(0,0,1)),[created[0] with { DegreesOfFreedom = 1 }]));
var aliased=created.Select(r=>r with{ComponentIdentity="old:instance:moving",ReferenceIdentity="old:instance:base"}).ToArray();
Pass("原生等价身份比较支持保存后的引用编码变化",()=>AssemblyMobilityContract.VerifySaved(Plan(R(0,0,1),T(0,0,1)),Session(R(0,0,1),T(0,0,1)),aliased,(a,b)=>a=="old:"+b));
Reject("原生比较不确认等价时拒绝编码变化",()=>AssemblyMobilityContract.VerifySaved(Plan(R(0,0,1),T(0,0,1)),Session(R(0,0,1),T(0,0,1)),aliased,(_,_)=>false));
Reject("参考实例也必须由原生比较确认等价",()=>AssemblyMobilityContract.VerifySaved(Plan(R(0,0,1),T(0,0,1)),Session(R(0,0,1),T(0,0,1)),aliased,(a,b)=>a=="old:"+b&&b.EndsWith("moving")));
Pass("JSON 保留运动类型和明确导程单位", () => {
    var json = JsonSerializer.Serialize(Plan(S(2)),ModelingIrJson.Options); var roundTrip = JsonSerializer.Deserialize<AssemblyPlan>(json,ModelingIrJson.Options)!;
    Check(roundTrip.Mobility!.Requirements[0].ExpectedBasis[0].PitchMmPerRadian == 2,"round trip");
    File.WriteAllText(Path.Combine(output,"螺旋运动合同.json"),json);
});
Pass("未声明合同保留已有装配行为", () => Check(AssemblyMobilityContract.Verify(Plan() with { Mobility = null },Session()).Count == 0,"legacy"));
var pair=new NativeConcentricPairReadback{CompleteMateInventory=true,TwoResolvedTopLevelParts=true,ReferenceFixed=true,ActiveMateCount=1,
    ConcentricDefinitionVerified=true,FirstAxisPointMm=new(20,10,0),SecondAxisPointMm=new(20,10,100),FirstAxis=new(0,0,2),SecondAxis=new(0,0,-3),FirstRadiusMm=20,SecondRadiusMm=20};
Pass("完整原生同心圆柱对保留两个独立运动",()=>Check(ConcentricMobilityContract.Basis(pair).Count==2,"two"));
Pass("原生旋转锁定仅保留轴向平移",()=>Check(ConcentricMobilityContract.Basis(pair with{LockRotation=true}).Count==1,"one"));
Pass("实际原生轴线位置进入相对旋量比较",()=>AssemblyMobilityContract.Verify(Plan(T(0,0,1),R(0,0,1,new(20,10,0))),Session(ConcentricMobilityContract.Basis(pair).ToArray())));
foreach(var bad in new[]{pair with{CompleteMateInventory=false},pair with{TwoResolvedTopLevelParts=false},pair with{ReferenceFixed=false},
    pair with{ActiveMateCount=0},pair with{ActiveMateCount=2},pair with{ConcentricDefinitionVerified=false},pair with{FirstRadiusMm=0},
    pair with{FirstAxis=new(0,0,0)},pair with{FirstAxisPointMm=new(double.NaN,0,0)},pair with{SecondAxis=new(1,0,0)},pair with{SecondAxisPointMm=new(21,10,100)}})
    Reject("原生同心对库存及实际轴线负例 "+checks.Count,()=>ConcentricMobilityContract.Basis(bad));
ConcentricTreeChecks.Run(checks);
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new { status = "pass", passed = checks.Count,
    scope = "offline_general_relative_instantaneous_mobility_contract_and_fake_port", native_acceptance = "not_run", checks },ModelingIrJson.Options));
Console.WriteLine($"通过 {checks.Count} 项纯离线检查；未创建或打开原生模型。");

sealed class FakeSession(AssemblyMobilityObservation observation) : IAssemblyMobilitySession
{
    public string Configuration { get; set; } = "默认";
    public Dictionary<string,string> Identities { get; } = new(StringComparer.Ordinal) { ["base"] = "instance:base", ["moving"] = "instance:moving" };
    public IReadOnlyDictionary<string,string> ComponentIdentities => Identities;
    public AssemblyMobilityObservation Observation { get; set; } = observation;
    public Action? AfterInspect { get; set; }
    public AssemblyMobilityObservation Inspect(string componentId,string relativeToComponentId) { AfterInspect?.Invoke(); return Observation; }
}
