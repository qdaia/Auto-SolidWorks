namespace CadModeling.Core;

public sealed record BoundaryControlCopyEvidence(string SourceSha256,string CopySha256,
    string SourceAfterSha256,string CopyAfterSha256,bool SourceWasModified,bool SourceIsModified,
    bool ParameterInputOccurred,BoundaryDefinitionSnapshot Original,BoundaryDefinitionSnapshot CopyBefore,
    BoundaryDefinitionSnapshot CopyAfter);

public static partial class BoundarySurfaceContract
{
    /// <summary>
    /// Bind a native no-parameter-edit observation to a byte-identical saved
    /// document. Raw API tangency is deliberately not treated as source truth:
    /// SOLIDWORKS 2025 can return None for a retained NormalToProfile boundary.
    /// No values are inferred from the requested end conditions.
    /// </summary>
    public static BoundaryDefinitionSnapshot VerifyCopiedSavedControls(BoundaryControlCopyEvidence evidence,
        Func<string,string,bool> same)
    {
        bool Hash(string value)=>value.Length==64 && value.All(Uri.IsHexDigit);
        Require(Hash(evidence.SourceSha256) && new[]{evidence.CopySha256,evidence.SourceAfterSha256,evidence.CopyAfterSha256}
            .All(h=>Hash(h)&&h.Equals(evidence.SourceSha256,StringComparison.OrdinalIgnoreCase)),
            "BOUNDARY_CONTROL_COPY_CHANGED","来源或独立副本的磁盘身份改变，不能使用副本读回。");
        Require(!evidence.SourceWasModified&&!evidence.SourceIsModified&&!evidence.ParameterInputOccurred,
            "BOUNDARY_CONTROL_COPY_MUTATION","只能读取未修改来源的无参数输入副本，不能认证修改中的文档。");
        var original=evidence.Original;
        Require(original.SurfaceBodiesOnly&&original.Directions.Count==2&&Identity(original.FeatureReference),
            "BOUNDARY_CONTROL_COPY_IDENTITY","来源曲面身份或方向库存不完整。");
        foreach(var actual in new[]{evidence.CopyBefore,evidence.CopyAfter})
            VerifyRebuiltSourceInventory(original,actual,same);
        return evidence.CopyAfter;
    }

    /// <summary>Check complete source identity after rebuild. This does not certify end controls.</summary>
    public static void VerifyRebuiltSourceInventory(BoundaryDefinitionSnapshot original,BoundaryDefinitionSnapshot actual,
        Func<string,string,bool> same)
    {
        Require(original.SurfaceBodiesOnly&&original.Directions.Count==2&&Identity(original.FeatureReference),
            "BOUNDARY_CONTROL_COPY_IDENTITY","来源曲面身份或方向库存不完整。");
        {
            Require(actual.SurfaceBodiesOnly&&actual.Directions.Count==2&&Identity(actual.FeatureReference)
                &&same(original.FeatureReference,actual.FeatureReference),
                "BOUNDARY_CONTROL_COPY_IDENTITY","副本编辑前后必须是同一来源曲面身份。");
            for(int direction=0;direction<2;direction++)
            {
                var a=original.Directions[direction];var b=actual.Directions[direction];
                Require(a.CompleteInventory&&b.CompleteInventory&&a.Curves.Count==b.Curves.Count
                    &&a.Influence==b.Influence&&a.TrimByD1==b.TrimByD1,
                    "BOUNDARY_CONTROL_COPY_INVENTORY","副本的完整曲线族、顺序或方向控制改变。");
                Unique(a.Curves.Select(c=>c.Reference).ToArray(),same);
                Unique(b.Curves.Select(c=>c.Reference).ToArray(),same);
                for(int i=0;i<a.Curves.Count;i++)
                {
                    var x=a.Curves[i];var y=b.Curves[i];
                    Require(Enum.IsDefined(x.Representation)&&x.Representation==y.Representation
                        &&same(x.Reference,y.Reference)&&x.OwnerSketchSegmentCount==y.OwnerSketchSegmentCount
                        &&(x.OwnerSketchReference is null&&y.OwnerSketchReference is null
                            ||x.OwnerSketchReference is not null&&y.OwnerSketchReference is not null&&same(x.OwnerSketchReference,y.OwnerSketchReference)),
                        "BOUNDARY_CONTROL_COPY_IDENTITY","副本曲线来源、表示或所属草图身份改变。");
                }
            }
        }
    }
}
