using System.Reflection;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
var part=Path.GetFullPath(args[0]);var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
var type=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor",true)!;
int passed=0;
var endpoint=new AssemblyMateEntity{ComponentId="a",Entity=new(){Kind=EntityKind.Face,Geometry=GeometryKind.Plane,PositionMm=new(40,0,5),Direction=new(1,0,0)}};
var valid=new AssemblyMateSpec{Name="规范配合",Kind=AssemblyMateKind.Distance,Value=100,First=endpoint,Second=endpoint with{ComponentId="b"}};
void Reject(AssemblyMateSpec mate,string label)
{
    try{AssemblyPlanValidation.ValidateMate(mate);}catch(ArgumentException){passed++;Console.WriteLine("PASS: reject "+label);return;}
    throw new Exception("Accepted "+label);
}
Reject(valid with{Kind=(AssemblyMateKind)999},"unknown mate kind");
Reject(valid with{Name=""},"empty mate name");
Reject(valid with{Value=double.NaN},"NaN mate value");
Reject(valid with{Kind=AssemblyMateKind.Angle,Value=181},"angle beyond native range");
Reject(valid with{Kind=AssemblyMateKind.Coincident,Value=2},"ignored coincident numeric value");
Reject(valid with{LockRotation=true},"rotation flag on distance mate");
Reject(valid with{Kind=AssemblyMateKind.Perpendicular,Value=0,AntiAligned=true},"unsupported perpendicular alignment");
Reject(valid with{First=endpoint with{Entity=endpoint.Entity! with{AllMatches=true}}},"ambiguous endpoint");
Reject(valid with{First=endpoint with{Entity=null}},"missing geometric endpoint");
Reject(valid with{Kind=AssemblyMateKind.Lock,Value=0},"geometric reference on whole-component lock");
Reject(valid with{First=endpoint with{Entity=endpoint.Entity! with{PositionMm=new(double.NaN,0,0)}}},"nonfinite endpoint position");
Reject(valid with{First=endpoint with{ComponentId=""}},"empty endpoint component identity");
foreach(var kind in new[]{AssemblyMateKind.Distance,AssemblyMateKind.Angle})
{
    var query=kind==AssemblyMateKind.Distance?new EntityQuery{Kind=EntityKind.Face,Geometry=GeometryKind.Plane,PositionMm=new(40,0,5),Direction=new(1,0,0)}:
        new EntityQuery{Kind=EntityKind.Face,Geometry=GeometryKind.Plane,PositionMm=new(0,0,10),Direction=new(0,0,1)};
    var plan=new AssemblyPlan{Name="配合读回_"+kind,NativePath=Path.Combine(output,kind+".SLDASM"),
        Components=[new(){Id="a",Name="基板",Path=part,Fixed=true},new(){Id="b",Name="配合板",Path=part,TranslationMm=new(100,0,0),RotationDegrees=kind==AssemblyMateKind.Angle?new(45,0,0):new(0,0,0)}],
        Mates=[new(){Name="验证配合",Kind=kind,Value=kind==AssemblyMateKind.Distance?100:45,First=new(){ComponentId="a",Entity=query},Second=new(){ComponentId="b",Entity=query}}]};
    File.WriteAllText(Path.Combine(output,kind+"-plan.json"),JsonSerializer.Serialize(plan,ModelingIrJson.Options));
    var completion=new TaskCompletionSource<AssemblyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread=new Thread(()=>{try{completion.SetResult((AssemblyResult)type.GetMethod("BuildAssemblyOnSta",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[plan,CancellationToken.None])!);}catch(Exception ex){completion.SetException(ex);}});
    thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();
    var result=await completion.Task;
    File.WriteAllText(Path.Combine(output,kind+"-result.json"),JsonSerializer.Serialize(result,ModelingIrJson.Options));
    if(!result.Success||!result.Reopened||result.Mates is not{Count:1}||result.MateReadbacks.Single().Endpoints.Count!=2)throw new Exception(kind+" failed: "+result.Message);
    passed++;Console.WriteLine("PASS: saved native "+kind+" mate type and value readback");
}
if(args.Length>2)
{
    var cylinder=Path.GetFullPath(args[2]);
    var concentric=new EntityQuery{Kind=EntityKind.Face,Geometry=GeometryKind.Cylinder,PositionMm=new(20,0,30),Direction=new(0,0,1),RadiusMm=20};
    foreach(var scenario in new[]{"anti-distance","concentric-free","concentric-locked","component-lock"})
    {
        bool isCylinder=scenario.StartsWith("concentric",StringComparison.Ordinal),isLock=scenario=="component-lock";
        var query=isCylinder?concentric:endpoint.Entity;
        var plan=new AssemblyPlan{Name=scenario,NativePath=Path.Combine(output,scenario+".SLDASM"),
            Components=[new(){Id="a",Name="基础",Path=isCylinder?cylinder:part,Fixed=true},new(){Id="b",Name="从动",Path=isCylinder?cylinder:part,
                TranslationMm=isCylinder?new(30,0,100):new(100,0,0),RotationDegrees=scenario=="anti-distance"?new(0,0,180):new(0,0,0)}],
            Mates=[new(){Name="验证配合",Kind=isCylinder?AssemblyMateKind.Concentric:isLock?AssemblyMateKind.Lock:AssemblyMateKind.Distance,
                Value=scenario=="anti-distance"?100:0,AntiAligned=scenario=="anti-distance",LockRotation=scenario=="concentric-locked",
                First=new(){ComponentId="a",Entity=isLock?null:query},Second=new(){ComponentId="b",Entity=isLock?null:query}}],
            Mobility=isCylinder||isLock?new(){Requirements=[new(){ComponentId="b",RelativeToComponentId="a",SourceLiteral="独立源意图：同心轴向平移；自由旋转模式另外保留绕轴旋转；锁定组件零相对运动",
                ExpectedBasis=isLock?[]:scenario=="concentric-locked"?[new(){Kind=AssemblyMotionKind.Translation,Direction=new(0,0,1)}]:
                [new(){Kind=AssemblyMotionKind.Translation,Direction=new(0,0,1)},new(){Kind=AssemblyMotionKind.Rotation,Direction=new(0,0,1),PointMm=new(0,0,0)}]}]}:null};
        File.WriteAllText(Path.Combine(output,scenario+"-plan.json"),JsonSerializer.Serialize(plan,ModelingIrJson.Options));
        var completion=new TaskCompletionSource<AssemblyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{completion.SetResult((AssemblyResult)type.GetMethod("BuildAssemblyOnSta",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[plan,CancellationToken.None])!);}catch(Exception ex){completion.SetException(ex);}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();var result=await completion.Task;
        File.WriteAllText(Path.Combine(output,scenario+"-result.json"),JsonSerializer.Serialize(result,ModelingIrJson.Options));
        if(!result.Success||!result.Reopened)throw new Exception(scenario+": "+result.Message);
        var readback=result.MateReadbacks.Single();
        if(readback.Endpoints.Select(e=>e.ComponentId).Distinct().Count()!=2||readback.Endpoints.Any(e=>string.IsNullOrEmpty(e.PersistentReference)))throw new Exception("missing endpoint identities");
        if(isCylinder&&readback.LockRotation!=plan.Mates[0].LockRotation)throw new Exception("wrong rotation lock");
        passed++;Console.WriteLine("PASS: saved native "+scenario+" endpoints/alignment/rotation");
        if(isCylinder||isLock)
        {
            int expected=isLock?0:scenario=="concentric-locked"?1:2;
            if(result.MobilityReadbacks is not {Count:1}||result.MobilityReadbacks[0].DegreesOfFreedom!=expected)throw new Exception("native mobility did not preserve expected relative motion");
            passed++;Console.WriteLine("PASS: saved native "+scenario+" independently declared relative degrees of freedom "+expected);
        }
        if(scenario=="concentric-locked")
        {
            var constrained=plan with{Name="轴向未约束负例",NativePath=Path.Combine(output,"underconstrained.SLDASM"),RequireFullyConstrainedComponents=true,Mobility=null};
            var check=new TaskCompletionSource<AssemblyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var checkThread=new Thread(()=>{try{check.SetResult((AssemblyResult)type.GetMethod("BuildAssemblyOnSta",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[constrained,CancellationToken.None])!);}catch(Exception ex){check.SetException(ex);}});
            checkThread.SetApartmentState(ApartmentState.STA);checkThread.Start();var rejected=await check.Task;
            File.WriteAllText(Path.Combine(output,"underconstrained-result.json"),JsonSerializer.Serialize(rejected,ModelingIrJson.Options));
            if(rejected.Success||!rejected.Message.Contains("ASSEMBLY_UNDERCONSTRAINED",StringComparison.Ordinal))throw new Exception("Axial degree of freedom accepted as fully constrained");
            passed++;Console.WriteLine("PASS: locked rotation still rejects free axial motion when fully constrained required");
        }
    }
    passed+=await SavedMateNegativeChecks.Run(type,output);
}
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{passed,scope="native_serial_STA_assembly_kernel_no_drawing_export"}));
