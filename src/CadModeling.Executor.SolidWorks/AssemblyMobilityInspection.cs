using CadModeling.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using CadModeling.Ir;

internal sealed partial class SolidWorksComExecutor
{
    /// <summary>
    /// 只读适配器。公开的完全约束／固定状态可确认两个静止组件之间零相对运动。
    /// 活动机构需要完整运动基，当前不把未文档化 GetRemainingDOFs 的输出解释为该证明。
    /// </summary>
    private sealed partial class NativeAssemblyMobilitySession(IModelDoc2 model, IReadOnlyDictionary<string,IComponent2> components)
        : IAssemblyMobilitySession
    {
        public string Configuration => model.ConfigurationManager.ActiveConfiguration.Name;
        public IReadOnlyDictionary<string,string> ComponentIdentities => components.ToDictionary(x => x.Key,
            x => Persistent(model,x.Value) is { Length: > 0 } identity ? identity
                : throw new IOException("ASSEMBLY_MOBILITY_IDENTITY：组件实例缺少装配持久身份。"),StringComparer.Ordinal);

        public AssemblyMobilityObservation Inspect(string componentId,string relativeToComponentId)
        {
            var component=components[componentId]; var reference=components[relativeToComponentId];
            var identities=ComponentIdentities;
            var bothStationary=Stationary(component)&&Stationary(reference);
            var tree=bothStationary?null:ReadConcentricTree();
            IReadOnlyList<AssemblyMotion>? treeBasis=null;
            if(tree is not null)
            {
                try { treeBasis=ConcentricTreeMobilityContract.Basis(tree,componentId,relativeToComponentId); }
                catch(ArgumentException) { tree=null; }
            }
            var movingBasis=bothStationary?null:treeBasis??ConcentricPair(component,reference);
            return new()
            {
                ComponentId=componentId,RelativeToComponentId=relativeToComponentId,
                ComponentIdentity=identities[componentId],ReferenceIdentity=identities[relativeToComponentId],Configuration=Configuration,
                EvidenceSource=bothStationary?"native_fixed_or_fully_constrained_pair":treeBasis is not null?"native_complete_grounded_concentric_tree_relative_spatial_basis":movingBasis is not null?"native_complete_single_concentric_cylinder_pair_regular_nullspace":"native_complete_motion_basis_unavailable",
                ConcentricTreeReadback=tree,
                State=bothStationary||movingBasis is not null?AssemblyMobilityEvidenceState.Complete:AssemblyMobilityEvidenceState.Unavailable,
                InAssemblyCoordinates=true,RegularConfiguration=bothStationary||movingBasis is not null,DegreesOfFreedom=bothStationary?0:movingBasis?.Count,Basis=movingBasis??[]
            };
        }

        private IReadOnlyList<AssemblyMotion>? ConcentricPair(IComponent2 moving,IComponent2 reference)
        {
            if(!reference.IsFixed()||moving.IsFixed()||components.Count!=2||model is not IAssemblyDoc assembly
                ||assembly.GetComponentCount(false)!=2||components.Values.Any(c=>c.GetModelDoc2() is not IPartDoc))return null;
            var mates=new List<(IFeature Feature,IMate2 Mate)>();var visited=new HashSet<int>();
            void Visit(IFeature f)
            {
                if(!visited.Add(f.GetID()))return;
                if(visited.Count>4096)throw new InvalidOperationException("ASSEMBLY_MOBILITY_UNVERIFIABLE：配合库存超限。");
                if(!f.IsSuppressed()&&f.GetSpecificFeature2() is IMate2 mate)mates.Add((f,mate));
                for(var child=f.IGetFirstSubFeature();child is not null;child=child.IGetNextSubFeature())Visit(child);
            }
            for(var f=model.IFirstFeature();f is not null;f=f.IGetNextFeature())Visit(f);
            if(mates.Count!=1||mates[0].Mate.Type!=(int)swMateType_e.swMateCONCENTRIC||mates[0].Mate.GetMateEntityCount()!=2
                ||mates[0].Feature.GetErrorCode2(out _)!=0||mates[0].Feature.GetDefinition() is not IConcentricMateFeatureData definition)return null;
            var attached=moving.GetMates() as object[];
            if(attached is not {Length:1})return null;
            // GetMates returns a component-context mate proxy. Its IUnknown and
            // persistent reference differ from the assembly feature's proxy.
            // Match the native feature ID in this complete, current assembly
            // inventory and independently check both actual endpoint owners.
            if(attached[0] is not IFeature attachedFeature || attachedFeature.GetID()!=mates[0].Feature.GetID()
                ||attached[0] is not IMate2 attachedMate || attachedMate.Type!=mates[0].Mate.Type
                ||attachedMate.GetMateEntityCount()!=2)return null;
            var attachedOwners=Enumerable.Range(0,2).Select(i=>(attachedMate.MateEntity(i).ReferenceComponent as IComponent2)?.Name2).ToHashSet();
            if(!attachedOwners.SetEquals(new[]{moving.Name2,reference.Name2}))return null;
            var ends=Enumerable.Range(0,2).Select(i=>mates[0].Mate.MateEntity(i)).ToArray();
            var names=ends.Select(e=>(e.ReferenceComponent as IComponent2)?.Name2).ToArray();
            if(!names.ToHashSet().SetEquals(new[]{moving.Name2,reference.Name2})
                ||ends.Any(e=>e.ReferenceType2!=(int)swSelectType_e.swSelFACES || e.Reference is not IFace2 face || !((ISurface)face.GetSurface()).IsCylinder()))return null;
            var values=ends.Select(e=>ToDoubles(e.EntityParams,8,"原生装配坐标圆柱配合参数")).ToArray();
            try{return ConcentricMobilityContract.Basis(new(){CompleteMateInventory=true,TwoResolvedTopLevelParts=true,ReferenceFixed=true,ActiveMateCount=1,
                ConcentricDefinitionVerified=true,LockRotation=definition.LockRotation,
                FirstAxisPointMm=new(values[0][0]*1000,values[0][1]*1000,values[0][2]*1000),SecondAxisPointMm=new(values[1][0]*1000,values[1][1]*1000,values[1][2]*1000),
                FirstAxis=new(values[0][3],values[0][4],values[0][5]),SecondAxis=new(values[1][3],values[1][4],values[1][5]),FirstRadiusMm=values[0][6]*1000,SecondRadiusMm=values[1][6]*1000});}
            catch(ArgumentException){return null;}
        }

        private static bool Stationary(IComponent2 component) => component.IsFixed()
            || component.GetConstrainedStatus()==(int)swConstrainedStatus_e.swFullyConstrained;
    }
}
