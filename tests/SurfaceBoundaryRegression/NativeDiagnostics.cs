using System.Reflection;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;

internal static class NativeDiagnostics
{
    public static Task<int> Run(string output,string input)
    {
        var completion=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{completion.SetResult(RunOnSta(output,input));}catch(Exception e){completion.SetException(e);}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
    private static int RunOnSta(string output,string input)
    {
        Directory.CreateDirectory(output);
        var draft=JsonSerializer.Deserialize<GenericModelDraft>(File.ReadAllText(input),ModelingIrJson.Options)!;
        var plan=new GenericPlanCompiler().Compile(draft).Plan!;
        var kernel=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor",true)!;
        object Invoke(string name,params object[] parameters)=>kernel.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,parameters)!;
        var app=(ISldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)!;
        var math=(IMathUtility)app.GetMathUtility();
        var reports=new List<object>();
        foreach(var mode in new[]{"legacy-direction","legacy-roles"})
        {
            var model=(IModelDoc2)app.NewDocument(@"C:\Users\csj17\Documents\SOLIDWORKS\Templates\MMGS_Clean_2025.prtdot",0,0,0);
            try
            {
                var objects=new Dictionary<string,object>();
                foreach(var operation in plan.Operations.Take(plan.Operations.Count-1))
                {
                    var result=operation is ProfileSketchOperation sketch?Invoke("ExecuteSketch",model,sketch,math,objects):Invoke("ExecuteNativeFeature",model,operation,objects,math);
                    ((IFeature)result).Name=operation.Id;objects[operation.Id]=result;
                }
                var surface=((NativeFeatureOperation)plan.Operations.Last()).Options;
                var curves=new List<object>();
                foreach(var pair in objects)
                {
                    var sketch=(ISketch)((IFeature)pair.Value).GetSpecificFeature2();
                    var segments=((object[])sketch.GetSketchSegments()).Cast<ISketchSegment>();
                    foreach(var segment in segments)
                    {
                        var curve=(ICurve)segment.GetCurve();curve.GetEndParams(out var start,out var end,out var closed,out var periodic);
                        curves.Add(new{id=pair.Key,is3d=sketch.Is3D(),start=curve.Evaluate2(start,0),end=curve.Evaluate2(end,0),transform=sketch.ModelToSketchTransform.ArrayData,closed,periodic});
                    }
                }
                model.ClearSelection2(true);int count=0;
                for(int direction=0;direction<2;direction++)
                {
                    var ids=direction==0?surface.ProfileIds:surface.GuideIds;
                    for(int i=0;i<ids.Count;i++)
                    {
                        var feature=(IFeature)objects[ids[i]];
                        int mark=mode.EndsWith("direction",StringComparison.Ordinal)?direction+1:BoundarySurfaceContract.SelectionMark(direction,i,ids.Count);
                        bool selected;
                        if(mode.StartsWith("point-",StringComparison.Ordinal))
                        {
                            var sketch=(ISketch)feature.GetSpecificFeature2();
                            var segment=(ISketchSegment)((object[])sketch.GetSketchSegments())[0];var curve=(ICurve)segment.GetCurve();
                            curve.GetEndParams(out var start,out _,out _,out _);
                            var data=((double[])curve.Evaluate2(start,0)).Take(3).ToArray();
                            var point=(IMathPoint)math.CreatePoint(data);if(!sketch.Is3D())point=(IMathPoint)point.MultiplyTransform(sketch.ModelToSketchTransform.IInverse());
                            var p=(double[])point.ArrayData;
                            selected=model.Extension.SelectByID2(feature.Name,"SKETCH",p[0],p[1],p[2],count>0,mark,null,0);
                        }
                        else if(mode=="name-roles")selected=model.Extension.SelectByID2(feature.Name,"SKETCH",0,0,0,count>0,mark,null,0);
                        else if(mode=="segment-roles")
                        {
                            var data=model.ISelectionManager.CreateSelectData();data.Mark=mark;
                            selected=((ISketchSegment)((object[])((ISketch)feature.GetSpecificFeature2()).GetSketchSegments())[0]).Select4(count>0,data);
                        }
                        else selected=feature.Select2(count>0,mark);
                        if(!selected)throw new InvalidOperationException("selection failed");count++;
                    }
                }
                object Selections()=>Enumerable.Range(1,model.ISelectionManager.GetSelectedObjectCount2(-1)).Select(i=>new{mark=model.ISelectionManager.GetSelectedObjectMark(i),type=model.ISelectionManager.GetSelectedObjectType3(i,-1)}).ToArray();
                var before=Selections();
                File.WriteAllText(Path.Combine(output,mode+"-inputs.json"),JsonSerializer.Serialize(new{curves,before},ModelingIrJson.Options));
                for(short dir=0;dir<2;dir++)
                {
                    var ids=dir==0?surface.ProfileIds:surface.GuideIds;
                    for(short i=0;i<ids.Count;i++)model.FeatureManager.SetNetBlendCurveData(dir,i,0,0,1,true);
                    model.FeatureManager.SetNetBlendDirectionData(dir,32,0,false,false);
                }
                var after=Selections();
                var resultFeature=model.FeatureManager.InsertNetBlend(2,(short)surface.ProfileIds.Count,(short)surface.GuideIds.Count,false,.0001,false,false,true,true,false,-1,-1,false,-1,false,false,-1,false,-1,true);
                reports.Add(new{mode,curves,before,after,success=resultFeature is not null,featureType=resultFeature?.GetTypeName2()});
                File.WriteAllText(Path.Combine(output,"diagnostics.json"),JsonSerializer.Serialize(reports,ModelingIrJson.Options));
                Console.WriteLine(mode+" "+(resultFeature?.GetTypeName2()??"null"));
            }
            finally{try{app.CloseDoc(model.GetTitle());}catch(Exception e){File.WriteAllText(Path.Combine(output,mode+"-cleanup-error.txt"),e.ToString());}}
        }
        File.WriteAllText(Path.Combine(output,"diagnostics.json"),JsonSerializer.Serialize(reports,ModelingIrJson.Options));return 0;
    }
}
