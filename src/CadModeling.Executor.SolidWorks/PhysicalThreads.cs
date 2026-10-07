using System.Globalization;
using System.Security.Cryptography;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static IFeature CreatePhysicalThread(IModelDoc2 model,NativeFeatureOptions o,IReadOnlyDictionary<string,object> objects)
    {
        var t=o.PhysicalThread!;PhysicalThreadContract.Validate(t);
        if(!File.Exists(t.ProfilePath)||!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(t.ProfilePath))).Equals(t.ProfileSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PHYSICAL_THREAD_PROFILE：原生牙型文件缺失或身份改变。");
        var edge=(IEdge)ResolveEntities(model,objects,o.Selections.Single()).Single();
        RequirePhysicalThreadEntry(edge,t);
        var data=(IThreadFeatureData)model.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmSweepThread);
        data.InitializeThreadData();data.Type=t.ProfilePath;data.Size=t.Designation;data.Edge=(Edge)edge;
        data.EndCondition=(int)swThreadEndCondition_e.swThreadEndCondition_Blind;data.BlindDepth=Mm(t.LengthMm);
        data.PitchOverride=true;data.Pitch=Mm(t.PitchMm);data.DiameterOverride=false;
        data.ThreadMethod=(int)swThreadMethod_e.swThreadMethod_Cut;data.RightHanded=t.RightHanded;
        data.ReverseDirection=o.Reverse;data.Offset=false;data.MultipleStart=false;data.TrimStartFace=false;data.TrimEndFace=false;
        var feature=model.FeatureManager.CreateFeature(data)??throw new InvalidOperationException("PHYSICAL_THREAD_CREATION：原生实体螺纹工厂未返回特征。");
        ReadPhysicalThreadState(model,feature,o);return feature;
    }
    private static void RequirePhysicalThreadEntry(IEdge edge,PhysicalThreadOptions t)
    {
        if(edge.GetCurve() is not ICurve c || !c.IsCircle())throw new InvalidOperationException("PHYSICAL_THREAD_ENTRY：入口不是圆边。");
        var p=ToDoubles(c.CircleParams,7,"螺纹入口圆");
        var n=Unit(t.AxisIntoPart);
        var entryDiameter=t.Location==PhysicalThreadLocation.Internal?t.BoreDiameterMm!.Value:t.MajorDiameterMm;
        if(Math.Abs(p[6]*1000-entryDiameter/2)>.01
            || PointDistance(new(p[0]*1000,p[1]*1000,p[2]*1000),t.AxisOriginMm)>.01
            || Math.Abs(p[3]*n.X+p[4]*n.Y+p[5]*n.Z)<1-1e-8)
            throw new InvalidOperationException("PHYSICAL_THREAD_ENTRY：实际入口直径、入口中心或轴线不符合声明。");
        if(t.Location==PhysicalThreadLocation.Internal)
        {
            var cylinders=(edge.GetTwoAdjacentFaces2() as object[]??[]).OfType<IFace2>()
                .Where(f=>f.GetSurface() is ISurface s&&s.IsCylinder()).ToArray();
            if(cylinders.Length!=1)throw new InvalidOperationException("PHYSICAL_THREAD_ENTRY：内螺纹入口缺少唯一原生孔壁。");
            var face=cylinders[0];var surface=(ISurface)face.GetSurface();
            var cp=ToDoubles(surface.CylinderParams,7,"内螺纹入口孔壁");
            var uv=ToDoubles(face.GetUVBounds(),4,"内螺纹入口孔壁裁剪");
            var sample=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,(uv[2]+uv[3])/2,0,0),6,"内螺纹入口孔壁法向");
            var axis=Unit(new(cp[3],cp[4],cp[5]));
            var relative=new Vector3(sample[0]-cp[0],sample[1]-cp[1],sample[2]-cp[2]);
            var radial=ModelVerification.Sub(relative,ModelVerification.Scale(axis,ModelVerification.Dot(relative,axis)));
            var normal=new Vector3(sample[3],sample[4],sample[5]);
            if(face.FaceInSurfaceSense())normal=ModelVerification.Scale(normal,-1);
            if(!ModelVerification.Finite(normal)||ModelVerification.Norm(normal)<1e-12||ModelVerification.Norm(radial)<1e-12
                ||Math.Abs(cp[6]*1000-entryDiameter/2)>.01||ModelVerification.Dot(normal,radial)>=0)
                throw new InvalidOperationException("PHYSICAL_THREAD_ENTRY：声明内螺纹的入口实际不是内孔壁。");
        }
    }
    private static IReadOnlyDictionary<string,string> ReadPhysicalThreadState(IModelDoc2 model,IFeature feature,NativeFeatureOptions o)
    {
        var t=o.PhysicalThread!;PhysicalThreadContract.Validate(t);
        if(feature.GetTypeName2()!="SweepThread"||feature.GetDefinition() is not IThreadFeatureData d||!d.AccessSelections((ModelDoc)model,null))
            throw new InvalidOperationException("PHYSICAL_THREAD_DEFINITION：实体牙型定义不可访问。");
        var state=new SortedDictionary<string,string>(StringComparer.Ordinal);
        try
        {
            if(!Path.GetFullPath(d.Type).Equals(Path.GetFullPath(t.ProfilePath),StringComparison.OrdinalIgnoreCase)||d.Size!=t.Designation
                || !d.PitchOverride || Math.Abs(d.Pitch-Mm(t.PitchMm))>1e-10 || d.DiameterOverride
                || Math.Abs(d.BlindDepth-Mm(t.LengthMm))>1e-10 || d.EndCondition!=(int)swThreadEndCondition_e.swThreadEndCondition_Blind
                || d.ThreadMethod!=(int)swThreadMethod_e.swThreadMethod_Cut || d.RightHanded!=t.RightHanded
                || d.ReverseDirection!=o.Reverse || d.Offset || d.MultipleStart || d.TrimStartFace || d.TrimEndFace)
                throw new InvalidOperationException("PHYSICAL_THREAD_DEFINITION：规格、螺距、牙长、旋向或显式控制未保留。");
            RequirePhysicalThreadEntry(d.Edge,t);
            state["thread_entry_identity"]=RequireDefinitionIdentity(model,d.Edge);
            state["thread_profile"]=Path.GetFullPath(d.Type).ToUpperInvariant();state["thread_size"]=d.Size;
            state["thread_pitch"]=d.Pitch.ToString("R",CultureInfo.InvariantCulture);
            state["thread_length"]=d.BlindDepth.ToString("R",CultureInfo.InvariantCulture);
            state["thread_right_handed"]=d.RightHanded.ToString();state["thread_reverse"]=d.ReverseDirection.ToString();
            state["thread_location"]=t.Location.ToString();
            if(t.Location==PhysicalThreadLocation.Internal)
            {
                state["thread_bore_diameter_mm"]=t.BoreDiameterMm!.Value.ToString("R",CultureInfo.InvariantCulture);
                state["thread_maximum_cut_diameter_mm"]=t.MaximumCutDiameterMm!.Value.ToString("R",CultureInfo.InvariantCulture);
            }
        }
        finally{d.ReleaseSelectionAccess();}
        // Inspect the final cut B-Rep after releasing rollback selection access.
        var faces=feature.GetFaces() as object[];
        if(faces is not {Length:>0}||faces.Length>4096||faces.Any(f=>f is not IFace2))throw new InvalidOperationException("PHYSICAL_THREAD_GEOMETRY：原生牙面库存不完整。");
        var edges=new Dictionary<string,IEdge>(StringComparer.Ordinal);
        foreach(var face in faces.Cast<IFace2>())
        {
            if(face.GetEdges() is not object[] inventory || inventory.Length==0 || inventory.Any(e=>e is not IEdge))throw new InvalidOperationException("PHYSICAL_THREAD_GEOMETRY：牙面边界库存不可访问。");
            foreach(var e in inventory.Cast<IEdge>())edges.TryAdd(RequireDefinitionIdentity(model,e),e);
        }
        var samples=new List<IReadOnlyList<Vector3>>();
        int sampleCount=Math.Clamp((int)Math.Ceiling(t.LengthMm/t.PitchMm*16)+1,65,1025);
        foreach(var e in edges.Values)
        {
            var c=(ICurve)e.GetCurve();if(c.IsLine()||c.IsCircle())continue;
            var trim=e.GetCurveParams3()??throw new InvalidOperationException("PHYSICAL_THREAD_GEOMETRY：原生牙边缺少实际裁剪范围。");
            var start=trim.UMinValue;var end=trim.UMaxValue;
            if(!double.IsFinite(start)||!double.IsFinite(end)||end<=start)throw new InvalidOperationException("PHYSICAL_THREAD_GEOMETRY：原生牙边裁剪范围无效。");
            var points=new List<Vector3>();
            for(int i=0;i<sampleCount;i++)
            {var p=ToDoubles(c.Evaluate2(start+(end-start)*i/(sampleCount-1),0),3,"实体螺旋边");points.Add(new(p[0]*1000,p[1]*1000,p[2]*1000));}
            samples.Add(points);
        }
        var receipt=PhysicalThreadContract.VerifyGeometry(t,samples);
        state["thread_geometry_helical_edge_count"]=receipt.HelicalEdgeCount.ToString(CultureInfo.InvariantCulture);
        state["thread_geometry_minimum_radius_mm"]=receipt.MinimumRadiusMm.ToString("R",CultureInfo.InvariantCulture);
        state["thread_geometry_maximum_radius_mm"]=receipt.MaximumRadiusMm.ToString("R",CultureInfo.InvariantCulture);
        return state;
    }
}
