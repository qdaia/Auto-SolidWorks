using CadModeling.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static AssemblyMateReadback VerifySavedMate(IModelDoc2 model,IFeature feature,IMate2 mate,AssemblyMateSpec spec,
        IReadOnlyList<AssemblyComponentResult> instances,IReadOnlyDictionary<string,IComponent2> components)
    {
        if(spec.First.ComponentId==spec.Second.ComponentId||new[]{spec.First,spec.Second}.Any(e=>!instances.Any(c=>c.Id==e.ComponentId)))
            throw new IOException("ASSEMBLY_MATE_COMPONENT: 请求的配合端点组件身份无效。");
        if(spec.Kind is not (AssemblyMateKind.Perpendicular or AssemblyMateKind.Lock)
            &&mate.Alignment!=(int)(spec.AntiAligned?swMateAlign_e.swMateAlignANTI_ALIGNED:swMateAlign_e.swMateAlignALIGNED))
            throw new IOException($"ASSEMBLY_MATE_ALIGNMENT: 配合 '{spec.Name}' 的对齐方向未保留。");
        bool? lockRotation=null;
        if(spec.Kind==AssemblyMateKind.Concentric)
        {
            if(feature.GetDefinition() is not IConcentricMateFeatureData definition||definition.LockRotation!=spec.LockRotation)
                throw new IOException($"ASSEMBLY_MATE_ROTATION: 配合 '{spec.Name}' 的旋转锁定未保留。");
            lockRotation=definition.LockRotation;
        }
        if(mate.GetMateEntityCount()!=2)throw new IOException("ASSEMBLY_MATE_ENTITIES: 配合未保留两个端点。");
        var remaining=new List<AssemblyMateEntity>{spec.First,spec.Second};
        var readbacks=new List<AssemblyMateEndpointReadback>();
        for(var index=0;index<2;index++)
        {
            var native=mate.MateEntity(index)??throw new IOException("ASSEMBLY_MATE_ENTITIES: 原生端点不可读。");
            var owner=(IComponent2?)native.ReferenceComponent??native.Reference as IComponent2;
            var expected=remaining.SingleOrDefault(e=>instances.Single(c=>c.Id==e.ComponentId).InstanceName==owner?.Name2)
                ??throw new IOException($"ASSEMBLY_MATE_COMPONENT: 配合 '{spec.Name}' 的端点绑定到了错误的组件实例。");
            remaining.Remove(expected);
            var component=components[owner!.Name2];
            var reference=native.Reference??throw new IOException("ASSEMBLY_MATE_ENTITIES: 配合实体引用为空。");
            if(expected.Entity is not null)
            {
                var part=(IModelDoc2?)component.GetModelDoc2()??throw new IOException("配合端点组件未解析。");
                var local=ResolveEntities(part,new Dictionary<string,object>(),expected.Entity).Single();
                var corresponding=component.GetCorrespondingEntity(local)??throw new IOException("配合端点无法映射到装配体。");
                if(!SameNativeEntity(model,corresponding,reference)&&!SameNativeEntity(part,local,reference))
                    throw new IOException($"ASSEMBLY_MATE_ENTITY: 配合 '{spec.Name}' 的实际几何实体不符合端点查询。");
            }
            else if(reference is not IComponent2 actual||actual.Name2!=component.Name2)
                throw new IOException("ASSEMBLY_MATE_ENTITY: 锁定配合未保留完整组件引用。");
            var identity=Persistent(model,reference);
            if(string.IsNullOrEmpty(identity))throw new IOException("ASSEMBLY_MATE_IDENTITY: 实际配合实体缺少可读回的装配持久引用。");
            readbacks.Add(new(expected.ComponentId,owner.Name2,native.ReferenceType2,identity));
        }
        return new(spec.Name,spec.Kind,mate.Alignment,lockRotation,readbacks);
    }
}
