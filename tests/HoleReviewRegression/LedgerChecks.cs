using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

static class LedgerChecks
{
    public static void Run(string output,Action<bool,string> check)
    {
        var source=Path.Combine(output,"source.txt");File.WriteAllText(source,"controlled annotation ledger fixture, not independent image recognition");
        var obs=Path.Combine(output,"observation.json");File.WriteAllText(obs,"{}");
        var raw=Path.Combine(output,"image.bin");File.WriteAllText(raw,"controlled inventory fixture");
        var box=new DrawingReviewBox(.1,.1,.6,.2);
        var inventory=new DrawingOmissionInventory{SourcePath=source,SourceSha256=DrawingPlanValidation.FileHash(source),TotalPages=1,
            Pages=[new(){PageNumber=1,Ingested=true,ObservationPath=obs,ObservationSha256=DrawingPlanValidation.FileHash(obs)}],
            Regions=[new(){Id="overview",PageNumber=1,Purpose="overview",Bounds=new(0,0,1,1),ImagePath=raw,ImageSha256=DrawingPlanValidation.FileHash(raw),PixelWidth=100,PixelHeight=100}]};
        var ip=Path.Combine(output,"inventory.json");File.WriteAllText(ip,JsonSerializer.Serialize(inventory,ModelingIrJson.Options));
        DrawingPlanContext Make(string literal)
        {
            var parsed=HoleCalloutParser.Parse(literal); var facts=new List<DrawingDimensionFact>();var bindings=new List<DrawingAnnotationBinding>();
            foreach(var c in parsed.Components)
            {
                if(c.Value is null || c.Role is "thread_pitch" or "pitch_circle_diameter") {bindings.Add(new(){ComponentKey=c.Key,RetainedLiteral=c.Literal,Evidence="source token separately reviewed"});continue;}
                var path=c.Role switch {"count"=>"feature.hole_center_count","bore_diameter"=>"feature.diameter_mm","bore_depth"=>"feature.depth_mm","chamfer_distance"=>"feature.distance_mm","radius"=>"feature.radius_mm",
                    "counterbore_diameter" or "spotface_diameter"=>"feature.counterbore_diameter_mm","counterbore_depth" or "spotface_depth"=>"feature.counterbore_depth_mm",
                    "countersink_diameter"=>"feature.countersink_diameter_mm","countersink_angle"=>"feature.countersink_angle_degrees",
                    "thread_major_diameter"=>"feature.thread_major_diameter_mm","thread_depth"=>"feature.thread_depth_mm",_=>"feature.depth_mm"};
                var evidence=new DrawingDimensionEvidence{FeatureId="holes",MeasurementId=c.Key,ViewId="top",QuantityKind=c.QuantityKind!.Value,TextBounds=box,TargetBounds=new(.6,.5,.8,.7),Datum="entry face and hole axis",AttachmentExplanation="controlled annotation leader",StartReference="entry face",EndReference="hole bottom"};
                facts.Add(new(){Id=c.Key,SourceAnnotationId="a1",SourceComponentKey=c.Key,OperationId="hole",ParameterPath=path,Value=c.Value.Value,Unit=c.Unit,SourceLiteral=literal,ViewIds=["top"],BindingEvidence=evidence,
                    CalloutReview=new(){State=DrawingReviewState.Reviewed,SourceLiteral=literal,SelectedComponent=c.Key,QuantityKind=c.QuantityKind.Value,Value=c.Value.Value,Unit=c.Unit,Evidence="explicitly reviewed selected component"}});
                bindings.Add(new(){ComponentKey=c.Key,DimensionId=c.Key});
            }
            return new(){SourcePath=source,WorkflowRevision=SourceWorkflow.Revision,Dimensions=facts,Views=[new(){Id="top",Kind=ModelDrawingView.Top}],Features=[new(){Id="holes",SourceLiteral=literal,ViewIds=["top"],OperationIds=["hole"]}],
                OmissionReview=new(){InventoryPath=ip,InventorySha256=DrawingPlanValidation.FileHash(ip)},
                AnnotationLedger=new(){SourceSha256=DrawingPlanValidation.FileHash(source),Annotations=[new(){Id="a1",ViewId="top",TextBounds=box,SourceLiteral=literal,FeatureId="holes",OperationId="hole",State=DrawingReviewState.Reviewed,Evidence="controlled full annotation",Components=bindings}],
                    Sweeps=[new(){RegionId="overview",Pass=1,State=DrawingReviewState.Reviewed,AnnotationIds=["a1"],Evidence="source first census"},new(){RegionId="overview",Pass=2,State=DrawingReviewState.Reviewed,AnnotationIds=["a1"],Evidence="reverse sweep after binding"}]}};
        }
        var ctx=Make("4×⌀6 ↧12 ⌴⌀12 ↧4");
        var hole=new GenericOperationDraft{Type=GenericOperationKind.NativeFeature,Id="hole",Feature=new(){Kind=NativeFeatureKind.Hole,HoleKind=HoleKind.Counterbore,DiameterMm=6,DepthMm=12,ThroughAll=false,CounterboreDiameterMm=12,CounterboreDepthMm=4,HoleCenters=[new(-10,-10),new(10,-10),new(10,10),new(-10,10)]}};
        IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext c,GenericOperationDraft? op=null)=>SourceAnnotationValidation.Validate(c,[op??hole]);
        void Good(DrawingPlanContext c,string name,GenericOperationDraft? op=null){var d=Validate(c,op);check(d.Count==0,name+" "+string.Join(";",d.Select(e=>e.Code)));}
        void Bad(DrawingPlanContext c,string code,string name,GenericOperationDraft? op=null)=>check(Validate(c,op).Any(e=>e.Code==code),name);
        Good(ctx,"complete counterbore ledger accepted");
        var cases=new List<object>();
        void Scenario(string name,DrawingPlanContext c,bool valid,GenericOperationDraft? op=null){cases.Add(new{name,valid,context=c,operations=new[]{op??hole}});check((Validate(c,op).Count==0)==valid,"comparison scenario "+name);}
        Scenario("valid-counterbore",ctx,true);
        foreach(var role in new[]{"count","bore_depth","counterbore_diameter","counterbore_depth"})
        {
            var missing=ctx with{Dimensions=ctx.Dimensions.Where(f=>f.Id!=role).ToArray(),AnnotationLedger=ctx.AnnotationLedger! with{Annotations=[ctx.AnnotationLedger.Annotations[0] with{Components=ctx.AnnotationLedger.Annotations[0].Components.Where(c=>c.ComponentKey!=role).ToArray()}]}};
            Bad(missing,"SOURCE_ANNOTATION_COMPONENT_MISSING","missing "+role+" rejected");Scenario("omitted-"+role,missing,false);
        }
        var wrong=ctx with{Dimensions=ctx.Dimensions.Select(f=>f.Id=="counterbore_diameter"?f with{ParameterPath="feature.depth_mm"}:f).ToArray()};
        Bad(wrong,"SOURCE_ANNOTATION_ROLE_TARGET","equal value second diameter cannot bind bore depth");Scenario("equal-value-role-swap",wrong,false);
        var swapped=ctx with{Dimensions=ctx.Dimensions.Select(f=>f.Id=="bore_depth"?f with{ParameterPath="feature.counterbore_diameter_mm"}:f).ToArray()};Scenario("equal-value-depth-swap",swapped,false);
        Bad(ctx with{AnnotationLedger=null},"SOURCE_ANNOTATION_LEDGER_REQUIRED","old single-component review cannot replace whole annotation census");Scenario("missing-ledger",ctx with{AnnotationLedger=null},false);
        var missingSweep=ctx with{AnnotationLedger=ctx.AnnotationLedger! with{Sweeps=ctx.AnnotationLedger.Sweeps.Take(1).ToArray()}};
        Bad(missingSweep,"SOURCE_ANNOTATION_SWEEP_MISMATCH","second source sweep required");Scenario("missing-reverse-sweep",missingSweep,false);
        var omittedId=ctx with{AnnotationLedger=ctx.AnnotationLedger! with{Sweeps=ctx.AnnotationLedger.Sweeps.Select(s=>s.Pass==2?s with{AnnotationIds=[]}:s).ToArray()}};Scenario("census-omits-annotation-id",omittedId,false);
        Bad(ctx with{AnnotationLedger=ctx.AnnotationLedger! with{SourceSha256=new string('0',64)}},"SOURCE_ANNOTATION_STALE","source hash change invalidates census");
        Bad(ctx,"SOURCE_ANNOTATION_HOLE_MODE","blind callout cannot execute through hole",hole with{Feature=hole.Feature! with{ThroughAll=true}});
        Bad(ctx,"SOURCE_ANNOTATION_HOLE_MODE","counterbore cannot execute simple hole",hole with{Feature=hole.Feature! with{HoleKind=HoleKind.Simple}});
        Bad(ctx,"SOURCE_ANNOTATION_EXECUTED_COUNT","unused generic count cannot hide missing actual hole centers",hole with{Feature=hole.Feature! with{Count=4,HoleCenters=[new(0,0)]}});
        var sink=Make("2×⌀5 THRU ⌵⌀10×90°");var sinkOp=hole with{Feature=hole.Feature! with{HoleKind=HoleKind.Countersink,ThroughAll=true,DiameterMm=5,CountersinkDiameterMm=10,CountersinkAngleDegrees=90,HoleCenters=[new(-10,0),new(10,0)]}};
        Good(sink,"complete countersink ledger accepted",sinkOp);
        Scenario("valid-countersink",sink,true,sinkOp);
        var thread=Make("M8×1,25-6H ↧16");var threadOp=hole with{Feature=hole.Feature! with{HoleKind=HoleKind.Tapped,ThreadMajorDiameterMm=8,ThreadDepthMm=16,ThreadDesignation="M8x1.25-6H"}};
        Good(thread,"explicit thread pitch checked against native designation",threadOp);
        Scenario("valid-thread-designation",thread,true,threadOp);
        Bad(thread,"SOURCE_ANNOTATION_THREAD_DESIGNATION","wrong pitch in designation rejected",threadOp with{Feature=threadOp.Feature! with{ThreadDesignation="M8x1.5-6H"}});
        Bad(thread,"SOURCE_ANNOTATION_THREAD_DESIGNATION","missing class in designation rejected",threadOp with{Feature=threadOp.Feature! with{ThreadDesignation="M8x1.25"}});
        var pattern=Make("4×⌀12 ±0,05 EQUALLY SPACED ON ⌀40 PCD");var patternOp=hole with{Feature=hole.Feature! with{HoleKind=HoleKind.Simple,DiameterMm=12,ThroughAll=true,HoleCenters=[new(-20,0),new(0,20),new(20,0),new(0,-20)]}};
        Good(pattern,"PCD semantic component binds actual hole centers without dummy circle",patternOp);
        Scenario("valid-pcd-pattern",pattern,true,patternOp);
        Bad(pattern,"SOURCE_ANNOTATION_PATTERN_LAYOUT","wrong PCD rejected",patternOp with{Feature=patternOp.Feature! with{HoleCenters=[new(-10,0),new(0,10),new(10,0),new(0,-10)]}});
        Bad(pattern,"SOURCE_ANNOTATION_PATTERN_LAYOUT","same radius unequal spacing rejected",patternOp with{Feature=patternOp.Feature! with{HoleCenters=[new(-20,0),new(12,16),new(20,0),new(0,-20)]}});
        var deferred=ctx.AnnotationLedger!.Annotations[0] with{Deferred=true,FeatureId="later",Components=[]};
        var stage=ctx with{Dimensions=[],Features=[],StageScope=new(){SessionId="s",StageId="one",Revision=1,ProjectionAndUnitsReview="reviewed",TopologyAndDatumReview="reviewed",DependencyClosureReview="reviewed",PendingFeatureIds=["later"]},AnnotationLedger=ctx.AnnotationLedger with{Annotations=[deferred]}};
        Good(stage,"declared deferred source annotation preserves progressive workflow");
        Bad(stage with{StageScope=null},"SOURCE_ANNOTATION_DEFERRED","deferred annotation cannot pass final acceptance");
        var extra=ctx with{Dimensions=ctx.Dimensions.Append(ctx.Dimensions[0] with{Id="unaccounted"}).ToArray()};Bad(extra,"SOURCE_ANNOTATION_FACT_UNACCOUNTED","model facts reverse-map to source census");
        check(SourceAnnotationValidation.Validate(ctx with{WorkflowRevision=SourceWorkflow.SourceReviewRevision,AnnotationLedger=null}).Count==0,"workflow4 remains explicitly legacy compatible");
        var edgeSelections=Enumerable.Range(0,4).Select(i=>new EntityQuery{Kind=EntityKind.Edge,PositionMm=new(i*10,0,0)}).ToArray();
        var finish=hole with{Feature=new(){Kind=NativeFeatureKind.Chamfer,DistanceMm=.8,AngleDegrees=45,TangentPropagation=false,Selections=edgeSelections}};
        var chamfer=Make("4× C 0,8");
        chamfer=chamfer with{Dimensions=chamfer.Dimensions.Select(f=>f.SourceComponentKey=="count"?f with{ParameterPath="feature.selection_count"}:f).ToArray()};
        Good(chamfer,"full C chamfer ledger accepts real selection count and distance",finish);
        Bad(chamfer with{Dimensions=chamfer.Dimensions.Select(f=>f.SourceComponentKey=="count"?f with{ParameterPath="feature.count"}:f).ToArray()},"SOURCE_ANNOTATION_ROLE_TARGET","same generic count cannot replace real edge-list count",finish);
        Bad(chamfer with{Dimensions=chamfer.Dimensions.Select(f=>f.SourceComponentKey=="chamfer_distance"?f with{ParameterPath="feature.depth_mm"}:f).ToArray()},"SOURCE_ANNOTATION_ROLE_TARGET","chamfer leg cannot bind equal depth slot",finish);
        Bad(chamfer with{AnnotationLedger=chamfer.AnnotationLedger! with{Annotations=[chamfer.AnnotationLedger.Annotations[0] with{Components=chamfer.AnnotationLedger.Annotations[0].Components.Where(c=>c.ComponentKey!="count").ToArray()}]}},"SOURCE_ANNOTATION_COMPONENT_MISSING","C multiplicity cannot disappear from source ledger",finish);
        var radius=Make("3× R1,5");radius=radius with{Dimensions=radius.Dimensions.Select(f=>f.SourceComponentKey=="count"?f with{ParameterPath="feature.selection_count"}:f).ToArray()};
        Good(radius,"full repeated radius ledger binds three real fillet edges",finish with{Feature=finish.Feature! with{Kind=NativeFeatureKind.Fillet,RadiusMm=1.5,Selections=edgeSelections.Take(3).ToArray()}});
        File.WriteAllText(Path.Combine(output,"comparison-cases.json"),JsonSerializer.Serialize(cases,ModelingIrJson.Options));
        File.WriteAllText(Path.Combine(output,"valid-context.json"),JsonSerializer.Serialize(ctx,ModelingIrJson.Options));
    }
}
