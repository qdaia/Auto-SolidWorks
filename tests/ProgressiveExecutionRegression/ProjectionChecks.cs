using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

static class ProjectionChecks
{
    public static async Task Run(string output,Action<bool,string> check)
    {
        var sourcePath=Path.Combine(output,"projection-source.txt");File.WriteAllText(sourcePath,"independent source circle radius 3 at 0,0");
        var nativePath=Path.Combine(output,"fake-native-for-offline-projection.sldprt");File.WriteAllText(nativePath,"offline mock; not SolidWorks");
        var source=new ProjectionSnapshot{ViewId="front",CoordinateFrameId="fixed-mm",SourceSha256=DrawingPlanValidation.FileHash(sourcePath),
            Primitives=[new(){Id="hole",Kind=ProjectionPrimitiveKind.Circle,RadiusMm=3}]};
        var request=new SourceProjectionComparisonRequest{SourcePath=sourcePath,NativePath=nativePath,Source=source,
            CalibrationEvidence="independent test specification",SourceRegions=new Dictionary<string,DrawingReviewBox>{{"hole",new(0.1,0.1,0.2,0.2)}}};
        var fake=new ProjectionExecutor{Actual=source with{NativeModelPath=nativePath,NativeModelSha256=DrawingPlanValidation.FileHash(nativePath),NativeModelReopened=true}};
        var store=new CompactArtifactStore(Path.Combine(output,"projection-artifacts"),"projection-offline");
        async Task<JsonElement> Compare(SourceProjectionComparisonRequest r)=>JsonSerializer.SerializeToElement(await SourceProjectionComparisonTools.Compare(fake,store,r,"full"),ModelingIrJson.Options);
        var result=await Compare(request);
        check(result.GetProperty("success").GetBoolean(),"independent declared projection matches controlled capture");
        fake.Actual=fake.Actual with{Primitives=[source.Primitives[0] with{Center=new(2,0)}]};
        result=await Compare(request);
        check(!result.GetProperty("success").GetBoolean() && result.GetProperty("locations")[0].GetProperty("source_bounds").ValueKind==JsonValueKind.Object,"shifted projection reports mismatch with source location");
        async Task Reject(SourceProjectionComparisonRequest r,string name)
        {var calls=fake.Calls;try{await Compare(r);}catch(ArgumentException){check(calls==fake.Calls,name);return;}throw new Exception(name);}
        await Reject(request with{Source=source with{Primitives=[source.Primitives[0] with{RadiusMm=-1}]}},"invalid source geometry rejected before native capture");
        await Reject(request with{Options=new(){PositionToleranceMm=-1}},"invalid projection tolerance rejected before native capture");
        await Reject(request with{Source=source with{NativeModelPath=nativePath}},"model-derived expected snapshot rejected before native capture");
        await Reject(request with{SourceRegions=new Dictionary<string,DrawingReviewBox>()},"unlocalized source geometry rejected before native capture");
        await Reject(request with{Source=source with{SourceSha256=new string('a',64)}},"changed source digest rejected before native capture");
    }
    sealed class ProjectionExecutor:IModelingExecutor
    {
        public required ProjectionSnapshot Actual;public int Calls;
        public Task<ExecutorHealth> HealthAsync(CancellationToken token=default)=>Task.FromResult(new ExecutorHealth(true,"fake","offline"));
        public Task<ExecutionResult> ExecuteAsync(ModelingPlan plan,bool dryRun,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<ProjectionCaptureResult> CaptureProjectionAsync(ProjectionCaptureRequest request,CancellationToken token=default)
        {Calls++;return Task.FromResult(new ProjectionCaptureResult(true,"offline controlled capture"){Snapshot=Actual,Complete=true});}
    }
}
