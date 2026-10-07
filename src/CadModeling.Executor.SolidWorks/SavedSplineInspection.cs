using CadModeling.Core;
using SolidWorks.Interop.sldworks;

internal sealed partial class SolidWorksComExecutor
{
    private static SavedSplineCurve CaptureSavedSplineCurve(ICurve curve)
    {
        if (!curve.GetEndParams(out _, out _, out bool closed, out bool periodic) || closed || periodic)
            throw new InvalidOperationException("保存修订认证暂不支持闭合或周期样条边。");
        // No cubic, non-rational or non-periodic conversion is requested.
        var d = curve.GetBCurveParams5(false, false, false, false)
            ?? throw new InvalidOperationException("样条边缺完整原生参数。");
        var count = d.KnotPointsCount;
        if (d.ControlPointsCount is < 2 or > 8192 || d.Dimension is not (3 or 4)
            || d.Order is < 2 or > 16 || d.Periodic != 0 || count != d.ControlPointsCount + d.Order
            || !d.GetKnotPoints(out object knots) || !d.GetControlPoints(out object points)
            || knots is not double[] k || k.Length != count
            || points is not double[] p || p.Length != d.ControlPointsCount * d.Dimension)
            throw new InvalidOperationException("样条边原生节点或控制网不完整。");
        return new(d.Order, d.Dimension, d.ControlPointsCount, false, k, p);
    }

    private static (SavedSplineSurface Surface, SavedSplineTrimLoop Loop) CaptureSavedSplineFace(
        IModelDoc2 model, IFace2 face, ISurface surface, IReadOnlyList<string> boundary)
    {
        // Only actual native BSURF faces enter here. Blends, offsets and other
        // surfaces must not be approximated and relabelled as complete splines.
        var d = surface.GetBSurfParams3(false, false, surface.Parameterization2(), 1e-12, out bool sense)
            ?? throw new InvalidOperationException("样条面缺完整原生参数。");
        var rows = d.ControlPointRowCount; var columns = d.ControlPointColumnCount;
        if (rows is < 2 or > 8192 || columns is < 2 or > 8192 || rows > 8192 / columns
            || d.UOrder is < 2 or > 16 || d.VOrder is < 2 or > 16 || d.ControlPointDimension is not (3 or 4)
            || d.UPeriodicity || d.VPeriodicity || !sense || d.UKnots is not double[] u
            || d.VKnots is not double[] v || u.Length != columns + d.UOrder || v.Length != rows + d.VOrder)
            throw new InvalidOperationException("样条面控制网超界、周期化或节点数据不完整。");
        var points = new List<double>();
        for (int row = 1; row <= rows; row++) for (int column = 1; column <= columns; column++)
        {
            if (d.GetControlPoints(row, column) is not double[] p || p.Length != d.ControlPointDimension)
                throw new InvalidOperationException("样条面控制点向量不完整。");
            points.AddRange(p);
        }
        var loops = (face.GetLoops() as object[] ?? []).Cast<ILoop2>().ToArray();
        if (loops.Length != 1 || !loops[0].IsOuter() || loops[0].GetCoEdgeCount() != boundary.Count)
            throw new InvalidOperationException("样条面认证仅支持唯一完整外环。");
        var loopEdges = new List<(string Reference, bool Sense)>();
        var coedge = (ICoEdge)loops[0].GetFirstCoEdge();
        for (int i = 0; i < boundary.Count; i++)
        {
            loopEdges.Add((Persistent(model, coedge.GetEdge())
                ?? throw new InvalidOperationException("样条面边环缺持久身份。"), coedge.GetSense()));
            coedge = (ICoEdge)coedge.GetNext();
        }
        if (Persistent(model, coedge.GetEdge()) != loopEdges[0].Reference
            || coedge.GetSense() != loopEdges[0].Sense || loopEdges.Select(e => e.Reference).Distinct().Count() != boundary.Count)
            throw new InvalidOperationException("样条面外环不闭合或包含重复接缝边。");
        // WantCubic=false preserves the underlying surface; SP-curves remain
        // the native kernel's trim representation. Exact 3D edge definitions
        // are captured separately in the complete entity inventory.
        int size = face.IGetTrimCurveSize2(0, 0);
        if (size is < 8 or > 262144) throw new InvalidOperationException("样条修剪读回尺寸超界。");
        var raw = face.GetTrimCurves2(false, false) as double[]
            ?? throw new InvalidOperationException("样条修剪数据未返回数组。");
        var topology = (face.GetTrimCurveTopology() as object[] ?? []).OfType<ICoEdge>().ToArray();
        // Consume the native face-specific temporary edge buffer immediately.
        var alignedEdges = (face.GetEdges() as object[] ?? []).OfType<IEdge>().ToArray();
        var curves = SavedSplineTrimDataReader.Read(raw);
        if (raw.Length != size || curves.Count != boundary.Count || topology.Length != curves.Count
            || alignedEdges.Length != curves.Count)
            throw new InvalidOperationException("修剪曲线与实际边界不能完整对应。");
        var trimEdges = topology.Select((c, i) => new SavedSplineTrimEdge(Persistent(model, c.GetEdge())
            ?? throw new InvalidOperationException("修剪拓扑缺边身份。"), c.GetSense(), curves[i])).ToArray();
        bool Identity(string a, string b) => model.Extension.IsSamePersistentID(Convert.FromBase64String(a), Convert.FromBase64String(b)) == 1;
        if (trimEdges.Where((e, i) => !Identity(e.EdgeReference, Persistent(model, alignedEdges[i])!)).Any()
            || Enumerable.Range(0, trimEdges.Length).Count(offset => loopEdges.Select((e, i) =>
                (e, trim: trimEdges[(i + offset) % trimEdges.Length])).All(p =>
                    p.e.Sense == p.trim.Sense && Identity(p.e.Reference, p.trim.EdgeReference))) != 1)
            throw new InvalidOperationException("修剪拓扑的顺序、方向或来源与实际外环不一致。");
        return (new(d.UOrder, d.VOrder, columns, rows, d.ControlPointDimension, false, false, sense,
            face.FaceInSurfaceSense(), u, v, points), new(true, trimEdges));
    }
}
