using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

if (args.Length != 1) throw new ArgumentException("Usage: DesignIntentRegression offline-output-directory");
var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); checks.Add(name); Console.WriteLine("PASS: " + name); }
void Reject(Action action, string name)
{
    try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or OperationCanceledException)
    { Check(true, name); return; }
    throw new Exception("FAIL accepted: " + name);
}
DesignExpression Lit(double value, DrawingValueUnit unit = DrawingValueUnit.Millimeter) => new()
{ Kind = DesignExpressionKind.Literal, Literal = new(value, unit) };
DesignExpression Var(string name) => new() { Kind = DesignExpressionKind.Variable, Variable = name };
DesignExpression Op(DesignExpressionKind kind, DesignExpression a, DesignExpression b) => new() { Kind = kind, Left = a, Right = b };
var spec = new DesignIntentSpec
{
    ActiveConfiguration = "加厚",
    GlobalVariables = [new() { Name = "倍宽", Expression = Op(DesignExpressionKind.Multiply, Var("基础宽度"), Lit(2, DrawingValueUnit.Unitless)) },
        new() { Name = "基础宽度", Expression = Lit(40) }],
    Equations = [new() { DimensionName = "宽度_0@尺寸轮廓", Expression = Var("倍宽"), ExpectedValue = new(80, DrawingValueUnit.Millimeter) }],
    Configurations = [new() { Name = "标准", ReuseExisting = true, Dimensions = [new() { DimensionName = "D1@凸台", Value = new(10, DrawingValueUnit.Millimeter) }] },
        new() { Name = "加厚", CreateFromConfiguration = "标准", Dimensions = [new() { DimensionName = "D1@凸台", Value = new(20, DrawingValueUnit.Millimeter) }] }],
    RequireFullyDefinedSketches = ["尺寸轮廓"]
};
var compiled = DesignIntentContract.Compile(spec);
Check(compiled.Equations.Select(e => e.Target).SequenceEqual(new[] { "基础宽度", "倍宽", "宽度_0@尺寸轮廓" }), "variables ordered by dependency");
Check(compiled.Equations[0].Equation == "\"基础宽度\" = 40mm", "explicit native mm literal");
Check(compiled.Equations[1].Value == new DesignValue(80, DrawingValueUnit.Millimeter), "independent arithmetic computes 80 mm");
var priorGlobal = new DesignEquationState(compiled.Equations[0].Equation,true,false,true,40,true);
var newGlobal = new DesignEquationState(compiled.Equations[1].Equation,true,false,true,80,true);
NativeDesignIntentReadback.VerifyInsertedEquation([priorGlobal],[newGlobal,priorGlobal],0,compiled.Equations[1]);
Check(true,"原生全局变量前插及库存重排按返回身份通过");
NativeDesignIntentReadback.VerifyInsertedEquation([priorGlobal],[priorGlobal,newGlobal],1,compiled.Equations[1]);
Check(true,"原生尾部添加仍核对身份");
Reject(()=>NativeDesignIntentReadback.VerifyInsertedEquation([priorGlobal],[newGlobal,priorGlobal],-1,compiled.Equations[1]),"原生添加负返回值拒绝");
Reject(()=>NativeDesignIntentReadback.VerifyInsertedEquation([priorGlobal],[newGlobal,priorGlobal],1,compiled.Equations[1]),"原生返回错误目标索引拒绝");
Reject(()=>NativeDesignIntentReadback.VerifyInsertedEquation([priorGlobal],[newGlobal],0,compiled.Equations[1]),"添加丢失原有方程拒绝");
Reject(()=>NativeDesignIntentReadback.VerifyInsertedEquation([priorGlobal],[newGlobal,newGlobal],0,compiled.Equations[1]),"重复目标及覆盖原定义拒绝");
Reject(()=>NativeDesignIntentReadback.VerifyInsertedEquation([priorGlobal],[newGlobal,priorGlobal with{Equation="\"基础宽度\" = 41mm"}],0,compiled.Equations[1]),"添加篡改原定义拒绝");
Reject(()=>NativeDesignIntentReadback.VerifyInsertedEquation([priorGlobal],[newGlobal with{AllConfigurations=false},priorGlobal],0,compiled.Equations[1]),"添加错误作用域拒绝");
Reject(()=>NativeDesignIntentReadback.VerifyInsertedEquation([priorGlobal],[newGlobal with{GlobalVariable=false},priorGlobal],0,compiled.Equations[1]),"添加错误原生目标类型拒绝");
Reject(()=>NativeDesignIntentReadback.VerifyInsertedEquation([priorGlobal],[newGlobal with{Disabled=true},priorGlobal],0,compiled.Equations[1]),"添加被禁用目标拒绝");
foreach(var modelType in new[]{"ProfileFeature","Extrusion","Fillet","RefPlane","SolidBodyFolder","SurfaceBodyFolder","UnknownFolder"})
    Check(!NativeDesignIntentReadback.IsPresentationTreeNode(modelType),"完整库存保留模型及未知类型 "+modelType);
