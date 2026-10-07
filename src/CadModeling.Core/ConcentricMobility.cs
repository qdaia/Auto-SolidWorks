using CadModeling.Ir;
namespace CadModeling.Core;

public sealed record NativeConcentricPairReadback
{
    public bool CompleteMateInventory { get; init; }
    public bool TwoResolvedTopLevelParts { get; init; }
    public bool ReferenceFixed { get; init; }
    public int ActiveMateCount { get; init; }
    public bool ConcentricDefinitionVerified { get; init; }
    public bool LockRotation { get; init; }
    public required Vector3 FirstAxisPointMm { get; init; }
    public required Vector3 SecondAxisPointMm { get; init; }
    public required Vector3 FirstAxis { get; init; }
    public required Vector3 SecondAxis { get; init; }
    public double FirstRadiusMm { get; init; }
    public double SecondRadiusMm { get; init; }
}
/// <summary>Exact regular nullspace of one native concentric cylinder mate to a
/// fixed part. Does not infer multi-mate or articulated mechanism motion.</summary>
public static class ConcentricMobilityContract
{
    public static IReadOnlyList<AssemblyMotion> Basis(NativeConcentricPairReadback r)
    {
        void Require(bool ok){if(!ok)throw new ArgumentException("ASSEMBLY_MOBILITY_UNVERIFIABLE：完整同心圆柱对的原生库存、固定参照或实际轴线无法确认。");}
        Require(r.CompleteMateInventory&&r.TwoResolvedTopLevelParts&&r.ReferenceFixed&&r.ActiveMateCount==1&&r.ConcentricDefinitionVerified);
        Require(NativeFeatureValidation.Finite(r.FirstAxisPointMm)&&NativeFeatureValidation.Finite(r.SecondAxisPointMm)
            &&NativeFeatureValidation.Finite(r.FirstAxis)&&NativeFeatureValidation.Finite(r.SecondAxis)
            &&NativeFeatureValidation.Norm(r.FirstAxis)>1e-10&&NativeFeatureValidation.Norm(r.SecondAxis)>1e-10
            &&double.IsFinite(r.FirstRadiusMm)&&r.FirstRadiusMm>0&&double.IsFinite(r.SecondRadiusMm)&&r.SecondRadiusMm>0);
        var a=r.FirstAxis;var b=r.SecondAxis;var na=NativeFeatureValidation.Norm(a);var nb=NativeFeatureValidation.Norm(b);
        a=new(a.X/na,a.Y/na,a.Z/na);b=new(b.X/nb,b.Y/nb,b.Z/nb);
        var dot=a.X*b.X+a.Y*b.Y+a.Z*b.Z;Require(Math.Abs(dot)>=1-1e-8);
        var p=r.FirstAxisPointMm;var q=r.SecondAxisPointMm;var x=q.X-p.X;var y=q.Y-p.Y;var z=q.Z-p.Z;
        var along=x*a.X+y*a.Y+z*a.Z;Require(Math.Sqrt(Math.Pow(x-along*a.X,2)+Math.Pow(y-along*a.Y,2)+Math.Pow(z-along*a.Z,2))<=.01);
        var result=new List<AssemblyMotion>{new(){Kind=AssemblyMotionKind.Translation,Direction=a}};
        if(!r.LockRotation)result.Add(new(){Kind=AssemblyMotionKind.Rotation,Direction=a,PointMm=p});
        return result;
    }
}
