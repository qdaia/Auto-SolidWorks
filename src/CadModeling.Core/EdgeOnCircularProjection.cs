using CadModeling.Ir;
namespace CadModeling.Core;

/// <summary>Native view coordinates in meters, before drawing placement is removed.</summary>
public sealed record NativeProjectedLine(Vector3 StartMeters,Vector3 EndMeters);

public static class EdgeOnCircularProjection
{
    /// <summary>
    /// Projects an analytic circular display record onto view XY. The native 3-D
    /// samples establish the signed trim; analytic extrema determine the exact span.
    /// No ellipse fitting, free scale or polyline-extrema approximation is performed.
    /// </summary>
    public static NativeProjectedLine Project(ProjectionDisplayPolylineRecord record)
    {
        var g=record.GeometryData;var xyz=record.PointsXyz;
        if(record.Type!=1||g.Count!=12||xyz.Count<6||xyz.Count%3!=0||g.Concat(xyz).Any(x=>!double.IsFinite(x)))
            throw new InvalidDataException("Circular display geometry is incomplete or non-finite.");
        Vector3 G(int index)=>new(g[index],g[index+1],g[index+2]);
        Vector3 P(int index)=>new(xyz[index],xyz[index+1],xyz[index+2]);
        var center=G(0);var start=G(3);var end=G(6);var rawNormal=G(9);
        var normalLength=ModelVerification.Norm(rawNormal);
        if(normalLength<=1e-12)throw new InvalidDataException("Circular display normal is invalid.");
        var normal=ModelVerification.Scale(rawNormal,1/normalLength);
        if(Math.Abs(normal.Z)>1e-8)throw new InvalidDataException("Circular support is not edge-on; oblique ellipses remain unsupported.");
        var delta=ModelVerification.Sub(start,center);var radius=ModelVerification.Norm(delta);
        const double tolerance=1e-7; // 0.0001 mm, native input is meters.
        if(radius<=1e-10||Math.Abs(ModelVerification.Norm(ModelVerification.Sub(end,center))-radius)>tolerance)
            throw new InvalidDataException("Native circular endpoints do not share a positive radius.");
        var u=ModelVerification.Scale(delta,1/radius);
        var v=new Vector3(normal.Y*u.Z-normal.Z*u.Y,normal.Z*u.X-normal.X*u.Z,normal.X*u.Y-normal.Y*u.X);
        if(Math.Abs(ModelVerification.Dot(normal,u))>1e-8)throw new InvalidDataException("Circular start lies outside its native plane.");
        var axis=ModelVerification.Unit(new(normal.Y,-normal.X,0));
        double Scalar(Vector3 p)=>ModelVerification.Dot(ModelVerification.Sub(p,center),axis);
        var points=Enumerable.Range(0,xyz.Count/3).Select(i=>P(i*3)).ToArray();
        if(ModelVerification.Norm(ModelVerification.Sub(points[0],start))>tolerance||ModelVerification.Norm(ModelVerification.Sub(points[^1],end))>tolerance)
            throw new InvalidDataException("Display trim endpoints disagree with analytic trim endpoints.");
        foreach(var point in points)
        {
            var radial=ModelVerification.Sub(point,center);
            if(Math.Abs(ModelVerification.Norm(radial)-radius)>tolerance||Math.Abs(ModelVerification.Dot(radial,normal))>tolerance)
                throw new InvalidDataException("Display points disagree with the native analytic circle/plane.");
        }
        // Antipodal endpoints already at both projected extrema fully bound either semicircle.
        var antipodal=ModelVerification.Norm(ModelVerification.Sub(ModelVerification.Add(start,end),ModelVerification.Scale(center,2)))<=tolerance;
        if(antipodal&&Math.Abs(Math.Abs(Scalar(start)-Scalar(end))-2*radius)<=tolerance)
            return new(start,end);
        if(points.Length<3)throw new InvalidDataException("Trimmed edge-on arc lacks an interior point to establish signed sweep.");
        double Angle(Vector3 point)
        {
            var radial=ModelVerification.Sub(point,center);
            return Math.Atan2(ModelVerification.Dot(radial,v),ModelVerification.Dot(radial,u));
        }
        var previous=Angle(points[0]);var sweep=0d;var direction=0;
        for(var i=1;i<points.Length;i++)
        {
            var angle=Angle(points[i]);var step=Math.IEEERemainder(angle-previous,2*Math.PI);previous=angle;
            if(Math.Abs(step)>=Math.PI-1e-8)throw new InvalidDataException("Circular display sampling cannot determine a unique signed trim.");
            if(Math.Abs(step)<=1e-9)continue;
            var sign=Math.Sign(step);if(direction!=0&&sign!=direction)throw new InvalidDataException("Circular display trim reverses direction.");
            direction=sign;sweep+=step;
        }
        if(Math.Abs(sweep)<=1e-9||Math.Abs(sweep)>2*Math.PI+1e-8)throw new InvalidDataException("Native circular trim is empty or wraps more than once.");
        var critical=Math.Atan2(ModelVerification.Dot(axis,v),ModelVerification.Dot(axis,u));
        var angles=new List<double>{0,sweep};var low=Math.Min(0,sweep);var high=Math.Max(0,sweep);
        for(var k=-3;k<=3;k++)
        {
            var angle=critical+k*Math.PI;if(angle>low+1e-10&&angle<high-1e-10)angles.Add(angle);
        }
        Vector3 At(double angle)=>ModelVerification.Add(center,ModelVerification.Scale(ModelVerification.Add(ModelVerification.Scale(u,Math.Cos(angle)),ModelVerification.Scale(v,Math.Sin(angle))),radius));
        var extrema=angles.Select(At).OrderBy(Scalar).ToArray();
        if(Scalar(extrema[^1])-Scalar(extrema[0])<=1e-10)throw new InvalidDataException("Projected arc has zero extent.");
        return new(extrema[0],extrema[^1]);
    }
}
