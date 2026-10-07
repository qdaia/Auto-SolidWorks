using CadModeling.Ir;
using CadModeling.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    [ThreadStatic] private static SketchFrame? _activeFrame;

    private static double Radians(double degrees) => degrees * Math.PI / 180;
    private static Vector3 Unit(Vector3 v)
    {
        var n = Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z);
        if (!double.IsFinite(n) || n < 1e-12) throw new ArgumentException("方向向量必须是有限且非零的。");
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
        if (created is null) throw new InvalidOperationException("SolidWorks 无法创建指定的基准面。");
        var feature=created as IFeature ?? model.IFeatureByPositionReverse(0)
            ?? throw new InvalidOperationException("参考平面特征缺失。");
        feature.Name=name; return feature;
    }
    private static IFeature ResolveFeature(IModelDoc2 model, IReadOnlyDictionary<string,object> objects, string id)
    {
        if(objects.TryGetValue(id,out var value) && value is IFeature f) return f;
        for (var candidate = model.IFirstFeature(); candidate is not null; candidate = candidate.IGetNextFeature())
            if (candidate.Name.Equals(id, StringComparison.OrdinalIgnoreCase)) return candidate;
        throw new InvalidOperationException($"特征 '{id}' 未找到。");
    }
    private static void SelectFeature(IModelDoc2 model, IFeature f, bool append, int mark)
    {
        if(!f.Select2(append,mark)) throw new InvalidOperationException($"无法选择特征 '{f.Name}'。");
    }
    private static object ExecuteNativeFeature(IModelDoc2 model, NativeFeatureOperation operation, IReadOnlyDictionary<string,object> objects, IMathUtility mathUtility)
    {
        var o=operation.Options; var fm=model.FeatureManager; object? result;
        var previousFeatureId=model.IFeatureByPositionReverse(0)?.GetID();
        model.ClearSelection2(true);
        switch(o.Kind)
        {
            case NativeFeatureKind.SpatialCurve:
                result=CreateSpatialCurve(model,o.SpatialCurve!); break;
            case NativeFeatureKind.Helix:
                result=CreateHelix(model,o,objects); break;
            case NativeFeatureKind.PhysicalThread:
                result=CreatePhysicalThread(model,o,objects); break;
            case NativeFeatureKind.SurfaceBoundary:
            case NativeFeatureKind.SurfaceFill:
            case NativeFeatureKind.SurfaceSweep:
            case NativeFeatureKind.SurfaceOffset:
                result = ExecuteSurfaceFeature(model, operation, objects); break;
            case NativeFeatureKind.Hole:
                return ExecuteHole(model,operation,objects,mathUtility);
            case NativeFeatureKind.Flatten:
                var flatPatterns=new List<IFeature>();
                for(var f=model.IFirstFeature();f is not null;f=f.IGetNextFeature())
                    if(f.GetTypeName2()=="FlatPattern") flatPatterns.Add(f);
                if(flatPatterns.Count==0) throw new InvalidOperationException("该模型中不存在平坦阵列特征。");
                foreach(var flat in flatPatterns)
                    if(!flat.SetSuppression2((int)(o.Flattened?swFeatureSuppressionAction_e.swUnSuppressFeature:swFeatureSuppressionAction_e.swSuppressFeature),
                        (int)swInConfigurationOpts_e.swThisConfiguration,null)) throw new InvalidOperationException("无法更改平铺模式状态。");
                if(!model.ForceRebuild3(false)) throw new InvalidOperationException("钣金在展平/折叠后无法重建。");
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
                    if(!((IEntity)flangeEdge).Select4(false,null)) throw new InvalidOperationException("无法选择翼边。");
                    var flangeSketchResult=model.InsertSketchForEdgeFlange(flangeEdge,Radians(o.AngleDegrees),o.Reverse);
                    var flangeSketch=flangeSketchResult as ISketch ?? (flangeSketchResult as IFeature)?.GetSpecificFeature2() as ISketch
                        ?? model.IGetActiveSketch2() as ISketch ?? throw new InvalidOperationException("无法创建边-法兰草图。");
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
                            throw new InvalidOperationException("无法将法兰边线投影到轮廓草图中。");
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
                SelectLoftCenterline(model,o,objects);
                model.InsertLoftRefSurface2(o.Loft?.Close??false,o.Loft?.MaintainTangency??o.TangentPropagation,false,1,(short)(o.Loft?.StartCondition??SurfaceEndCondition.None),(short)(o.Loft?.EndCondition??SurfaceEndCondition.None)); result=model.IFeatureByPositionReverse(0); break;
            case NativeFeatureKind.SurfaceKnit:
                SelectQueries(model,objects,o.Selections.Select(s=>s with { SelectionMark=1 }).ToArray());
                result=fm.InsertSewRefSurface(false,o.TryToFormSolid,o.Merge,Mm(o.DistanceMm>0?o.DistanceMm:0.01),0); break;
            case NativeFeatureKind.SurfaceTrim:
                var keepQueries=o.Selections.Where(s=>s.SelectionMark==2).ToArray();
                var toolQueries=o.Selections.Where(s=>s.SelectionMark!=2).Select(s=>s with { SelectionMark=0 }).ToArray();
                if(toolQueries.Length==0 || keepQueries.Length==0 || keepQueries.Any(s=>s.Kind!=EntityKind.Body || s.PositionMm is null))
                    throw new ArgumentException("SurfaceTrim 需要修剪工具，并且在每个区域内标记一个点以保持 2 曲面体。");
                var keepBodies=keepQueries.Select(keep=>(Query:keep,Bodies:ResolveEntities(model,objects,keep).Cast<IBody2>().ToArray())).ToArray();
                foreach(var keep in keepBodies)
                    if(keep.Bodies.Any(b=>b.GetType()!=(int)swBodyType_e.swSheetBody))
                        throw new ArgumentException("SurfaceTrim 选择必须保持为曲面体。");
                SelectQueries(model,objects,toolQueries);
                if(!fm.PreTrimSurface(false,true,false,false)) throw new InvalidOperationException("曲面修剪工具没有分割目标曲面。");
                var trimTargets=new List<IBody2>();
                var trimPoints=new List<Vector3>();
                foreach(var keep in keepBodies)
                {
                    foreach(var body in keep.Bodies)
                    {
                        var pieces=fm.GetPreTrimmedBodies((Body2)body) as object[] ?? [];
                        var matching=pieces.Cast<IBody2>().Where(piece=>TrimRegionContainsPoint(piece,keep.Query.PositionMm!,keep.Query.ToleranceMm)).ToArray();
                        if(matching.Length!=1) throw new InvalidOperationException($"曲面区域点必须标识一个修剪的部分；匹配{matching.Length}的{pieces.Length}。");
                        trimTargets.Add(body);
                        trimPoints.Add(keep.Query.PositionMm!);
                    }
                }
                result=CommitVerifiedTrimPoints(model,trimTargets.ToArray(),trimPoints.ToArray(),o.Merge); break;
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
                if(splitBodies.Length<2) throw new InvalidOperationException("分割工具未能生成多个体。");
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
                if(trimBodies.Length==0 || trimTools.Length==0) throw new ArgumentException("切割焊件需要修剪的体和标记-2修剪工具。");
                result=fm.InsertWeldmentTrimFeature2((int)o.WeldmentEndCondition,8,Mm(o.DistanceMm),trimBodies,trimTools); break;
            case NativeFeatureKind.SetDimension:
                var dimension=(IDimension?)model.Parameter(o.DimensionName ?? throw new ArgumentException("需要 dimension_name。"))
                    ?? throw new InvalidOperationException($"未找到尺寸 '{o.DimensionName}'。");
                var parameterKind=(swDimensionParamType_e)dimension.GetType() switch
                {
                    swDimensionParamType_e.swDimensionParamTypeDoubleLinear=>NativeDimensionParameterKind.Length,
                    swDimensionParamType_e.swDimensionParamTypeDoubleAngular=>NativeDimensionParameterKind.Angle,
                    swDimensionParamType_e.swDimensionParamTypeInteger=>NativeDimensionParameterKind.Integer,
                    _=>throw new InvalidOperationException("不支持此原生参数类型；未修改模型。")
                };
                var systemValue=NativeDimensionValues.ToSystemValue(o.DimensionValue,parameterKind,o.DimensionUnit,o.DimensionIsAngle);
                var status=dimension.SetSystemValue3(systemValue,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null);
                if(status!=0) throw new InvalidOperationException($"更新尺寸失败 (状态 ={status}).");
                if(!model.ForceRebuild3(false)) throw new InvalidOperationException("模型在尺寸更新后未能重建。");
                if(!NativeDimensionValues.Matches(dimension.GetSystemValue2(""),systemValue,parameterKind))
                    throw new InvalidOperationException("DIMENSION_READBACK_MISMATCH: 重建后参数未保留请求值。");
                return ResolveFeature(model,objects,o.DimensionName!.Split('@')[1]);
            case NativeFeatureKind.Suppress: case NativeFeatureKind.Restore:
                var targets=o.Selections.SelectMany(s=>ResolveEntities(model,objects,s)).Cast<IFeature>().ToArray();
                if(targets.Length==0) throw new ArgumentException("选择至少一个特征以抑制/恢复。");
                foreach(var target in targets)
                    if(!target.SetSuppression2((int)(o.Kind==NativeFeatureKind.Suppress?swFeatureSuppressionAction_e.swSuppressFeature:swFeatureSuppressionAction_e.swUnSuppressFeature),
                        (int)swInConfigurationOpts_e.swThisConfiguration,null)) throw new InvalidOperationException($"无法更改{target.Name}的抑制。");
                return targets[0];
            case NativeFeatureKind.ReferencePlane:
                return CreateFramePlane(model,o.Frame!,operation.Name);
            case NativeFeatureKind.ReferenceAxis:
                var a=o.AxisStartMm!; var b=o.AxisEndMm!;
                model.SketchManager.Insert3DSketch(true);
                var segment=model.SketchManager.CreateLine(Mm(a.X),Mm(a.Y),Mm(a.Z),Mm(b.X),Mm(b.Y),Mm(b.Z))
                    ?? throw new InvalidOperationException("无法创建轴向构造线。");
                segment.ConstructionGeometry=true;
                model.SketchManager.Insert3DSketch(true);
                model.ClearSelection2(true);
                if(!segment.Select4(false,null) || !model.InsertAxis2(true)) throw new InvalidOperationException("无法创建基准轴。");
                result=model.IFeatureByPositionReverse(0); break;
            case NativeFeatureKind.Chamfer:
                SelectQueries(model,objects,o.Selections);
                // EqualDistance is a modifier of DistanceDistance, not a standalone chamfer type.
                var mode=o.ChamferMode switch {
                    ChamferMode.DistanceAngle=>(int)swChamferType_e.swChamferAngleDistance,
                    ChamferMode.TwoDistances=>(int)swChamferType_e.swChamferDistanceDistance,
                    _=>(int)swChamferType_e.swChamferDistanceDistance|(int)swChamferType_e.swChamferEqualDistance };
                var chamferOptions=(o.TangentPropagation?(int)swFeatureChamferOption_e.swFeatureChamferTangentPropagation:0)|
                    (o.Reverse?(int)swFeatureChamferOption_e.swFeatureChamferFlipDirection:0);
                result=fm.InsertFeatureChamfer(chamferOptions,mode,Mm(o.DistanceMm),
                    o.ChamferMode==ChamferMode.DistanceAngle?Radians(o.AngleDegrees):0,
                    Mm(o.ChamferMode==ChamferMode.EqualDistance?o.DistanceMm:o.SecondDistanceMm),0,0,0); break;
            case NativeFeatureKind.Fillet:
                if(o.VariableFillet is not null){result=CreateVariableFillet(model,o,objects);break;}
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
                        0,0,0,0,0,0,o.Copy,o.Copy?o.Count:1) ?? throw new InvalidOperationException("体移动失败。");
                    translated.Name=operation.Name+"_平移";
                    model.ClearSelection2(true);
                    var movedBodies=(translated.GetFaces() as object[] ?? []).Cast<IFace2>().Select(f=>(IBody2)f.GetBody()).Distinct().ToArray();
                    if(movedBodies.Length==0) throw new InvalidOperationException("无法为旋转操作解析到变形体。");
                    for(var i=0;i<movedBodies.Length;i++)
                    { var data=model.ISelectionManager.CreateSelectData(); data.Mark=1; if(!movedBodies[i].Select2(i>0,data)) throw new InvalidOperationException("无法选择已翻译的体。"); }
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
                if(selectedBodies.Length<2) throw new ArgumentException("合并需要至少两个不同的体；第一个是减去的目标。");
                for(var i=0;i<selectedBodies.Length;i++)
                {
                    var data=model.ISelectionManager.CreateSelectData(); data.Mark=o.BooleanMode==BooleanMode.Subtract && i==0 ? 1 : 2;
                    if(!selectedBodies[i].Select2(i>0,data)) throw new InvalidOperationException("无法为组合操作选择体。");
                }
                result=fm.InsertCombineFeature(o.BooleanMode switch {BooleanMode.Union=>15903,BooleanMode.Subtract=>15902,_=>15901},
                    null,Array.Empty<object>()); break;
            case NativeFeatureKind.Mirror:
                SelectQueries(model,objects,o.Selections);
                result=fm.InsertMirrorFeature2(o.Selections.Any(s=>s.Kind==EntityKind.Body),true,o.Merge,false,0); break;
            case NativeFeatureKind.LinearPattern:
                SelectQueries(model,objects,o.Selections);
                var pattern=o.LinearPattern??new();
                result=fm.FeatureLinearPattern5(o.Count,Mm(o.SpacingMm),pattern.SecondCount,Mm(pattern.SecondSpacingMm),o.Reverse,pattern.ReverseSecondDirection,"","",pattern.GeometryPattern,false,
                    false,false,true,true,false,false,false,false,0,0,pattern.SecondDirectionSeedOnly,false); break;
            case NativeFeatureKind.CircularPattern:
                SelectQueries(model,objects,o.Selections);
                result=fm.FeatureCircularPattern5(o.Count,Radians(o.AngleDegrees),o.Reverse,"",true,true,false,false,false,false,1,0,"",false); break;
            case NativeFeatureKind.LoftBoss: case NativeFeatureKind.LoftCut:
                var loft=o.Loft??new();
                var append=false;
                foreach(var id in o.ProfileIds) { SelectFeature(model,ResolveFeature(model,objects,id),append,1); append=true; }
                foreach(var id in o.GuideIds) SelectFeature(model,ResolveFeature(model,objects,id),true,2);
                SelectLoftCenterline(model,o,objects);
                result=o.Kind==NativeFeatureKind.LoftBoss
                    ? fm.InsertProtrusionBlend2(loft.Close,loft.MaintainTangency,false,1,(short)loft.StartCondition,(short)loft.EndCondition,0,0,false,false,o.ThicknessMm>0,Mm(o.ThicknessMm),0,0,o.Merge,false,true,(int)(loft.GuideInfluence??LoftGuideInfluence.NextGuide))
                    : fm.InsertCutBlend(loft.Close,loft.MaintainTangency,false,1,(short)loft.StartCondition,(short)loft.EndCondition,o.ThicknessMm>0,Mm(o.ThicknessMm),0,0,false,true); break;
            case NativeFeatureKind.SweepBoss: case NativeFeatureKind.SweepCut:
                var sweep=o.Sweep??new();
                SelectFeature(model,ResolveFeature(model,objects,o.SketchId!),false,1);
                SelectFeature(model,ResolveFeature(model,objects,o.PathSketchId!),true,4);
                foreach(var id in o.GuideIds) SelectFeature(model,ResolveFeature(model,objects,id),true,2);
                result=o.Kind==NativeFeatureKind.SweepBoss
                    ? fm.InsertProtrusionSwept4(o.TangentPropagation,false,SweepMode(sweep),sweep.KeepTangency,sweep.AdvancedSmoothing,0,0,o.ThicknessMm>0,Mm(o.ThicknessMm),0,0,0,o.Merge,false,true,Radians(sweep.TwistAngleDegrees),sweep.MergeSmoothFaces,false,0,o.Reverse?2:1)
                    : fm.InsertCutSwept5(o.TangentPropagation,false,SweepMode(sweep),sweep.KeepTangency,sweep.AdvancedSmoothing,0,0,o.ThicknessMm>0,Mm(o.ThicknessMm),0,0,0,false,true,Radians(sweep.TwistAngleDegrees),sweep.MergeSmoothFaces,false,false,false,false,0,o.Reverse?2:1); break;
            default: throw new NotSupportedException($"原生特征{o.Kind}尚未实现。");
        }
        if(result is null) throw new InvalidOperationException($"SolidWorks 返回了{o.Kind}没有特征。");
        var feature=result as IFeature ?? model.IFeatureByPositionReverse(0)
            ?? throw new InvalidOperationException($"没有在{o.Kind}之后的特征。");
        if(feature.GetID()==previousFeatureId) throw new InvalidOperationException($"SolidWorks 未能为{o.Kind}创建新的特征。");
        feature.Name=operation.Name;
        ConfigureComplexLoft(model,feature,o);
        VerifyComplexLoft(model,feature,o,objects);
        VerifyAdvancedDefinition(model,feature,o,objects);
        return feature;
    }
    private static void SetBodyRotation(IModelDoc2 model,IFeature? feature,Vector3 rotation)
    {
        if(feature?.GetDefinition() is not IMoveCopyBodyFeatureData data || !data.AccessSelections((ModelDoc2)model,null))
            throw new InvalidOperationException("无法访问体旋转定义。");
        data.TransformType=(int)swMoveCopyBodyFeatureTransformType_e.swTransformType_Rotation;
        data.RotationOriginX=0;data.RotationOriginY=0;data.RotationOriginZ=0;
        data.TransformX=Radians(rotation.X);data.TransformY=Radians(rotation.Y);data.TransformZ=Radians(rotation.Z);
        if(!feature.ModifyDefinition(data,model,null)) { data.ReleaseSelectionAccess();throw new InvalidOperationException("无法设置体 XYZ 旋转。"); }
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
            if(!selected) throw new InvalidOperationException($"无法选择匹配的{query.Kind}。");
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
        if(query.RequirePersistentIdentity&&(query.PersistentReference is null||persisted is null||query.AllMatches))
            throw new InvalidOperationException("ENTITY_IDENTITY_UNAVAILABLE：严格身份目标不可恢复，禁止坐标后备选择。");
        if(query.Kind is EntityKind.Feature or EntityKind.Plane or EntityKind.Axis)
        {
            var f=query.FeatureId??query.Name;
            var selected=f is not null?ResolveFeature(model,objects,f):persisted as IFeature
                ??throw new ArgumentException("特征选择需要 feature_id，名称或有效的持久参考。");
            if(query.RequirePersistentIdentity&&!SameNativeEntity(model,selected,persisted!))
                throw new InvalidOperationException("ENTITY_IDENTITY_MISMATCH：名称／特征范围与持久身份目标不一致。");
            if(query.Kind==EntityKind.Plane && selected.GetTypeName2()!="RefPlane" || query.Kind==EntityKind.Axis && selected.GetTypeName2()!="RefAxis")
                throw new InvalidOperationException("选择的特征不是所请求的参考平面或基准轴。");
            return new object[]{selected};
        }
        var bodies=((IPartDoc)model).GetBodies2(-1,false) as object[] ?? [];
        IEnumerable<object> candidates;
        var scope="CurrentBodies";
        if(query.FeatureId is { } id)
        {
            var feature=ResolveFeature(model,objects,id);
            var faces=feature.GetFaces() as object[] ?? [];
            scope="FeatureGeneratedFaces";
            if(query.Kind==EntityKind.Body && faces.Length==0)
            {
                // Draft and similar modifying features can have affected faces without generated faces.
                // Never substitute all model bodies for a feature-scoped request.
                faces=feature.GetAffectedFaces() as object[]??[];
                scope="FeatureAffectedFaces";
            }
            if(query.Kind==EntityKind.Body && faces.Length==0)
            {
                candidates=BodiesWithNativeFeatureMembership(model,bodies,feature);
                scope="NativeBodyFeatureMembership";
            }
            else candidates=query.Kind switch { EntityKind.Face=>faces,
                EntityKind.Edge=>faces.Cast<IFace2>().SelectMany(f=>f.GetEdges() as object[] ?? []).Distinct(),
                EntityKind.Body=>faces.Cast<IFace2>().Select(f=>(object)f.GetBody()).Distinct(), _=>[] };
        }
        else candidates=query.Kind switch { EntityKind.Body=>bodies,
            EntityKind.Face=>bodies.Cast<IBody2>().SelectMany(b=>b.GetFaces() as object[] ?? []),
            EntityKind.Edge=>bodies.Cast<IBody2>().SelectMany(b=>b.GetEdges() as object[] ?? []),_=>[] };
        var candidateArray=candidates.Distinct().ToArray();
        var matches=candidateArray.Where(e=>MatchesEntity(e,query)).ToArray();
        if(query.RequirePersistentIdentity)
        {
            var identityMatches=matches.Where(e=>SameNativeEntity(model,e,persisted!)).ToArray();
            if(identityMatches.Length!=1)throw new InvalidOperationException("ENTITY_IDENTITY_MISMATCH：持久目标不在源几何与所属特征范围内。");
            return identityMatches;
        }
        if(persisted is not null && MatchesEntity(persisted,query) && matches.Any(e=>Persistent(model,e)==query.PersistentReference)) return new[]{persisted};
        if(matches.Length==0 || matches.Length>1 && !query.AllMatches) throw EntitySelectionFailure(query,candidateArray,matches.Length,scope);
        return matches;
    }
    private static bool MatchesEntity(object entity,EntityQuery query)
    {
        if(query.Kind==EntityKind.Face && entity is not IFace2 || query.Kind==EntityKind.Edge && entity is not IEdge || query.Kind==EntityKind.Body && entity is not IBody2) return false;
        double[]? parameters=null; double[]? closest=null; Vector3? direction=null; double? radius=null;
        if(entity is IFace2 face)
        {
            if(query.AreaMm2 is { } area)
            {
                var measuredArea=face.GetArea()*1e6;
                if(!double.IsFinite(measuredArea) || Math.Abs(measuredArea-area)>query.AreaToleranceMm2) return false;
            }
            var surface=(ISurface)face.GetSurface();
            var kind=surface.IsPlane()?GeometryKind.Plane:surface.IsCylinder()?GeometryKind.Cylinder:
                surface.IsCone()?GeometryKind.Cone:surface.IsSphere()?GeometryKind.Sphere:surface.IsTorus()?GeometryKind.Torus:GeometryKind.Any;
            if(query.Geometry!=GeometryKind.Any && query.Geometry!=kind) return false;
            if(kind==GeometryKind.Plane) { parameters=(double[])surface.PlaneParams; direction=new(parameters[0],parameters[1],parameters[2]); }
            if(kind==GeometryKind.Cylinder) { parameters=(double[])surface.CylinderParams; direction=new(parameters[3],parameters[4],parameters[5]); radius=parameters[6]*1000; }
            if(kind==GeometryKind.Sphere) { var sphere=ReadSphereParameters(surface);radius=sphere.RadiusMm; }
            if(query.PositionMm is { } p) closest=(double[])face.GetClosestPointOn(Mm(p.X),Mm(p.Y),Mm(p.Z));
        }
        else if(entity is IEdge edge)
        {
            if(query.LengthMm is { } length && Math.Abs(EdgeLengthMm(edge)-length)>query.ToleranceMm) return false;
            if(query.AdjacentFaceCount is { } count && (edge.GetTwoAdjacentFaces2() as object[]??[]).OfType<IFace2>().Count()!=count) return false;
            if(query.StartPointMm is { } startPoint && query.EndPointMm is { } endPoint)
            {
                if(edge.GetStartVertex() is not IVertex start || edge.GetEndVertex() is not IVertex end) return false;
                var a=VertexPoint(start);var b=VertexPoint(end);var tol=query.ToleranceMm;
                if(!(PointDistance(a,startPoint)<=tol && PointDistance(b,endPoint)<=tol || PointDistance(b,startPoint)<=tol && PointDistance(a,endPoint)<=tol)) return false;
            }
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
        if(query.RadiusMm is { } r && (radius is null || !double.IsFinite(radius.Value) || Math.Abs(radius.Value-r)>query.ToleranceMm)) return false;
        if(query.Direction is { } n)
        {
            if(direction is null) return false; var a=Unit(n); var b=Unit(direction);
            if(Math.Abs(a.X*b.X+a.Y*b.Y+a.Z*b.Z)<1-1e-6) return false;
        }
        if(query.PositionMm is { } pos && entity is not IBody2)
        {
            if(closest is null || closest.Length<3 || closest.Take(3).Any(x=>!double.IsFinite(x))) return false;
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
            ?? throw new InvalidOperationException("椭圆创建失败。");
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
            if(segment is null) throw new InvalidOperationException("创建草图段失败。");
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
            ?? throw new InvalidOperationException("创建样条线失败。");
    }
}