foreach(var treeType in new[]{"FavoriteFolder","HistoryFolder","SelectionSetFolder","SensorFolder","EnvFolder","InkMarkupFolder","MaterialFolder","ConfigCommentsFolder"})
    Check(NativeDesignIntentReadback.IsPresentationTreeNode(treeType),"明确识别共享占位身份的系统树节点 "+treeType);
var angular = spec with { GlobalVariables = [new() { Name = "倍宽", Expression = Lit(-90, DrawingValueUnit.Degree) }],
    Equations = [spec.Equations[0] with { DimensionName = "角度@尺寸轮廓", ExpectedValue = new(-90, DrawingValueUnit.Degree) }] };
Check(DesignIntentContract.Compile(angular).Equations[0].Equation.EndsWith("-90") && DesignIntentContract.Compile(angular).RequiresDegreeEquationUnits, "angle uses independently checked native Degrees mode");
var ratio = spec with { GlobalVariables = [new() { Name = "倍宽", Expression = Op(DesignExpressionKind.Divide, Lit(80), Lit(20)) }],
    Equations = [spec.Equations[0] with { DimensionName = "数量@阵列", ExpectedValue = new(4, DrawingValueUnit.Unitless) }] };
Check(DesignIntentContract.Compile(ratio).Equations[0].Value.Unit == DrawingValueUnit.Unitless, "same-unit division produces unitless ratio");
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [spec.GlobalVariables[0], spec.GlobalVariables[0]] }), "duplicate global rejected");
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [new() { Name = "A", Expression = Lit(1) }, new() { Name = "a", Expression = Lit(2) }] }), "case ambiguous globals rejected");
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [new() { Name = "倍宽", Expression = Var("未知") }] }), "undeclared reference rejected");
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [new() { Name = "倍宽", Expression = Var("倍宽") }] }), "self cycle rejected");
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [new() { Name = "倍宽", Expression = Var("B") }, new() { Name = "B", Expression = Var("倍宽") }] }), "mutual cycle rejected");
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [new() { Name = "倍宽", Expression = Op(DesignExpressionKind.Add, Lit(80), Lit(1, DrawingValueUnit.Degree)) }] }), "length plus angle rejected");
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [new() { Name = "倍宽", Expression = Op(DesignExpressionKind.Multiply, Lit(80), Lit(2)) }] }), "unsupported compound dimension rejected");
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [new() { Name = "倍宽", Expression = Op(DesignExpressionKind.Divide, Lit(80), Lit(0, DrawingValueUnit.Unitless)) }] }), "division by zero rejected");
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [new() { Name = "倍宽", Expression = Lit(double.NaN) }] }), "nonfinite literal rejected");
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [new() { Name = "倍宽", Expression = Lit(1, DrawingValueUnit.Inch) }] }), "unsupported unit rejected explicitly");
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [new() { Name = "倍宽", Expression = Lit(80) with { Variable = "unused" } }] }), "unconsumed expression field rejected");
Reject(() => DesignIntentContract.Compile(spec with { Equations = [spec.Equations[0] with { ExpectedValue = new(81, DrawingValueUnit.Millimeter) }] }), "wrong independent expectation rejected");
Reject(() => DesignIntentContract.Compile(spec with { Equations = [spec.Equations[0], spec.Equations[0]] }), "duplicate dimension equations rejected");
Reject(() => DesignIntentContract.Compile(spec with { ActiveConfiguration = "缺失" }), "unknown active configuration rejected");
Reject(() => DesignIntentContract.Compile(spec with { Configurations = [spec.Configurations[0], spec.Configurations[0]] }), "duplicate configurations rejected");
Reject(() => DesignIntentContract.Compile(spec with { Configurations = [spec.Configurations[0] with { Dimensions = [new() { DimensionName = spec.Equations[0].DimensionName, Value = new(5, DrawingValueUnit.Millimeter) }] }] , ActiveConfiguration = "标准" }), "equation and numeric override conflict rejected");
Reject(() => DesignIntentContract.Compile(spec with { Configurations = [spec.Configurations[1] with { CreateFromConfiguration = null }], ActiveConfiguration = "加厚" }), "new configuration needs explicit source");
Reject(() => DesignIntentContract.Compile(spec with { Configurations = [spec.Configurations[0] with { Geometry = new() { RequireValidTopology = false } }], ActiveConfiguration = "标准" }), "configuration topology gate cannot be disabled");
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [new() { Name = "倍宽\" = 1", Expression = Lit(1) }] }), "native expression injection rejected");
Reject(() => DesignIntentContract.Compile(spec with { Equations = [spec.Equations[0] with { DimensionName = "D1@草图@零件" }] }), "ambiguous multi-owner parameter rejected");
Reject(() => DesignIntentContract.Compile(ratio with { Equations = [ratio.Equations[0] with { ExpectedValue = new(1.5, DrawingValueUnit.Unitless) }] }), "fractional count rejected");
var deep = Lit(1); for (int i = 0; i < 40; i++) deep = Op(DesignExpressionKind.Add, deep, Lit(1));
Reject(() => DesignIntentContract.Compile(spec with { GlobalVariables = [new() { Name = "倍宽", Expression = deep }] }), "excessive expression depth rejected");
Check(DesignIntentContract.Validate(spec with { Configurations = null! }).Count == 1, "malformed null collection yields diagnostic");
Check(DesignIntentContract.Validate(spec with { Equations = [spec.Equations[0] with { Expression = null! }] }).Count == 1, "malformed null expression yields diagnostic");
Check(DesignIntentContract.NormalizeEquation("\"A B\" = 2 mm") != DesignIntentContract.NormalizeEquation("\"AB\"=2mm"), "identifier whitespace is preserved");

