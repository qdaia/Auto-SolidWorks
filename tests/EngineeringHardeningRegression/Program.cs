using System.Reflection;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

int passed=0;
void Check(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS: "+name);}
void Reject(Action action,string name){try{action();}catch(ArgumentException){Check(true,name);return;}throw new Exception("FAIL accepted: "+name);}
var output=Path.GetFullPath(args.LastOrDefault()??Path.Combine(Path.GetTempPath(),"AutoSolidWorksEngineeringRegression"));
Directory.CreateDirectory(output);
Check(NativeDimensionValues.ToSystemValue(5,NativeDimensionParameterKind.Integer)==5,"integer count remains 5");
Check(NativeDimensionValues.ToSystemValue(12,NativeDimensionParameterKind.Integer,DrawingValueUnit.Unitless)==12,"explicit unitless count");
Check(Math.Abs(NativeDimensionValues.ToSystemValue(25.4,NativeDimensionParameterKind.Length)-.0254)<1e-12,"millimeter length");
Check(Math.Abs(NativeDimensionValues.ToSystemValue(1,NativeDimensionParameterKind.Length,DrawingValueUnit.Inch)-.0254)<1e-12,"inch length");
Check(NativeDimensionValues.ToSystemValue(.1,NativeDimensionParameterKind.Length,DrawingValueUnit.Meter)==.1,"meter length");
Check(Math.Abs(NativeDimensionValues.ToSystemValue(90,NativeDimensionParameterKind.Angle)-Math.PI/2)<1e-12,"inferred degrees");
Check(Math.Abs(NativeDimensionValues.ToSystemValue(-90,NativeDimensionParameterKind.Angle,legacyAngle:true)+Math.PI/2)<1e-12,"negative native angle");
Check(double.IsFinite(NativeDimensionValues.ToSystemValue(double.MaxValue,NativeDimensionParameterKind.Angle)),"large finite angle conversion avoids intermediate overflow");
foreach(var value in new[]{0.0,-1,1.2,double.NaN,double.PositiveInfinity,2147483648})
    Reject(()=>NativeDimensionValues.ToSystemValue(value,NativeDimensionParameterKind.Integer),"invalid integer "+value);
Reject(()=>NativeDimensionValues.ToSystemValue(5,NativeDimensionParameterKind.Integer,DrawingValueUnit.Millimeter),"integer rejects length unit");
Reject(()=>NativeDimensionValues.ToSystemValue(5,NativeDimensionParameterKind.Length,DrawingValueUnit.Degree),"length rejects degree unit");
Reject(()=>NativeDimensionValues.ToSystemValue(5,NativeDimensionParameterKind.Angle,DrawingValueUnit.Millimeter),"angle rejects length unit");
Reject(()=>NativeDimensionValues.ToSystemValue(5,NativeDimensionParameterKind.Integer,legacyAngle:true),"legacy flag cannot convert integer to angle");
Check(!NativeDimensionValues.Matches(.005,5,NativeDimensionParameterKind.Integer),"incorrect count readback rejected");
Check(!NativeDimensionValues.Matches(double.NaN,5,NativeDimensionParameterKind.Length),"nonfinite readback rejected");
Check(new NativeBodyValidity(true,1,0).Passed,"complete native validity");
Check(!new NativeBodyValidity(false,1,0).Passed,"incomplete native validity rejected");
Check(!new NativeBodyValidity(true,1,1).Passed,"native faults rejected");
Check(!new NativeBodyValidity(true,0,0).Passed,"no bodies rejected");
var compiler=new RuleBasedTextCompiler();
var plate=compiler.Compile("80 x 50 x 10 mm plate",Path.Combine(output,"plate.SLDPRT"));
Check(plate.Success,"plate compile");
Check(plate.Plan!.Operations.OfType<ProfileSketchOperation>().All(s=>s.AutoDimensionPrimitives&&s.RequireFullyDefined),"plate requires native driving dimensions and definition");
var cylinder=compiler.Compile("cylinder diameter 40 mm height 60 mm",Path.Combine(output,"cylinder.SLDPRT"));
Check(cylinder.Success&&cylinder.Plan!.Operations.OfType<ProfileSketchOperation>().All(s=>s.AutoDimensionPrimitives&&s.RequireFullyDefined),"cylinder requires driving diameter");
var cm=compiler.Compile("80 x 50 x 10 cm plate",Path.Combine(output,"units.SLDPRT"));
Check(cm.Success&&cm.Plan!.Acceptance.ExpectedBoundingBoxMm==new BoundingBoxSpec(800,500,100),"centimeter regression");
Check(!compiler.Compile("cylinder diameter 40 mm height 60 mm shell thickness 2 mm").Success,"unconsumed shell still rejected");
var shortRadius=compiler.Compile("80 x 50 x 10 mm plate all edges fillet R2");
Check(shortRadius.Success&&shortRadius.Plan!.Operations.OfType<NativeFeatureOperation>().Any(o=>o.Options.Kind==NativeFeatureKind.Fillet&&o.Options.RadiusMm==2),"fillet R shorthand consumed with its explicit radius");
var tapped=new NativeFeatureOptions{Kind=NativeFeatureKind.Hole,HoleKind=HoleKind.Tapped,DiameterMm=5,ThreadMajorDiameterMm=6,ThreadDesignation="M6",ThroughAll=false,DepthMm=14,ThreadDepthMm=10,HoleCenters=[new(0,0)]};
List<ModelingDiagnostic> Validate(NativeFeatureOptions options){var errors=new List<ModelingDiagnostic>();NativeFeatureValidation.Validate(new(){Id="test",Name="测试",Options=options},"feature",errors);return errors;}
Check(Validate(tapped).Count==0,"separate drill 14 and thread 10");
Check(Validate(tapped with{ThreadDepthMm=15}).Count>0,"thread depth beyond blind drill rejected");
Check(Validate(tapped with{ThreadDepthMm=double.NaN}).Count>0,"nonfinite thread depth rejected");
Check(Validate(tapped with{HoleKind=HoleKind.Simple}).Count>0,"thread depth on simple hole rejected");
Check(Validate(new(){Kind=NativeFeatureKind.SetDimension,DimensionName="D1@pattern",DimensionValue=5,DimensionUnit=DrawingValueUnit.Unitless}).Count==0,"valid explicit count edit");
Check(Validate(new(){Kind=NativeFeatureKind.SetDimension,DimensionName="D1@pattern",DimensionValue=1.2,DimensionUnit=DrawingValueUnit.Unitless}).Count>0,"fractional count fails compile");
var assembly=new AssemblyPlan{Name="测试",NativePath=Path.Combine(output,"assembly.SLDASM"),AllowedInterferences=[new(["a","b"],10)]};
Check(assembly.RejectUnapprovedInterference,"assembly rejects unapproved interference by default");
Check(AssemblyPlanValidation.InterferenceAllowed(assembly,["b","a"],9),"explicit allowance independent of ordering");
Check(!AssemblyPlanValidation.InterferenceAllowed(assembly,["a","b"],11),"excess interference rejected");
Check(!AssemblyPlanValidation.InterferenceAllowed(assembly,["a","c"],1),"other component interference rejected");
Check(!AssemblyPlanValidation.InterferenceAllowed(assembly,["a","b"],double.NaN),"invalid interference measurement rejected");
Check(!AssemblyPlanValidation.InterferenceAllowed(assembly,["a","b","b"],1),"duplicate component identity rejected");
var countPlan=new ModelingPlan{PlanId="saved_count",Name="数量验收",Operations=[new NativeFeatureOperation{Id="count",Name="数量",Options=new(){Kind=NativeFeatureKind.SetDimension,DimensionName="D1@pattern",DimensionValue=5}}]};
var countSaved=new ModelInspection(true,"",""){Features=[new("pattern","LPattern",false,null,[new("D1@pattern",5,""){ParameterType="swDimensionParamTypeInteger",Driving=true}])]};
SavedModelRequirements.Validate(countPlan,countSaved);Check(true,"saved integer value verified without length conversion");
void RejectSaved(ModelInspection readback,string name){try{SavedModelRequirements.Validate(countPlan,readback);}catch(InvalidOperationException){Check(true,name);return;}throw new Exception("FAIL accepted: "+name);}
RejectSaved(countSaved with{Features=[]},"missing saved parameter rejected");
RejectSaved(countSaved with{Features=[countSaved.Features![0] with{Dimensions=[countSaved.Features[0].Dimensions[0] with{SystemValue=.005}]}]},"scaled integer in saved file rejected");
RejectSaved(countSaved with{Features=[countSaved.Features![0] with{Dimensions=[countSaved.Features[0].Dimensions[0] with{Driving=false}]}]},"reference-only saved parameter rejected");
try{SavedModelRequirements.Validate(plate.Plan!,new(true,"",""){Features=[]});throw new Exception("FAIL missing sketch accepted");}catch(InvalidOperationException){Check(true,"missing final template sketch rejected");}

if(args.Contains("--native"))
{
    var type=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor",true)!;
    var executor=(IModelingExecutor)Activator.CreateInstance(type,true)!;
    async Task<ExecutionResult> Build(ModelingPlan plan,string id)
    {
        plan=plan with{Recovery=new(){Enabled=false}};
        File.WriteAllText(Path.Combine(output,id+"-plan.json"),ModelingIrJson.Serialize(plan));
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var result=await executor.ExecuteAsync(plan,false,deadline.Token);
        File.WriteAllText(Path.Combine(output,id+"-result.json"),JsonSerializer.Serialize(result,ModelingIrJson.Options));
        if(!result.Success&&File.Exists(plan.Output.NativePath))File.WriteAllText(Path.Combine(output,id+"-failed-inspection.json"),JsonSerializer.Serialize(await executor.InspectAsync(new(plan.Output.NativePath!)),ModelingIrJson.Options));
        Check(result.Success,id+" native build: "+result.Message);
        Check(result.Evidence.Any(e=>e.Stage=="native_body_validity"&&e.Passed),id+" native fault check");
        Check(result.Evidence.Any(e=>e.Stage=="saved_model_readback"&&e.Passed),id+" unconditional final reopen");
        return result;
    }
    if(!args.Contains("--extended"))
    {
    await Build(plate.Plan!,"plate");
    await Build(cylinder.Plan!,"cylinder");
    var plateInspection=await executor.InspectAsync(new(plate.Plan!.Output.NativePath!));
    File.WriteAllText(Path.Combine(output,"plate-inspection.json"),JsonSerializer.Serialize(plateInspection,ModelingIrJson.Options));
    Check(plateInspection.Success&&plateInspection.BodyValidity is {Passed:true},"plate independent inspection");
    Check(plateInspection.Features!.Where(f=>f.SketchConstraintStatus is not null).All(f=>f.SketchConstraintStatus==3),"saved template sketch fully defined");
    var linear=plateInspection.Features!.SelectMany(f=>f.Dimensions).First(d=>d.Name.StartsWith("宽度_0@",StringComparison.Ordinal));
    Check(Math.Abs(linear.Value!.Value-80)<1e-6,"native driving width readback");
    var probe=await executor.InspectAsync(new(plate.Plan.Output.NativePath!,EditabilityProbes:[new("width_trial",linear.Name,90)]));
    File.WriteAllText(Path.Combine(output,"plate-editability.json"),JsonSerializer.Serialize(probe,ModelingIrJson.Options));
    Check(probe.Success&&probe.EditabilityProbes.Single().Passed,"driving width trial and restore");
    var perforated=compiler.Compile("80 x 50 x 10 mm plate center diameter 10 mm through hole",Path.Combine(output,"perforated.SLDPRT"));
    Check(perforated.Success,"plate with centered hole compiles");
    await Build(perforated.Plan!,"perforated");
    var perforatedRead=await executor.InspectAsync(new(perforated.Plan!.Output.NativePath!));
    File.WriteAllText(Path.Combine(output,"perforated-inspection.json"),JsonSerializer.Serialize(perforatedRead,ModelingIrJson.Options));
    Check(perforatedRead.Success&&perforatedRead.Features!.Single(f=>f.Name=="基础轮廓草图").SketchConstraintStatus==3,"plate and hole both fully constrained");
    Check(perforatedRead.Features!.SelectMany(f=>f.Dimensions).Any(d=>d.Name.StartsWith("孔径_1@")&&d.Driving is true&&Math.Abs(d.Value!.Value-10)<1e-6),"saved center-hole driving diameter");
    var surfacePlan=new ModelingPlan{PlanId="sheet",Name="平面曲面",Output=new(){NativePath=Path.Combine(output,"sheet.SLDPRT")},
        Operations=[new ProfileSketchOperation{Id="sheet_sketch",Name="曲面轮廓",AutoDimensionPrimitives=true,RequireFullyDefined=true,Primitives=[new CenteredRectangleProfile{WidthMm=20,HeightMm=30}]},
            new NativeFeatureOperation{Id="sheet_surface",Name="平面曲面",DependsOn=["sheet_sketch"],Options=new(){Kind=NativeFeatureKind.SurfacePlanar,SketchId="sheet_sketch"}}],
        Acceptance=new(){Geometry=new(){ExpectedSolidBodyCount=0,ExpectedSurfaceBodyCount=1,RequirePositiveVolume=false}}};
    await Build(surfacePlan,"sheet");
    }
    else plate=plate with{Plan=plate.Plan! with{Output=new(){NativePath=Path.GetFullPath("artifacts/engineering-hardening-20261002/native-04/plate.SLDPRT")}}};
    if(!args.Contains("--remaining"))
    {
    var seed=compiler.Compile("10 x 10 x 10 mm plate",Path.Combine(output,"pattern3.SLDPRT")).Plan!;
    var axis=new NativeFeatureOperation{Id="axis",Name="阵列方向轴",Options=new(){Kind=NativeFeatureKind.ReferenceAxis,AxisStartMm=new(0,0,0),AxisEndMm=new(100,0,0)}};
    var pattern=new NativeFeatureOperation{Id="pattern",Name="实体阵列",DependsOn=["axis"],Options=new(){Kind=NativeFeatureKind.LinearPattern,Count=3,SpacingMm=20,
        Selections=[new(){Kind=EntityKind.Axis,FeatureId="axis",SelectionMark=1},new(){Kind=EntityKind.Body,AllMatches=true,SelectionMark=256}]}};
    var patternPlan=seed with{Operations=seed.Operations.Concat(new ModelingOperation[]{axis,pattern}).ToArray(),Acceptance=new(){Geometry=new(){ExpectedSolidBodyCount=3}}};
    await Build(patternPlan,"pattern3");
    var patternInspection=await executor.InspectAsync(new(patternPlan.Output.NativePath!));
    File.WriteAllText(Path.Combine(output,"pattern3-inspection.json"),JsonSerializer.Serialize(patternInspection,ModelingIrJson.Options));
    var count=patternInspection.Features!.Single(f=>f.Name=="实体阵列").Dimensions.Single(d=>d.ParameterType=="swDimensionParamTypeInteger");
    Check(count.SystemValue==3,"native pattern count read as integer 3");
    var edit=patternPlan with{PlanId="edit_count",Name="数量修改",SourceModelPath=patternPlan.Output.NativePath,
        Operations=[new NativeFeatureOperation{Id="count_edit",Name="数量改为五",Options=new(){Kind=NativeFeatureKind.SetDimension,DimensionName=count.Name,DimensionValue=5,DimensionUnit=DrawingValueUnit.Unitless}}],
        Output=new(){NativePath=Path.Combine(output,"pattern5.SLDPRT")},Acceptance=new(){Geometry=new(){ExpectedSolidBodyCount=5}}};
    await Build(edit,"pattern5");
    var edited=await executor.InspectAsync(new(edit.Output.NativePath!));
    File.WriteAllText(Path.Combine(output,"pattern5-inspection.json"),JsonSerializer.Serialize(edited,ModelingIrJson.Options));
    Check(edited.Success&&edited.Geometry!.SolidBodyCount==5&&edited.Features!.SelectMany(f=>f.Dimensions).Single(d=>d.Name==count.Name).SystemValue==5,"saved integer edit produces five bodies");
    var invalidEdit=edit with{PlanId="invalid_unit",Output=new(){NativePath=Path.Combine(output,"invalid-unit.SLDPRT")},
        Operations=[new NativeFeatureOperation{Id="invalid_count",Name="错误单位",Options=new(){Kind=NativeFeatureKind.SetDimension,DimensionName=count.Name,DimensionValue=5,DimensionUnit=DrawingValueUnit.Millimeter}}]};
    var invalidResult=await executor.ExecuteAsync(invalidEdit with{Recovery=new(){Enabled=false}},false);
    File.WriteAllText(Path.Combine(output,"invalid-unit-result.json"),JsonSerializer.Serialize(invalidResult,ModelingIrJson.Options));
    Check(!invalidResult.Success,"real integer parameter rejects millimeter edit");
    }
    var holeSeed=compiler.Compile("30 x 30 x 20 mm plate",Path.Combine(output,"tapped.SLDPRT")).Plan!;
    var hole=new NativeFeatureOperation{Id="tapped_hole",Name="M6攻丝孔",Options=tapped with{Frame=new(){OriginMm=new(0,0,20)},Reverse=true}};
    await Build(holeSeed with{Operations=holeSeed.Operations.Append(hole).ToArray(),Acceptance=holeSeed.Acceptance with{Geometry=holeSeed.Acceptance.Geometry with{ExpectedVolumeMm3=18000-Math.PI*2.5*2.5*14}}},"tapped");
    var holeRead=await executor.InspectAsync(new(holeSeed.Output.NativePath!));
    File.WriteAllText(Path.Combine(output,"tapped-inspection.json"),JsonSerializer.Serialize(holeRead,ModelingIrJson.Options));
    var thread=holeRead.CosmeticThreads.Single();
    Check(thread.Complete&&!thread.ThroughAll&&Math.Abs(thread.BlindDepthMm-10)<1e-6,"saved cosmetic thread depth 10 mm");
    Check(holeRead.Features!.Single(f=>f.Name=="M6攻丝孔").Dimensions.Any(d=>d.Value is{} value&&Math.Abs(value-14)<1e-6),"saved drill depth 14 mm");
    var assemblyPlan=new AssemblyPlan{Name="干涉验收",NativePath=Path.Combine(output,"interference.SLDASM"),RequireFullyConstrainedComponents=true,
        Components=[new(){Id="a",Name="板一",Path=plate.Plan.Output.NativePath!,Fixed=true},new(){Id="b",Name="板二",Path=plate.Plan.Output.NativePath!,Fixed=true}]};
    async Task<AssemblyResult> BuildAssemblyKernel(AssemblyPlan plan,string id)
    {
        var completion=new TaskCompletionSource<AssemblyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{completion.SetResult((AssemblyResult)type.GetMethod("BuildAssemblyOnSta",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[plan,CancellationToken.None])!);}catch(Exception ex){completion.SetException(ex);}});
        thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();
        var result=await completion.Task;
        File.WriteAllText(Path.Combine(output,id+"-result.json"),JsonSerializer.Serialize(result,ModelingIrJson.Options));
        return result;
    }
    var interference=await BuildAssemblyKernel(assemblyPlan,"interference");
    Check(!interference.Success&&interference.Message.StartsWith("ASSEMBLY_INTERFERENCE")&&interference.Interferences is{Count:>0}&&!File.Exists(assemblyPlan.NativePath),"overlapping assembly rejected before save");
    var allowed=await BuildAssemblyKernel(assemblyPlan with{NativePath=Path.Combine(output,"allowed.SLDASM"),AllowedInterferences=[new(["a","b"],40000.01)]},"allowed");
    Check(allowed.Success&&allowed.Reopened&&allowed.Interferences is{Count:>0},"explicit limited interference allowed and assembly reopened");
    var under=await BuildAssemblyKernel(assemblyPlan with{NativePath=Path.Combine(output,"underconstrained.SLDASM"),Components=[assemblyPlan.Components[0],assemblyPlan.Components[1] with{Fixed=false,TranslationMm=new(100,0,0)}]},"underconstrained");
    Check(!under.Success&&under.Message.StartsWith("ASSEMBLY_UNDERCONSTRAINED")&&!File.Exists(Path.Combine(output,"underconstrained.SLDASM")),"requested complete assembly constraint rejects floating component");
}
File.WriteAllText(Path.Combine(output,"regression-summary.json"),JsonSerializer.Serialize(new{passed,scope=args.Contains("--native")?"core_and_selected_native_fixtures":"core_only"}));
Console.WriteLine($"EngineeringHardeningRegression: {passed} passed");
