namespace CadModeling.Core;

public sealed record BoundaryCreatedFeature(string Reference, int ErrorCode, bool IsWarning, bool Suppressed);
public sealed record BoundaryFeatureInventory(bool Complete, IReadOnlyList<BoundaryCreatedFeature> Features);

public static partial class BoundarySurfaceContract
{
    /// <summary>Some native versions return null while synchronously adding a
    /// NetBlend. A complete before/after inventory identifies this call's result;
    /// the caller must still verify its full declared source and control state.</summary>
    public static string ResolveCreatedFeature(BoundaryFeatureInventory before,
        BoundaryFeatureInventory after, string? returnedReference, Func<string,string,bool> same)
    {
        static InvalidOperationException Invalid(string detail) => new("BOUNDARY_NATIVE_CREATION_UNVERIFIABLE："+detail);
        foreach(var inventory in new[]{before,after})
        {
            if(!inventory.Complete || inventory.Features is null || inventory.Features.Count>4096
                || inventory.Features.Any(f=>f is null || string.IsNullOrWhiteSpace(f.Reference)))
                throw Invalid("Boundary 特征库存缺失或不完整。");
            for(var i=0;i<inventory.Features.Count;i++)
                if(inventory.Features.Skip(i+1).Any(f=>same(inventory.Features[i].Reference,f.Reference)))
                    throw Invalid("Boundary 特征身份有歧义。");
        }
        if(before.Features.Any(old=>!after.Features.Any(f=>same(old.Reference,f.Reference))))
            throw Invalid("工厂调用删除或替换了已有 Boundary 特征。");
        var added=after.Features.Where(f=>!before.Features.Any(old=>same(old.Reference,f.Reference))).ToArray();
        if(added.Length==0)
            throw new InvalidOperationException("BOUNDARY_NATIVE_CREATION：工厂没有新增 Boundary 特征。");
        if(added.Length!=1 || added[0].ErrorCode!=0 || added[0].IsWarning || added[0].Suppressed)
            throw Invalid("新增特征不唯一、存在原生错误／警告或被抑制。");
        if(returnedReference is not null && !same(returnedReference,added[0].Reference))
            throw Invalid("返回指针不是本次唯一新增的 Boundary 特征。");
        return added[0].Reference;
    }
}
