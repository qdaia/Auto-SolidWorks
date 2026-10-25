using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

static class AnchorChecks
{
    public static void Run(string output,Action<bool,string> check)
    {
        var source=Path.Combine(output,"anchor-source.txt");File.WriteAllText(source,"synthetic source fixture, not a visual recognition test");
        var observation=Path.Combine(output,"observations.json");File.WriteAllText(observation,"{}");
        var image=Path.Combine(output,"region.bin");File.WriteAllText(image,"synthetic region fixture");
        var box=new DrawingReviewBox(.1,.1,.9,.9);
        var inventory=new DrawingOmissionInventory{SourcePath=source,SourceSha256=DrawingPlanValidation.FileHash(source),TotalPages=1,
            Pages=[new(){PageNumber=1,Ingested=true,ObservationPath=observation,ObservationSha256=DrawingPlanValidation.FileHash(observation)}],
            Regions=[new(){Id="overview",PageNumber=1,Bounds=box,Purpose="overview",ImagePath=image,ImageSha256=DrawingPlanValidation.FileHash(image),PixelWidth=100,PixelHeight=100}],
            Candidates=[new(){Id="geometry",PageNumber=1,Kind=DrawingCandidateKind.Geometry,Bounds=box,ObservationIds=["g1"],ReviewRegionIds=["overview"]},
                new(){Id="text",PageNumber=1,Kind=DrawingCandidateKind.Annotation,Bounds=box,ObservationIds=["t1"],ReviewRegionIds=["overview"]}]};
        var path=Path.Combine(output,"anchor-inventory.json");File.WriteAllText(path,JsonSerializer.Serialize(inventory,ModelingIrJson.Options));
        DrawingGeometryAnchor Anchor(string id,string entity,string view)=>new(){Id=id,PhysicalEntityId=entity,ViewId=view,FeatureId="plate",Kind=DrawingAnchorKind.Plane,
            Bounds=new(.2,.2,.4,.4),State=DrawingReviewState.Reviewed,ObservationIds=["g1"],Evidence="explicit source endpoint"};
        var start=Anchor("left-front","left","front");var end=Anchor("right-front","right","front");
        var evidence=new DrawingDimensionEvidence{FeatureId="plate",MeasurementId="width",ViewId="front",QuantityKind=DrawingQuantityKind.Linear,
            TextBounds=box,TargetBounds=box,AttachmentExplanation="extension lines to left/right planes",Datum="left plane",
            StartReference="left",EndReference="right",StartAnchorId=start.Id,EndAnchorId=end.Id};
        var fact=new DrawingDimensionFact{Id="width",OperationId="sk",ParameterPath="primitives.0.width_mm",Value=20,SourceLiteral="20",ViewIds=["front"],BindingEvidence=evidence};
        var context=new DrawingPlanContext{SourcePath=source,RequireGeometryAnchors=true,Dimensions=[fact],GeometryAnchors=[start,end],
            Views=[new(){Id="front",Kind=ModelDrawingView.Front},new(){Id="top",Kind=ModelDrawingView.Top}],
            Features=[new(){Id="plate",SourceLiteral="plate",ViewIds=["front","top"],OperationIds=["sk"]}],
            OmissionReview=new(){InventoryPath=path,InventorySha256=DrawingPlanValidation.FileHash(path)}};
        void Pass(DrawingPlanContext c,string name)=>check(DrawingGeometryAnchorValidation.Validate(c).Count==0,name);
        void Reject(DrawingPlanContext c,string code,string name)=>check(DrawingGeometryAnchorValidation.Validate(c).Any(d=>d.Code==code),name);
        Pass(context,"real source observations and distinct endpoint identities accepted");
        Reject(context with{GeometryAnchors=[start,start]},"ANCHOR_ID","duplicate anchor ID rejected");
        Reject(context with{GeometryAnchors=[start with{State=DrawingReviewState.Conflict},end]},"ANCHOR_EVIDENCE","conflicted anchor rejected");
        Reject(context with{GeometryAnchors=[start with{ObservationIds=["invented"]},end]},"ANCHOR_OBSERVATION","fabricated observation rejected");
        Reject(context with{GeometryAnchors=[start with{ObservationIds=["t1"]},end]},"ANCHOR_OBSERVATION","numeric text cannot be used as geometry observation");
        Reject(context with{GeometryAnchors=[start with{Bounds=new(.91,.91,.99,.99)},end]},"ANCHOR_OBSERVATION","distant geometry observation rejected");
        Reject(context with{GeometryAnchors=[start with{ViewId="unknown"},end]},"ANCHOR_EVIDENCE","unknown anchor view rejected");
        Reject(context with{GeometryAnchors=[start with{Kind=(DrawingAnchorKind)100},end]},"ANCHOR_EVIDENCE","unknown anchor kind rejected");
        Reject(context with{GeometryAnchors=[start,end with{PhysicalEntityId="left"}]},"ANCHOR_ENDPOINTS","same physical endpoint under different IDs rejected");
        Reject(context with{Dimensions=[fact with{BindingEvidence=evidence with{StartAnchorId=null}}]},"ANCHOR_ENDPOINTS","required endpoint identity cannot be omitted");
        Reject(context with{Dimensions=[fact with{BindingEvidence=null}]},"ANCHOR_BINDING_REQUIRED","required source binding cannot be omitted");
        var topStart=Anchor("left-top","left","top");var topEnd=Anchor("right-top","right","top");
        var topFact=fact with{Id="width-top",ViewIds=["top"],BindingEvidence=evidence with{ViewId="top",StartAnchorId=topStart.Id,EndAnchorId=topEnd.Id}};
        var cross=context with{GeometryAnchors=[start,end,topStart,topEnd],Dimensions=[fact,topFact]};
        Pass(cross,"same physical endpoints across views accepted");
        Reject(cross with{GeometryAnchors=[start,end,topStart,topEnd with{PhysicalEntityId="inner-right"}]},"ANCHOR_CROSS_VIEW","equal numbers bound to different endpoint pairs rejected");
        Reject(cross with{GeometryAnchors=[start,end,topStart with{Kind=DrawingAnchorKind.Point},topEnd]},"ANCHOR_PHYSICAL_ID","same physical entity cannot change kind across views");
        var circular=fact with{BindingEvidence=evidence with{QuantityKind=DrawingQuantityKind.Diameter,TargetAnchorId="circle",StartAnchorId=null,EndAnchorId=null}};
        Pass(context with{Dimensions=[circular],GeometryAnchors=[start,end,Anchor("circle","bore","front") with{Kind=DrawingAnchorKind.Circle}]},"diameter target circle accepted");
        Reject(context with{Dimensions=[circular],GeometryAnchors=[start,end,Anchor("circle","bore","front")]},"ANCHOR_CIRCULAR","diameter target plane rejected");
        var circle=Anchor("circle","bore","front") with{Kind=DrawingAnchorKind.Circle};
        var circleTop=circle with{Id="circle-top",ViewId="top"};
        var radius=circular with{Id="radius",Value=10,ViewIds=["top"],BindingEvidence=circular.BindingEvidence! with{MeasurementId="different-id",ViewId="top",TargetAnchorId="circle-top",QuantityKind=DrawingQuantityKind.Radius}};
        var circles=context with{Dimensions=[circular,radius],GeometryAnchors=[circle,circleTop]};
        Pass(circles,"physical circle reconciles diameter and radius across different measurement IDs");
        Reject(circles with{Dimensions=[circular,radius with{Value=20}]},"ANCHOR_CIRCULAR_CONFLICT","same physical circle cannot hide doubled radius under another measurement ID");
        Reject(circles with{Dimensions=[circular,radius with{Value=12,BindingEvidence=radius.BindingEvidence! with{QuantityKind=DrawingQuantityKind.Diameter}}]},"ANCHOR_CIRCULAR_CONFLICT","same circle conflicting diameters with different IDs rejected");
        Pass(circles with{Dimensions=[circular,radius with{Value=10/25.4,Unit=DrawingValueUnit.Inch}]},"physical radius diameter comparison normalizes units");
        Pass(circles with{GeometryAnchors=[circle,circleTop with{PhysicalEntityId="different-bore"}],Dimensions=[circular,radius with{Value=12}]},"distinct circles can have different radii");
        Reject(circles with{Dimensions=[circular with{BindingEvidence=circular.BindingEvidence! with{TargetBounds=new(.6,.6,.8,.8)}},radius]},"ANCHOR_TARGET_REGION","dimension target cannot point to distant circle anchor");
        Reject(circles with{Dimensions=[circular with{Value=0}]},"ANCHOR_CIRCULAR_VALUE","zero diameter rejected at source anchor gate");
        Reject(circles with{Dimensions=[circular with{Value=double.NaN}]},"ANCHOR_CIRCULAR_VALUE","nonfinite diameter rejected at source anchor gate");
        Pass(circles with{Dimensions=[circular with{Value=19.9,NominalValue=20,Status=DrawingFactStatus.Derived,Derivation="authorized offset",DeviationReason="fixture clearance",DeviationAuthorization="explicit fixture authorization"},radius]},"authorized execution deviation preserves nominal circle consistency");
        Reject(circles with{Dimensions=[circular with{Value=19.9,NominalValue=20},radius]},"ANCHOR_CIRCULAR_CONFLICT","unauthorized nominal override cannot suppress circle mismatch");
        Reject(context with{OmissionReview=null},"ANCHOR_SOURCE","source inventory required");
        Pass(context with{RequireGeometryAnchors=false,GeometryAnchors=[],Dimensions=[fact with{BindingEvidence=evidence with{StartAnchorId=null,EndAnchorId=null}}]},"legacy evidence remains compatible");
    }
}
