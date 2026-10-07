using CadModeling.Ir;
namespace CadModeling.Core;

/// <summary>Model-space derivatives in metres. Normal is the underlying surface normal.</summary>
public sealed record SurfaceDerivatives(Vector3 Du,Vector3 Dv,Vector3 Duu,Vector3 Duv,Vector3 Dvv,Vector3 Normal);

public static class SurfaceDifferentialGeometry
{
    /// <summary>Decode ISurface.Evaluate(u,v,2,2): P, Pu, Puu, Pv, Puv, Puuv, Pvv, Puvv, Puuvv, N.</summary>
    public static SurfaceDerivatives FromNativeEvaluation(IReadOnlyList<double> values)
    {
        if(values.Count!=30 || values.Any(v=>!double.IsFinite(v)))throw new ArgumentException("SURFACE_DERIVATIVES: 二阶原生曲面评估必须包含 30 个有限值。");
        Vector3 At(int i)=>new(values[i],values[i+1],values[i+2]);
        return new(At(3),At(9),At(6),At(12),At(18),At(27));
    }

    /// <summary>Largest absolute difference of normal curvature over all directions in a common tangent plane, in 1/mm.</summary>
    public static double MaximumNormalCurvatureDifferencePerMm(SurfaceDerivatives first,SurfaceDerivatives second,Vector3 seamTangent)
    {
        var n1=Normalize(first.Normal);var n2=Normalize(second.Normal);
        // Opposite underlying surface orientations must use the same comparison normal.
        if(Dot(n1,n2)<0)n2=Scale(n2,-1);
        var along=Normalize(Sub(seamTangent,Scale(n1,Dot(seamTangent,n1))));
        var across=Normalize(Cross(n1,along));var diagonal=Normalize(Add(along,across));
        // Rotate the second tangent plane onto the first with the minimum normal-aligning
        // rotation. This preserves lengths and the quadratic form even for a tolerated G1 angle.
        var rotationAxis=Cross(n1,n2);var cosine=Dot(n1,n2);
        Vector3 Transport(Vector3 direction)=>Add(Add(direction,Cross(rotationAxis,direction)),
            Scale(Cross(rotationAxis,Cross(rotationAxis,direction)),1/(1+cosine)));
        double Delta(Vector3 direction)=>NormalCurvature(first,n1,direction)-NormalCurvature(second,n2,Transport(direction));
        var a=Delta(along);var c=Delta(across);var b=Delta(diagonal)-(a+c)/2;
        // Three quadratic-form evaluations recover the full symmetric 2x2 difference.
        // Its spectral radius bounds every tangent direction, including mixed curvature.
        var halfTrace=(a+c)/2;var root=Math.Sqrt(Math.Pow((a-c)/2,2)+b*b);
        var result=Math.Max(Math.Abs(halfTrace-root),Math.Abs(halfTrace+root))/1000;
        if(!double.IsFinite(result))throw new ArgumentException("SURFACE_CURVATURE: 法曲率差不是有限值。");
        return result;
    }

    private static double NormalCurvature(SurfaceDerivatives surface,Vector3 normal,Vector3 direction)
    {
        if(new[]{surface.Du,surface.Dv,surface.Duu,surface.Duv,surface.Dvv}.Any(v=>!Finite(v)))
            throw new ArgumentException("SURFACE_DERIVATIVES: 非有限的曲面导数。");
        // Normalize both parameter directions before solving: UV units and scales can differ arbitrarily.
        var lu=Norm(surface.Du);var lv=Norm(surface.Dv);
        if(!double.IsFinite(lu)||!double.IsFinite(lv)||lu<=0||lv<=0)
            throw new ArgumentException("SURFACE_DERIVATIVES: 退化或不支持的参数化。");
        var u=Scale(surface.Du,1/lu);var v=Scale(surface.Dv,1/lv);
        var uv=Dot(u,v);var det=1-uv*uv;
        if(!double.IsFinite(det)||det<=1e-12)throw new ArgumentException("SURFACE_DERIVATIVES: 奇异的曲面度量。");
        var cross=Normalize(Cross(u,v));
        if(Math.Abs(Dot(cross,normal))<1-1e-8)throw new ArgumentException("SURFACE_DERIVATIVES: 原生法向与一阶导数不一致。");
        var projected=Normalize(Sub(direction,Scale(normal,Dot(direction,normal))));
        var ud=Dot(u,projected);var vd=Dot(v,projected);
        var alpha=(ud-uv*vd)/det;var beta=(vd-uv*ud)/det;
        var denominator=alpha*alpha+2*uv*alpha*beta+beta*beta;
        // The second fundamental form must transform with the same parameter scales.
        var e=Dot(normal,surface.Duu)/lu/lu;
        var f=Dot(normal,surface.Duv)/lu/lv;
        var g=Dot(normal,surface.Dvv)/lv/lv;
        var result=(e*alpha*alpha+2*f*alpha*beta+g*beta*beta)/denominator;
        if(!double.IsFinite(result)||denominator<=0)throw new ArgumentException("SURFACE_CURVATURE: 无法求得法曲率。");
        return result;
    }

    private static bool Finite(Vector3 v)=>double.IsFinite(v.X)&&double.IsFinite(v.Y)&&double.IsFinite(v.Z);
    private static double Dot(Vector3 a,Vector3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static double Norm(Vector3 v)=>Math.Sqrt(Dot(v,v));
    private static Vector3 Normalize(Vector3 v)
    {
        var length=Norm(v);if(!Finite(v)||!double.IsFinite(length)||length<=0)throw new ArgumentException("SURFACE_DERIVATIVES: 零或无效的方向。");
        return Scale(v,1/length);
    }
    private static Vector3 Scale(Vector3 v,double scale)=>new(v.X*scale,v.Y*scale,v.Z*scale);
    private static Vector3 Add(Vector3 a,Vector3 b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    private static Vector3 Sub(Vector3 a,Vector3 b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    private static Vector3 Cross(Vector3 a,Vector3 b)=>new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
}
