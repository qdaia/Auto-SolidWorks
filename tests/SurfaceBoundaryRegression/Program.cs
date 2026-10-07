using System.Reflection;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

if(args.Length<2)throw new ArgumentException("Usage: output-directory draft-json [draft-json ...]");
if(args[1]=="--curvature-core")return CurvatureCoreChecks.Run(args[0]);
if(args[1]=="--curvature-native")return await CurvatureNativeChecks.Run(args[0],args[2],args[3]);
if(args[1]=="--diagnose")return await NativeDiagnostics.Run(args[0],args[2]);
if(args[1]=="--spatial")return await SpatialCurveChecks.Run(args[0]);
var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
var type=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor",true)!;
var executor=(IModelingExecutor)Activator.CreateInstance(type,true)!;
int failed=0;
foreach(var input in args.Skip(1))
{
    var id=Path.GetFileNameWithoutExtension(input);
    var draft=JsonSerializer.Deserialize<GenericModelDraft>(File.ReadAllText(input),ModelingIrJson.Options)!;
    var compilation=new GenericPlanCompiler().Compile(draft,Path.Combine(output,id+".SLDPRT"));
    File.WriteAllText(Path.Combine(output,id+"-compilation.json"),JsonSerializer.Serialize(compilation,ModelingIrJson.Options));
    if(!compilation.Success){Console.WriteLine("FAIL compile "+id);failed++;continue;}
    var plan=compilation.Plan! with{Recovery=new(){Enabled=false}};
    File.WriteAllText(Path.Combine(output,id+"-plan.json"),ModelingIrJson.Serialize(plan));
    using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var result=await executor.ExecuteAsync(plan,false,deadline.Token);
    File.WriteAllText(Path.Combine(output,id+"-result.json"),JsonSerializer.Serialize(result,ModelingIrJson.Options));
    Console.WriteLine($"{(result.Success?"PASS":"FAIL")} native {id}: {result.Message}");
    if(!result.Success)failed++;
}
return failed==0?0:1;
