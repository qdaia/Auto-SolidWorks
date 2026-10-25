using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
static class CheckpointChecks
{
    public static void Run(string output,Action<bool,string> check)
    {
        var directory=Path.Combine(output,"checkpoints");Directory.CreateDirectory(directory);
        var draft=new GenericModelDraft{SourceText="checkpoint fixture",Recovery=new(){Directory=directory,SourceRevisionId="fixture"},Operations=[
            new(){Id="sk",Type=GenericOperationKind.ProfileSketch,Plane=ReferencePlane.Front,Primitives=[new(){Type=GenericPrimitiveKind.Circle,DiameterMm=20,CenterXmm=0,CenterYmm=0}]},
            new(){Id="boss",Type=GenericOperationKind.ExtrudeBoss,SketchId="sk",EndCondition=ExtrudeEndCondition.Blind,DepthMm=10},
            new(){Id="hole",Type=GenericOperationKind.NativeFeature,Feature=new(){Kind=NativeFeatureKind.Hole,DiameterMm=4,ThroughAll=true,Reverse=true,Frame=new(){OriginMm=new(0,0,10)},HoleCenters=[new(0,0)]}}]};
        var initial=new GenericPlanCompiler().Compile(draft,Path.Combine(output,"checkpoint-not-native.SLDPRT"));
        if(!initial.Success)throw new Exception(JsonSerializer.Serialize(initial.Diagnostics,ModelingIrJson.Options));
        var plan=initial.Plan!;
        string Save(string name,int count,ModelingPlan basis)
        {
            var folder=Path.Combine(directory,name);Directory.CreateDirectory(folder);var native=Path.Combine(folder,"fixture.SLDPRT");
            File.WriteAllText(native,"Managed hash fixture, NOT a native model: "+name);
            var m=new ModelingCheckpointManifest{NativePath=native,NativeSha256=DrawingPlanValidation.FileHash(native),OriginalPlan=basis,
                SourceRevisionId="fixture",CompletedOperationCount=count,CompletedOperationIds=basis.Operations.Take(count).Select(o=>o.Id).ToArray(),
                FeatureReferences=basis.Operations.Take(count).Select(o=>new StableFeatureReference(o.Id,o.Name,o.GetType().Name,o.Name,1,"test",o.DependsOn)).ToArray(),
                Geometry=new(1,3,2,100,100,new(0,0,0),new(20,20,10)),WriteState=CheckpointWriteState.Complete,ModelReopened=true,
                Environment=new(){CompilerVersion=ComponentContentIdentity.ForType(typeof(GenericPlanCompiler),"generic-plan-compiler/v1"),ExecutorVersion="managed fixture",VerifierVersion="managed fixture"},
                TypedPlanFingerprint=ModelingRecovery.TypedPlanFingerprint(basis),CompletedPrefixFingerprint=ModelingRecovery.PrefixFingerprint(basis,count)};
            var path=Path.Combine(folder,"checkpoint.json");File.WriteAllText(path,JsonSerializer.Serialize(m,ModelingIrJson.Options));return path;
        }
        var old=Save("base",2,plan);var latest=Save("latest",3,plan);
        var changed=new GenericPlanCompiler().Compile(draft with{Operations=draft.Operations.Select(o=>o.Id=="hole"?o with{Feature=o.Feature! with{DiameterMm=5}}:o).ToArray()},Path.Combine(output,"changed.SLDPRT")).Plan!;
        var selection=ModelingRecovery.SelectCompatibleCheckpoint(changed);
        check(selection.ManifestPath==old,"changed suffix selects actual earlier base snapshot");
        check(selection.Candidates.Single(c=>c.Path==latest).Compatible==false,"later snapshot is rejected rather than truncated in memory");
        check(ModelingRecovery.SelectCompatibleCheckpoint(plan).ManifestPath==latest,"unchanged plan selects longest compatible prefix");
        File.AppendAllText(Path.Combine(directory,"base","fixture.SLDPRT"),"changed");
        check(ModelingRecovery.SelectCompatibleCheckpoint(changed).ManifestPath is null,"tampered base hash cannot be reused");
        var sourceChanged=plan with{Recovery=plan.Recovery with{SourceRevisionId="different"}};
        check(ModelingRecovery.SelectCompatibleCheckpoint(sourceChanged).ManifestPath is null,"source revision mismatch cannot be reused");
        var auto=new GenericPlanCompiler().Compile(draft with{Recovery=draft.Recovery with{AutoSelectCheckpoint=true}},Path.Combine(output,"auto.SLDPRT"));
        check(auto.Success&&auto.Plan!.Recovery.ResumeManifestPath==latest&&auto.Diagnostics.Any(d=>d.Code=="RECOVERY_AUTO_SELECTED"),"compiler chooses compatible checkpoint and records reason");
    }
}
