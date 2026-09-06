using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    [ThreadStatic] private static SketchFrame? _activeFrame;

    private static double Radians(double degrees) => degrees * Math.PI / 180;
    private static Vector3 Unit(Vector3 v)
    {
        var n = Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z);
        if (n < 1e-12) throw new ArgumentException("Zero direction vector.");
        return new(v.X/n,v.Y/n,v.Z/n);
    }
    private static (Vector3 U, Vector3 V, Vector3 N) FrameBasis(SketchFrame f)
    {
        var n=Unit(f.Normal); var x=f.XDirection;
        var dot=x.X*n.X+x.Y*n.Y+x.Z*n.Z;
        var u=Unit(new(x.X-dot*n.X,x.Y-dot*n.Y,x.Z-dot*n.Z));
        return (u,new(n.Y*u.Z-n.Z*u.Y,n.Z*u.X-n.X*u.Z,n.X*u.Y-n.Y*u.X),n);
    }
    private static IFeature CreateFramePlane(IModelDoc2 model, SketchFrame frame, string name)
    {
        model.ClearSelection2(true);
        var (u,v,_) = FrameBasis(frame); var p=frame.OriginMm;
        var created=model.CreatePlaneFixed2(new[]{Mm(p.X),Mm(p.Y),Mm(p.Z)},
            new[]{Mm(p.X+u.X*10),Mm(p.Y+u.Y*10),Mm(p.Z+u.Z*10)},
            new[]{Mm(p.X+v.X*10),Mm(p.Y+v.Y*10),Mm(p.Z+v.Z*10)},true);
        if (created is null) throw new InvalidOperationException("SolidWorks could not create the specified reference plane.");
        var feature=created as IFeature ?? model.IFeatureByPositionReverse(0)
            ?? throw new InvalidOperationException("Reference plane feature missing.");
        feature.Name=name; return feature;
    }
    private static IFeature ResolveFeature(IModelDoc2 model, IReadOnlyDictionary<string,object> objects, string id)
    {
        if(objects.TryGetValue(id,out var value) && value is IFeature f) return f;
        for (var candidate = model.IFirstFeature(); candidate is not null; candidate = candidate.IGetNextFeature())
            if (candidate.Name.Equals(id, StringComparison.OrdinalIgnoreCase)) return candidate;
        throw new InvalidOperationException($"Feature '{id}' was not found.");
    }
    private static void SelectFeature(IModelDoc2 model, IFeature f, bool append, int mark)
    {
        if(!f.Select2(append,mark)) throw new InvalidOperationException($"Cannot select feature '{f.Name}'.");
    }
    private static object ExecuteNativeFeature(IModelDoc2 model, NativeFeatureOperation operation, IReadOnlyDictionary<string,object> objects, IMathUtility mathUtility)
    {
        var o=operation.Options; var fm=model.FeatureManager; object? result;
        var previousFeatureId=model.IFeatureByPositionReverse(0)?.GetID();
        model.ClearSelection2(true);
        switch(o.Kind)
        {
            case NativeFeatureKind.Hole:
                return ExecuteHole(model,operation,objects,mathUtility);
            case NativeFeatureKind.Flatten:
                var flatPatterns=new List<IFeature>();
                for(var f=model.IFirstFeature();f is not null;f=f.IGetNextFeature())
                    if(f.GetTypeName2()=="FlatPattern") flatPatterns.Add(f);
                if(flatPatterns.Count==0) throw new InvalidOperationException("No flat-pattern feature exists in this model.");
                foreach(var flat in flatPatterns)
                    if(!flat.SetSuppression2((int)(o.Flattened?swFeatureSuppressionAction_e.swUnSuppressFeature:swFeatureSuppressionAction_e.swSuppressFeature),
                        (int)swInConfigurationOpts_e.swThisConfiguration,null)) throw new InvalidOperationException("Cannot change flat-pattern state.");
                if(!model.ForceRebuild3(false)) throw new InvalidOperationException("Sheet metal did not rebuild after flatten/unflatten.");
                return flatPatterns[0];
            case NativeFeatureKind.ThinExtrude:
                SelectFeature(model,ResolveFeature(model,objects,o.SketchId!),false,0);
                result=fm.FeatureExtrusionThin2(true,false,o.Reverse,0,0,Mm(o.DepthMm),0,false,false,false,false,0,0,false,false,false,false,
                    o.Merge,Mm(o.ThicknessMm),0,0,0,0,false,0,false,true,0,0,false); break;
            case NativeFeatureKind.Rib:
                SelectFeature(model,ResolveFeature(model,objects,o.SketchId!),false,0);
                fm.InsertRib(true,false,Mm(o.ThicknessMm),0,o.Reverse,false,false,0,false,false); result=model.IFeatureByPositionReverse(0); break;
            case NativeFeatureKind.SheetMetalBase:
                SelectFeature(model,ResolveFeature(model,objects,o.SketchId!),false,0);
                model.EditSketch();
                var allowance=fm.CreateCustomBendAllowance(); allowance.Type=(int)swBendAllowanceTypes_e.swBendAllowanceKFactor; allowance.KFactor=o.KFactor;
                var existingBodies=((IPartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody,false) as object[] ?? [];
                result=fm.InsertSheetMetalBaseFlange2(Mm(o.ThicknessMm),o.Reverse,Mm(o.RadiusMm),Mm(o.DepthMm>0?o.DepthMm:20),0.01,false,0,0,1,
                    allowance,false,0,0.0001,0.0001,0.5,true,o.Merge && existingBodies.Length>0,true,true); break;
            case NativeFeatureKind.EdgeFlange:
                var flangeEdges=o.Selections.SelectMany(s=>ResolveEntities(model,objects,s)).Cast<IEdge>().ToArray();
                var flangeSketches=new List<System.Runtime.InteropServices.DispatchWrapper>();
                foreach(var flangeEdge in flangeEdges)
                {
                    model.ClearSelection2(true);
                    if(!((IEntity)flangeEdge).Select4(false,null)) throw new InvalidOperationException("Cannot select the flange edge.");
                    var flangeSketchResult=model.InsertSketchForEdgeFlange(flangeEdge,Radians(o.AngleDegrees),o.Reverse);
                    var flangeSketch=flangeSketchResult as ISketch ?? (flangeSketchResult as IFeature)?.GetSpecificFeature2() as ISketch
                        ?? model.IGetActiveSketch2() as ISketch ?? throw new InvalidOperationException("Could not create edge-flange sketch.");
                    if(model.IGetActiveSketch2() is null)
                    {
                        var sketchFeature=flangeSketchResult as IFeature ?? model.IFeatureByPositionReverse(0);
                        SelectFeature(model,sketchFeature,false,0); model.EditSketch();
                    }
                    var flangeSegments=flangeSketch.GetSketchSegments() as object[] ?? [];
                    if(flangeSegments.Length==0)
                    {
                        model.ClearSelection2(true);
                        if(!((IEntity)flangeEdge).Select4(false,null) || !model.SketchManager.SketchUseEdge3(false,false))
                            throw new InvalidOperationException("Could not project the flange edge into the profile sketch.");
                        flangeSegments=flangeSketch.GetSketchSegments() as object[] ?? [];
                    }
                    var flangeLine=flangeSegments.OfType<ISketchLine>().Single();
                    var start=(ISketchPoint)flangeLine.GetStartPoint2(); var end=(ISketchPoint)flangeLine.GetEndPoint2();
                    var length=Mm(o.DistanceMm);
                    model.SketchManager.CreateLine(start.X,start.Y,0,start.X,start.Y+length,0);
                    model.SketchManager.CreateLine(start.X,start.Y+length,0,end.X,end.Y+length,0);
                    model.SketchManager.CreateLine(end.X,end.Y+length,0,end.X,end.Y,0);
                    model.SketchManager.InsertSketch(true);
                    flangeSketches.Add(new(flangeSketch));
                }
                result=fm.InsertSheetMetalEdgeFlange2(flangeEdges.Select(e=>new System.Runtime.InteropServices.DispatchWrapper(e)).ToArray(),flangeSketches.ToArray(),
                    (int)(swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRadius|swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRelief),
                    Radians(o.AngleDegrees),0,(int)swFlangePositionTypes_e.swFlangePositionTypeBendOutside,Mm(o.DistanceMm),
                    (int)swSheetMetalReliefTypes_e.swSheetMetalReliefNone,0,0,0,(int)swFlangeDimTypes_e.swFlangeDimTypeInnerVirtualSharp,null); break;
            case NativeFeatureKind.SurfaceExtrude:
                SelectFeature(model,ResolveFeature(model,objects,o.SketchId!),false,0);
                fm.FeatureExtruRefSurface3(true,o.Reverse,0,0,0,0,Mm(o.DepthMm),0,false,false,false,false,0,0,false,false,false,false,false,false,false,false); result=model.IFeatureByPositionReverse(0); break;
            case NativeFeatureKind.SurfacePlanar:
                SelectFeature(model,ResolveFeature(model,objects,o.SketchId!),false,0);
                model.InsertPlanarRefSurface(); result=model.IFeatureByPositionReverse(0); break;
            case NativeFeatureKind.SurfaceLoft:
                for(var i=0;i<o.ProfileIds.Count;i++) SelectFeature(model,ResolveFeature(model,objects,o.ProfileIds[i]),i>0,1);
                foreach(var id in o.GuideIds) SelectFeature(model,ResolveFeature(model,objects,id),true,2);
                model.InsertLoftRefSurface2(false,true,false,1,0,0); result=model.IFeatureByPositionReverse(0); break;
            case NativeFeatureKind.SurfaceKnit:
                SelectQueries(model,objects,o.Selections.Select(s=>s with { SelectionMark=1 }).ToArray());
                result=fm.InsertSewRefSurface(false,o.TryToFormSolid,o.Merge,Mm(o.DistanceMm>0?o.DistanceMm:0.01),0); break;
            case NativeFeatureKind.SurfaceTrim:
                var keepQueries=o.Selections.Where(s=>s.SelectionMark==2).ToArray();
                var toolQueries=o.Selections.Where(s=>s.SelectionMark!=2).Select(s=>s with { SelectionMark=0 }).ToArray();
                if(toolQueries.Length==0 || keepQueries.Length==0 || keepQueries.Any(s=>s.Kind!=EntityKind.Body || s.PositionMm is null))
                    throw new ArgumentException("SurfaceTrim requires trimming tools and mark-2 surface bodies with a point inside each region to keep.");
                var keepBodies=keepQueries.Select(keep=>(Query:keep,Bodies:ResolveEntities(model,objects,keep).Cast<IBody2>().ToArray())).ToArray();
                foreach(var keep in keepBodies)
                    if(keep.Bodies.Any(b=>b.GetType()!=(int)swBodyType_e.swSheetBody))
                        throw new ArgumentException("SurfaceTrim keep selections must be surface bodies.");
                SelectQueries(model,objects,toolQueries);
                if(!fm.PreTrimSurface(false,true,false,false)) throw new InvalidOperationException("Surface trim tools did not divide the target surface.");
                foreach(var keep in keepBodies)
                {
                    foreach(var body in keep.Bodies)
                    {
                        var pieces=fm.GetPreTrimmedBodies((Body2)body) as object[] ?? [];
                        var matching=pieces.Cast<IBody2>().Where(piece=>MatchesEntity(piece,keep.Query with { Name=null })).ToArray();
                        if(matching.Length!=1) throw new InvalidOperationException($"Surface region point must identify one trimmed piece; matched {matching.Length} of {pieces.Length}.");
                        var point=keep.Query.PositionMm!; var select=model.ISelectionManager.CreateSelectData();
                        select.X=Mm(point.X); select.Y=Mm(point.Y); select.Z=Mm(point.Z);
                        if(!matching[0].Select2(true,select)) throw new InvalidOperationException("Could not select the requested surface region to keep.");
                    }
                }
                result=fm.PostTrimSurface(true); break;
            case NativeFeatureKind.Thicken:
                SelectQueries(model,objects,o.Selections.Select(s=>s with { SelectionMark=1 }).ToArray());
                result=fm.FeatureBossThicken(Mm(o.ThicknessMm),o.Reverse?1:0,0,false,o.Merge,false,true); break;
            case NativeFeatureKind.SketchPattern:
                SelectQueries(model,objects,o.Selections);
                SelectFeature(model,ResolveFeature(model,objects,o.SketchId!),true,1);
                result=fm.FeatureSketchDrivenPattern(true,false); break;
            case NativeFeatureKind.Split:
                SelectQueries(model,objects,o.Selections);
                var splitBodies=fm.PreSplitBody2() as object[] ?? [];
                if(splitBodies.Length<2) throw new InvalidOperationException("Split tools did not produce multiple bodies.");
                model.ClearSelection2(true);
                result=fm.PostSplitBody2(splitBodies.Select(b=>new System.Runtime.InteropServices.DispatchWrapper(b)).ToArray(),false,
                    Enumerable.Range(0,splitBodies.Length).Select(_=>new System.Runtime.InteropServices.DispatchWrapper(null)).ToArray(),
                    Enumerable.Repeat("",splitBodies.Length).ToArray(),""); break;
            case NativeFeatureKind.WeldmentMember:
                var pathSketch=(ISketch)ResolveFeature(model,objects,o.PathSketchId!).GetSpecificFeature2();
                var group=fm.CreateStructuralMemberGroup();
                group.Segments=(pathSketch.GetSketchSegments() as object[] ?? []).Cast<ISketchSegment>().Where(s=>!s.ConstructionGeometry)
                    .Select(s=>new System.Runtime.InteropServices.DispatchWrapper(s)).ToArray();
                group.Angle=Radians(o.AngleDegrees==360?0:o.AngleDegrees);
                group.ApplyCornerTreatment=true; group.CornerTreatmentType=1;
                result=fm.InsertStructuralWeldment5(o.ProfilePath!,1,false,
                    new[]{new System.Runtime.InteropServices.DispatchWrapper(group)},o.ProfileConfiguration ?? ""); break;
            case NativeFeatureKind.TrimWeldment:
                var trimBodies=o.Selections.Where(s=>s.SelectionMark!=2).SelectMany(s=>ResolveEntities(model,objects,s))
                    .Select(e=>new System.Runtime.InteropServices.DispatchWrapper(e)).ToArray();
                var trimTools=o.Selections.Where(s=>s.SelectionMark==2).SelectMany(s=>ResolveEntities(model,objects,s))
                    .Select(e=>new System.Runtime.InteropServices.DispatchWrapper(e)).ToArray();
                if(trimBodies.Length==0 || trimTools.Length==0) throw new ArgumentException("Weldment trim requires bodies to trim and mark-2 trimming tools.");
                result=fm.InsertWeldmentTrimFeature2((int)o.WeldmentEndCondition,8,Mm(o.DistanceMm),trimBodies,trimTools); break;
            case NativeFeatureKind.SetDimension:
                var dimension=(IDimension?)model.Parameter(o.DimensionName ?? throw new ArgumentException("dimension_name is required."))
                    ?? throw new InvalidOperationException($"Dimension '{o.DimensionName}' was not found.");
                var status=dimension.SetSystemValue3(o.DimensionIsAngle?Radians(o.DimensionValue):Mm(o.DimensionValue),(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null);
                if(status!=0) throw new InvalidOperationException($"Dimension update failed (status={status}).");
                if(!model.ForceRebuild3(false)) throw new InvalidOperationException("Model did not rebuild after dimension update.");
                return ResolveFeature(model,objects,o.DimensionName!.Split('@')[1]);
            case NativeFeatureKind.Suppress: case NativeFeatureKind.Restore:
                var targets=o.Selections.SelectMany(s=>ResolveEntities(model,objects,s)).Cast<IFeature>().ToArray();
                if(targets.Length==0) throw new ArgumentException("Select at least one feature to suppress/restore.");
                foreach(var target in targets)
                    if(!target.SetSuppression2((int)(o.Kind==NativeFeatureKind.Suppress?swFeatureSuppressionAction_e.swSuppressFeature:swFeatureSuppressionAction_e.swUnSuppressFeature),
                        (int)swInConfigurationOpts_e.swThisConfiguration,null)) throw new InvalidOperationException($"Could not change suppression of {target.Name}.");
                return targets[0];
            case NativeFeatureKind.ReferencePlane:
                return CreateFramePlane(model,o.Frame!,operation.Name);
            case NativeFeatureKind.ReferenceAxis:
                var a=o.AxisStartMm!; var b=o.AxisEndMm!;
                model.SketchManager.Insert3DSketch(true);
                var segment=model.SketchManager.CreateLine(Mm(a.X),Mm(a.Y),Mm(a.Z),Mm(b.X),Mm(b.Y),Mm(b.Z))
                    ?? throw new InvalidOperationException("Cannot create axis construction line.");
                segment.ConstructionGeometry=true;
                model.SketchManager.Insert3DSketch(true);
                model.ClearSelection2(true);
                if(!segment.Select4(false,null) || !model.InsertAxis2(true)) throw new InvalidOperationException("Cannot create reference axis.");
                result=model.IFeatureByPositionReverse(0); break;
            case NativeFeatureKind.Chamfer:
                SelectQueries(model,objects,o.Selections);
                var mode=o.ChamferMode switch { ChamferMode.DistanceAngle=>1, ChamferMode.TwoDistances=>2, _=>16 };
                result=fm.InsertFeatureChamfer(o.TangentPropagation ? 1 : 0,mode,Mm(o.DistanceMm),Radians(o.AngleDegrees),Mm(o.SecondDistanceMm),0,0,0); break;
            case NativeFeatureKind.Fillet:
                SelectQueries(model,objects,o.Selections);
                var faceMode=o.Selections.Any(s=>s.Kind==EntityKind.Face);
                result=fm.FeatureFillet3(2|(o.TangentPropagation?1:0),Mm(o.RadiusMm),0,0,faceMode?2:0,0,0,null,null,null,null,null,null,null); break;
            case NativeFeatureKind.RevolveBoss: case NativeFeatureKind.RevolveCut:
                SelectFeature(model,ResolveFeature(model,objects,o.SketchId!),false,0);
                SelectFeature(model,ResolveFeature(model,objects,o.AxisId!),true,16);
                result=fm.FeatureRevolve2(true,true,o.ThicknessMm>0,o.Kind==NativeFeatureKind.RevolveCut,o.Reverse,false,
                    0,0,Radians(o.AngleDegrees),0,false,false,0,0,0,Mm(o.ThicknessMm),0,o.Merge,false,true); break;
            case NativeFeatureKind.Shell:
                SelectQueries(model,objects,o.Selections);
                model.InsertFeatureShell(Mm(o.ThicknessMm),o.Reverse);
                result=model.IFeatureByPositionReverse(0); break;
            case NativeFeatureKind.Draft:
                SelectQueries(model,objects,o.Selections);
                result=fm.InsertMultiFaceDraft(Radians(o.AngleDegrees),o.Reverse,false,0,false,false); break;
            case NativeFeatureKind.MoveBody:
                SelectQueries(model,objects,o.Selections.Select(s=>s with { SelectionMark=1 }).ToArray());
                var hasTranslation=o.TranslationMm.X!=0 || o.TranslationMm.Y!=0 || o.TranslationMm.Z!=0;
                var hasRotation=o.RotationDegrees.X!=0 || o.RotationDegrees.Y!=0 || o.RotationDegrees.Z!=0;
                if(hasTranslation && hasRotation)
                {
                    var translated=fm.InsertMoveCopyBody2(Mm(o.TranslationMm.X),Mm(o.TranslationMm.Y),Mm(o.TranslationMm.Z),0,
                        0,0,0,0,0,0,o.Copy,o.Copy?o.Count:1) ?? throw new InvalidOperationException("Body translation failed.");
                    translated.Name=operation.Name+"_Translation";
                    model.ClearSelection2(true);
                    var movedBodies=(translated.GetFaces() as object[] ?? []).Cast<IFace2>().Select(f=>(IBody2)f.GetBody()).Distinct().ToArray();
                    if(movedBodies.Length==0) throw new InvalidOperationException("Cannot resolve translated bodies for rotation.");
                    for(var i=0;i<movedBodies.Length;i++)
                    { var data=model.ISelectionManager.CreateSelectData(); data.Mark=1; if(!movedBodies[i].Select2(i>0,data)) throw new InvalidOperationException("Cannot select translated body."); }
                    result=fm.InsertMoveCopyBody2(0,0,0,0,0,0,0,Radians(o.RotationDegrees.X),Radians(o.RotationDegrees.Y),Radians(o.RotationDegrees.Z),false,1);
                    SetBodyRotation(model,(IFeature?)result,o.RotationDegrees);
                    break;
                }
                result=fm.InsertMoveCopyBody2(Mm(o.TranslationMm.X),Mm(o.TranslationMm.Y),Mm(o.TranslationMm.Z),0,
                    0,0,0,Radians(o.RotationDegrees.X),Radians(o.RotationDegrees.Y),Radians(o.RotationDegrees.Z),o.Copy,o.Copy?o.Count:1);
                if(hasRotation) SetBodyRotation(model,(IFeature?)result,o.RotationDegrees);
                break;
            case NativeFeatureKind.Combine:
                var selectedBodies=o.Selections.SelectMany(s=>ResolveEntities(model,objects,s)).Cast<IBody2>().Distinct().ToArray();
                if(selectedBodies.Length<2) throw new ArgumentException("Combine requires at least two different bodies; the first is the subtraction target.");
                for(var i=0;i<selectedBodies.Length;i++)
                {
                    var data=model.ISelectionManager.CreateSelectData(); data.Mark=o.BooleanMode==BooleanMode.Subtract && i==0 ? 1 : 2;
                    if(!selectedBodies[i].Select2(i>0,data)) throw new InvalidOperationException("Could not select body for Combine.");
                }
                result=fm.InsertCombineFeature(o.BooleanMode switch {BooleanMode.Union=>15903,BooleanMode.Subtract=>15902,_=>15901},
                    null,Array.Empty<object>()); break;
            case NativeFeatureKind.Mirror:
                SelectQueries(model,objects,o.Selections);
                result=fm.InsertMirrorFeature2(o.Selections.Any(s=>s.Kind==EntityKind.Body),true,o.Merge,false,0); break;
            case NativeFeatureKind.LinearPattern:
                SelectQueries(model,objects,o.Selections);
                result=fm.FeatureLinearPattern5(o.Count,Mm(o.SpacingMm),1,0,o.Reverse,false,"","",true,false,
                    false,false,true,true,false,false,false,false,0,0,false,false); break;
            case NativeFeatureKind.CircularPattern:
                SelectQueries(model,objects,o.Selections);
                result=fm.FeatureCircularPattern5(o.Count,Radians(o.AngleDegrees),o.Reverse,"",true,true,false,false,false,false,1,0,"",false); break;
            case NativeFeatureKind.LoftBoss: case NativeFeatureKind.LoftCut:
                var append=false;
                foreach(var id in o.ProfileIds) { SelectFeature(model,ResolveFeature(model,objects,id),append,1); append=true; }
                foreach(var id in o.GuideIds) SelectFeature(model,ResolveFeature(model,objects,id),true,2);
                result=o.Kind==NativeFeatureKind.LoftBoss
                    ? fm.InsertProtrusionBlend2(false,true,false,1,0,0,0,0,false,false,o.ThicknessMm>0,Mm(o.ThicknessMm),0,0,o.Merge,false,true,0)
                    : fm.InsertCutBlend(false,true,false,1,0,0,o.ThicknessMm>0,Mm(o.ThicknessMm),0,0,false,true); break;
            case NativeFeatureKind.SweepBoss: case NativeFeatureKind.SweepCut:
                SelectFeature(model,ResolveFeature(model,objects,o.SketchId!),false,1);
                SelectFeature(model,ResolveFeature(model,objects,o.PathSketchId!),true,4);
                foreach(var id in o.GuideIds) SelectFeature(model,ResolveFeature(model,objects,id),true,2);
                result=o.Kind==NativeFeatureKind.SweepBoss
                    ? fm.InsertProtrusionSwept4(false,false,0,true,true,0,0,o.ThicknessMm>0,Mm(o.ThicknessMm),0,0,0,o.Merge,false,true,0,true,false,0,0)
                    : fm.InsertCutSwept5(false,false,0,true,true,0,0,o.ThicknessMm>0,Mm(o.ThicknessMm),0,0,0,false,true,0,true,false,false,false,false,0,0); break;
            default: throw new NotSupportedException($"Native feature {o.Kind} is not implemented yet.");
        }
        if(result is null) throw new InvalidOperationException($"SolidWorks returned no feature for {o.Kind}.");
        var feature=result as IFeature ?? model.IFeatureByPositionReverse(0)
            ?? throw new InvalidOperationException($"No feature after {o.Kind}.");
        if(feature.GetID()==previousFeatureId) throw new InvalidOperationException($"SolidWorks created no new feature for {o.Kind}.");
        feature.Name=operation.Name; return feature;
    }
    private static void SetBodyRotation(IModelDoc2 model,IFeature? feature,Vector3 rotation)
    {
        if(feature?.GetDefinition() is not IMoveCopyBodyFeatureData data || !data.AccessSelections((ModelDoc2)model,null))
            throw new InvalidOperationException("Cannot access body rotation definition.");
        data.TransformType=(int)swMoveCopyBodyFeatureTransformType_e.swTransformType_Rotation;
        data.RotationOriginX=0;data.RotationOriginY=0;data.RotationOriginZ=0;
        data.TransformX=Radians(rotation.X);data.TransformY=Radians(rotation.Y);data.TransformZ=Radians(rotation.Z);
        if(!feature.ModifyDefinition(data,model,null)) { data.ReleaseSelectionAccess();throw new InvalidOperationException("Could not set body XYZ rotation."); }
    }
    private static void SelectQueries(IModelDoc2 model,IReadOnlyDictionary<string,object> objects,IReadOnlyList<EntityQuery> queries)
    {
        var append=false;
        foreach(var query in queries)
        foreach(var entity in ResolveEntities(model,objects,query))
        {
            var data=model.ISelectionManager.CreateSelectData(); data.Mark=query.SelectionMark;
            if(query.PositionMm is { } position) { data.X=Mm(position.X); data.Y=Mm(position.Y); data.Z=Mm(position.Z); }
            bool selected=entity switch { IFeature f=>f.Select2(append,query.SelectionMark), IBody2 b=>b.Select2(append,data),
                IEntity e=>e.Select4(append,data), _=>false };
            if(!selected) throw new InvalidOperationException($"Could not select matched {query.Kind}.");
            append=true;
        }
    }
    private static IReadOnlyList<object> ResolveEntities(IModelDoc2 model,IReadOnlyDictionary<string,object> objects,EntityQuery query)
    {
        object? persisted=null;
        if(query.PersistentReference is { } encoded)
        {
            int state=0;
            persisted=model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(encoded),out state);
            if(state!=0) persisted=null;
        }
        if(query.Kind is EntityKind.Feature or EntityKind.Plane or EntityKind.Axis)
        {
            var f=query.FeatureId??query.Name;
            var selected=f is not null?ResolveFeature(model,objects,f):persisted as IFeature
                ??throw new ArgumentException("Feature selection requires feature_id, name or a valid persistent reference.");
            if(query.Kind==EntityKind.Plane && selected.GetTypeName2()!="RefPlane" || query.Kind==EntityKind.Axis && selected.GetTypeName2()!="RefAxis")
                throw new InvalidOperationException("Selected feature is not the requested reference plane or axis.");
            return new object[]{selected};
        }
        var bodies=((IPartDoc)model).GetBodies2(-1,false) as object[] ?? [];
        IEnumerable<object> candidates;
        if(query.FeatureId is { } id)
        {
            var feature=ResolveFeature(model,objects,id);
            var faces=feature.GetFaces() as object[] ?? [];
            candidates=query.Kind switch { EntityKind.Face=>faces,
                EntityKind.Edge=>faces.Cast<IFace2>().SelectMany(f=>f.GetEdges() as object[] ?? []).Distinct(),
                EntityKind.Body=>faces.Cast<IFace2>().Select(f=>(object)f.GetBody()).Distinct(), _=>[] };
        }
        else candidates=query.Kind switch { EntityKind.Body=>bodies,
            EntityKind.Face=>bodies.Cast<IBody2>().SelectMany(b=>b.GetFaces() as object[] ?? []),
            EntityKind.Edge=>bodies.Cast<IBody2>().SelectMany(b=>b.GetEdges() as object[] ?? []),_=>[] };
        var matches=candidates.Where(e=>MatchesEntity(e,query)).ToArray();
        if(persisted is not null && MatchesEntity(persisted,query) && matches.Any(e=>Persistent(model,e)==query.PersistentReference)) return new[]{persisted};
        if(matches.Length==0) throw new InvalidOperationException($"No {query.Geometry} {query.Kind} matches the requested geometry.");
        if(matches.Length>1 && !query.AllMatches) throw new InvalidOperationException($"Entity selection is ambiguous ({matches.Length} matches); add position/direction/radius or explicitly select all matches.");
        return matches;
    }
    private static bool MatchesEntity(object entity,EntityQuery query)
    {
        if(query.Kind==EntityKind.Face && entity is not IFace2 || query.Kind==EntityKind.Edge && entity is not IEdge || query.Kind==EntityKind.Body && entity is not IBody2) return false;
        double[]? parameters=null; double[]? closest=null; Vector3? direction=null; double? radius=null;
        if(entity is IFace2 face)
        {
            var surface=(ISurface)face.GetSurface();
            var kind=surface.IsPlane()?GeometryKind.Plane:surface.IsCylinder()?GeometryKind.Cylinder:
                surface.IsCone()?GeometryKind.Cone:surface.IsSphere()?GeometryKind.Sphere:surface.IsTorus()?GeometryKind.Torus:GeometryKind.Any;
            if(query.Geometry!=GeometryKind.Any && query.Geometry!=kind) return false;
            if(kind==GeometryKind.Plane) { parameters=(double[])surface.PlaneParams; direction=new(parameters[0],parameters[1],parameters[2]); }
            if(kind==GeometryKind.Cylinder) { parameters=(double[])surface.CylinderParams; direction=new(parameters[3],parameters[4],parameters[5]); radius=parameters[6]*1000; }
            if(query.PositionMm is { } p) closest=(double[])face.GetClosestPointOn(Mm(p.X),Mm(p.Y),Mm(p.Z));
        }
        else if(entity is IEdge edge)
        {
            var curve=(ICurve)edge.GetCurve();
            var kind=curve.IsCircle()?GeometryKind.Circle:curve.IsLine()?GeometryKind.Line:GeometryKind.Any;
            if(query.Geometry!=GeometryKind.Any && query.Geometry!=kind) return false;
            if(kind==GeometryKind.Circle) { parameters=(double[])curve.CircleParams; direction=new(parameters[3],parameters[4],parameters[5]); radius=parameters[6]*1000; }
            if(kind==GeometryKind.Line) { parameters=(double[])curve.LineParams; direction=new(parameters[3],parameters[4],parameters[5]); }
            if(query.PositionMm is { } p) closest=(double[])edge.GetClosestPointOn(Mm(p.X),Mm(p.Y),Mm(p.Z));
        }
        else if(entity is IBody2 body)
        {
            if(query.Name is not null && body.Name!=query.Name) return false;
            if(query.PositionMm is { } p)
            {
                var box=(double[])body.GetBodyBox(); var tol=Mm(query.ToleranceMm);
                if(Mm(p.X)<box[0]-tol || Mm(p.X)>box[3]+tol || Mm(p.Y)<box[1]-tol || Mm(p.Y)>box[4]+tol || Mm(p.Z)<box[2]-tol || Mm(p.Z)>box[5]+tol) return false;
                if(body.GetType()==(int)swBodyType_e.swSheetBody && !(body.GetFaces() as object[]??[]).Cast<IFace2>().Any(face=>
                {
                    var q=(double[])face.GetClosestPointOn(Mm(p.X),Mm(p.Y),Mm(p.Z));
                    return Math.Sqrt(Math.Pow(q[0]-Mm(p.X),2)+Math.Pow(q[1]-Mm(p.Y),2)+Math.Pow(q[2]-Mm(p.Z),2))<=tol;
                })) return false;
            }
        }
        if(query.RadiusMm is { } r && (radius is null || Math.Abs(radius.Value-r)>query.ToleranceMm)) return false;
        if(query.Direction is { } n)
        {
            if(direction is null) return false; var a=Unit(n); var b=Unit(direction);
            if(Math.Abs(a.X*b.X+a.Y*b.Y+a.Z*b.Z)<1-1e-6) return false;
        }
        if(query.PositionMm is { } pos && entity is not IBody2)
        {
            if(closest is null) return false;
            var distance=Math.Sqrt(Math.Pow(closest[0]*1000-pos.X,2)+Math.Pow(closest[1]*1000-pos.Y,2)+Math.Pow(closest[2]*1000-pos.Z,2));
            if(distance>query.ToleranceMm) return false;
        }
        return true;
    }
    private static object CreateEllipse(IModelDoc2 model,IMathUtility math,MathTransform transform,ReferencePlane plane,EllipseProfile e)
    {
        var c=PointInSketch(math,transform,plane,e.CenterXmm,e.CenterYmm);
        var a=PointInSketch(math,transform,plane,e.CenterXmm+e.MajorRadiusMm,e.CenterYmm);
        var b=PointInSketch(math,transform,plane,e.CenterXmm,e.CenterYmm+e.MinorRadiusMm);
        return model.SketchManager.CreateEllipse(c.X,c.Y,c.Z,a.X,a.Y,a.Z,b.X,b.Y,b.Z)
            ?? throw new InvalidOperationException("Ellipse creation failed.");
    }
    private static object CreateOpenCurve(IModelDoc2 model,IMathUtility math,MathTransform transform,ReferencePlane plane,OpenCurveProfile profile)
    {
        foreach(var curve in profile.Curves)
        {
            var a=PointInSketch(math,transform,plane,curve.Start.Xmm,curve.Start.Ymm);
            var b=PointInSketch(math,transform,plane,curve.End.Xmm,curve.End.Ymm);
            var segment=curve is ThreePointArcProfileCurve arc
                ? (ISketchSegment)CreateThreePointArc(model.SketchManager,math,transform,plane,a,b,arc)
                : model.SketchManager.CreateLine(a.X,a.Y,a.Z,b.X,b.Y,b.Z);
            if(segment is null) throw new InvalidOperationException("Open sketch segment creation failed.");
            segment.ConstructionGeometry=profile.Construction;
        }
        return model.IGetActiveSketch2();
    }
    private static object CreateSpline(IModelDoc2 model,IMathUtility math,MathTransform transform,ReferencePlane plane,SplineProfile profile)
    {
        var points=profile.Points.ToList();
        if(profile.Closed && points[0]!=points[^1]) points.Add(points[0]);
        var coords=points.SelectMany(p=>{var q=PointInSketch(math,transform,plane,p.Xmm,p.Ymm);return new[]{q.X,q.Y,q.Z};}).ToArray();
        object status;
        return model.SketchManager.CreateSpline3(coords,null,null,false,out status)
            ?? throw new InvalidOperationException("Spline creation failed.");
    }
}