var plan = new RuleBasedTextCompiler().Compile("80 x 50 x 10 mm plate").Plan! with { DesignIntent = spec };
var roundtrip = ModelingIrJson.Deserialize(ModelingIrJson.Serialize(plan));
Check(DesignIntentContract.Compile(roundtrip.DesignIntent!).Equations.Count == 3, "typed IR JSON roundtrip preserves design intent");
Check(ModelingRecovery.TypedPlanFingerprint(plan) != ModelingRecovery.TypedPlanFingerprint(plan with { DesignIntent = angular }), "design intent changes invalidate typed identity");
Check(ModelingRecovery.PrefixFingerprint(plan, 1) != ModelingRecovery.PrefixFingerprint(plan with { DesignIntent = angular }, 1), "design intent changes conservatively invalidate checkpoint prefix");
var draft = new GenericModelDraft { SourceText = "离线设计意图合同", DesignIntent = spec,
    Operations = [new() { Type = GenericOperationKind.ProfileSketch, Id = "profile", Plane = ReferencePlane.Front,
        Primitives = [new() { Type = GenericPrimitiveKind.CenteredRectangle, CenterXmm = 0, CenterYmm = 0, WidthMm = 80, HeightMm = 50 }] },
        new() { Type = GenericOperationKind.ExtrudeBoss, Id = "body", DependsOn = ["profile"], SketchId = "profile", DepthMm = 10, EndCondition = ExtrudeEndCondition.Blind }] };
