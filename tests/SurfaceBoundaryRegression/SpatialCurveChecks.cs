using System.Reflection;
using System.Text.Json;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;

internal static class SpatialCurveChecks
{
    public static Task<int> Run(string output)
    {
        var done=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{done.SetResult(OnSta(Path.GetFullPath(output)));}catch(Exception ex){done.SetException(ex);}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
    private static int OnSta(string output)
    {
        Directory.CreateDirectory(output);int passed=0;
        var kernel=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor",true)!;
        var app=(ISldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)!;
        foreach(var kind in new[]{SpatialCurveKind.Polyline,SpatialCurveKind.InterpolatingSpline})
        foreach(var zoom in new[]{.01,100.0})
        {
            var id=kind+"-"+zoom.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var path=Path.Combine(output,id+".SLDPRT");if(File.Exists(path))throw new IOException("output already exists");
            IModelDoc2? model=(IModelDoc2)app.NewDocument(@"C:\Users\csj17\Documents\SOLIDWORKS\Templates\MMGS_Clean_2025.prtdot",0,0,0);
            var options=new SpatialCurveOptions{CurveKind=kind,PointsMm=[new(1,2,3),new(1.05,2.02,3.03),new(1.1,2.04,3.06)]};
            try
            {
                var view=(IModelView)model.ActiveView;view.Scale2*=zoom;
                var previousAdd=model.SketchManager.AddToDB;var previousDisplay=model.SketchManager.DisplayWhenAdded;
                var feature=(IFeature)kernel.GetMethod("CreateSpatialCurve",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[model,options])!;
                feature.Name="坐标曲线";
                if(model.SketchManager.AddToDB!=previousAdd||model.SketchManager.DisplayWhenAdded!=previousDisplay)throw new Exception("sketch preferences leaked");
                Verify(feature,options);
                int errors=0,warnings=0;if(!model.Extension.SaveAs(path,0,1,null,ref errors,ref warnings)||errors!=0)throw new IOException("save failed "+errors);
                app.CloseDoc(model.GetTitle());model=null;
                model=(IModelDoc2)app.OpenDoc6(path,1,3,"",ref errors,ref warnings);if(model is null||errors!=0)throw new IOException("reopen failed");
                feature=model.IFirstFeature();while(feature is not null&&feature.Name!="坐标曲线")feature=feature.IGetNextFeature();
                if(feature is null)throw new Exception("saved spatial curve missing");Verify(feature,options);
                File.WriteAllText(Path.Combine(output,id+"-result.json"),JsonSerializer.Serialize(new{passed=true,kind,zoom,options,scope="native_spatial_curve_coordinates_and_preferences_saved_reopen"},ModelingIrJson.Options));
                passed++;Console.WriteLine("PASS: native saved "+id+" coordinates and restored preferences");
            }
            finally{if(model is not null)app.CloseDoc(model.GetTitle());}
        }
        File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{passed,scope="native_spatial_curve_saved_reopen"}));return 0;
    }
    private static void Verify(IFeature feature,SpatialCurveOptions options)
    {
        var sketch=(ISketch)feature.GetSpecificFeature2();if(!sketch.Is3D())throw new Exception("not native 3D sketch");
        var segments=((object[])sketch.GetSketchSegments()).Cast<ISketchSegment>().Where(s=>!s.ConstructionGeometry).ToArray();
        if(options.CurveKind==SpatialCurveKind.InterpolatingSpline)
        {
            if(segments.Length!=1||segments[0] is not ISketchSpline spline)throw new Exception("wrong spline inventory");
            var points=((object[])spline.GetPoints2()).Cast<ISketchPoint>().Select(p=>new Vector3(p.X*1000,p.Y*1000,p.Z*1000)).ToArray();
            if(points.Length!=options.PointsMm.Count||points.Where((p,i)=>Distance(p,options.PointsMm[i])>1e-6).Any())throw new Exception("spline input points changed");
        }
        else
        {
            if(segments.Length!=options.PointsMm.Count-1)throw new Exception("wrong polyline inventory");
            var pairs=segments.Select(s=>{var line=(ISketchLine)s;Vector3 Point(ISketchPoint p)=>new(p.X*1000,p.Y*1000,p.Z*1000);return (A:Point((ISketchPoint)line.GetStartPoint2()),B:Point((ISketchPoint)line.GetEndPoint2()));}).ToList();
            for(int i=1;i<options.PointsMm.Count;i++)
            {
                var a=options.PointsMm[i-1];var b=options.PointsMm[i];int index=pairs.FindIndex(p=>Math.Max(Distance(p.A,a),Distance(p.B,b))<=1e-6||Math.Max(Distance(p.B,a),Distance(p.A,b))<=1e-6);
                if(index<0)throw new Exception("polyline coordinates changed");pairs.RemoveAt(index);
            }
        }
    }
    private static double Distance(Vector3 a,Vector3 b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2)+Math.Pow(a.Z-b.Z,2));
}
