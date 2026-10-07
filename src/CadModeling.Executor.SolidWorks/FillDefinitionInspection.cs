using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static IReadOnlyList<FillBoundaryBinding> BindFillRequest(IModelDoc2 model, NativeFeatureOptions options,
        IReadOnlyDictionary<string,object> objects) => options.Surface!.FillBoundaries.Select(b=>
    {
        var edge=(IEdge)ResolveEntities(model,objects,b.Edge).Single();
        FillSupportFace? support=null;
        if(b.SupportFace is { } query)
        {
            var face=(IFace2)ResolveEntities(model,objects,query).Single();
            var adjacent=edge.GetTwoAdjacentFaces2() as object[];
            if(adjacent is null || adjacent.Length!=2 || adjacent.Any(x=>x is not null && x is not IFace2)
                || !adjacent.OfType<IFace2>().Any(f=>SameNativeEntity(model,f,face)))
                throw new InvalidOperationException("FILL_SUPPORT：支撑面不是完整相邻面库存中的原生实体。");
            support=new(RequireDefinitionIdentity(model,face),RequireFillSupportOwner(model,face));
        }
        return new FillBoundaryBinding(RequireDefinitionIdentity(model,edge),b.Contact,support);
    }).ToArray();

    private static string RequireFillSupportOwner(IModelDoc2 model,IFace2 face) => face.GetFeature() is IFeature owner
        ? RequireDefinitionIdentity(model,owner)
        : throw new InvalidOperationException("FILL_SUPPORT_OWNER：支撑面缺少可核对的来源特征。");

    // GetAlternateFace has no per-edge argument and is documented for Tangent only.
    // It cannot certify a Curvature support binding. Capture the complete input-edge
    // adjacency while AccessSelections exposes the definition's input topology.
    private sealed class NativeFillDefinitionSession : IFillDefinitionSession, IDisposable
    {
        private readonly IModelDoc2 model;
        private readonly IFeature feature;
        private readonly string[] outputFaces;
        private IFillSurfaceFeatureData? definition;
        private object[] boundaries=[];
        internal NativeFillDefinitionSession(IModelDoc2 model,IFeature feature)
        {
            this.model=model;this.feature=feature;
            // Capture before AccessSelections rolls the result back. Compare identities,
            // not retained result RCWs, to exclude patch faces from input support evidence.
            outputFaces=(feature.GetFaces() as object[]??[]).OfType<IFace2>()
                .Select(f=>RequireDefinitionIdentity(model,f)).ToArray();
            Acquire();
        }
        private void Acquire()
        {
            if(feature.GetDefinition() is not IFillSurfaceFeatureData d || !d.AccessSelections(model,null))
                throw new InvalidOperationException("FILL_DEFINITION_UNVERIFIABLE：原生定义选择不可访问。");
            definition=d;
        }
        public FillDefinitionSnapshot Capture()
        {
            var d=definition??throw new InvalidOperationException("FILL_DEFINITION_UNVERIFIABLE：定义访问已释放。");
            boundaries=d.GetPatchBoundary(out var types) as object[]??[];
            var kinds=types as Array;
            var complete=boundaries.Length==d.GetPatchBoundaryCount() && kinds is not null && kinds.Length==boundaries.Length;
            var rows=new List<FillBoundaryState>();
            for(int i=0;i<boundaries.Length;i++)
            {
                var boundary=boundaries[i];var contact=(SurfaceContact)d.GetCurvatureControl(boundary);
                var kind=boundary is IEdge?EntityKind.Edge:boundary is IFeature?EntityKind.Feature:(EntityKind)(-1);
                var nativeKind=kinds is not null && i<kinds.Length?Convert.ToInt32(kinds.GetValue(i)):-1;
                complete &= kind==EntityKind.Edge && nativeKind==(int)swSelectType_e.swSelEDGES
                    || kind==EntityKind.Feature && nativeKind==(int)swSelectType_e.swSelSKETCHES;
                bool adjacency=false;var faces=new List<FillSupportFace>();
                var evidence=contact==SurfaceContact.Contact?FillSupportEvidence.NotRequired:FillSupportEvidence.Unavailable;
                string? selected=null;
                if(contact!=SurfaceContact.Contact && boundary is IEdge edge)
                {
                    var raw=edge.GetTwoAdjacentFaces2() as object[];
                    adjacency=raw is {Length:2} && raw.All(f=>f is null or IFace2) && outputFaces.Length>0;
                    foreach(var face in (raw??[]).OfType<IFace2>())
                    {
                        var reference=RequireDefinitionIdentity(model,face);
                        if(outputFaces.Any(r=>SamePersistentIdentity(model,r,reference)))continue;
                        faces.Add(new(reference,RequireFillSupportOwner(model,face)));
                    }
                    if(adjacency && faces.Count==1)
                    { evidence=FillSupportEvidence.UniqueExternalAdjacency;selected=faces[0].Reference; }
                }
                rows.Add(new(RequireDefinitionIdentity(model,boundary),kind,contact,adjacency,faces,evidence,selected));
            }
            return new(RequireDefinitionIdentity(model,feature),d.ResolutionControl,d.OptimizeSurface,d.ReverseSurface,d.Merge,d.TryToFormSolid,complete,rows);
        }
        public bool SetContact(string boundaryReference,SurfaceContact contact)
        {
            var matches=boundaries.Where(b=>SamePersistentIdentity(model,RequireDefinitionIdentity(model,b),boundaryReference)).ToArray();
            return matches.Length==1 && definition!.SetCurvatureControl(matches[0],(int)contact,false);
        }
        public bool Commit()
        {
            var d=definition!;
            if(!feature.ModifyDefinition(d,model,null))return false;
            definition=null; // Successful ModifyDefinition consumes selection access.
            Acquire(); // Verify a freshly acquired committed definition, not the edited wrapper.
            return true;
        }
        public void Dispose(){definition?.ReleaseSelectionAccess();definition=null;}
    }

    private static FillDefinitionSnapshot ReadFillDefinition(IModelDoc2 model,IFeature feature)
    {
        using var session=new NativeFillDefinitionSession(model,feature);
        var snapshot=session.Capture();FillSurfaceContract.Validate(snapshot,(a,b)=>SamePersistentIdentity(model,a,b));
        return snapshot;
    }
}