var draftResult = new GenericPlanCompiler().Compile(draft);
File.WriteAllText(Path.Combine(output, "draft-compilation.json"), JsonSerializer.Serialize(draftResult, ModelingIrJson.Options));
Check(draftResult.Success && draftResult.Plan!.DesignIntent == spec, "generic compiler retains typed design intent");
Check(!new GenericPlanCompiler().Compile(draft with { Operations = [] }).Success, "empty new part remains rejected");
var sourceOnly = new GenericPlanCompiler().Compile(draft with { Operations = [], SourceModelPath = Path.Combine(output, "missing-source.SLDPRT") });
Check(!sourceOnly.Success && sourceOnly.Diagnostics.Any(d => d.Code == "SOURCE_MODEL")
    && sourceOnly.Diagnostics.All(d => d.Code is not ("GPC003" or "IR005")), "parameter-only source edit accepts intent shape but still requires real input file");

var session = new FakeSession(); var receipt = DesignIntentExecution.Apply(session, spec);
Check(session.ActiveConfiguration == "加厚", "requested final active configuration retained");
Check(session.ConfigurationNames().SequenceEqual(new[] { "标准", "原样", "加厚" }), "new configuration created and old ones preserved");
Check(receipt.Configurations.All(c => c.Dimensions.Single(d => d.Name == "宽度_0@尺寸轮廓").SystemValue == .08), "all-configuration equation drives every target");
Check(receipt.Configurations.Single(c => c.Name == "标准").Dimensions.Single(d => d.Name == "D1@凸台").SystemValue == .01
    && receipt.Configurations.Single(c => c.Name == "加厚").Dimensions.Single(d => d.Name == "D1@凸台").SystemValue == .02, "configuration numeric values isolated");
Check(receipt.Configurations.Single(c => c.Name == "原样").Dimensions.Single(d => d.Name == "D1@凸台").SystemValue == .007, "unspecified configuration override dimension preserved");
DesignIntentExecution.VerifySaved(session, spec, receipt); Check(true, "saved receipt workflow passes unchanged fake readback");
Check(session.ActiveConfiguration == "加厚", "saved verification restores final active configuration");
session.CorruptValue = true;
Reject(() => DesignIntentExecution.VerifySaved(session, spec, receipt), "corrupt saved dimension rejected");
Check(session.ActiveConfiguration == "加厚", "saved verification failure restores active configuration");
session.CorruptValue = false; session.CorruptEquation = true;
Reject(() => DesignIntentExecution.VerifySaved(session, spec, receipt), "corrupt saved equation rejected");
session.CorruptEquation = false; session.DisabledEquation = true;
Reject(() => DesignIntentExecution.VerifySaved(session, spec, receipt), "disabled saved equation rejected");
session.DisabledEquation = false; session.BadScope = true;
Reject(() => DesignIntentExecution.VerifySaved(session, spec, receipt), "saved scope narrowing rejected");
session.BadScope = false; session.FailedEvaluation = true;
Reject(() => DesignIntentExecution.VerifySaved(session, spec, receipt), "failed equation evaluation rejected");
session.FailedEvaluation = false; session.ExtraConfiguration = true;
Reject(() => DesignIntentExecution.VerifySaved(session, spec, receipt), "unexpected saved configuration rejected");
foreach (var failure in new[] { "write-equation", "create", "rebuild", "write-dimension", "geometry", "sketch" })
{
    var broken = new FakeSession { FailAt = failure };
    Reject(() => DesignIntentExecution.Apply(broken, spec), "native-port failure rejected: " + failure);
    Check(broken.ActiveConfiguration == "原样", "failure restores original active configuration: " + failure);
}
var leaked = new FakeSession { LeakDimensions = true };
Reject(() => DesignIntentExecution.Apply(leaked, spec), "dimension leakage across configurations rejected");
var referenceOnly = new FakeSession { ReferenceOnly = true };
Reject(() => DesignIntentExecution.Apply(referenceOnly, spec), "reference-only dimension rejected before mutation");
Check(referenceOnly.Mutations == 0, "preflight performs no mutations on non-driving parameter");
var wrongType = new FakeSession { WrongType = true };
Reject(() => DesignIntentExecution.Apply(wrongType, spec), "wrong parameter unit rejected before mutation");
Check(wrongType.Mutations == 0, "unit mismatch preflight has no mutations");
var external = new FakeSession { FailAt = "external" };
Reject(() => DesignIntentExecution.Apply(external, spec), "external control rejected before mutation");
Check(external.Mutations == 0, "external-control rejection has no mutations");
var collision = new FakeSession { ExistingTarget = true };
Reject(() => DesignIntentExecution.Apply(collision, spec), "existing equation without replace permission rejected");
Check(collision.Mutations == 0, "equation collision preflight has no mutations");
var controlledOverride = new FakeSession { ExistingDimensionEquation = true };
Reject(() => DesignIntentExecution.Apply(controlledOverride, spec), "numeric override of existing equation controlled dimension rejected");
Check(controlledOverride.Mutations == 0, "existing equation dimension conflict has no mutations");
var reused = new FakeSession { ExistingTarget = true };
DesignIntentExecution.Apply(reused, spec with { GlobalVariables = spec.GlobalVariables.Select(g => g with { ReplaceExisting = true }).ToArray() });
Check(reused.Mutations > 0, "explicit replacement updates controlled existing variable");
using var cts = new CancellationTokenSource(); cts.Cancel(); var cancelled = new FakeSession();
Reject(() => DesignIntentExecution.Apply(cancelled, spec, cts.Token), "cancelled request rejected before mutation");
Check(cancelled.Mutations == 0 && cancelled.ActiveConfiguration == "原样", "cancellation preserves original state before mutation");
using var lateCts = new CancellationTokenSource(); var lateCancelled = new FakeSession { CancelAfterCreation = lateCts };
Reject(() => DesignIntentExecution.Apply(lateCancelled, spec, lateCts.Token), "late cancellation stops after a known native-port mutation boundary");
Check(lateCancelled.Mutations == 1 && lateCancelled.ActiveConfiguration == "原样" && lateCancelled.ConfigurationNames().Contains("加厚"),
    "late cancellation restores active state without claiming mutation rollback");

