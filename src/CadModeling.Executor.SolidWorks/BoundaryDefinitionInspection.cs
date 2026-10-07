using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static BoundaryDefinitionRequest BindBoundaryRequest(IModelDoc2 model,NativeFeatureOptions options,IReadOnlyDictionary<string,object> objects)
    {
        BoundaryCurveBinding Bind(string id)
        {
            var feature=ResolveFeature(model,objects,id);var reference=RequireDefinitionIdentity(model,feature);
            var aliases=new List<string>{reference};var segments=new List<string>();
            if(feature.GetSpecificFeature2() is ISketch sketch)
            {
                var sketchIdentity=RequireDefinitionIdentity(model,sketch);
                if(!aliases.Any(a=>SamePersistentIdentity(model,a,sketchIdentity)))aliases.Add(sketchIdentity);
                var inventory=sketch.GetSketchSegments() as object[];
                if(inventory is null || inventory.Length>4096 || inventory.Any(s=>s is not ISketchSegment))
                    throw new InvalidOperationException("BOUNDARY_SOURCE_INCOMPLETE：来源草图的完整段库存不可访问。");
                segments.AddRange(inventory.Cast<ISketchSegment>().Where(s=>!s.ConstructionGeometry).Select(s=>RequireDefinitionIdentity(model,s)));
                if(segments.Count==0)throw new InvalidOperationException("BOUNDARY_SOURCE_INCOMPLETE：来源草图没有非构造曲线。");
            }
            else if(feature.GetDefinition() is not IHelixFeatureData)
                throw new InvalidOperationException("BOUNDARY_SOURCE_INVALID：来源必须是原生曲线草图或螺旋线。");
            return new(id,reference,aliases,segments);
        }
        var surface=options.Surface??new();
        return new(options.ProfileIds.Select(Bind).ToArray(),options.GuideIds.Select(Bind).ToArray(),surface.StartCondition,surface.EndCondition,options.Merge);
    }

    private static BoundaryDefinitionSnapshot ReadBoundaryDefinition(IModelDoc2 model,IFeature feature)
    {
        var output=feature.GetFaces() as object[];
        bool surfaceOnly=output is {Length:>0} && output.All(f=>f is IFace2 face && face.GetBody() is IBody2 body && body.GetType()==(int)swBodyType_e.swSheetBody);
        var featureIdentity=RequireDefinitionIdentity(model,feature);
        if(feature.GetDefinition() is not IBoundaryBossFeatureData data || !data.AccessSelections(model,null))
            throw new InvalidOperationException("BOUNDARY_DEFINITION_UNVERIFIABLE：无法访问原生曲线族及控制定义。");
        try
        {
            var directions=new List<BoundaryDirectionState>();
            for(int direction=0;direction<2;direction++)
            {
                var curves=(direction==0?data.D1Curves:data.D2Curves) as object[]??[];
                int nativeDirection=direction==0?(int)swBoundaryBossDirection_e.swBoundaryBossDirection_First:(int)swBoundaryBossDirection_e.swBoundaryBossDirection_Second;
                var rows=new List<BoundaryCurveState>();
                foreach(var curve in curves)
                {
                    var representation=curve is IFeature?BoundaryCurveRepresentation.Feature:curve is ISketch?BoundaryCurveRepresentation.Sketch:
                        curve is ISketchSegment?BoundaryCurveRepresentation.SingleSketchSegment:(BoundaryCurveRepresentation)(-1);
                    string? owner=null;int? segmentCount=null;
                    if(curve is ISketchSegment segment && segment.GetSketch() is ISketch sketch)
                    {
                        owner=RequireDefinitionIdentity(model,sketch);
                        var inventory=sketch.GetSketchSegments() as object[];
                        if(inventory is not null && inventory.Length<=4096 && inventory.All(x=>x is ISketchSegment))
                            segmentCount=inventory.Cast<ISketchSegment>().Count(x=>!x.ConstructionGeometry);
                    }
                    int index=rows.Count;var tangency=data.GetGuideTangencyType(nativeDirection,index);
                    // None has no valid tangent length getter; multi-direction networks
                    // have no valid GetTangentApplyToAll. Do not query unavailable controls.
                    bool active=tangency!=(int)swBoundaryBossTangencyType_e.swBoundaryBossTangency_None;
                    rows.Add(new(RequireDefinitionIdentity(model,curve),representation,owner,segmentCount,tangency,
                        data.GetDraftAngle(nativeDirection,index),data.GetDraftAngleReverseDirection(nativeDirection,index),
                        active?data.GetTangentLength(nativeDirection,index):null,
                        active?data.GetTangentDirectionReversed(nativeDirection,index):null,
                        active && direction==0 && ((data.D2Curves as object[]??[]).Length==0)?data.GetTangentApplyToAll(nativeDirection,index):null));
                }
                directions.Add(new(curves.Length==data.GetCurvesCount(nativeDirection),direction==0?data.D1CurveInfluence:data.D2CurveInfluence,
                    direction==0 && (data.D2Curves as object[]??[]).Length>0?data.TrimByD1:null,rows));
            }
            return new(featureIdentity,surfaceOnly,directions);
        }
        finally{data.ReleaseSelectionAccess();}
    }

    private sealed class NativeBoundarySurfaceSession(IModelDoc2 model,IReadOnlyDictionary<string,object> objects,BoundaryDefinitionRequest request) : IBoundarySurfaceSession
    {
        public IFeature? Feature {get;private set;}
        public void ClearSelection()=>model.ClearSelection2(true);
        public bool Select(string id,int mark,bool append)=>ResolveFeature(model,objects,id).Select2(append,mark);
        public IReadOnlyList<BoundarySelection> CaptureSelection()
        {
            var selection=model.ISelectionManager;var rows=new List<BoundarySelection>();
            for(int i=1;i<=selection.GetSelectedObjectCount2(-1);i++)
                rows.Add(new(RequireDefinitionIdentity(model,selection.GetSelectedObject6(i,-1)),selection.GetSelectedObjectMark(i)));
            return rows;
        }
        // Both SetNetBlend* helpers return Feature, not a bool success signal.
        // Their controls are certified against the resulting retained definition.
        public void SetCurveData(short direction,short index,short tangency,double draft,double length,bool applyAll)=>
            model.FeatureManager.SetNetBlendCurveData(direction,index,tangency,draft,length,applyAll);
        public void SetDirectionData(short direction,int influence,short trim,bool closed,bool split)=>
            model.FeatureManager.SetNetBlendDirectionData(direction,checked((short)influence),trim,closed,split);
        public bool Create(BoundaryCreationParameters p)
        {
            if(BoundaryNeedsNormal(request))RequireBoundaryNormalSources(model,request);
            // The static native kernel does not retain an application reference.
            // Activation attaches to the existing application; verify its active
            // document identity before using its command state as evidence.
            var app=(ISldWorks)(Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)
                ??throw new InvalidOperationException("BOUNDARY_NATIVE_CREATION_UNVERIFIABLE：无法取得原生应用。"));
            if(app.IActiveDoc2 is not { } active || app.IsSame(active,model)!=(int)swObjectEquality.swObjectSame)
                throw new InvalidOperationException("BOUNDARY_NATIVE_CREATION_UNVERIFIABLE：活动文档不是本次工厂的文档。");
            app.GetRunningCommandInfo(out _,out _,out var uiActive);
            if(uiActive)throw new InvalidOperationException("BOUNDARY_NATIVE_CREATION_UNVERIFIABLE：存在活动原生命令，不能绑定本次工厂结果。");
            var before=Inventory();
            var returned=model.FeatureManager.InsertNetBlend2(p.Type,p.ProfileCount,p.GuideCount,p.HasCenterline,p.TessellationFactor,p.WantsSolid,
                p.Merge,p.FeatureScope,p.AutoSelect,p.Thin,0,0,false,0,false,false,0,false,0,p.ForceNonRational,p.CreateSolid);
            var after=Inventory();
            app.GetRunningCommandInfo(out _,out _,out uiActive);
            if(uiActive)throw new InvalidOperationException("BOUNDARY_NATIVE_CREATION_UNVERIFIABLE：工厂遗留活动原生命令，结果不可验收。");
            var identity=BoundarySurfaceContract.ResolveCreatedFeature(before.State,after.State,
                returned is null?null:RequireDefinitionIdentity(model,returned),(a,b)=>SamePersistentIdentity(model,a,b));
            Feature=after.Features.Single(f=>SamePersistentIdentity(model,RequireDefinitionIdentity(model,f),identity));
            if(BoundaryNeedsNormal(request))ApplyBoundaryNormalControls(model,Feature,request);
            return true;
        }
        private (BoundaryFeatureInventory State,IFeature[] Features) Inventory()
        {
            var features=new List<IFeature>();var rows=new List<BoundaryCreatedFeature>();int count=0;
            for(var f=model.IFirstFeature();f is not null;f=f.IGetNextFeature())
            {
                if(++count>4096)throw new InvalidOperationException("BOUNDARY_NATIVE_CREATION_UNVERIFIABLE：原生特征库存超限或循环。");
                var type=f.GetTypeName2();
                if(string.IsNullOrWhiteSpace(type))throw new InvalidOperationException("BOUNDARY_NATIVE_CREATION_UNVERIFIABLE：特征类型库存不可读取。");
                if(type!="NetBlend")continue;
                var error=f.GetErrorCode2(out var warning);
                rows.Add(new(RequireDefinitionIdentity(model,f),error,warning,f.IsSuppressed()));features.Add(f);
            }
            return (new(true,rows),features.ToArray());
        }
        public BoundaryDefinitionSnapshot CaptureDefinition()=>ReadBoundaryDefinition(model,Feature??throw new InvalidOperationException("BOUNDARY_NATIVE_CREATION：没有待读取的曲面特征。"));
    }
}
