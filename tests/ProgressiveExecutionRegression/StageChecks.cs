using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

static class StageChecks
{
    public static void Run(string fixturePath,string output,Action<bool,string> check)
    {
        var full=JsonSerializer.Deserialize<GenericModelDraft>(File.ReadAllText(fixturePath),ModelingIrJson.Options)!;
        var c=full.DrawingContext!;
        var inventory=DrawingOmissionValidation.Load(c.OmissionReview!.InventoryPath,c.OmissionReview.InventorySha256);
        var geometry=inventory.Candidates.First(x=>x.Kind==DrawingCandidateKind.Geometry);
        var residual=inventory.Candidates.First(x=>x.Kind==DrawingCandidateKind.ResidualInk);
        var anchors=new List<DrawingGeometryAnchor>();
        var facts=new List<DrawingDimensionFact>();
        foreach(var f in c.Dimensions)
        {
            var feature=f.Id is "diameter" or "count" ? "holes" : "plate";
            var kind=f.Id=="count" ? DrawingQuantityKind.Count : f.Id=="diameter" ? DrawingQuantityKind.Diameter : DrawingQuantityKind.Linear;
            DrawingGeometryAnchor Anchor(string side,DrawingAnchorKind anchorKind,DrawingOmissionCandidate origin)=>new(){Id=f.Id+"-"+side,
                PhysicalEntityId=f.Id+"-"+side,FeatureId=feature,ViewId="front",Kind=anchorKind,Bounds=origin.Bounds,
                State=DrawingReviewState.Reviewed,ObservationIds=origin.ObservationIds,CandidateIds=[origin.Id],Evidence="Synthetic fixture endpoint identity supplied by its test definition"};
            var start=Anchor("start",DrawingAnchorKind.Plane,geometry);var end=Anchor("end",DrawingAnchorKind.Plane,geometry);
            var target=Anchor("target",DrawingAnchorKind.Circle,residual);
            if(kind==DrawingQuantityKind.Linear)anchors.AddRange([start,end]);
            if(kind==DrawingQuantityKind.Diameter)anchors.Add(target);
            facts.Add(f with{BindingEvidence=new(){FeatureId=feature,MeasurementId=f.Id,ViewId="front",QuantityKind=kind,
                TextBounds=inventory.Candidates.First(x=>x.ObservationIds.Any(f.ObservationIds.Contains)).Bounds,TargetBounds=geometry.Bounds,
                Datum="fixture design datum",AttachmentExplanation="Synthetic fixture, not an independently recognized drawing",
                StartReference="start "+f.Id,EndReference="end "+f.Id,
                StartAnchorId=kind==DrawingQuantityKind.Linear?start.Id:null,EndAnchorId=kind==DrawingQuantityKind.Linear?end.Id:null,
                TargetAnchorId=kind==DrawingQuantityKind.Diameter?target.Id:null}});
        }
        var reviews=c.Features.Select(f=>new SourceInterpretationReview{FeatureId=f.Id,SelectedHypothesisId="fixture",DimensionDatumDerivation="Known source fixture datum",
            Hypotheses=[new(){Id="fixture",Description="Known fixture topology",Observations=[new(){ViewId="front",Bounds=geometry.Bounds,Observation="Source fixture depicts plate and four bores"}]},
                new(){Id="wrong",Description="No plate or no holes",Observations=[new(){ViewId="front",Bounds=geometry.Bounds,Observation="Contradicted by explicit source fixture geometry",Contradicts=true}]}]}).ToArray();
        var decomposition=new DrawingShapeDecomposition{State=DrawingReviewState.Reviewed,Features=c.Features.Select(f=>new DrawingFeatureDecomposition{
            FeatureId=f.Id,Kind=f.Id=="plate"?DrawingShapeKind.Prismatic:DrawingShapeKind.General,State=DrawingReviewState.Reviewed,
            TopologyReview="Synthetic source fixture explicitly defines base material and a separate through-hole void group",
            Contours=f.ViewIds.Select(v=>new DrawingShapeContour{Id=v,ViewId=v,Bounds=geometry.Bounds,Role=f.Id=="plate"?DrawingContourRole.OuterBoundary:DrawingContourRole.Opening,
                Evidence="Synthetic source fixture boundary, not independent image recognition",RegionIds=["region"]}).ToArray(),
            Regions=[new DrawingMaterialRegion{Id="region",MaterialState=f.Id=="plate"?DrawingMaterialState.Material:DrawingMaterialState.Void,
                Description="Known fixture stock or through-hole group",SourceContourIds=f.ViewIds,OperationIds=f.OperationIds,VerificationCheckIds=f.VerificationCheckIds,
                Realization="Known fixture profile extrusion or hole operation"}]}).ToArray()};
        full=full with{DrawingContext=c with{WorkflowRevision=SourceWorkflow.Revision,RequireShapeDecomposition=true,ShapeDecomposition=decomposition,RequireDimensionEvidence=true,RequireGeometryAnchors=true,RequireInterpretationReview=true,
            Dimensions=facts,GeometryAnchors=anchors,InterpretationReviews=reviews},
            Operations=full.Operations.Select(o=>o.Type==GenericOperationKind.ProfileSketch?o with{AutoDimensionPrimitives=true,RequireEditable=true,RequireFullyDefined=true}:
                o.Id=="block"?o with{PlacementIntent=new(){AxisDirection=new(0,0,1),DatumPointMm=new(0,0,0),MinimumMm=0,MaximumMm=10,
                    SourceExplanation="Synthetic source fixture declares plate from z0 to z10",ObservationIds=c.Dimensions.Single(d=>d.Id=="depth").ObservationIds}}:o).ToArray(),
            Recovery=new(){SourceRevisionId=inventory.SourceSha256,CheckpointMode=CheckpointMode.Explicit,AfterOperationIds=["block","holes"],Directory=Path.Combine(output,"native-checkpoints")},
            PreviewViews=["isometric","front","top","right","left"],PreviewScopeReason="Source drawing fixture preserves all required views"};
        c=full.DrawingContext!;
        var stage=c with{StageScope=new(){SessionId=Guid.NewGuid().ToString("N"),StageId="base",Revision=1,PendingFeatureIds=["holes"],
                ProjectionAndUnitsReview="Fixture uses front projection and millimetres",TopologyAndDatumReview="Base stock and datum fixed; holes do not change stock datum",
                DependencyClosureReview="Base profile and extrusion depend only on confirmed plate dimensions"},
            Dimensions=c.Dimensions.Where(f=>f.OperationId!="holes").ToArray(),Features=c.Features.Where(f=>f.Id!="holes").ToArray(),
            GeometryAnchors=c.GeometryAnchors.Where(a=>a.FeatureId!="holes").ToArray(),InterpretationReviews=c.InterpretationReviews.Where(r=>r.FeatureId!="holes").ToArray(),
            ShapeDecomposition=c.ShapeDecomposition! with{Features=c.ShapeDecomposition.Features.Where(f=>f.FeatureId!="holes").ToArray()},
            OmissionReview=c.OmissionReview! with{
                Candidates=c.OmissionReview.Candidates.Select(r=>r.FeatureIds.SequenceEqual(new[]{"holes"}) ? r with{
                    Disposition=DrawingCandidateDisposition.Deferred,DeferredFeatureId="holes",FeatureIds=[],DimensionIds=[],Rationale="Hole group is explicitly pending; no base dimension depends on it"}
                    :r with{FeatureIds=r.FeatureIds.Where(id=>id!="holes").ToArray()}).ToArray(),
                Regions=c.OmissionReview.Regions.Select(r=>r with{FeatureIds=r.FeatureIds.Where(id=>id!="holes").ToArray()}).ToArray(),
                AdditionalFindings=c.OmissionReview.AdditionalFindings.Where(r=>r.FeatureIds.Any(id=>id!="holes")).Select(r=>r with{FeatureIds=r.FeatureIds.Where(id=>id!="holes").ToArray()}).ToArray()}};
        var draft=full with{DrawingContext=stage,Operations=full.Operations.Where(o=>o.Id!="holes").ToArray(),
            Verification=full.Verification with{CylinderGroups=[],Bindings=full.Verification.Bindings.Where(b=>b.CheckId!="hole-check").ToArray()},
            Recovery=full.Recovery with{AfterOperationIds=["block"]}};
        var compiler=new GenericPlanCompiler();
        var compiled=compiler.Compile(draft,Path.Combine(output,"stage-not-built.sldprt"));
        if(!compiled.Success)File.WriteAllText(Path.Combine(output,"stage-diagnostics.json"),JsonSerializer.Serialize(compiled.Diagnostics,ModelingIrJson.Options));
        check(compiled.Success,"locally complete stage compiles with explicit deferred hole group");
        var omission=DrawingOmissionValidation.Evaluate(stage);
        check(omission.Passed && omission.IntermediateScope && omission.DeferredCandidates>0 && omission.Status=="declared_stage_review_complete","stage review is distinguished from final drawing review");
        void Reject(DrawingPlanContext ctx,string code,string name)=>check(DrawingPlanValidation.Validate(draft with{DrawingContext=ctx}).Any(d=>d.Code==code),name);
        Reject(stage with{StageScope=null},"DRAWING_STAGE_SCOPE","removing stage marker cannot make deferred source final");
        Reject(stage with{RequireGeometryAnchors=false},"DRAWING_STAGE_SCOPE","stage cannot disable endpoint identity gate");
        Reject(stage with{ShapeDecomposition=null},"SOURCE_SHAPE_REQUIRED","stage cannot omit material/void decomposition for its active feature");
        Reject(stage with{StageScope=stage.StageScope! with{TopologyAndDatumReview=""}},"DRAWING_STAGE_SCOPE","global topology must be reviewed before local execution");
        Reject(stage with{StageScope=stage.StageScope! with{PendingFeatureIds=["plate"]}},"DRAWING_STAGE_SCOPE","active feature cannot also be deferred");
        Reject(stage with{OmissionReview=stage.OmissionReview! with{Regions=[]}},"DRAWING_OMISSION_REGION","unread overview/details still block stage execution");
        var widthCandidate=stage.OmissionReview!.Candidates.First(r=>r.DimensionIds.Contains("width"));
        Reject(stage with{OmissionReview=stage.OmissionReview with{Candidates=stage.OmissionReview.Candidates.Select(r=>r==widthCandidate?r with{Disposition=DrawingCandidateDisposition.Deferred,
            FeatureIds=[],DimensionIds=[],DeferredFeatureId="holes"}:r).ToArray()}},"DRAWING_STAGE_DEFERRED","a consumed dimension cannot be hidden in deferred source");
        check(DrawingPlanValidation.Validate(draft with{Recovery=new(){Enabled=false}}).Any(d=>d.Code=="DRAWING_STAGE_CHECKPOINT"),"intermediate stage must retain reusable checkpoint boundary");
        var final=compiler.Compile(full,Path.Combine(output,"final-not-built.sldprt"));
        if(!final.Success)File.WriteAllText(Path.Combine(output,"final-diagnostics.json"),JsonSerializer.Serialize(final.Diagnostics,ModelingIrJson.Options));
        check(final.Success,"resolved full drawing compiles through original full coverage checks");
        check(ModelingRecovery.PrefixFingerprint(compiled.Plan!,2)==ModelingRecovery.PrefixFingerprint(final.Plan!,2),"stage-to-final extension preserves verified native prefix identity");
        File.WriteAllText(Path.Combine(output,"stage-draft.json"),JsonSerializer.Serialize(draft,ModelingIrJson.Options));
        File.WriteAllText(Path.Combine(output,"final-draft.json"),JsonSerializer.Serialize(full,ModelingIrJson.Options));
    }
}
