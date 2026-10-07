using System.Reflection;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;

internal static class SavedMateNegativeChecks
{
    public static Task<int> Run(Type kernel,string output)
    {
        var done=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{done.SetResult(OnSta(kernel,output));}catch(Exception ex){done.SetException(ex);}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
    private static int OnSta(Type kernel,string output)
    {
        int passed=0;
        var app=(ISldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)!;
        foreach(var scenario in new[]{"Distance","concentric-locked"})
        {
            var plan=JsonSerializer.Deserialize<AssemblyPlan>(File.ReadAllText(Path.Combine(output,scenario+"-plan.json")),ModelingIrJson.Options)!;
            var result=JsonSerializer.Deserialize<AssemblyResult>(File.ReadAllText(Path.Combine(output,scenario+"-result.json")),ModelingIrJson.Options)!;
            int errors=0,warnings=0;var model=(IModelDoc2)app.OpenDoc6(plan.NativePath,2,3,"",ref errors,ref warnings);
            try
            {
                var features=(IReadOnlyList<IFeature>)kernel.GetMethod("MateFeatures",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[model])!;
                var feature=features.Single(f=>f.Name==plan.Mates[0].Name);var mate=(IMate2)feature.GetSpecificFeature2();
                var components=((object[])((IAssemblyDoc)model).GetComponents(true)).Cast<IComponent2>().ToDictionary(c=>c.Name2);
                void Reject(AssemblyMateSpec corrupt,string expectedCode,string label)
                {
                    try{kernel.GetMethod("VerifySavedMate",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[model,feature,mate,corrupt,result.Components!,components]);}
                    catch(TargetInvocationException ex)when(ex.InnerException is IOException error&&error.Message.Contains(expectedCode,StringComparison.Ordinal))
                    {passed++;File.WriteAllText(Path.Combine(output,label+"-negative.json"),JsonSerializer.Serialize(new{passed=true,error=error.Message},ModelingIrJson.Options));Console.WriteLine("PASS: saved mate rejects "+label);return;}
                    throw new Exception("Saved mate accepted "+label);
                }
                var spec=plan.Mates[0];
                if(scenario=="Distance")
                {
                    Reject(spec with{AntiAligned=true},"ASSEMBLY_MATE_ALIGNMENT","wrong-alignment");
                    Reject(spec with{First=spec.First with{ComponentId=spec.Second.ComponentId}},"ASSEMBLY_MATE_COMPONENT","wrong-instance");
                    Reject(spec with{First=spec.First with{Entity=spec.First.Entity! with{PositionMm=new(-40,0,5),Direction=new(-1,0,0)}}},"ASSEMBLY_MATE_ENTITY","wrong-face");
                }
                else Reject(spec with{LockRotation=false},"ASSEMBLY_MATE_ROTATION","wrong-rotation-lock");
            }
            finally{app.CloseDoc(model.GetTitle());}
        }
        return passed;
    }
}
