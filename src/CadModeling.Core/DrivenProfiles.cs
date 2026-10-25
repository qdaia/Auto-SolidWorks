using System.Text.Json.Serialization;
using CadModeling.Ir;
namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<DrivenProfileKind>))]
public enum DrivenProfileKind { RoundEar, HalfAnnulus, QuarterAnnulus }
public sealed record DrivenProfileSpec
{
    public required DrivenProfileKind Kind { get; init; }
    public required double RadiusMm { get; init; }
    public double? InnerRadiusMm { get; init; }
    /// <summary>Ear height to the arc centre from the bottom midpoint at the local origin.</summary>
    public double? WebHeightMm { get; init; }
}

/// <summary>Bounded mechanical profiles with explicit constraints and driving dimensions.</summary>
public static class DrivenProfiles
{
    public static GenericOperationDraft Expand(GenericOperationDraft source)
    {
        var p = source.DrivenProfile ?? throw new ArgumentException("缺少 driven_profile。");
        if(source.Type!=GenericOperationKind.ProfileSketch || source.Primitives.Count>0 || source.Constraints.Count>0 || source.Dimensions.Count>0 || source.Edits.Count>0 || source.AutoDimensionPrimitives)
            throw new ArgumentException("driven_profile 自带显式几何、约束和尺寸，不能与其他草图输入混用。");
        if(!Enum.IsDefined(p.Kind) || !double.IsFinite(p.RadiusMm) || p.RadiusMm<=0)
            throw new ArgumentException("驱动轮廓需要受支持的类型和有限正半径。");
        var curves=new List<GenericCurveDraft>(); var relations=new List<SketchConstraintSpec>(); var dims=new List<SketchDimensionSpec>();
        SketchEntityReference Ref(int segment,SketchEntityPart part=SketchEntityPart.Segment)=>new(){SegmentIndex=segment,Part=part};
        void Rel(SketchConstraintKind kind,params SketchEntityReference[] entities)=>relations.Add(new(){Kind=kind,Entities=entities});
        void Dim(string name,SketchDimensionKind kind,double value,int segment,ProfilePoint label)=>dims.Add(new(){Name=name,Kind=kind,Value=value,Entities=[Ref(segment)],LabelPosition=label});
        void Line(ProfilePoint a,ProfilePoint b)=>curves.Add(new(){Type=GenericCurveKind.Line,Start=a,End=b});
        void Arc(ProfilePoint a,ProfilePoint mid,ProfilePoint b)=>curves.Add(new(){Type=GenericCurveKind.ThreePointArc,Start=a,PointOnArc=mid,End=b});
        var r=p.RadiusMm;
        if(p.Kind==DrivenProfileKind.RoundEar)
        {
            if(p.InnerRadiusMm is not null || p.WebHeightMm is not {} h || !double.IsFinite(h) || h<=0)
                throw new ArgumentException("圆头耳板需要有限正 web_height_mm，不接受 inner_radius_mm。孔应另行声明。");
            Line(new(-r,0),new(r,0)); Line(new(r,0),new(r,h));
            Arc(new(r,h),new(0,h+r),new(-r,h)); Line(new(-r,h),new(-r,0));
            Rel(SketchConstraintKind.Horizontal,Ref(0)); Rel(SketchConstraintKind.Vertical,Ref(1)); Rel(SketchConstraintKind.Vertical,Ref(3));
            Rel(SketchConstraintKind.Tangent,Ref(1),Ref(2)); Rel(SketchConstraintKind.Tangent,Ref(2),Ref(3));
            Rel(SketchConstraintKind.Midpoint,Ref(0,SketchEntityPart.Origin),Ref(0));
            Dim("耳板宽度",SketchDimensionKind.Horizontal,2*r,0,new(0,-10));
            Dim("耳板直段高度",SketchDimensionKind.Vertical,h,1,new(r+10,h/2));
        }
        else
        {
            if(p.WebHeightMm is not null || p.InnerRadiusMm is not {} inner || !double.IsFinite(inner) || inner<=0 || inner>=r)
                throw new ArgumentException("局部圆环需要 0 < inner_radius_mm < radius_mm，不接受 web_height_mm。");
            var quarter=p.Kind==DrivenProfileKind.QuarterAnnulus;
            var outerEnd=quarter?new ProfilePoint(0,r):new(-r,0);
            var innerEnd=quarter?new ProfilePoint(0,inner):new(-inner,0);
            var k=quarter?1/Math.Sqrt(2):0;
            Arc(new(r,0),new(r*k,quarter?r*k:r),outerEnd);
            Line(outerEnd,innerEnd);
            Arc(innerEnd,new(inner*k,quarter?inner*k:inner),new(inner,0));
            Line(new(inner,0),new(r,0));
            // Fix only the zero-coordinate reference centre, never a design edge or radius.
            Rel(SketchConstraintKind.Fixed,Ref(0,SketchEntityPart.CenterPoint));
            Rel(SketchConstraintKind.Concentric,Ref(0),Ref(2));
            Rel(quarter?SketchConstraintKind.Vertical:SketchConstraintKind.Horizontal,Ref(1));
            Rel(SketchConstraintKind.Horizontal,Ref(3));
            Rel(quarter?SketchConstraintKind.Vertical:SketchConstraintKind.Horizontal,Ref(1,SketchEntityPart.StartPoint),Ref(0,SketchEntityPart.CenterPoint));
            Rel(SketchConstraintKind.Horizontal,Ref(3,SketchEntityPart.StartPoint),Ref(0,SketchEntityPart.CenterPoint));
            Dim("外圆半径",SketchDimensionKind.Radius,r,0,new(r+10,r+10));
            Dim("内圆半径",SketchDimensionKind.Radius,inner,2,new(inner/2,inner/2));
        }
        var joins=Enumerable.Range(0,4).Select(i=>new SketchConstraintSpec {Kind=SketchConstraintKind.Coincident,
            Entities=[Ref(i,SketchEntityPart.EndPoint),Ref((i+1)%4,SketchEntityPart.StartPoint)]}).ToArray();
        return source with {DrivenProfile=null,Primitives=[new(){Type=GenericPrimitiveKind.CompositeCurve,Curves=curves}],
            Constraints=joins.Concat(relations).ToArray(),Dimensions=dims,RequireEditable=true,RequireFullyDefined=true};
    }
}
