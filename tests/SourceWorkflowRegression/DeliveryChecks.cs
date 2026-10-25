using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;
static class DeliveryChecks
{
    public static async Task Run(string output,Action<bool,string> check)
    {
        var native=Path.Combine(output,"managed-delivery.SLDPRT");File.WriteAllText(native,"Managed contract fixture, not native CAD evidence");
        var preview=Path.Combine(output,"managed-preview.bmp");File.WriteAllText(preview,"Managed preview identity fixture");
        var store=new CompactArtifactStore(Path.Combine(output,"delivery-store"),"delivery-managed");
        var plan=new ModelingPlan{PlanId="managed-plan",Name="fixture",SourceText="managed receipt contract test",Operations=[],Output=new(){NativePath=native,PreviewViews=["isometric"]}};
        var planId=store.SavePlan(plan).ArtifactId;var hash=DrawingPlanValidation.FileHash(native);var previewHash=DrawingPlanValidation.FileHash(preview);
        var execution=new ExecutionResult(true,"completed","managed fixture",[
            new("saved_model_readback","managed",true,new Dictionary<string,string>{{"sha256",hash}}),
            new("preview","managed",true,new Dictionary<string,string>{{"view","isometric"},{"path",preview},{"sha256",previewHash}})],native,
            PlanFingerprint:ModelingPlanIdentity.Fingerprint(plan)){GeometryStatus="passed",ParametricStatus="passed",Verification=new([new("declared",true,"passed","managed")])};
        var receipt=store.SaveResult(execution).ArtifactId;
        var reviews=new[]{new DeclaredVisualReview{View="isometric",ImagePath=preview,ImageSha256=previewHash,NativeSha256=hash,Observation="Managed declaration, not a visual certification",Passed=true}};
        async Task<JsonNode> Review(string id,DeclaredVisualReview[]? visual=null)=>JsonSerializer.SerializeToNode(await DeliveryReviewTools.Review(new NoNative(),store,planId,id,visualReviews:visual,responseDetail:"full"),ModelingIrJson.Options)!;
        var result=await Review(receipt);
        check(result["success"]!.GetValue<bool>()==false&&result["acceptance"]!["visual_status"]!.GetValue<string>()=="review_required","saved native success cannot auto-accept an unreviewed image");
        result=await Review(receipt,reviews);
        check(result["success"]!.GetValue<bool>()&&result["acceptance"]!["delivery_status"]!.GetValue<string>()=="declared_scope_accepted","identity-bound declared review composes separate acceptance states");
        var weak=store.SaveResult(execution with{ParametricStatus="review_required"}).ArtifactId;
        result=await Review(weak,reviews);
        check(!result["success"]!.GetValue<bool>()&&result["acceptance"]!["native_status"]!.GetValue<string>()=="passed","geometry success does not hide incomplete parametric acceptance");
        var stage=store.SaveResult(execution with{IsIntermediateStage=true}).ArtifactId;
        result=await Review(stage,reviews);
        check(result["acceptance"]!["delivery_status"]!.GetValue<string>()=="stage_accepted","accepted intermediate result cannot claim final delivery");
        File.AppendAllText(preview,"changed");result=await Review(receipt,reviews);
        check(!result["success"]!.GetValue<bool>(),"changed preview invalidates visual acceptance");
        File.AppendAllText(native,"changed");result=await Review(receipt,reviews);
        check(result["acceptance"]!["native_status"]!.GetValue<string>()=="failed_or_stale","changed model invalidates saved acceptance");
        var wrong=store.SaveResult(execution with{PlanFingerprint="wrong"}).ArtifactId;
        try{await Review(wrong,reviews);throw new Exception("wrong plan accepted");}catch(ArgumentException){check(true,"receipt from another plan rejected");}
    }
    private sealed class NoNative:IModelingExecutor
    {
        public Task<ExecutorHealth> HealthAsync(CancellationToken token=default)=>throw new NotSupportedException();
        public Task<ExecutionResult> ExecuteAsync(ModelingPlan plan,bool dryRun,CancellationToken token=default)=>throw new NotSupportedException();
    }
}
