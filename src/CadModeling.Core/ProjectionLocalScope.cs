namespace CadModeling.Core;
public sealed record ProjectionRegionMm(double MinimumX,double MinimumY,double MaximumX,double MaximumY);

public static class ProjectionLocalScope
{
    public static ProjectionSnapshot Select(ProjectionSnapshot snapshot,ProjectionRegionMm region)
    {
        if(!double.IsFinite(region.MinimumX)||!double.IsFinite(region.MinimumY)||!double.IsFinite(region.MaximumX)||!double.IsFinite(region.MaximumY)||
            region.MinimumX>=region.MaximumX||region.MinimumY>=region.MaximumY)throw new ArgumentException("局部投影区间必须为有限正面积的源毫米坐标框。");
        // Retain whole intersecting primitives: never trim endpoints or silently fit geometry to pass.
        // Arc full-circle envelopes conservatively over-include; they cannot conceal a local extra edge.
        return snapshot with {Primitives=snapshot.Primitives.Where(p=>Intersects(p,region)).ToArray()};
    }
    private static bool Intersects(ProjectionPrimitive p,ProjectionRegionMm r)
    {
        var circular=p.Kind is ProjectionPrimitiveKind.Circle or ProjectionPrimitiveKind.Arc;
        var minX=circular?p.Center.X-p.RadiusMm:Math.Min(p.Start.X,p.End.X);
        var minY=circular?p.Center.Y-p.RadiusMm:Math.Min(p.Start.Y,p.End.Y);
        var maxX=circular?p.Center.X+p.RadiusMm:Math.Max(p.Start.X,p.End.X);
        var maxY=circular?p.Center.Y+p.RadiusMm:Math.Max(p.Start.Y,p.End.Y);
        return minX<=r.MaximumX && minY<=r.MaximumY && maxX>=r.MinimumX && maxY>=r.MinimumY;
    }
}
