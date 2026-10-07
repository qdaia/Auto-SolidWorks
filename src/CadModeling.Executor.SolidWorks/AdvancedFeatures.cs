using CadModeling.Ir;
using CadModeling.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System.Runtime.InteropServices;

internal sealed partial class SolidWorksComExecutor
{
    private static int SweepMode(SweepOptions s) => s.Orientation switch
    {
        SweepOrientation.FollowPath => 0, SweepOrientation.KeepNormalConstant => 1,
        SweepOrientation.FollowFirstGuide => 2, SweepOrientation.FollowTwoGuides => 3,
        SweepOrientation.TwistAlongPath => 8, SweepOrientation.TwistWithConstantNormal => 9,
        _ => throw new ArgumentException("未知的扫掠方向。")
    };
    private static object CreateSpatialCurve(IModelDoc2 model, SpatialCurveOptions o)
    {
        if(model.IGetActiveSketch2() is not null) throw new InvalidOperationException("SPATIAL_CURVE_CONTEXT: 草图已经打开。");
        var manager=model.SketchManager;
        var previousAddToDb=manager.AddToDB;var previousDisplayWhenAdded=manager.DisplayWhenAdded;
        manager.Insert3DSketch(true);
        try
        {
            manager.AddToDB=true;manager.DisplayWhenAdded=false;
            if(model.IGetActiveSketch2() is not ISketch sketch || !sketch.Is3D()) throw new InvalidOperationException("SPATIAL_CURVE_CONTEXT: 没有原生的 3D 草图。");
            var points=o.PointsMm.ToList(); if(o.Closed) points.Add(points[0]);
            if(o.CurveKind == SpatialCurveKind.InterpolatingSpline)
            {
                var coordinates=points.SelectMany(p=>new[]{Mm(p.X),Mm(p.Y),Mm(p.Z)}).ToArray();
                if(manager.CreateSpline2(coordinates,o.NaturalEnds) is null) throw new InvalidOperationException("SPATIAL_CURVE_CREATION: 公开的 样条 API 返回 null。");
            }
            else for(var i=1;i<points.Count;i++)
            {
                var a=points[i-1];var b=points[i];
                if(manager.CreateLine(Mm(a.X),Mm(a.Y),Mm(a.Z),Mm(b.X),Mm(b.Y),Mm(b.Z)) is null)
                    throw new InvalidOperationException("SPATIAL_CURVE_CREATION: 线 API 返回 null。");
            }
        }
        finally
        {
            manager.DisplayWhenAdded=previousDisplayWhenAdded;manager.AddToDB=previousAddToDb;
            if(model.IGetActiveSketch2() is not null) manager.Insert3DSketch(true);
        }
        var feature=model.IFeatureByPositionReverse(0) ?? throw new InvalidOperationException("SPATIAL_CURVE_CREATION: 特征缺失。");
        if(feature.GetSpecificFeature2() is not ISketch created || !created.Is3D()) throw new InvalidOperationException("SPATIAL_CURVE_DEFINITION: 结果不是一个有效的草图 3D。");
        var segments=(created.GetSketchSegments() as object[]??[]).OfType<ISketchSegment>().Where(s=>!s.ConstructionGeometry).ToArray();
        var expected=o.CurveKind==SpatialCurveKind.InterpolatingSpline?1:o.PointsMm.Count-1+(o.Closed?1:0);
        if(segments.Length!=expected) throw new InvalidOperationException("SPATIAL_CURVE_DEFINITION: 段落库存不完整。");
        return feature;
    }
    private static object CreateHelix(IModelDoc2 model, NativeFeatureOptions o, IReadOnlyDictionary<string,object> objects)
    {
        var profile=ResolveFeature(model,objects,o.SketchId!);
        if(profile.GetSpecificFeature2() is not ISketch sketch || sketch.Is3D()) throw new InvalidOperationException("HELIX_PROFILE: 需要一个平面的圆草图。");
        var segments=(sketch.GetSketchSegments() as object[]??[]).OfType<ISketchSegment>().Where(s=>!s.ConstructionGeometry).ToArray();
        if(segments.Length!=1 || segments[0] is not ISketchArc arc || arc.IsCircle()==0) throw new InvalidOperationException("HELIX_PROFILE：原生草图必须且只能包含一个圆。");
        var previous=model.IFeatureByPositionReverse(0)?.GetID(); var h=o.Helix!;
        SelectFeature(model,profile,false,0);
        model.InsertHelix(o.Reverse,h.Clockwise,false,false,(int)swHelixDefinedBy_e.swHelixDefinedByPitchAndRevolution,
            Mm(h.PitchMm*h.Revolutions),Mm(h.PitchMm),h.Revolutions,0,Radians(h.StartAngleDegrees));
        var feature=model.IFeatureByPositionReverse(0);
        if(feature is null || feature.GetID()==previous || feature.GetDefinition() is not IHelixFeatureData data)
            throw new InvalidOperationException("HELIX_CREATION: 没有新的原生螺旋定义。");
        if(Math.Abs(data.Pitch-Mm(h.PitchMm))>1e-9 || Math.Abs(data.Revolution-h.Revolutions)>1e-8 || Math.Abs(data.StartingAngle-Radians(h.StartAngleDegrees))>1e-8
            || data.Clockwise!=h.Clockwise || data.ReverseDirection!=o.Reverse || data.Taper || data.DefinedBy!=(int)swHelixDefinedBy_e.swHelixDefinedByPitchAndRevolution)
            throw new InvalidOperationException("HELIX_DEFINITION: 轮廓、转数或方向在创建过程中未存活。");
        return feature;
    }
    private static Vector3 VertexPoint(IVertex vertex)
    {
        var p=ToDoubles(vertex.GetPoint(),3,"边端点");return new(p[0]*1000,p[1]*1000,p[2]*1000);
    }
    private static double PointDistance(Vector3 a,Vector3 b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2)+Math.Pow(a.Z-b.Z,2));
    private static bool SameNativeEntity(IModelDoc2 model,object a,object b)
    {
        if(ReferenceEquals(a,b))return true;
        var identity=Persistent(model,a);
        return !string.IsNullOrEmpty(identity) && Persistent(model,b) is {Length:>0} other
            && SamePersistentIdentity(model,identity,other);
    }
    private static bool SamePersistentIdentity(IModelDoc2 model,string first,string second) =>
        first==second || model.Extension.IsSamePersistentID(Convert.FromBase64String(first),Convert.FromBase64String(second))==(int)swObjectEquality.swObjectSame;
    private static double EdgeLengthMm(IEdge edge)
    {
        var data=edge.GetCurveParams3() ?? throw new InvalidOperationException("EDGE_LENGTH: 缺少参数范围。");
        var length=((ICurve)edge.GetCurve()).GetLength3(data.UMinValue,data.UMaxValue)*1000;
        if(!double.IsFinite(length) || length<=0) throw new InvalidOperationException("EDGE_LENGTH: 特征修剪后的曲线长度无效。");
        return length;
    }
    private static object CreateVariableFillet(IModelDoc2 model, NativeFeatureOptions o, IReadOnlyDictionary<string,object> objects)
    {
        var f=o.VariableFillet!;var edges=new List<IEdge>();var radii=new List<double>();
        foreach(var entry in f.Edges)
        {
            var edge=(IEdge)ResolveEntities(model,objects,entry.Edge with{StartPointMm=entry.StartPointMm,EndPointMm=entry.EndPointMm}).Single();
            if(edges.Any(e=>SameNativeEntity(model,e,edge))) throw new InvalidOperationException("VARIABLE_FILLET_EDGE: 复制边。");
            var start=edge.GetStartVertex() as IVertex;var end=edge.GetEndVertex() as IVertex;
            if(start is null || end is null) throw new InvalidOperationException("VARIABLE_FILLET_EDGE: 不支持封闭/无顶点的边。");
            var a=VertexPoint(start);var b=VertexPoint(end);var tol=entry.Edge.ToleranceMm;
            if(PointDistance(a,entry.StartPointMm)<=tol && PointDistance(b,entry.EndPointMm)<=tol) radii.AddRange([Mm(entry.StartRadiusMm),Mm(entry.EndRadiusMm)]);
            else if(PointDistance(b,entry.StartPointMm)<=tol && PointDistance(a,entry.EndPointMm)<=tol) radii.AddRange([Mm(entry.EndRadiusMm),Mm(entry.StartRadiusMm)]);
            else throw new InvalidOperationException("VARIABLE_FILLET_ENDPOINT: 确定的端点未能标识原生开放边。");
            edges.Add(edge);
        }
        model.ClearSelection2(true);
        foreach(var edge in edges)
        {
            var select=model.ISelectionManager.CreateSelectData();select.Mark=1;
            if(!((IEntity)edge).Select4(true,select)) throw new InvalidOperationException("VARIABLE_FILLET_SELECTION: 选取边失败。");
        }
        var flags=(o.TangentPropagation?1:0)|(f.CurvatureContinuous?256:32);
        return model.FeatureManager.FeatureFillet3(flags,0,0,0,1,0,0,radii.ToArray(),null,null,null,null,null,null)
            ?? throw new InvalidOperationException("VARIABLE_FILLET_CREATION: FeatureFillet3 返回 null。");
    }
    private static object CreateConstrainedFill(IModelDoc2 model, NativeFeatureOptions o, IReadOnlyDictionary<string,object> objects)
    {
        var requested=BindFillRequest(model,o,objects);
        var s=o.Surface!;var edges=new List<IEdge>();var faces=new List<DispatchWrapper>();var contacts=new List<int>();
        foreach(var boundary in s.FillBoundaries)
        {
            var edge=(IEdge)ResolveEntities(model,objects,boundary.Edge).Single();
            if(edges.Any(e=>SameNativeEntity(model,e,edge))) throw new InvalidOperationException("FILL_BOUNDARY: 复制边。");
            IFace2? support=null;
            if(boundary.SupportFace is not null)
            {
                support=(IFace2)ResolveEntities(model,objects,boundary.SupportFace).Single();
                var adjacent=edge.GetTwoAdjacentFaces2() as object[]??[];
                if(!adjacent.Any(f=>f is IFace2 && SameNativeEntity(model,f,support)))
                    throw new InvalidOperationException("FILL_SUPPORT: 支撑面不是其边界边线的相邻面。");
            }
            edges.Add(edge);faces.Add(new(support));contacts.Add((int)boundary.Contact);
        }
        model.ClearSelection2(true);
        for(var i=0;i<edges.Count;i++)
        {
            var data=model.ISelectionManager.CreateSelectData();data.Mark=s.FillBoundaries[i].Contact switch{SurfaceContact.Contact=>257,SurfaceContact.Tangent=>513,SurfaceContact.Curvature=>1,_=>throw new ArgumentException("未知的填充接触。")};
            if(!((IEntity)edges[i]).Select4(i>0,data)) throw new InvalidOperationException("FILL_BOUNDARY: 无法选择原生边线。");
        }
        var constraints=o.GuideIds.Select(id=>ResolveFeature(model,objects,id)).ToArray();
        foreach(var c in constraints) SelectFeature(model,c,true,4);
        var feature=model.FeatureManager.InsertFillSurface2(s.FillResolution,s.OptimizeFill?(int)swFeatureFillSurfaceOptions_e.swOptimizeSurface:0,
            edges.Select(e=>new DispatchWrapper(e)).ToArray(),contacts.ToArray(),s.FillBoundaries.All(b=>b.SupportFace is null)?null:faces.ToArray(),constraints.Length==0?null:constraints.Select(c=>new DispatchWrapper(c)).ToArray())
            ?? throw new InvalidOperationException("FILL_CREATION: 没有原生约束填充特征。");
        using(var session=new NativeFillDefinitionSession(model,feature))
            FillSurfaceContract.EnsureControls(session,requested,s.FillResolution,s.OptimizeFill,(a,b)=>SamePersistentIdentity(model,a,b));
        return feature;
    }
    private static void VerifyAdvancedDefinition(IModelDoc2 model, IFeature feature, NativeFeatureOptions o,
        IReadOnlyDictionary<string,object>? operationObjects=null)
    {
        if(o.Kind==NativeFeatureKind.PhysicalThread)ReadPhysicalThreadState(model,feature,o);
        if(o.VariableFillet is { } fillet)
        {
            if(feature.GetDefinition() is not IVariableFilletFeatureData2 data || !data.AccessSelections(model,null)) throw new InvalidOperationException("VARIABLE_FILLET_DEFINITION: 无法访问已保留的定义。");
            try
            {
                if(data.FilletEdgeCount!=fillet.Edges.Count || data.CurvatureContinuous!=fillet.CurvatureContinuous) throw new InvalidOperationException("VARIABLE_FILLET_DEFINITION: 边缘数量或曲率选项已更改。");
                var remaining=fillet.Edges.ToList();
                for(var i=0;i<data.FilletEdgeCount;i++)
                {
                    var edge=(IEdge)data.GetFilletEdgeAtIndex(i);
                    if(edge.GetStartVertex() is not IVertex a || edge.GetEndVertex() is not IVertex b) throw new InvalidOperationException("VARIABLE_FILLET_DEFINITION: 端点顶点缺失。");
                    var start=VertexPoint(a);var end=VertexPoint(b);
                    var entry=remaining.SingleOrDefault(e=>PointDistance(start,e.StartPointMm)<=e.Edge.ToleranceMm && PointDistance(end,e.EndPointMm)<=e.Edge.ToleranceMm || PointDistance(end,e.StartPointMm)<=e.Edge.ToleranceMm && PointDistance(start,e.EndPointMm)<=e.Edge.ToleranceMm)
                        ?? throw new InvalidOperationException("VARIABLE_FILLET_DEFINITION: 边线保留与声明的端点不同。");
                    var same=PointDistance(start,entry.StartPointMm)<=entry.Edge.ToleranceMm;
                    var r1=data.GetRadius(a)*1000;var r2=data.GetRadius(b)*1000;
                    if(!double.IsFinite(r1) || !double.IsFinite(r2) || Math.Abs(r1-(same?entry.StartRadiusMm:entry.EndRadiusMm))>1e-6 || Math.Abs(r2-(same?entry.EndRadiusMm:entry.StartRadiusMm))>1e-6)
                        throw new InvalidOperationException("VARIABLE_FILLET_DEFINITION: 端点半径未保留。");
                    remaining.Remove(entry);
                }
            }
            finally {data.ReleaseSelectionAccess();}
        }
        if(o.LinearPattern is { } pattern)
        {
            if(feature.GetDefinition() is not ILinearPatternFeatureData data || data.D1TotalInstances!=o.Count || data.D2TotalInstances!=pattern.SecondCount
                || Math.Abs(data.D1Spacing-Mm(o.SpacingMm))>1e-9 || pattern.SecondCount>1 && Math.Abs(data.D2Spacing-Mm(pattern.SecondSpacingMm))>1e-9
                || data.D1ReverseDirection!=o.Reverse || data.D2ReverseDirection!=pattern.ReverseSecondDirection || data.D2PatternSeedOnly!=pattern.SecondDirectionSeedOnly)
                throw new InvalidOperationException("PATTERN_DEFINITION: 保留的计数、间距或方向不同。");
        }
        if(o.Sweep is { } sweep)
        {
            if(feature.GetDefinition() is not ISweepFeatureData data || data.TwistControlType!=SweepMode(sweep)
                || sweep.Orientation is SweepOrientation.TwistAlongPath or SweepOrientation.TwistWithConstantNormal && Math.Abs(data.GetTwistAngle()-Radians(sweep.TwistAngleDegrees))>1e-8)
                throw new InvalidOperationException("SWEEP_DEFINITION: 扭曲模式或角度不同。");
        }
        if(o.Surface is { FillBoundaries.Count: >0 } surface)
        {
            var requested=BindFillRequest(model,o,operationObjects??new Dictionary<string,object>());
            FillSurfaceContract.VerifyDeclared(ReadFillDefinition(model,feature),requested,surface.FillResolution,surface.OptimizeFill,
                (a,b)=>SamePersistentIdentity(model,a,b));
        }
    }
}
