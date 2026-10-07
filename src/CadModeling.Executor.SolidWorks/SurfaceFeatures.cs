using CadModeling.Ir;
using CadModeling.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System.Runtime.InteropServices;

internal sealed partial class SolidWorksComExecutor
{
    // GetPreTrimmedBodies returns temporary bodies, which cannot be selected until
    // Display3 makes them selectable. Always release its swModifyBlock state.
    private static object? CommitTrimRegions(IModelDoc2 model, IBody2[] regions, Vector3[] points, bool sew)
    {
        if (regions.Length == 0 || regions.Length != points.Length)
            throw new ArgumentException("剪裁区域需要每个保留区域一个模型空间点。");
        var displayed = new List<IBody2>();
        try
        {
            for (var i = 0; i < regions.Length; i++)
            {
                var region = regions[i];
                if (region.Display3(model, 0x80C0FF, (int)swTempBodySelectOptions_e.swTempBodySelectable) != 0)
                    throw new InvalidOperationException("无法为选择显示临时剪裁区域。");
                displayed.Add(region);
                var point = points[i];
                var select = model.ISelectionManager.CreateSelectData();
                // mark 2 belongs to our typed keep query; native trim uses mark 0.
                select.Mark = 0;
                select.X = Mm(point.X); select.Y = Mm(point.Y); select.Z = Mm(point.Z);
                if (!region.Select2(true, select))
                    throw new InvalidOperationException("无法选择显示的临时剪裁区域。");
            }
            return model.FeatureManager.PostTrimSurface(sew)
                ?? throw new InvalidOperationException("SolidWorks 返回没有曲面修剪特征。");
        }
        finally
        {
            foreach (var region in displayed.AsEnumerable().Reverse())
            {
                // A successful commit may already have consumed the temporary body.
                try { region.Hide(model); } catch (COMException) { }
            }
        }
    }

    private static object? ExecuteSurfaceFeature(IModelDoc2 model, NativeFeatureOperation operation, IReadOnlyDictionary<string, object> objects)
    {
        var o = operation.Options;
        var s = o.Surface ?? new();
        var fm = model.FeatureManager;
        IFeature Sketch(string id)
        {
            var f = ResolveFeature(model, objects, id);
            if (f.GetSpecificFeature2() is not ISketch && f.GetDefinition() is not IHelixFeatureData) throw new InvalidOperationException($"曲面参照 '{id}' 不是一个原生的草图或螺旋线。");
            return f;
        }
        switch (o.Kind)
        {
            case NativeFeatureKind.SurfaceBoundary:
                var request=BindBoundaryRequest(model,o,objects);
                var port=new NativeBoundarySurfaceSession(model,objects,request);
                BoundarySurfaceContract.Execute(port,request,(a,b)=>SamePersistentIdentity(model,a,b));
                return port.Feature!;
            case NativeFeatureKind.SurfaceFill:
                if(s.FillBoundaries.Count>0) return CreateConstrainedFill(model,o,objects);
                var boundaries = o.ProfileIds.Select(Sketch).ToArray();
                var constraints = o.GuideIds.Select(Sketch).ToArray();
                for (var i = 0; i < boundaries.Length; i++) SelectFeature(model, boundaries[i], i > 0, 257);
                foreach (var constraint in constraints) SelectFeature(model, constraint, true, 4);
                // Contact only: never silently substitute contact for requested tangent/curvature constraints.
                return fm.InsertFillSurface2(s.FillResolution, s.OptimizeFill ? (int)swFeatureFillSurfaceOptions_e.swOptimizeSurface : 0,
                    boundaries.Select(x => new DispatchWrapper(x)).ToArray(),
                    Enumerable.Repeat((int)swContactType_e.swContact, boundaries.Length).ToArray(), null,
                    constraints.Length == 0 ? null : constraints.Select(x => new DispatchWrapper(x)).ToArray());
            case NativeFeatureKind.SurfaceSweep:
                var sweep=o.Sweep??new SweepOptions{AdvancedSmoothing=false,Orientation=s.SweepOrientation==SurfaceSweepOrientation.KeepNormalConstant?SweepOrientation.KeepNormalConstant:SweepOrientation.FollowPath};
                SelectFeature(model, Sketch(o.SketchId!), false, 1);
                SelectFeature(model, Sketch(o.PathSketchId!), true, 4);
                foreach (var id in o.GuideIds) SelectFeature(model, Sketch(id), true, 2);
                // Existing plugin uses this legacy sweep API family. Signature checked against local interop.
                return fm.InsertSweepSurface3(o.TangentPropagation,
                    SweepMode(sweep),
                    sweep.KeepTangency, sweep.AdvancedSmoothing, 0, 0, 0, false, true, Radians(sweep.TwistAngleDegrees), sweep.MergeSmoothFaces && o.Merge, false, 0,
                    (int)(o.Reverse ? swSweepDirection_e.swSweepDirection2 : swSweepDirection_e.swSweepDirection1));
            case NativeFeatureKind.SurfaceOffset:
                SelectQueries(model, objects, o.Selections.Select(x => x with { SelectionMark = 0 }).ToArray());
                model.InsertOffsetSurface(Mm(o.DistanceMm), o.Reverse);
                return model.IFeatureByPositionReverse(0);
            default: throw new NotSupportedException($"不支持的曲面特征{o.Kind}。");
        }
    }
}