var savedPlan = new ModelingPlan { PlanId = "final-dimensions", Name = "最终配置尺寸", DesignIntent = spec, Operations =
    [new ProfileSketchOperation { Id = "s", Name = "尺寸轮廓", AutoDimensionPrimitives = true, RequireFullyDefined = true,
        Primitives = [new CenteredRectangleProfile { WidthMm = 40, HeightMm = 50 }] }] };
var savedModel = new ModelInspection(true, "offline synthetic inspection", "") { Features =
    [new("尺寸轮廓", "ProfileFeature", false, null,
        [new("宽度_0@尺寸轮廓", .08, "") { Driving = true, ParameterType = "swDimensionParamTypeDoubleLinear" },
         new("高度_0@尺寸轮廓", .05, "") { Driving = true, ParameterType = "swDimensionParamTypeDoubleLinear" }]) { SketchConstraintStatus = 3 },
     new("凸台", "Extrusion", false, null, [new("D1@凸台", .02, "") { Driving = true, ParameterType = "swDimensionParamTypeDoubleLinear" }])] };
SavedModelRequirements.Validate(savedPlan, savedModel); Check(true, "final equation and active configuration override primitive initialization");
var equationSaved=savedModel with{Features=[savedModel.Features![0] with{Dimensions=[savedModel.Features[0].Dimensions[0] with{Driving=false,EquationControlled=true},savedModel.Features[0].Dimensions[1]]},savedModel.Features[1]]};
SavedModelRequirements.Validate(savedPlan,equationSaved);Check(true,"方程控制尺寸保留原生从动状态并按绑定验收");
Reject(()=>SavedModelRequirements.Validate(savedPlan,equationSaved with{Features=[equationSaved.Features![0] with{Dimensions=[equationSaved.Features[0].Dimensions[0] with{EquationControlled=false},equationSaved.Features[0].Dimensions[1]]},equationSaved.Features[1]]}),"普通参考尺寸不能冒充方程控制");
Reject(()=>SavedModelRequirements.Validate(savedPlan,equationSaved with{Features=[equationSaved.Features![0],equationSaved.Features[1] with{Dimensions=[equationSaved.Features[1].Dimensions[0] with{Driving=false,EquationControlled=true}]}]}),"数值覆盖目标不能借用方程控制许可");
var nativeStateSession=new FakeSession{EquationUsesDrivenState=true};var nativeStateReceipt=DesignIntentExecution.Apply(nativeStateSession,spec);
DesignIntentExecution.VerifySaved(nativeStateSession,spec,nativeStateReceipt);Check(true,"方程写入后原生驱动转从动的完整执行及保存合同");
Reject(() => SavedModelRequirements.Validate(savedPlan, savedModel with { Features = [savedModel.Features![0]] }), "active configuration driving dimension must remain present");
Reject(() => SavedModelRequirements.Validate(savedPlan, savedModel with { Features =
    [savedModel.Features![0] with { Dimensions = [savedModel.Features[0].Dimensions[0] with { SystemValue = .04 }, savedModel.Features[0].Dimensions[1]] }, savedModel.Features[1]] }),
    "saved primitive initialization cannot substitute final equation value");
