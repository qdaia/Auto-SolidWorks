using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private sealed partial class NativeAssemblyMobilitySession
    {
        private NativeConcentricTreeReadback? ReadConcentricTree()
        {
            if(model is not IAssemblyDoc assembly || components.Count is <2 or >32
                || assembly.GetComponentCount(false)!=components.Count
                || components.Values.Any(c=>c.GetModelDoc2() is not IPartDoc)
                || components.Values.Select(c=>c.Name2).Distinct(StringComparer.Ordinal).Count()!=components.Count)return null;
            var fixedIds=components.Where(p=>p.Value.IsFixed()).Select(p=>p.Key).ToArray();
            if(fixedIds.Length!=1)return null;
            var byName=components.ToDictionary(p=>p.Value.Name2,p=>p.Key,StringComparer.Ordinal);
            var mates=new List<(IFeature Feature,IMate2 Mate)>();var visited=new HashSet<int>();
            void Visit(IFeature feature)
            {
                if(!visited.Add(feature.GetID()))return;
                if(visited.Count>4096)throw new IOException("ASSEMBLY_MOBILITY_UNVERIFIABLE：配合库存超限。");
                if(!feature.IsSuppressed() && feature.GetSpecificFeature2() is IMate2 mate)mates.Add((feature,mate));
                for(var child=feature.IGetFirstSubFeature();child is not null;child=child.IGetNextSubFeature())Visit(child);
            }
            for(var feature=model.IFirstFeature();feature is not null;feature=feature.IGetNextFeature())Visit(feature);
            if(mates.Count is 0 or >62 || mates.Any(m=>m.Feature.GetErrorCode2(out _)!=0
                ||m.Mate.GetMateEntityCount()!=2
                ||m.Mate.Type is not ((int)swMateType_e.swMateCONCENTRIC) and not ((int)swMateType_e.swMateCOINCIDENT)))return null;
            var groups=new Dictionary<(string First,string Second),List<(IFeature Feature,IMate2 Mate,IMateEntity2[] Ends,string[] Ids)>>();
            var incidence=components.Keys.ToDictionary(id=>id,_=>new HashSet<int>(),StringComparer.Ordinal);
            foreach(var m in mates)
            {
                var ends=Enumerable.Range(0,2).Select(i=>m.Mate.MateEntity(i)).ToArray();
                if(ends.Any(e=>e is null ||e.ReferenceComponent is not IComponent2
                    ||e.ReferenceType2!=(int)swSelectType_e.swSelFACES ||e.Reference is not IFace2))return null;
                var owners=ends.Select(e=>(IComponent2)e.ReferenceComponent).ToArray();
                if(owners.Any(c=>!byName.ContainsKey(c.Name2)))return null;
                var ids=owners.Select(c=>byName[c.Name2]).ToArray();
                if(ids[0]==ids[1] || !SameNativeEntity(model,owners[0],components[ids[0]])
                    ||!SameNativeEntity(model,owners[1],components[ids[1]]))return null;
                var pair=ids.Order(StringComparer.Ordinal).ToArray();var key=(pair[0],pair[1]);
                if(!groups.TryGetValue(key,out var group))groups.Add(key,group=[]);
                group.Add((m.Feature,m.Mate,ends,ids));
                foreach(var id in ids)incidence[id].Add(m.Feature.GetID());
            }
            // Independently reconcile every component-context mate list with the
            // complete assembly feature inventory. Proxy identity is not IUnknown.
            foreach(var c in components)
            {
                var attached=c.Value.GetMates() as object[]??[];var actual=new HashSet<int>();
                foreach(var entry in attached)
                {
                    if(entry is not IFeature f)return null;
                    if(f.IsSuppressed())continue;
                    if(entry is not IMate2 mate ||!actual.Add(f.GetID()))return null;
                    var original=mates.SingleOrDefault(m=>m.Feature.GetID()==f.GetID());
                    if(original.Feature is null ||mate.Type!=original.Mate.Type ||mate.GetMateEntityCount()!=2)return null;
                    var owners=Enumerable.Range(0,2).Select(i=>(mate.MateEntity(i).ReferenceComponent as IComponent2)?.Name2).ToHashSet();
                    var expected=Enumerable.Range(0,2).Select(i=>(original.Mate.MateEntity(i).ReferenceComponent as IComponent2)?.Name2).ToHashSet();
                    if(owners.Contains(null)||!owners.SetEquals(expected)||!owners.Contains(c.Value.Name2))return null;
                }
                if(!actual.SetEquals(incidence[c.Key]))return null;
            }
            var joints=new List<NativeCylindricalJoint>();
            foreach(var group in groups.Values)
            {
                if(group.Count is <1 or >2)return null;
                var concentric=group.Where(m=>m.Mate.Type==(int)swMateType_e.swMateCONCENTRIC).ToArray();
                if(concentric.Length!=1)return null;
                var c=concentric[0];
                if(c.Feature.GetDefinition() is not IConcentricMateFeatureData definition
                    ||c.Ends.Any(e=>!((ISurface)((IFace2)e.Reference).GetSurface()).IsCylinder()))return null;
                var values=c.Ends.Select(e=>ToDoubles(e.EntityParams,8,"完整开链原生圆柱参数")).ToArray();
                Vector3 Point(double[] v)=>new(v[0]*1000,v[1]*1000,v[2]*1000);
                Vector3 Axis(double[] v)=>new(v[3],v[4],v[5]);
                var identity=Persistent(model,c.Feature);if(string.IsNullOrWhiteSpace(identity))return null;
                string? axialIdentity=null;NativeAxialCoincidence? axial=null;
                if(group.Count==2)
                {
                    var stop=group.Single(m=>m.Mate.Type==(int)swMateType_e.swMateCOINCIDENT);
                    if(stop.Ends.Any(e=>!((ISurface)((IFace2)e.Reference).GetSurface()).IsPlane()))return null;
                    var planes=stop.Ends.Select(e=>ToDoubles(e.EntityParams,8,"完整开链原生端面参数")).ToArray();
                    axialIdentity=Persistent(model,stop.Feature);if(string.IsNullOrWhiteSpace(axialIdentity))return null;
                    axial=new(Point(planes[0]),Point(planes[1]),Axis(planes[0]),Axis(planes[1]));
                }
                joints.Add(new(){FirstComponentId=c.Ids[0],SecondComponentId=c.Ids[1],ConcentricMateIdentity=identity,
                    FirstAxis=new(Point(values[0]),Axis(values[0]),values[0][6]*1000),
                    SecondAxis=new(Point(values[1]),Axis(values[1]),values[1][6]*1000),
                    NativeDefinitionsVerified=true,LockRotation=definition.LockRotation,
                    AxialMateIdentity=axialIdentity,AxialCoincidence=axial});
            }
            return new(){CompleteMateInventory=true,AllResolvedTopLevelParts=true,ComponentIds=components.Keys.ToArray(),
                FixedComponentIds=fixedIds,ActiveMateCount=mates.Count,Joints=joints};
        }
    }
}
