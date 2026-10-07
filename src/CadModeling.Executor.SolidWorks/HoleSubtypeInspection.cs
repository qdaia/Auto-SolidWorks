using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static IReadOnlyList<MeasuredCone> MeasureCones(IModelDoc2 model)
    {
        if(model is not IPartDoc part)return[];
        return (part.GetBodies2(0,false) as object[]??[]).OfType<IBody2>()
            .SelectMany(b=>b.GetFaces() as object[]??[]).OfType<IFace2>()
            .Where(f=>((ISurface)f.GetSurface()).IsCone()).Select(f=>MeasureCone(model,f)).ToArray();
    }

    private static MeasuredCone MeasureCone(IModelDoc2 model,IFace2 face)
    {
        var surface=(ISurface)face.GetSurface();
        if(!surface.IsCone())throw new InvalidOperationException("原生特征不是解析圆锥特征。");
        var p=ToDoubles(surface.ConeParams2,11,"原生圆锥参数");
        var origin=new Vector3(p[0]*1000,p[1]*1000,p[2]*1000);
        var axis=ModelVerification.Unit(new(p[3],p[4],p[5]));
        var uv=ToDoubles(face.GetUVBounds(),4,"修剪圆锥的边界");
        var a=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,uv[2],0,0),6,"圆锥开始");
        var b=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,uv[3],0,0),6,"圆锥端");
        var mid=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,(uv[2]+uv[3])/2,0,0),6,"圆锥的法线");
        Vector3 Point(double[] values)=>new(values[0]*1000,values[1]*1000,values[2]*1000);
        Vector3 OnAxis(double[] values)=>AxisProjection(origin,axis,Point(values));
        var start=OnAxis(a);var end=OnAxis(b);
        var ra=ModelVerification.Norm(ModelVerification.Sub(Point(a),start));
        var rb=ModelVerification.Norm(ModelVerification.Sub(Point(b),end));
        var normal=new Vector3(mid[3],mid[4],mid[5]);
        if(face.FaceInSurfaceSense())normal=ModelVerification.Scale(normal,-1);
        var radial=ModelVerification.Sub(Point(mid),OnAxis(mid));
        var angle=Math.Abs(p[7])*360/Math.PI;
        if(!ModelVerification.Finite(normal)||ModelVerification.Norm(normal)<=1e-12||!double.IsFinite(angle)||angle<=0||angle>=180)
            throw new InvalidOperationException("原生圆锥壁有无效的法向/角度证据。");
        var token=Persistent(model,face);
        var boundary=ConeHasCompleteBoundaries(model,face,start,end,axis,ra,rb,token);
        return new(start,end,axis,ra,rb,angle,ModelVerification.Dot(normal,radial)<0,boundary.Complete,token,(face.GetFeature() as IFeature)?.Name){BoundaryEvidence=boundary.Evidence};
    }

    private static (bool Complete,string Evidence) ConeHasCompleteBoundaries(IModelDoc2 model,IFace2 face,Vector3 start,Vector3 end,Vector3 axis,double ra,double rb,string? faceToken)
    {
        const double tolerance=.001;
        var height=ModelVerification.Norm(ModelVerification.Sub(end,start));
        if(faceToken is null||ra<=0||rb<=0||height<=0||!double.IsFinite(ra+rb+height))return(false,"无效的有限环锥尺寸或面标识。");
        // IFace2.GetArea is approximate. Completeness is established by the native
        // trimmed topology: two full end circles and only same-face periodic seams.
        // Circular support alone is insufficient: a slot leaves circular arcs.
        var atStart=0;var atEnd=0;
        foreach(var edge in (face.GetEdges() as object[]??[]).OfType<IEdge>())
        {
            var curve=(ICurve)edge.GetCurve();
            if(curve.IsCircle())
            {
                var trim=edge.GetCurveParams3();
                var sweep=Math.Abs(trim.UMaxValue-trim.UMinValue);
                if(!double.IsFinite(sweep)||Math.Abs(sweep-2*Math.PI)>1e-8)return(false,$"边界圆被修剪成一段弧线：扫掠 ={sweep:R}rad。");
                var data=ToDoubles(curve.CircleParams,7,"圆锥边界圆周");
                var center=new Vector3(data[0]*1000,data[1]*1000,data[2]*1000);
                var direction=ModelVerification.Unit(new(data[3],data[4],data[5]));
                if(Math.Abs(ModelVerification.Dot(axis,direction))<.999999)return(false,"边界圆具有不同的轴。");
                var first=ModelVerification.Norm(ModelVerification.Sub(center,start))<=tolerance&&Math.Abs(data[6]*1000-ra)<=tolerance;
                var last=ModelVerification.Norm(ModelVerification.Sub(center,end))<=tolerance&&Math.Abs(data[6]*1000-rb)<=tolerance;
                if(!first&&!last)return(false,$"边界圆与修剪端点不符：中心={center}，半径={data[6]*1000:R}mm。");
                if(first)atStart++;if(last)atEnd++;
            }
            else if(curve.IsLine())
            {
                // A true periodic seam has the very same conical face on both sides.
                // Split or slotted walls remain unsupported rather than hiding an outlet.
                var adjacent=(edge.GetTwoAdjacentFaces2() as object[]??[]).OfType<IFace2>().ToArray();
                if(adjacent.Length!=2||adjacent.Any(f=>Persistent(model,f)!=faceToken))return(false,$"线边界不是同一面的周期性缝合：相邻={adjacent.Length}，同一面={adjacent.Count(f=>Persistent(model,f)==faceToken)}。");
            }
            else return(false,"边界曲线既不是解析圆也不是直线。");
        }
        return(atStart==1&&atEnd==1,$"圆周全封闭：起始点为{atStart}，终点为{atEnd}；每隔一个边界是一个同面周期性缝合。");
    }

    private static MeasuredCosmeticThread? ReadCosmeticThread(IModelDoc2 model,IFeature feature)
    {
        if(feature.IsSuppressed()||feature.GetDefinition() is not ICosmeticThreadFeatureData thread)return null;
        var accessed=false;
        try
        {
            accessed=thread.AccessSelections(model,null);
            if(!accessed)throw new InvalidOperationException("原生装饰螺纹选择访问失败。");
            var edge=(IEdge?)thread.Edge??throw new InvalidOperationException("原生装饰螺纹没有入口边线。");
            var curve=(ICurve)edge.GetCurve();
            if(!curve.IsCircle())throw new InvalidOperationException("装饰螺纹入口不是圆形的。");
            var data=ToDoubles(curve.CircleParams,7,"螺纹入口圆");
            var token=Persistent(model,edge);
            var diameter=thread.Diameter*1000;var depth=thread.BlindDepth*1000;
            var through=thread.ApplyThread==(int)swCosmeticThreadType_e.swApplyCosmeticThread_ThroughFeature;
            var blind=thread.ApplyThread==(int)swCosmeticThreadType_e.swApplyCosmeticThread_Blind;
            var complete=token is not null&&thread.DiameterType==(int)swCosmeticThreadDiameterType_e.swCosmeticThread_MajorDiameter&&
                double.IsFinite(diameter)&&diameter>0&&!string.IsNullOrWhiteSpace(thread.ThreadCallout)&&(through||blind&&double.IsFinite(depth)&&depth>0);
            return new(feature.Name,token,new(data[0]*1000,data[1]*1000,data[2]*1000),ModelVerification.Unit(new(data[3],data[4],data[5])),
                data[6]*1000,diameter,thread.ThreadCallout,through,through?0:depth,complete);
        }
        finally{if(accessed)thread.ReleaseSelectionAccess();}
    }
}
