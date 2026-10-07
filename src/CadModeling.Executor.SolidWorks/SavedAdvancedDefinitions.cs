using System.Globalization;
using CadModeling.Ir;
using CadModeling.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    // Capture after the last Save3: native persistent references can include the document path.
    // The baseline is the final, deliberately edited state, rather than initial creation values.
    private sealed record NativeDefinitionReceipt(string OperationId, string FeatureName, string FeatureType,bool Suppressed,
        string FeatureReference, IReadOnlyDictionary<string,string> State, FillDefinitionSnapshot? FillDefinition=null,
        BoundaryDefinitionReceipt? BoundaryDefinition=null);

    private static IReadOnlyList<NativeDefinitionReceipt> CaptureAdvancedDefinitions(IModelDoc2 model,
        ModelingPlan plan, IReadOnlyList<StableFeatureReference> references)
    {
        IFeature ResolveOperation(string id)
        {
            var matches=references.Where(r=>r.OperationId.Equals(id,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length!=1 || matches[0].PersistentReferenceBase64 is not {Length:>0} reference)
                throw new InvalidOperationException("ADVANCED_DEFINITION_IDENTITY: 操作缺少唯一的原生持久身份。"+id);
            var prior=matches[0];int state=0;
            var resolved=model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(reference),out state);
            if(state!=0 || resolved is not IFeature feature || feature.Name!=prior.SolidWorksName
                || prior.SolidWorksFeatureId!=feature.GetID()
                || !SamePersistentIdentity(model,reference,RequireDefinitionIdentity(model,feature)))
                throw new InvalidOperationException("ADVANCED_DEFINITION_IDENTITY: 无法重取相同的原生操作特征。"+id);
            return feature;
        }
        var receipts=new List<NativeDefinitionReceipt>();
        foreach(var operation in plan.Operations.OfType<NativeFeatureOperation>())
        {
            var o=operation.Options;
            if(o.Kind is not (NativeFeatureKind.SurfaceBoundary or NativeFeatureKind.SurfaceFill or NativeFeatureKind.SpatialCurve or NativeFeatureKind.Helix or NativeFeatureKind.PhysicalThread)
                && o.LinearPattern is null && o.Sweep is null)continue;
            // Presentation traversal releases feature RCWs. Resolve the captured operation
            // identity: display names may repeat or be changed by SolidWorks during creation.
            var feature=ResolveOperation(operation.Id);
            BoundaryDefinitionReceipt? boundary=null;
            if(o.Kind==NativeFeatureKind.SurfaceBoundary)
            {
                var sources=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
                foreach(var id in o.ProfileIds.Concat(o.GuideIds))
                    sources[id]=ResolveOperation(id);
                var request=BindBoundaryRequest(model,o,sources);var snapshot=ReadSavedBoundaryControls(model,feature);
                BoundarySurfaceContract.VerifyDeclared(snapshot,request,(a,b)=>SamePersistentIdentity(model,a,b));
                RequireBoundaryNormalGeometry(model,feature,request);
                boundary=new(request,snapshot);
            }
            FillDefinitionSnapshot? fill=null;
            if(o.Kind==NativeFeatureKind.SurfaceFill && o.Surface is {FillBoundaries.Count:>0} surface)
            {
                // Reacquire source features as well: presentation releases creation-time RCWs.
                var sourceObjects=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
                foreach(var id in surface.FillBoundaries.SelectMany(b=>new[]{b.Edge.FeatureId,b.SupportFace?.FeatureId}).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    sourceObjects[id]=ResolveOperation(id);
                }
                var request=BindFillRequest(model,o,sourceObjects);
                fill=ReadFillDefinition(model,feature);
                FillSurfaceContract.VerifyDeclared(fill,request,surface.FillResolution,surface.OptimizeFill,(a,b)=>SamePersistentIdentity(model,a,b));
            }
            receipts.Add(new(operation.Id,feature.Name,feature.GetTypeName2(),feature.IsSuppressed(),RequireDefinitionIdentity(model,feature),
                ReadAdvancedStateWithBoundary(model,feature,o,boundary?.Snapshot),fill,boundary));
        }
        return receipts;
    }

    private static IFeature FindFinalDefinitionFeature(IModelDoc2 model,string name)
    {
        var matches=new List<IFeature>();var visited=new HashSet<int>();
        void Visit(IFeature feature)
        {
            if(!visited.Add(feature.GetID()))return;
            if(visited.Count>1000)throw new InvalidOperationException("ADVANCED_DEFINITION_INCOMPLETE: 最终特征库存超限。");
            if(feature.Name==name)matches.Add(feature);
            for(var child=feature.IGetFirstSubFeature();child is not null;child=child.IGetNextSubFeature())Visit(child);
        }
        for(var f=model.IFirstFeature();f is not null;f=f.IGetNextFeature())Visit(f);
        return matches.Count==1?matches[0]:throw new InvalidOperationException("ADVANCED_DEFINITION_IDENTITY: 最终特征名称不唯一或缺失。"+name);
    }

    private static string RequireDefinitionIdentity(IModelDoc2 model,object entity) =>
        Persistent(model,entity) is {Length:>0} value ? value : throw new InvalidOperationException("ADVANCED_DEFINITION_IDENTITY: 原生定义实体缺少持久身份。");

    private static IReadOnlyDictionary<string,string> ReadAdvancedState(IModelDoc2 model,IFeature feature,NativeFeatureOptions o)=>
        ReadAdvancedStateWithBoundary(model,feature,o,null);

    private static IReadOnlyDictionary<string,string> ReadAdvancedStateWithBoundary(IModelDoc2 model,IFeature feature,NativeFeatureOptions o,BoundaryDefinitionSnapshot? boundarySnapshot)
    {
        var state=new SortedDictionary<string,string>(StringComparer.Ordinal);
        if(o.Kind==NativeFeatureKind.PhysicalThread)return ReadPhysicalThreadState(model,feature,o);
        void Number(string name,double value)
        {
            if(!double.IsFinite(value))throw new InvalidOperationException("ADVANCED_DEFINITION_INVALID: 非有限的原生控制参数。"+name);
            state[name]=value.ToString("R",CultureInfo.InvariantCulture);
        }
        void Flag(string name,bool value)=>state[name]=value?"true":"false";
        if(o.Kind==NativeFeatureKind.SurfaceBoundary)
        {
            var snapshot=boundarySnapshot??ReadSavedBoundaryControls(model,feature);
            Flag("surface_bodies_only",snapshot.SurfaceBodiesOnly);
            for(int direction=0;direction<2;direction++)
            {
                var d=snapshot.Directions[direction];Number($"direction_{direction}_count",d.Curves.Count);
                Number($"direction_{direction}_influence",d.Influence);
                if(d.TrimByD1 is { } trim)Flag($"direction_{direction}_trim_by_d1",trim);
                for(int i=0;i<d.Curves.Count;i++)
                {
                    var c=d.Curves[i];state[$"direction_{direction}_curve_{i}_identity"]=c.Reference;
                    Number($"direction_{direction}_tangency_{i}",c.Tangency);
                    Number($"direction_{direction}_draft_{i}",c.DraftAngle);
                    Flag($"direction_{direction}_draft_reversed_{i}",c.ReverseDraft);
                    if(c.TangentLength is { } length)Number($"direction_{direction}_tangent_length_{i}",length);
                    if(c.ReverseTangent is { } reverse)Flag($"direction_{direction}_tangent_reverse_{i}",reverse);
                    if(c.TangentApplyAll is { } all)Flag($"direction_{direction}_tangent_all_{i}",all);
                }
            }
        }
        if(o.Kind==NativeFeatureKind.SurfaceFill)
        {
            if(feature.GetDefinition() is not IFillSurfaceFeatureData data || !data.AccessSelections(model,null))
                throw new InvalidOperationException("FILL_DEFINITION_UNVERIFIABLE: 无法读回原生边界控制。");
            try
            {
                var boundaries=data.GetPatchBoundary(out _) as object[]??[];
                if(boundaries.Length!=data.GetPatchBoundaryCount())throw new InvalidOperationException("FILL_DEFINITION_INCOMPLETE: 边界库存不完整。");
                Number("boundary_count",boundaries.Length);
                for(int i=0;i<boundaries.Length;i++)
                {
                    var boundary=boundaries[i];var identity=RequireDefinitionIdentity(model,boundary);
                    if(boundaries.Take(i).Any(prior=>SameNativeEntity(model,prior,boundary)))throw new InvalidOperationException("FILL_DEFINITION_INCOMPLETE: 原生边界身份重复。");
                    state[$"boundary_{i}_identity"]=identity;
                    Number($"boundary_{i}_control",data.GetCurvatureControl(boundary));
                }
                Number("resolution",data.ResolutionControl);Flag("optimize",data.OptimizeSurface);
                Flag("reverse_surface",data.ReverseSurface);Flag("merge",data.Merge);Flag("form_solid",data.TryToFormSolid);
                // Controlled edge fills have a separate typed support/owner receipt.
                // Neither receipt proves geometric G2 continuity.
            }
            finally{data.ReleaseSelectionAccess();}
        }
        if(o.Kind==NativeFeatureKind.SpatialCurve)
        {
            if(feature.GetSpecificFeature2() is not ISketch sketch || !sketch.Is3D())
                throw new InvalidOperationException("SPATIAL_CURVE_DEFINITION: 原生 3D 草图缺失。");
            var segments=(sketch.GetSketchSegments() as object[]??[]).OfType<ISketchSegment>().Where(s=>!s.ConstructionGeometry).ToArray();
            Number("segments",segments.Length);
            foreach(var segment in segments)
            {
                var prefix="segment_"+Array.IndexOf(segments,segment)+"_";
                state[prefix+"identity"]=RequireDefinitionIdentity(model,segment);
                if(segment is ISketchSpline spline)
                {
                    var points=(spline.GetPoints2() as object[]??[]).OfType<ISketchPoint>().ToArray();
                    Number(prefix+"points",points.Length);
                    for(int i=0;i<points.Length;i++)Point(prefix+"point_"+i,points[i]);
                }
                else if(segment is ISketchLine line)
                {Point(prefix+"start",(ISketchPoint)line.GetStartPoint2());Point(prefix+"end",(ISketchPoint)line.GetEndPoint2());}
                else throw new InvalidOperationException("SPATIAL_CURVE_DEFINITION: 不支持的保留段类型。");
            }
            void Point(string name,ISketchPoint p){Number(name+"_x",p.X);Number(name+"_y",p.Y);Number(name+"_z",p.Z);}
        }
        if(o.Kind==NativeFeatureKind.Helix)
        {
            if(feature.GetDefinition() is not IHelixFeatureData data)throw new InvalidOperationException("HELIX_DEFINITION: 原生螺旋定义缺失。");
            Number("pitch",data.Pitch);Number("revolutions",data.Revolution);Number("starting_angle",data.StartingAngle);
            Number("defined_by",data.DefinedBy);Flag("clockwise",data.Clockwise);Flag("reverse",data.ReverseDirection);Flag("taper",data.Taper);
        }
        if(o.LinearPattern is not null)
        {
            if(feature.GetDefinition() is not ILinearPatternFeatureData data)throw new InvalidOperationException("PATTERN_DEFINITION: 原生阵列定义缺失。");
            Number("count_1",data.D1TotalInstances);Number("count_2",data.D2TotalInstances);
            Number("spacing_1",data.D1Spacing);Number("spacing_2",data.D2Spacing);
            Flag("reverse_1",data.D1ReverseDirection);Flag("reverse_2",data.D2ReverseDirection);Flag("seed_only_2",data.D2PatternSeedOnly);
        }
        if(o.Sweep is not null)
        {
            if(feature.GetDefinition() is not ISweepFeatureData data)throw new InvalidOperationException("SWEEP_DEFINITION: 原生扫掠定义缺失。");
            Number("twist_control",data.TwistControlType);Number("twist_angle",data.GetTwistAngle());
        }
        return state;
    }

    private static void VerifySavedAdvancedDefinitions(IModelDoc2 model,ModelingPlan plan,IReadOnlyList<NativeDefinitionReceipt> expected)
        =>VerifyAdvancedDefinitions(model,plan,expected,false);

    private static void VerifyCleanSavedBoundaryDefinitions(IModelDoc2 model,IReadOnlyList<NativeDefinitionReceipt> expected)
    {
        foreach(var receipt in expected.Where(r=>r.BoundaryDefinition is not null))
        {
            int error=0;
            var feature=model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(receipt.FeatureReference),out error) as IFeature;
            if(error!=0||feature is null||feature.Name!=receipt.FeatureName||feature.GetTypeName2()!=receipt.FeatureType
                ||feature.IsSuppressed()!=receipt.Suppressed)
                throw new InvalidOperationException("SAVED_ADVANCED_IDENTITY：干净重开文档的边界身份不符。");
            var boundary=receipt.BoundaryDefinition!;
            BoundarySurfaceContract.VerifyRetained(boundary,ReadSavedBoundaryControls(model,feature),
                (a,b)=>SamePersistentIdentity(model,a,b));
            RequireBoundaryNormalGeometry(model,feature,boundary.Request);
        }
    }

    private static void VerifyAdvancedDefinitions(IModelDoc2 model,ModelingPlan plan,IReadOnlyList<NativeDefinitionReceipt> expected,
        bool boundaryControlsAlreadyVerified)
    {
        var objects=new Dictionary<string,object>();
        foreach(var receipt in expected)
        {
            int error=0;
            var resolved=model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(receipt.FeatureReference),out error);
            if(error!=0 || resolved is not IFeature feature || feature.Name!=receipt.FeatureName || feature.GetTypeName2()!=receipt.FeatureType
                || !SamePersistentIdentity(model,RequireDefinitionIdentity(model,feature),receipt.FeatureReference) || feature.IsSuppressed()!=receipt.Suppressed)
                throw new InvalidOperationException("SAVED_ADVANCED_IDENTITY: 保存后的高级特征身份或类型不符："+receipt.OperationId);
            objects.Add(receipt.OperationId,feature);
            var operation=plan.Operations.OfType<NativeFeatureOperation>().Single(o=>o.Id==receipt.OperationId);
            if(receipt.FillDefinition is { } fill)
                FillSurfaceContract.VerifyRetained(fill,ReadFillDefinition(model,feature),(a,b)=>SamePersistentIdentity(model,a,b));
            BoundaryDefinitionSnapshot? savedBoundary=null;
            if(receipt.BoundaryDefinition is { } boundary)
            {
                if(boundaryControlsAlreadyVerified)
                {
                    // Controls were measured on the clean, byte-bound saved source before
                    // rebuild. Here inspect the actual rebuilt source inventory and face;
                    // do not use a saved copy to certify a dirty document's control values.
                    BoundarySurfaceContract.VerifyRebuiltSourceInventory(boundary.Snapshot,ReadBoundaryDefinition(model,feature),
                        (a,b)=>SamePersistentIdentity(model,a,b));
                    RequireBoundaryNormalGeometry(model,feature,boundary.Request);
                    continue;
                }
                savedBoundary=ReadSavedBoundaryControls(model,feature);
                BoundarySurfaceContract.VerifyRetained(boundary,savedBoundary,(a,b)=>SamePersistentIdentity(model,a,b));
                RequireBoundaryNormalGeometry(model,feature,boundary.Request);
            }
            var actual=new Dictionary<string,string>(ReadAdvancedStateWithBoundary(model,feature,operation.Options,savedBoundary),StringComparer.Ordinal);
            if(receipt.BoundaryDefinition is not null)
            {
                // The typed receipt has already proved ordered complete-source identity.
                // Normalize Feature vs whole Sketch wrappers for the legacy dictionary,
                // whose raw wrapper persistent IDs are not necessarily equivalent.
                for(int direction=0;direction<2;direction++)
                for(int i=0;i<receipt.BoundaryDefinition.Snapshot.Directions[direction].Curves.Count;i++)
                    actual[$"direction_{direction}_curve_{i}_identity"]=receipt.State[$"direction_{direction}_curve_{i}_identity"];
            }
            if(operation.Options.Kind==NativeFeatureKind.SurfaceFill && actual.GetValueOrDefault("boundary_count")==receipt.State.GetValueOrDefault("boundary_count"))
            {
                var count=int.Parse(actual["boundary_count"],CultureInfo.InvariantCulture);var used=new HashSet<int>();
                var original=new Dictionary<string,string>(actual,StringComparer.Ordinal);
                for(int i=0;i<count;i++)
                {
                    var matches=Enumerable.Range(0,count).Where(j=>!used.Contains(j)&&SamePersistentIdentity(model,
                        receipt.State[$"boundary_{i}_identity"],original[$"boundary_{j}_identity"])).ToArray();
                    if(matches.Length!=1)throw new InvalidOperationException("SAVED_ADVANCED_IDENTITY: 保存后的 Fill 边界无法唯一对应原生实体。");
                    int index=matches[0];used.Add(index);
                    actual[$"boundary_{i}_identity"]=original[$"boundary_{index}_identity"];
                    actual[$"boundary_{i}_control"]=original[$"boundary_{index}_control"];
                }
            }
            var differences=receipt.State.Keys.Concat(actual.Keys).Distinct(StringComparer.Ordinal)
                .Where(key=>!receipt.State.TryGetValue(key,out var before)||!actual.TryGetValue(key,out var after)
                    || (key.EndsWith("_identity",StringComparison.Ordinal)?!SamePersistentIdentity(model,before,after):before!=after))
                .Select(key=>key+": "+receipt.State.GetValueOrDefault(key,"<missing>")+" -> "+actual.GetValueOrDefault(key,"<missing>")).ToArray();
            if(differences.Length>0)
                throw new InvalidOperationException("SAVED_ADVANCED_DEFINITION: 保存重开未保留最终原生控制或曲线身份："+receipt.OperationId+"；"+string.Join("；",differences));
        }
    }
}
