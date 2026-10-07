using CadModeling.Ir;

namespace CadModeling.Core;

public enum FillSupportEvidence { NotRequired, UniqueExternalAdjacency, NativePerBoundaryDefinition, Unavailable }
public sealed record FillSupportFace(string Reference, string OwnerReference);
public sealed record FillBoundaryBinding(string EdgeReference, SurfaceContact Contact, FillSupportFace? Support);
public sealed record FillBoundaryState(string Reference, EntityKind Kind, SurfaceContact Contact,
    bool CompleteAdjacency, IReadOnlyList<FillSupportFace> ExternalAdjacentFaces,
    FillSupportEvidence SupportEvidence, string? SelectedSupportReference);
public sealed record FillDefinitionSnapshot(string FeatureReference, int Resolution, bool Optimize,
    bool Reverse, bool Merge, bool FormSolid, bool CompleteInventory, IReadOnlyList<FillBoundaryState> Boundaries);

/// <summary>Observed native definitions, not caller-supplied geometry evidence.</summary>
public interface IFillDefinitionSession
{
    FillDefinitionSnapshot Capture();
    bool SetContact(string boundaryReference, SurfaceContact contact);
    bool Commit();
}

public static class FillSurfaceContract
{
    private static void Require(bool condition, string code, string message)
    { if (!condition) throw new InvalidOperationException(code + "：" + message); }
    private static bool Identity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 4096;
    private static void ValidateRequest(IReadOnlyList<FillBoundaryBinding> requested,int resolution)
    {
        Require(requested.Count is >=1 and <=128 && resolution is >=1 and <=3,"FILL_REQUEST_INVALID", "请求数量或分辨率无效。");
        foreach(var expected in requested)
            Require(Identity(expected.EdgeReference) && Enum.IsDefined(expected.Contact)
                && (expected.Contact==SurfaceContact.Contact ? expected.Support is null : expected.Support is { } f && Identity(f.Reference) && Identity(f.OwnerReference)),
                "FILL_REQUEST_INVALID", "请求必须绑定单一原生边及相邻来源面。");
    }
    private static void Unique<T>(IReadOnlyList<T> items, Func<T,string> identity, Func<string,string,bool> same)
    {
        for (int i=0;i<items.Count;i++)
            Require(!items.Take(i).Any(x=>same(identity(x),identity(items[i]))),
                "FILL_IDENTITY_AMBIGUOUS", "原生身份重复，不能唯一对应。");
    }
    public static void Validate(FillDefinitionSnapshot snapshot, Func<string,string,bool> same)
    {
        Require(Identity(snapshot.FeatureReference) && snapshot.CompleteInventory && snapshot.Resolution is >=1 and <=3
            && snapshot.Boundaries.Count is >=1 and <=128, "FILL_DEFINITION_INCOMPLETE", "特征身份、分辨率或边界库存不可核对。");
        Unique(snapshot.Boundaries,b=>b.Reference,same);
        foreach (var b in snapshot.Boundaries)
        {
            Require(Identity(b.Reference) && Enum.IsDefined(b.Contact) && b.Kind is EntityKind.Edge or EntityKind.Feature
                && Enum.IsDefined(b.SupportEvidence), "FILL_DEFINITION_INVALID", "边界身份、实体类型或控制枚举无效。");
            Require(b.ExternalAdjacentFaces.Count<=2, "FILL_SUPPORT_INCOMPLETE", "相邻支撑面库存超限。");
            Unique(b.ExternalAdjacentFaces,f=>f.Reference,same);
            foreach(var face in b.ExternalAdjacentFaces)
                Require(Identity(face.Reference) && Identity(face.OwnerReference) && !same(face.OwnerReference,snapshot.FeatureReference),
                    "FILL_SUPPORT_OWNER", "支撑面缺少来源特征或属于填充特征本身。");
            if(b.Contact==SurfaceContact.Contact)
            {
                Require(b.SupportEvidence==FillSupportEvidence.NotRequired && b.SelectedSupportReference is null,
                    "FILL_SUPPORT_INVALID", "接触边界不应声明相切／曲率支撑面。");
                continue;
            }
            Require(b.Kind==EntityKind.Edge && b.CompleteAdjacency && Identity(b.SelectedSupportReference)
                && b.SupportEvidence is FillSupportEvidence.UniqueExternalAdjacency or FillSupportEvidence.NativePerBoundaryDefinition,
                "FILL_SUPPORT_UNVERIFIABLE", "相切／曲率边界的支撑面不能逐边确认。");
            Require(b.SupportEvidence!=FillSupportEvidence.UniqueExternalAdjacency || b.ExternalAdjacentFaces.Count==1,
                "FILL_SUPPORT_AMBIGUOUS", "多个相邻来源面不能作为唯一支撑面证据。");
            Require(b.ExternalAdjacentFaces.Count(f=>same(f.Reference,b.SelectedSupportReference!))==1,
                "FILL_SUPPORT_MISMATCH", "已保留的支撑面不在完整相邻面库存中。");
        }
    }
    public static void VerifyDeclared(FillDefinitionSnapshot actual, IReadOnlyList<FillBoundaryBinding> requested,
        int resolution, bool optimize, Func<string,string,bool> same)
    {
        ValidateRequest(requested,resolution);
        Validate(actual,same);
        Require(requested.Count is >=1 and <=128 && requested.Count==actual.Boundaries.Count,
            "FILL_BOUNDARY_MISMATCH", "请求与定义的边界数量不同。");
        Unique(requested,b=>b.EdgeReference,same);
        Require(actual.Resolution==resolution && actual.Optimize==optimize && !actual.Merge && !actual.FormSolid && !actual.Reverse,
            "FILL_OPTIONS_MISMATCH", "填充分辨率、优化或曲面体选项未保留。");
        foreach(var expected in requested)
        {
            Require(Identity(expected.EdgeReference) && Enum.IsDefined(expected.Contact)
                && (expected.Contact==SurfaceContact.Contact ? expected.Support is null : expected.Support is { } f && Identity(f.Reference) && Identity(f.OwnerReference)),
                "FILL_REQUEST_INVALID", "请求必须绑定单一原生边及相邻来源面。");
            var matches=actual.Boundaries.Where(b=>same(b.Reference,expected.EdgeReference)).ToArray();
            Require(matches.Length==1 && matches[0].Kind==EntityKind.Edge && matches[0].Contact==expected.Contact,
                "FILL_CONTROL_MISMATCH", "边界身份或请求的连续性控制未保留。");
            if(expected.Support is { } support)
            {
                var b=matches[0];
                Require(same(b.SelectedSupportReference!,support.Reference), "FILL_SUPPORT_MISMATCH", "实际支撑面与源请求不同。");
                var owner=b.ExternalAdjacentFaces.Single(f=>same(f.Reference,b.SelectedSupportReference!)).OwnerReference;
                Require(same(owner,support.OwnerReference), "FILL_SUPPORT_OWNER", "支撑面的来源特征与声明不同。");
            }
        }
    }
    public static void VerifyRetained(FillDefinitionSnapshot before, FillDefinitionSnapshot after, Func<string,string,bool> same)
    {
        Validate(before,same);Validate(after,same);
        Require(same(before.FeatureReference,after.FeatureReference) && before.Resolution==after.Resolution
            && before.Optimize==after.Optimize && before.Reverse==after.Reverse && before.Merge==after.Merge
            && before.FormSolid==after.FormSolid && before.Boundaries.Count==after.Boundaries.Count,
            "SAVED_FILL_OPTIONS_MISMATCH", "保存后填充特征身份或控制选项改变。");
        foreach(var b in before.Boundaries)
        {
            var matches=after.Boundaries.Where(a=>same(a.Reference,b.Reference)).ToArray();
            Require(matches.Length==1,"SAVED_FILL_IDENTITY", "保存后的边界无法唯一恢复。");
            var a=matches[0];
            Require(a.Kind==b.Kind && a.Contact==b.Contact && a.SupportEvidence==b.SupportEvidence
                && a.CompleteAdjacency==b.CompleteAdjacency && a.ExternalAdjacentFaces.Count==b.ExternalAdjacentFaces.Count
                && (b.SelectedSupportReference is null ? a.SelectedSupportReference is null : a.SelectedSupportReference is not null && same(b.SelectedSupportReference,a.SelectedSupportReference)),
                "SAVED_FILL_CONTROL_MISMATCH", "保存后的连续性或支撑绑定改变。");
            foreach(var f in b.ExternalAdjacentFaces)
                Require(a.ExternalAdjacentFaces.Count(g=>same(g.Reference,f.Reference) && same(g.OwnerReference,f.OwnerReference))==1,
                    "SAVED_FILL_SUPPORT_MISMATCH", "保存后的支撑面身份或来源特征改变。");
        }
    }
    public static void EnsureControls(IFillDefinitionSession session, IReadOnlyList<FillBoundaryBinding> requested,
        int resolution, bool optimize, Func<string,string,bool> same)
    {
        ValidateRequest(requested,resolution);Unique(requested,b=>b.EdgeReference,same);
        // A newly created definition may initially retain Contact. Correct controls only;
        // never fabricate a support binding or replace a failed Curvature with Tangent.
        var initial=session.Capture();
        Require(initial.CompleteInventory && Identity(initial.FeatureReference) && initial.Boundaries.Count==requested.Count
            && requested.Count is >=1 and <=128,"FILL_DEFINITION_INCOMPLETE", "创建后的边界库存不完整。");
        Unique(initial.Boundaries,b=>b.Reference,same);
        bool changed=false;
        foreach(var b in requested)
        {
            var found=initial.Boundaries.Where(a=>same(a.Reference,b.EdgeReference)).ToArray();
            Require(found.Length==1 && found[0].Kind==EntityKind.Edge,"FILL_BOUNDARY_MISMATCH", "创建后边界不唯一或类型不同。");
            if(found[0].Contact==b.Contact)continue;
            Require(session.SetContact(found[0].Reference,b.Contact), "FILL_CONTROL_UNAVAILABLE", "原生端拒绝请求的连续性设置。");
            changed=true;
        }
        if(changed)Require(session.Commit(), "FILL_CONTROL_COMMIT", "连续性定义提交失败。");
        var final=session.Capture();
        Require(same(initial.FeatureReference,final.FeatureReference), "FILL_FEATURE_CHANGED", "连续性更新替换了源特征身份。");
        VerifyDeclared(final,requested,resolution,optimize,same);
    }
}
