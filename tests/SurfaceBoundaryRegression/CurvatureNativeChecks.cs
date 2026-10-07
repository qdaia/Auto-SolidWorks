using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;

internal static class CurvatureNativeChecks
{
    internal static async Task<int> Run(string output,string analyticDirectory,string fillPath)
    {
        output=Path.GetFullPath(output);analyticDirectory=Path.GetFullPath(analyticDirectory);fillPath=Path.GetFullPath(fillPath);
        Directory.CreateDirectory(output);int passed=0;var checkNames=new List<string>();
        void Check(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);passed++;checkNames.Add(name);Console.WriteLine("PASS: "+name);}
        var kernel=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor",true)!;
        var executor=(IModelingExecutor)Activator.CreateInstance(kernel,true)!;
        foreach(var test in new[]{(Id:"coplanar-knit",A:new Vector3(0,-10,0),B:new Vector3(0,10,0),ExpectedCurvature:0.0),
            (Id:"plane-cylinder",A:new Vector3(8,10,0),B:new Vector3(8,10,10),ExpectedCurvature:.5)})
        {
            var path=Path.Combine(analyticDirectory,test.Id+".SLDPRT");var hash=SHA256.HashData(File.ReadAllBytes(path));
            var edge=new EntityQuery{Kind=EntityKind.Edge,Geometry=GeometryKind.Line,StartPointMm=test.A,EndPointMm=test.B};
            var check=new SurfaceContinuityCheck{Id="G1",SourceLiteral="Analytic native seam reference",Edge=edge,Samples=33};
            var inspection=await executor.InspectAsync(new(path,Verification:new(){SurfaceContinuity=[check,check with{Id="G2",RequireCurvatureContinuity=true,CurvatureTolerancePerMm=1e-6}]}));
            File.WriteAllText(Path.Combine(output,test.Id+"-inspection.json"),JsonSerializer.Serialize(inspection,ModelingIrJson.Options));
            Check(inspection.CaptureComplete&&inspection.BodyValidity is {Passed:true},test.Id+" complete native rebuild and body validity");
            var g1=inspection.Verification!.Checks.Single(c=>c.Id=="G1");var g2=inspection.Verification.Checks.Single(c=>c.Id=="G2");
            Check(g1.Passed,test.Id+" passes measured G0/G1 seam");
            Check(g2.Measurements is not null&&g2.Measurements["curvature_samples"]=="33",test.Id+" has all 33 native second derivative samples");
            var measured=double.Parse(g2.Measurements!["max_normal_curvature_difference_per_mm"],CultureInfo.InvariantCulture);
            Check(Math.Abs(measured-test.ExpectedCurvature)<1e-8,test.Id+" independently matches analytic curvature "+test.ExpectedCurvature);
            Check(test.ExpectedCurvature==0?g2.Passed&&inspection.Success:!g2.Passed&&!inspection.Success&&g2.Status=="mismatch",test.Id+" requested G2 acceptance is accurate");
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))),test.Id+" read-only source bytes unchanged");
        }
        var done=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{done.SetResult(DefinitionNegatives(kernel,fillPath,output));}catch(Exception e){done.SetException(e);}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();var negatives=await done.Task;passed+=negatives;
        File.WriteAllText(Path.Combine(output,"native-summary.json"),JsonSerializer.Serialize(new{passed,seam_checks=checkNames,definition_checks=negatives,
            scope="native_analytic_G2_seams_and_saved_definition_positive_negative_readback"},ModelingIrJson.Options));return 0;
    }

    private static int DefinitionNegatives(Type kernel,string path,string output)
    {
        int passed=0;var receipts=new List<object>();
        var app=(ISldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)!;
        var hash=SHA256.HashData(File.ReadAllBytes(path));int errors=0,warnings=0;
        var model=(IModelDoc2)app.OpenDoc6(path,1,3,"",ref errors,ref warnings);
        if(model is null||errors!=0)throw new IOException("definition fixture cannot reopen");
        try
        {
            var parameterPath=Path.Combine(Path.GetDirectoryName(path)!,Path.GetFileNameWithoutExtension(path)+"_parameters.json");
            var plan=JsonSerializer.Deserialize<ModelingPlan>(File.ReadAllText(parameterPath),ModelingIrJson.Options)!;
            var capture=kernel.GetMethod("CaptureAdvancedDefinitions",BindingFlags.Static|BindingFlags.NonPublic)!;
            var verify=kernel.GetMethod("VerifySavedAdvancedDefinitions",BindingFlags.Static|BindingFlags.NonPublic)!;
            var snapshot=capture.Invoke(null,[model,plan,new Dictionary<string,object>()])!;
            verify.Invoke(null,[model,plan,snapshot]);passed++;Console.WriteLine("PASS: saved native Fill definition positive readback");
            receipts.Add(new{test="positive",passed=true});
            foreach(var test in new[]{"wrong-contact","wrong-feature-type","wrong-boundary-identity"})
            {
                var json=JsonNode.Parse(JsonSerializer.Serialize(snapshot,snapshot.GetType(),ModelingIrJson.Options))!;
                var item=json[0]!;
                if(test=="wrong-contact")item["state"]!["boundary_0_control"]="2";
                else if(test=="wrong-feature-type")item["feature_type"]="RefSurface";
                else item["state"]!["boundary_0_identity"]=item["state"]!["boundary_1_identity"]!.GetValue<string>();
                var altered=JsonSerializer.Deserialize(json.ToJsonString(),snapshot.GetType(),ModelingIrJson.Options)!;
                bool rejected=false;string? message=null;
                try{verify.Invoke(null,[model,plan,altered]);}
                catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){rejected=true;message=e.InnerException.Message;}
                if(!rejected)throw new Exception("FAIL: native saved definition accepted "+test);
                passed++;Console.WriteLine("PASS: native saved definition rejects "+test);receipts.Add(new{test,passed=true,message});
            }
        }
        finally{app.CloseDoc(model.GetTitle());}
        if(!hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))))throw new Exception("definition checks changed source bytes");
        passed++;receipts.Add(new{test="read_only_source_hash",passed=true});
        File.WriteAllText(Path.Combine(output,"definition-checks.json"),JsonSerializer.Serialize(receipts,ModelingIrJson.Options));return passed;
    }
}
