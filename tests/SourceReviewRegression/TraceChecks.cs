using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

static class TraceChecks
{
    public static void Run(string output,Action<bool,string> check)
    {
        var source=Path.Combine(output,"trace-source.txt");File.WriteAllText(source,"synthetic known-pixel fixture, not recognition accuracy evidence");
        var observation=Path.Combine(output,"trace-observation.json");File.WriteAllText(observation,"{}");
        var raw=Path.Combine(output,"trace-image.bin");File.WriteAllText(raw,"synthetic region identity");
        var maskPath=Path.Combine(output,"trace-mask.bin");var mask=new byte[(201*201+7)/8];
        void Pixel(int x,int y){var n=y*201+x;mask[n/8]|=(byte)(1<<(n%8));}
        for(var y=50;y<=140;y++){Pixel(50,y);Pixel(150,y);}
        for(var x=50;x<=150;x++)Pixel(x,50);
        // A separate circular contour and leader, independent of the linear fixture.
        var circle=Enumerable.Range(0,97).Select(i=>new DrawingPagePoint(.65+.1*Math.Cos(2*Math.PI*i/96),.75+.1*Math.Sin(2*Math.PI*i/96))).ToArray();
        foreach(var p in circle)Pixel((int)Math.Round(p.X*200),(int)Math.Round(p.Y*200));
        for(var y=120;y<=150;y++)Pixel(180,y);
        for(var x=150;x<=180;x++)Pixel(x,150);
        for(var x=130;x<=150;x++)Pixel(x,150);
        for(var y=150;y<=170;y++)Pixel(130,y);
        File.WriteAllBytes(maskPath,mask);
        var overview=new DrawingReviewRegion{Id="overview",PageNumber=1,Bounds=new(0,0,1,1),Purpose="overview",ImagePath=raw,ImageSha256=DrawingPlanValidation.FileHash(raw),PixelWidth=201,PixelHeight=201,
            InkMaskPath=maskPath,InkMaskSha256=DrawingPlanValidation.FileHash(maskPath),InkMaskEncoding="bitset-row-major-lsb0-v1"};
        var inventory=new DrawingOmissionInventory{SourcePath=source,SourceSha256=DrawingPlanValidation.FileHash(source),TotalPages=1,
            Pages=[new(){PageNumber=1,Ingested=true,ObservationPath=observation,ObservationSha256=DrawingPlanValidation.FileHash(observation)}],Regions=[overview]};
        var inventoryPath=Path.Combine(output,"trace-inventory.json");File.WriteAllText(inventoryPath,JsonSerializer.Serialize(inventory,ModelingIrJson.Options));
        DrawingGeometryAnchor Anchor(string id,double x)=>new(){Id=id,FeatureId="plate",PhysicalEntityId=id,ViewId="front",Kind=DrawingAnchorKind.Line,State=DrawingReviewState.Reviewed,
            Bounds=new(x-.02,.38,x+.02,.72),SourcePath=[new(x,.4),new(x,.7)],Evidence="known fixture line"};
        var trace=new DrawingDimensionTrace{Kind=DrawingDimensionTraceKind.LinearDimension,MeasurementMode=DrawingMeasurementMode.Horizontal,ModelDirection=new(1,0,0),
            Path=[new(.25,.25),new(.75,.25)],StartExtension=[new(.25,.5),new(.25,.25)],EndExtension=[new(.75,.5),new(.75,.25)],Evidence="dimension arrowheads at both extension intersections"};
        var evidence=new DrawingDimensionEvidence{FeatureId="plate",MeasurementId="width",ViewId="front",QuantityKind=DrawingQuantityKind.Linear,Trace=trace,StartAnchorId="left",EndAnchorId="right",
            TextBounds=new(.4,.15,.6,.23),TargetBounds=new(.23,.38,.77,.72),Datum="left edge",AttachmentExplanation="width between side edges"};
        var fact=new DrawingDimensionFact{Id="width",OperationId="sk",ParameterPath="primitives.0.width_mm",Value=50,SourceLiteral="50",BindingEvidence=evidence};
        var context=new DrawingPlanContext{SourcePath=source,WorkflowRevision=SourceWorkflow.Revision,Dimensions=[fact],GeometryAnchors=[Anchor("left",.25),Anchor("right",.75)],
            Views=[new(){Id="front",Kind=ModelDrawingView.Front}],OmissionReview=new(){InventoryPath=inventoryPath,InventorySha256=DrawingPlanValidation.FileHash(inventoryPath)}};
        void Pass(DrawingPlanContext c,string name){var d=DrawingDimensionTraceValidation.Validate(c);check(d.Count==0,name+ (d.Count>0?": "+string.Join(";",d.Select(x=>x.Code+":"+x.Message)):""));}
        void Reject(DrawingPlanContext c,string code,string name)=>check(DrawingDimensionTraceValidation.Validate(c).Any(d=>d.Code==code),name);
        DrawingPlanContext With(DrawingDimensionTrace? t)=>context with{Dimensions=[fact with{BindingEvidence=evidence with{Trace=t}}]};
        Pass(context,"source-supported horizontal dimension with connected extensions accepted");
        File.WriteAllText(Path.Combine(output,"trace-context.json"),JsonSerializer.Serialize(context,ModelingIrJson.Options));
        Reject(With(null),"DIMENSION_TRACE_REQUIRED","new workflow rejects missing trace");
        Pass(With(null) with{WorkflowRevision=SourceWorkflow.DecompositionRevision},"legacy workflow remains readable without trace");
        Reject(With(trace with{Path=[new(.25,.05),new(.75,.05)]}),"DIMENSION_TRACE_INK","invented line across blank source is rejected");
        Reject(With(trace with{MeasurementMode=DrawingMeasurementMode.Vertical}),"DIMENSION_MEASUREMENT_MODE","horizontal line cannot claim vertical measurement");
        Reject(With(trace with{ModelDirection=new(0,0,0)}),"DIMENSION_MEASUREMENT_DIRECTION","zero physical direction rejected");
        Reject(With(trace with{StartExtension=[]}),"DIMENSION_TRACE_SHAPE","missing extension path rejected");
        Reject(With(trace with{StartExtension=trace.EndExtension}),"DIMENSION_TRACE_ATTACHMENT","real ink on wrong extension cannot bind other arrow");
        Reject(context with{GeometryAnchors=[Anchor("left",.25) with{SourcePath=[]},Anchor("right",.75)]},"DIMENSION_TRACE_ANCHOR_PATH","bounding box alone is not a contour trace");
        Reject(context with{GeometryAnchors=[Anchor("left",.25) with{SourcePath=[new(.45,.4),new(.45,.7)]},Anchor("right",.75)]},"DIMENSION_TRACE_ANCHOR_PATH","source path cannot leave declared anchor bounds");
        Reject(With(trace with{Path=[new(double.NaN,0),new(.75,.25)]}),"DIMENSION_TRACE_SHAPE","nonfinite page coordinates rejected");
        Reject(With(trace with{Path=Enumerable.Repeat(new DrawingPagePoint(.5,.5),129).ToArray()}),"DIMENSION_TRACE_SHAPE","oversized trace bounded");
        var other=fact with{Id="other",BindingEvidence=evidence with{Trace=trace with{ModelDirection=new(0,1,0)}}};
        Reject(context with{Dimensions=[fact,other]},"DIMENSION_MEASUREMENT_CONFLICT","same measurement cannot switch physical direction");
        Pass(With(trace with{MeasurementMode=DrawingMeasurementMode.Aligned}),"aligned distance along same endpoint direction accepted");
        var target=new DrawingGeometryAnchor{Id="circle",FeatureId="plate",PhysicalEntityId="bore",ViewId="front",Kind=DrawingAnchorKind.Circle,State=DrawingReviewState.Reviewed,
            SourcePath=circle,Bounds=new(.54,.64,.76,.86),Evidence="known fixture circle"};
        var leader=new DrawingDimensionTrace{Kind=DrawingDimensionTraceKind.Leader,Path=[new(.9,.6),new(.9,.75),new(.75,.75)],Evidence="arrow reaches circle rightmost point"};
        var diameter=fact with{Id="dia",SourceLiteral="Ø20",Value=20,BindingEvidence=evidence with{QuantityKind=DrawingQuantityKind.Diameter,TargetAnchorId="circle",Trace=leader,TextBounds=new(.88,.58,.98,.62)}};
        var circular=context with{GeometryAnchors=[target],Dimensions=[diameter]};
        Pass(circular,"source-supported leader must terminate at circle contour");
        Reject(circular with{Dimensions=[diameter with{BindingEvidence=diameter.BindingEvidence! with{Trace=leader with{Path=[new(.9,.6),new(.9,.75),new(.82,.75)]}}}]},"DIMENSION_TRACE_ATTACHMENT","arrow stopping short on ink cannot claim circle attachment");
        var arc=circle.Take(25).ToArray();
        var angular=trace with{Kind=DrawingDimensionTraceKind.AngularDimension,MeasurementMode=DrawingMeasurementMode.MinorAngle,ModelDirection=new(0,0,1),AngularVertex=new(.65,.75),Path=arc};
        var angle=fact with{Value=90,SourceLiteral="90°",BindingEvidence=evidence with{QuantityKind=DrawingQuantityKind.Angle,Trace=angular,StartAnchorId="ray1",EndAnchorId="ray2",TextBounds=new(.01,.01,.02,.02)}};
        var angularContext=context with{Dimensions=[angle],GeometryAnchors=[target with{Id="ray1",Kind=DrawingAnchorKind.Line,SourcePath=[new(.65,.75),new(.75,.75)]},target with{Id="ray2",Kind=DrawingAnchorKind.Line,SourcePath=[new(.65,.75),new(.65,.85)]}]};
        Pass(angularContext,"minor angle has source arc endpoints and common vertex");
        DrawingPlanContext AngleWith(DrawingDimensionTrace t)=>angularContext with{Dimensions=[angle with{BindingEvidence=angle.BindingEvidence! with{Trace=t}}]};
        Reject(AngleWith(angular with{MeasurementMode=DrawingMeasurementMode.ReflexAngle}),"DIMENSION_ANGLE_PATH","quarter arc cannot claim reflex angle");
        Reject(AngleWith(angular with{MeasurementMode=DrawingMeasurementMode.DirectedAngle}),"DIMENSION_ANGLE_DIRECTION","directed angle requires explicit page sense");
        Pass(AngleWith(angular with{MeasurementMode=DrawingMeasurementMode.DirectedAngle,ClockwiseInPage=true}),"directed clockwise arc accepted");
        Reject(AngleWith(angular with{MeasurementMode=DrawingMeasurementMode.DirectedAngle,ClockwiseInPage=false}),"DIMENSION_ANGLE_DIRECTION","reversed directed angle rejected");
        Reject(AngleWith(angular with{AngularVertex=new(.55,.75)}),"DIMENSION_ANGLE_PATH","wrong angular vertex rejected");
        Reject(With(trace with{Path=[new(.25,.25),new(.25,.25)]}),"DIMENSION_TRACE_SHAPE","coincident dimension arrowheads rejected");
        var compound=diameter with{SourceAnnotationId="hole-note",SourceComponentKey="bore_depth",SourceLiteral="⌀20 ↧8",Value=8,
            BindingEvidence=diameter.BindingEvidence! with{QuantityKind=DrawingQuantityKind.Linear,Trace=null,TargetAnchorId="circle"}};
        var annotation=new DrawingSourceAnnotation{Id="hole-note",SourceLiteral=compound.SourceLiteral,FeatureId="plate",OperationId="sk",ViewId="front",TextBounds=compound.BindingEvidence!.TextBounds,
            State=DrawingReviewState.Reviewed,Evidence="source hole callout uses one leader",LeaderTargetAnchorId="circle",LeaderTrace=leader with{ModelDirection=new(0,0,-1)}};
        var compoundContext=circular with{Dimensions=[compound],AnnotationLedger=new(){SourceSha256=DrawingPlanValidation.FileHash(source),Annotations=[annotation]}};
        Pass(compoundContext,"compound depth follows actual shared source leader without invented extension lines");
        Reject(compoundContext with{AnnotationLedger=compoundContext.AnnotationLedger! with{Annotations=[annotation with{LeaderTrace=null}]}},"DIMENSION_TRACE_REQUIRED","compound annotation cannot omit its leader");
        Reject(compoundContext with{AnnotationLedger=compoundContext.AnnotationLedger! with{Annotations=[annotation with{LeaderTrace=leader}]}},"DIMENSION_MEASUREMENT_DIRECTION","compound depth needs declared physical hole axis");
        Reject(compoundContext with{AnnotationLedger=compoundContext.AnnotationLedger! with{Annotations=[annotation with{LeaderTrace=annotation.LeaderTrace! with{Path=[new(.9,.6),new(.9,.75),new(.82,.75)]}}]}},"DIMENSION_TRACE_ATTACHMENT","compound role mapping cannot excuse wrong leader endpoint");
        var changed=(byte[])mask.Clone();changed[0]^=1;File.WriteAllBytes(maskPath,changed);
        Reject(context,"DIMENSION_TRACE_SOURCE","changed source ink mask invalidates trace proof");
        File.WriteAllBytes(maskPath,mask);
    }
}
