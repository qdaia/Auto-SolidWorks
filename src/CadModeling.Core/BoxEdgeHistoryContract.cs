using CadModeling.Ir;

namespace CadModeling.Core;

public static class BoxEdgeHistoryContract
{
    public static void Validate(ModelingPlan plan)
    {
        if (plan.BoxEdgeHistory is not { } s) return;
        void Require(bool condition, string message) { if (!condition) throw new ArgumentException("SEMANTIC_HISTORY_CONTRACT: " + message); }
        Require(!string.IsNullOrWhiteSpace(plan.SourceModelPath) && plan.DesignIntent is null && !plan.Recovery.Enabled
            && !plan.Output.OverwriteAllowed && plan.Operations.Count == 2, "仅支持全新副本的受控两步尺寸／单边圆角；不得叠加配置、恢复或覆盖。");
        Require(new[] { s.SemanticKey, s.WidthDimensionName, s.LengthDimensionName, s.ThicknessDimensionName, s.BaseFeatureName,
            s.ResizeOperationId, s.FilletOperationId }.All(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 256), "源定义标识缺失或过长。");
        Require(new[] { s.WidthMm, s.LengthMm, s.InitialThicknessMm }.All(v => double.IsFinite(v) && v > 0), "源尺寸需正有限值。");
        Require(new[] { s.WidthDimensionName, s.LengthDimensionName, s.ThicknessDimensionName }.Distinct().Count() == 3,
            "三个源尺寸必须独立绑定。");
        Require(s.ThicknessDimensionName.Split('@') is { Length: 2 } name && name[1] == s.BaseFeatureName,
            "厚度维度必须属于声明的基体。");
        Require(plan.Operations[0] is NativeFeatureOperation { Options.Kind: NativeFeatureKind.SetDimension }
            && plan.Operations[1] is NativeFeatureOperation { Options.Kind: NativeFeatureKind.Fillet }, "操作顺序必须是改厚度后单边圆角。");
        var resize = (NativeFeatureOperation)plan.Operations[0]; var fillet = (NativeFeatureOperation)plan.Operations[1];
        Require(resize.Id == s.ResizeOperationId && fillet.Id == s.FilletOperationId && resize.Id != fillet.Id
            && resize.Options.DimensionName == s.ThicknessDimensionName && !resize.Options.DimensionIsAngle
            && resize.Options.DimensionUnit is null or DrawingValueUnit.Millimeter, "原生修改必须与源厚度身份及毫米单位一致。");
        var t = resize.Options.DimensionValue; var radius = fillet.Options.RadiusMm;
        Require(double.IsFinite(t) && t > 0 && double.IsFinite(radius) && radius > 0 && radius < Math.Min(t, s.WidthMm / 2), "新厚度／圆角半径无效。");
        Require(!fillet.Options.TangentPropagation && fillet.Options.VariableFillet is null && fillet.Options.Selections.Count == 1,
            "必须关闭切向传播，仅选择一条恒定半径边。");
        var q = fillet.Options.Selections[0];
        Require(q.Kind == EntityKind.Edge && q.Geometry == GeometryKind.Line && !q.AllMatches && q.Name is null
            && q.PositionMm == new Vector3(s.WidthMm / 2, 0, t) && q.Direction == new Vector3(0, 1, 0)
            && q.StartPointMm is null && q.EndPointMm is null && q.PersistentReference is null
            && q.ToleranceMm is > 0 and <= 0.05, "选择必须对应源定义的右上纵向边；持久目标由生产端捕获。");
    }

    public static GeometrySignature Expected(BoxEdgeHistorySpec s, double thicknessMm, double? filletRadiusMm = null) =>
        filletRadiusMm is { } r
            ? new() { EntityKind=EntityKind.Edge,GeometryKind=GeometryKind.Circle,AnchorMm=new(s.WidthMm / 2-r,s.LengthMm / 2,thicknessMm-r),Direction=new(0,1,0),RadiusMm=r,
                PositionToleranceMm=0.001,RadiusToleranceMm=0.001 }
            : new() { EntityKind=EntityKind.Edge,GeometryKind=GeometryKind.Line,AnchorMm=new(s.WidthMm / 2,0,thicknessMm),Direction=new(0,1,0),PositionToleranceMm=0.001 };
}
