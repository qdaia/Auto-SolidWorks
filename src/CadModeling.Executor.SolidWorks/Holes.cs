using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    // Hole geometry uses ordinary named sketches/cuts. Tapped holes add a native cosmetic-thread annotation.
    // DiameterMm is the explicitly supplied drill diameter, never inferred from an unrecognized thread string.
    private static object ExecuteHole(IModelDoc2 model,NativeFeatureOperation operation,IReadOnlyDictionary<string,object> sourceObjects,IMathUtility math)
    {
        var o=operation.Options; var objects=new Dictionary<string,object>(sourceObjects);
        var frame=o.Frame ?? new SketchFrame(); var (u,v,n)=FrameBasis(frame);
        object MakeProfile(string id,double diameter,SketchFrame location,IReadOnlyList<ProfilePoint> centers)
        {
            var sketch=new ProfileSketchOperation { Id=id,Name=id,Frame=location,
                Primitives=centers.Select(p=>(ProfilePrimitive)new CircleProfile { CenterXmm=p.Xmm,CenterYmm=p.Ymm,DiameterMm=diameter }).ToArray() };
            return objects[id]=ExecuteSketch(model,sketch,math,objects);
        }
        var profileId=operation.Name+"_钻孔轮廓草图";
        MakeProfile(profileId,o.DiameterMm,frame,o.HoleCenters);
        object result=ExecuteCutExtrude(model,new ExtrudeCutOperation { Id=operation.Id,Name=operation.Name,SketchId=profileId,
            DepthMm=o.DepthMm,ReverseDirection=o.Reverse,EndCondition=o.ThroughAll?ExtrudeEndCondition.ThroughAll:ExtrudeEndCondition.Blind },objects);
        if(o.HoleKind==HoleKind.Counterbore)
        {
            var counterId=operation.Name+"_柱形沉孔轮廓草图"; MakeProfile(counterId,o.CounterboreDiameterMm,frame,o.HoleCenters);
            result=ExecuteCutExtrude(model,new ExtrudeCutOperation { Id=operation.Id,Name=operation.Name+"_柱形沉孔",SketchId=counterId,
                DepthMm=o.CounterboreDepthMm,ReverseDirection=o.Reverse },objects);
        }
        if(o.HoleKind==HoleKind.Countersink)
        {
            var depth=(o.CountersinkDiameterMm-o.DiameterMm)/2/Math.Tan(Radians(o.CountersinkAngleDegrees/2));
            var sign=o.Reverse?-1:1;
            for(var i=0;i<o.HoleCenters.Count;i++)
            {
                var topId=operation.Name+"_锥形沉孔上轮廓草图"+i; var bottomId=operation.Name+"_锥形沉孔下轮廓草图"+i;
                MakeProfile(topId,o.CountersinkDiameterMm,frame,[o.HoleCenters[i]]);
                var p=frame.OriginMm;
                MakeProfile(bottomId,o.DiameterMm,frame with { OriginMm=new(p.X+sign*n.X*depth,p.Y+sign*n.Y*depth,p.Z+sign*n.Z*depth) },[o.HoleCenters[i]]);
                result=ExecuteNativeFeature(model,new NativeFeatureOperation { Id=operation.Id,Name=operation.Name+"_锥形沉孔"+i,
                    Options=new() { Kind=NativeFeatureKind.LoftCut,ProfileIds=[topId,bottomId] } },objects,math);
            }
        }
        if(o.HoleKind==HoleKind.Tapped)
        {
            // Query only the drilled feature's faces/edges. Scanning the whole shaft for each hole
            // is expensive and risks selecting an unrelated circular edge with the same radius.
            objects[operation.Id]=result;
            var p=frame.OriginMm;
            for(var i=0;i<o.HoleCenters.Count;i++)
            {
                var c=o.HoleCenters[i]; var x=c.Xmm+o.DiameterMm/2; var y=c.Ymm;
                try
                {
                    SelectQueries(model,objects,[new() { Kind=EntityKind.Edge,FeatureId=operation.Id,
                        Geometry=GeometryKind.Circle,RadiusMm=o.DiameterMm/2,
                        Direction=n,PositionMm=new(p.X+u.X*x+v.X*y,p.Y+u.Y*x+v.Y*y,p.Z+u.Z*x+v.Z*y) }]);
                }
                catch(InvalidOperationException ex)
                {
                    throw new InvalidOperationException($"已攻丝的孔{i+1}在 '{operation.Name}' 中没有唯一解析的圆形入口边。" +
                        "曲线或开放的入口可能不支持原生的装饰螺纹。未应用螺纹替换。" +
                        "如果用户模型可能使用名义螺纹表示，则显式重建一个简单钻孔 '" +
                        "带有名称中的螺纹规格，并在假设中记录这种表示方式。",ex);
                }
                var thread=model.FeatureManager.InsertCosmeticThread2((short)(o.ThroughAll && o.ThreadDepthMm is null?swCosmeticThreadType_e.swApplyCosmeticThread_ThroughFeature:swCosmeticThreadType_e.swApplyCosmeticThread_Blind),
                    Mm(o.ThreadMajorDiameterMm),Mm(o.ThreadDepthMm??o.DepthMm),o.ThreadDesignation!)
                    ?? throw new InvalidOperationException("原生装饰螺纹创建失败。");
                thread.Name=operation.Name+"_装饰螺纹"+i;
            }
        }
        return result;
    }
}