Reject(() => SavedModelRequirements.Validate(savedPlan, savedModel with { Features =
    [savedModel.Features![0] with { Dimensions = [savedModel.Features[0].Dimensions[0]] }, savedModel.Features[1]] }),
    "uncontrolled primitive dimension is still mandatory");
var angleSession = new FakeSession(); var angleReceipt = DesignIntentExecution.Apply(angleSession, angular);
var radiansSession=new FakeSession{FailAt="degrees"};
Reject(()=>DesignIntentExecution.Apply(radiansSession,angular),"角度字面量拒绝未经确认的Radians模式");
Check(radiansSession.Mutations==0,"角度模式预检拒绝发生在修改之前");
Check(DesignIntentContract.Close(angleReceipt.Configurations[0].Dimensions.Single(d => d.Name == "角度@尺寸轮廓").SystemValue, -Math.PI / 2),
    "angle equation independently reads radians from port");
var integerReceipt = DesignIntentExecution.Apply(new FakeSession(), ratio);
Check(integerReceipt.Configurations.All(c => c.Dimensions.Single(d => d.Name == "数量@阵列").SystemValue == 4), "integer equation stays unitless across configurations");
var negativeGlobal = spec with { GlobalVariables = [new() { Name = "偏置", Expression = Lit(-1, DrawingValueUnit.Unitless) }], Equations = [] };
var negativeReceipt = DesignIntentExecution.Apply(new FakeSession(), negativeGlobal);
Check(negativeReceipt.Configurations.All(c => c.Equations.Single().RawValue == -1), "negative one is valid equation value when evaluation status succeeds");

File.WriteAllText(Path.Combine(output, "compiled-design-intent.json"), JsonSerializer.Serialize(compiled, ModelingIrJson.Options));
File.WriteAllText(Path.Combine(output, "fake-session-receipt.json"), JsonSerializer.Serialize(receipt, ModelingIrJson.Options));
File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { status = "pass", passed = checks.Count,
    evidence_layer = "offline_core_and_fake_native_port", solidworks_started = false, native_authoring = "not_run",
    native_save_reopen = "not_run", checks }, new JsonSerializerOptions(ModelingIrJson.Options) { WriteIndented = true }));
Console.WriteLine("Offline checks passed: " + checks.Count);

