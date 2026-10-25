using CadModeling.Core;
using CadModeling.Ir;
static class PlacementChecks
{
    public static void Run(Action<bool,string> check)
    {
        var sketch=new GenericOperationDraft{Id="sk",Type=GenericOperationKind.ProfileSketch,Frame=new(){OriginMm=new(0,0,0)},
            Primitives=[new(){Type=GenericPrimitiveKind.CenteredRectangle,WidthMm=20,HeightMm=20}]};
        var boss=new GenericOperationDraft{Id="boss",Type=GenericOperationKind.ExtrudeBoss,SketchId="sk",DepthMm=8,EndCondition=ExtrudeEndCondition.MidPlane,
            PlacementIntent=new(){AxisDirection=new(0,0,1),DatumPointMm=new(0,0,0),MinimumMm=-4,MaximumMm=4,SourceExplanation="Independent symmetric thickness8 at z0"}};
        var draft=new GenericModelDraft{SourceText="placement fixture",Operations=[sketch,boss]};
        bool Has(GenericModelDraft d,string code)=>ModelingPreflight.Validate(d).Any(x=>x.Code==code);
        check(Has(draft,"EXTRUDE_SOURCE_PLACEMENT_CHECKED"),"midplane extent agrees with independently specified -4..4 source interval");
        check(Has(draft with{Operations=[sketch,boss with{EndCondition=ExtrudeEndCondition.Blind}]},"EXTRUDE_SOURCE_PLACEMENT_MISMATCH"),"same8mm thickness with wrong one-sided placement is rejected");
        check(Has(draft with{Operations=[sketch,boss with{EndCondition=ExtrudeEndCondition.Blind,ReverseStartOffset=true,StartOffsetMm=4}]},"EXTRUDE_SOURCE_PLACEMENT_CHECKED"),"explicit offset blind extent equivalent to symmetric interval");
        check(Has(draft with{Operations=[sketch,boss with{PlacementIntent=boss.PlacementIntent! with{AxisDirection=new(1,0,0)}}]},"EXTRUDE_SOURCE_AXIS_MISMATCH"),"wrong source thickness axis rejected");
        check(Has(draft with{DrawingContext=new(){SourcePath="fixture",WorkflowRevision=SourceWorkflow.Revision},Operations=[sketch,boss with{PlacementIntent=null}]},"EXTRUDE_SOURCE_PLACEMENT_REQUIRED"),"current source workflow cannot omit placement evidence");
        var next=sketch with{Id="ear",Frame=new(){OriginMm=new(24,0,0)}};
        var extrude=boss with{Id="ear-boss",SketchId="ear",PlacementIntent=null};
        check(Has(draft with{Operations=[sketch,boss,next,extrude]},"BOSS_CONNECTION_GAP"),"disconnected ear envelope rejected without changing dimensions");
        check(!Has(draft with{Operations=[sketch,boss,next with{Frame=new(){OriginMm=new(19,0,0)}},extrude]},"BOSS_CONNECTION_GAP"),"overlap is not incorrectly rejected");
        check(!Has(draft with{Operations=[sketch,boss,next,extrude with{Merge=false}]},"BOSS_CONNECTION_GAP"),"explicit independent body does not require contact");
        check(Has(draft with{Operations=[sketch,boss,next with{Frame=new(){OriginMm=new(20,20,0)}},extrude]},"BOSS_EDGE_CONTACT_RISK"),"line contact reported as zero thickness risk");
        check(!Has(draft with{Operations=[sketch,boss,next with{Frame=new(){OriginMm=new(20,0,0)}},extrude]},"BOSS_EDGE_CONTACT_RISK"),"finite face contact is not called zero thickness");
        var unknown=new GenericOperationDraft{Id="unknown",Type=GenericOperationKind.NativeFeature,Feature=new(){Kind=NativeFeatureKind.MoveBody}};
        check(!Has(draft with{Operations=[sketch,boss,unknown,next,extrude]},"BOSS_CONNECTION_GAP"),"unknown previous topology cannot justify analytic disconnection verdict");
        foreach(var kind in Enum.GetValues<DrivenProfileKind>())
        {
            var op=new GenericOperationDraft{Id="profile",Type=GenericOperationKind.ProfileSketch,Plane=ReferencePlane.Front,
                DrivenProfile=new(){Kind=kind,RadiusMm=10,WebHeightMm=kind==DrivenProfileKind.RoundEar?20:null,InnerRadiusMm=kind==DrivenProfileKind.RoundEar?null:7}};
            var compiled=new GenericPlanCompiler().Compile(new(){SourceText="explicit profile",Operations=[op,boss with{SketchId="profile",PlacementIntent=null}]});
            check(compiled.Success&&compiled.Plan!.Operations[0] is ProfileSketchOperation{RequireEditable:true,RequireFullyDefined:true},kind+" compiles with required driving constraints");
        }
    }
}
