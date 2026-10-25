using CadModeling.Ir;
namespace CadModeling.Core;

public static class SourceInterpretationValidation
{
    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext context)
    {
        var errors = new List<ModelingDiagnostic>();
        void Error(string code, string message) => errors.Add(new(code, DiagnosticSeverity.Error, message, "drawing_context.interpretation_reviews"));
        var features = context.Features.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var views = context.Views.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        if (context.InterpretationReviews.Select(r => r.FeatureId).Distinct(StringComparer.Ordinal).Count() != context.InterpretationReviews.Count)
            Error("SOURCE_INTERPRETATION_INVALID", "每个源特征只能有一份解释复核。");
        foreach (var review in context.InterpretationReviews)
        {
            var feature = context.Features.FirstOrDefault(f => f.Id == review.FeatureId);
            var selected = review.Hypotheses.FirstOrDefault(h => h.Id == review.SelectedHypothesisId);
            if (!features.Contains(review.FeatureId) || string.IsNullOrWhiteSpace(review.DimensionDatumDerivation) || selected is null ||
                review.Hypotheses.Count < 2 || review.Hypotheses.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count() != review.Hypotheses.Count)
                Error("SOURCE_INTERPRETATION_INVALID", "特征 " + review.FeatureId + " 需要唯一的备选解释、已选解释及尺寸基准推导。");
            foreach (var hypothesis in review.Hypotheses)
            {
                if (string.IsNullOrWhiteSpace(hypothesis.Id) || string.IsNullOrWhiteSpace(hypothesis.Description) || hypothesis.Observations.Count == 0)
                    Error("SOURCE_INTERPRETATION_INVALID", "每个解释都需要描述及定位到原图的观测。");
                foreach (var o in hypothesis.Observations)
                    if (!views.Contains(o.ViewId) || feature is null || !feature.ViewIds.Contains(o.ViewId) || string.IsNullOrWhiteSpace(o.Observation) || !ValidBox(o.Bounds))
                        Error("SOURCE_INTERPRETATION_INVALID", "解释观测需要特征声明的视图、有限的原图区域及原始观察。");
            }
            if (selected?.Observations.Any(o => o.Contradicts) == true)
                Error("SOURCE_INTERPRETATION_CONFLICT", "已选解释 " + selected.Id + " 存在原图反证；必须改选或重新推导，不能以显示/颜色歧义消除反证。");
            if (selected is not null && feature is not null && feature.ViewIds.Any(id => !selected.Observations.Any(o => o.ViewId == id && !o.Contradicts)))
                Error("SOURCE_INTERPRETATION_INCOMPLETE", "已选解释缺少特征各源视图的正面支持：" + feature.Id);
            foreach (var rejected in review.Hypotheses.Where(h => h.Id != review.SelectedHypothesisId))
                if (!rejected.Observations.Any(o => o.Contradicts))
                    Error("SOURCE_INTERPRETATION_AMBIGUOUS", "备选解释 " + rejected.Id + " 尚无源反证；不能由同一模型的数值检查代替来源判别。");
        }
        if (context.RequireInterpretationReview)
            foreach (var f in context.Features.Where(f => f.Critical))
                if (!context.InterpretationReviews.Any(r => r.FeatureId == f.Id))
                    Error("SOURCE_INTERPRETATION_REQUIRED", "关键源特征需要独立于建模数值预期的解释复核：" + f.Id);
        if (!context.RequireInterpretationReview && !SourceWorkflow.IsFourStage(context.WorkflowRevision) && context.Features.Any(f => f.Critical))
            errors.Add(new("SOURCE_INTERPRETATION_LEGACY", DiagnosticSeverity.Warning,
                "旧合同未强制解释复核；数值验收不能证明源形状正确。完整新建读图任务必须设置 require_interpretation_review=true。", "drawing_context.require_interpretation_review"));
        foreach (var fact in context.Dimensions)
        {
            if (fact.NominalValue is { } nominal)
            {
                if (!ValidDeviation(fact))
                    errors.Add(new("SOURCE_DEVIATION_INVALID", DiagnosticSeverity.Error, "尺寸 " + fact.Id + " 必须分别记录原标称值、执行值、授权依据及偏差推导。", "drawing_context.dimensions"));
            }
            else if (fact.DeviationReason is not null || fact.DeviationAuthorization is not null)
                errors.Add(new("SOURCE_DEVIATION_INVALID", DiagnosticSeverity.Error, "偏差记录缺少原标称值：" + fact.Id, "drawing_context.dimensions"));
        }
        return errors;
    }
    // Source ink still states the nominal value; operation and verification bindings use executed Value.
    public static double SourceLiteralValue(DrawingDimensionFact fact) => ValidDeviation(fact) ? fact.NominalValue!.Value : fact.Value;
    private static bool ValidDeviation(DrawingDimensionFact fact) => fact.NominalValue is { } nominal && double.IsFinite(nominal) &&
        double.IsFinite(fact.Value) && nominal!=fact.Value && !string.IsNullOrWhiteSpace(fact.DeviationReason) &&
        !string.IsNullOrWhiteSpace(fact.DeviationAuthorization) && fact.Status==DrawingFactStatus.Derived && !string.IsNullOrWhiteSpace(fact.Derivation);
    private static bool ValidBox(DrawingReviewBox? b) => b is not null && double.IsFinite(b.Left) && double.IsFinite(b.Top) && double.IsFinite(b.Right) && double.IsFinite(b.Bottom)
        && b.Left >= 0 && b.Top >= 0 && b.Right <= 1 && b.Bottom <= 1 && b.Right > b.Left && b.Bottom > b.Top;
}