sealed class FakeSession : IDesignIntentSession
{
    private readonly Dictionary<string, Dictionary<string, double>> dimensions = new(StringComparer.Ordinal)
    {
        ["标准"] = new() { ["宽度_0@尺寸轮廓"] = .04, ["D1@凸台"] = .01, ["角度@尺寸轮廓"] = 0, ["数量@阵列"] = 2 },
        ["原样"] = new() { ["宽度_0@尺寸轮廓"] = .04, ["D1@凸台"] = .007, ["角度@尺寸轮廓"] = 0, ["数量@阵列"] = 2 }
    };
    private readonly List<CompiledDesignEquation> equations = [];
    public string ActiveConfiguration { get; private set; } = "原样";
    public string? FailAt { get; init; }
    public bool LeakDimensions { get; init; }
    public bool ReferenceOnly { get; init; }
    public bool EquationUsesDrivenState { get; init; }
    public bool WrongType { get; init; }
    public bool ExistingTarget { get; init; }
    public bool ExistingDimensionEquation { get; init; }
    public CancellationTokenSource? CancelAfterCreation { get; init; }
    public bool CorruptValue { get; set; }
    public bool CorruptEquation { get; set; }
    public bool DisabledEquation { get; set; }
    public bool BadScope { get; set; }
    public bool FailedEvaluation { get; set; }
    public bool ExtraConfiguration { get; set; }
    public int Mutations { get; private set; }
    public IReadOnlyList<string> ConfigurationNames() => dimensions.Keys.Concat(ExtraConfiguration ? ["额外"] : Array.Empty<string>()).ToArray();
    public void EnsureEditable() => Failure("external");
    public void RequireDegreeEquationUnits() => Failure("degrees");
    public void SelectConfiguration(string name)
    {
        if (!dimensions.ContainsKey(name)) throw new InvalidOperationException("missing fake configuration");
        ActiveConfiguration = name;
    }
    public void CreateConfiguration(string name, string source)
    { Failure("create"); Mutations++; dimensions.Add(name, new(dimensions[source])); ActiveConfiguration = name; CancelAfterCreation?.Cancel(); }
    public IReadOnlyList<DesignEquationState> ReadEquations()
    {
        var result = equations.Select(e => new DesignEquationState(CorruptEquation ? e.Equation + "+1mm" : e.Equation,
            e.GlobalVariable, DisabledEquation, !BadScope, e.Value.Value, !FailedEvaluation)).ToList();
        if (ExistingTarget && !equations.Any(e => e.Target == "基础宽度")) result.Add(new("\"基础宽度\"=35mm", true, false, true, 35, true));
        if (ExistingDimensionEquation) result.Add(new("\"D1@凸台\"=10mm", false, false, true, 10, true));
        return result;
    }
    public void WriteEquation(CompiledDesignEquation eq)
    { Failure("write-equation"); Mutations++; equations.RemoveAll(e => e.Target == eq.Target); equations.Add(eq); }
    public void EvaluateAndRebuild()
    {
        Failure("rebuild");
        foreach (var eq in equations.Where(e => !e.GlobalVariable)) dimensions[ActiveConfiguration][eq.Target] = eq.Value.Unit switch
        { DrawingValueUnit.Degree => eq.Value.Value / 180 * Math.PI, DrawingValueUnit.Unitless => eq.Value.Value, _ => eq.Value.Value / 1000 };
    }
    public DesignDimensionState ReadDimension(string name) => new(name, WrongType || name.StartsWith("角度@") ? NativeDimensionParameterKind.Angle
        : name.StartsWith("数量@") ? NativeDimensionParameterKind.Integer : NativeDimensionParameterKind.Length,
        dimensions[ActiveConfiguration][name] + (CorruptValue ? .001 : 0), !ReferenceOnly && !(EquationUsesDrivenState && equations.Any(e=>!e.GlobalVariable && e.Target==name)))
        {EquationControlled=EquationUsesDrivenState && equations.Any(e=>!e.GlobalVariable && e.Target==name)};
    public void WriteDimension(string name, double systemValue)
    {
        Failure("write-dimension"); Mutations++;
        if (LeakDimensions) foreach (var dims in dimensions.Values) dims[name] = systemValue;
        else dimensions[ActiveConfiguration][name] = systemValue;
    }
    public void RequireFullyDefinedSketch(string name) => Failure("sketch");
    public void RequireGeometry(GeometryQualitySpec geometry) => Failure("geometry");
    private void Failure(string stage) { if (FailAt == stage) throw new InvalidOperationException("injected fake native failure: " + stage); }
}
