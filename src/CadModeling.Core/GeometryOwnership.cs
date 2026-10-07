namespace CadModeling.Core;

public sealed record GeometryOwnerIdentity(string FeatureId,string PersistentReference);

/// <summary>来源由原生相邻面给出；共有边不能任意选择一个来源或丢弃全部来源。</summary>
public static class GeometryOwnership
{
    public static IReadOnlyList<GeometryOwnerIdentity> Owners(GeometryCandidate candidate)
        => candidate.OwnerFeatures is { Count: > 0 } owners ? owners
            : !string.IsNullOrWhiteSpace(candidate.FeatureId) && !string.IsNullOrWhiteSpace(candidate.OwnerFeaturePersistentReference)
                ? [new(candidate.FeatureId,candidate.OwnerFeaturePersistentReference)] : [];

    public static bool HasFeature(GeometryCandidate candidate,string featureId)
        => candidate.OwnerFeatures is { Count: > 0 }
            ? candidate.OwnerFeatures.Any(o=>o.FeatureId==featureId) : candidate.FeatureId==featureId;

    public static bool HasOwner(GeometryCandidate candidate,string persistentReference,string? featureId=null)
        => Owners(candidate).Any(o=>o.PersistentReference==persistentReference && (featureId is null || o.FeatureId==featureId));

    public static bool SameOwners(GeometryCandidate a,GeometryCandidate b)
        => Owners(a).Count==Owners(b).Count && Owners(a).ToHashSet().SetEquals(Owners(b));

    public static bool TryValidate(GeometryCandidate candidate,out string? error)
    {
        error=null;
        if(candidate.OwnerFeatures is null || candidate.OwnerFeatures.Count>16
            || candidate.OwnerFeatures.Any(o=>o is null || !Valid(o.FeatureId) || !Valid(o.PersistentReference))
            || candidate.OwnerFeatures.Select(o=>o.PersistentReference).Distinct(StringComparer.Ordinal).Count()!=candidate.OwnerFeatures.Count)
        { error="实际来源集合需有界，名称和持久身份非空，持久身份唯一。";return false; }
        if(candidate.OwnerFeaturePersistentReference is not null && (!Valid(candidate.OwnerFeaturePersistentReference) || !Valid(candidate.FeatureId)))
        { error="单一来源需完整的名称和持久身份。";return false; }
        if(candidate.OwnerFeatures.Count>0 && (candidate.FeatureId is not null || candidate.OwnerFeaturePersistentReference is not null)
            && (candidate.OwnerFeatures.Count!=1 || candidate.OwnerFeatures[0].FeatureId!=candidate.FeatureId
                || candidate.OwnerFeatures[0].PersistentReference!=candidate.OwnerFeaturePersistentReference))
        { error="单一来源字段与实际来源集合矛盾。";return false; }
        return true;
    }

    private static bool Valid(string? value)=>!string.IsNullOrWhiteSpace(value) && value.Length<=4096;
}
