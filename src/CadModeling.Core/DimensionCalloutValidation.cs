using System.Globalization;
using System.Text.RegularExpressions;
using CadModeling.Ir;

namespace CadModeling.Core;

/// <summary>Checks AI-transcribed, unambiguous callouts; never recognizes pixels or infers missing dimensions.</summary>
public static partial class DimensionCalloutValidation
{
    public sealed record Assessment(string DimensionId, DrawingCalloutCoverage AutomaticCoverage,
        bool VisualReviewComplete, bool SourceReviewRequired, string Scope, IReadOnlyList<ModelingDiagnostic> Diagnostics);

    public static Assessment Assess(DrawingDimensionFact fact, bool requireReview = false)
    {
        var diagnostics = Validate(fact).ToList();
        var literal = Callout().Match(fact.SourceLiteral.Trim());
        var supported = literal.Success && !(literal.Groups["count"].Success && literal.Groups["symbol"].Length == 0);
        var coverage = fact.Status != DrawingFactStatus.Stated ? DrawingCalloutCoverage.NotApplicable :
            diagnostics.Count > 0 ? DrawingCalloutCoverage.Conflict : supported ? DrawingCalloutCoverage.Verified :
            PartialCallout().IsMatch(fact.SourceLiteral.Trim()) ? DrawingCalloutCoverage.Partial : DrawingCalloutCoverage.Unsupported;
        var visualComplete = false;
        void Error(string code, string message) => diagnostics.Add(new(code, DiagnosticSeverity.Error, message, "drawing_context.dimensions." + fact.Id));
        if (fact.CalloutReview is { } review)
        {
            if (review.SourceLiteral != fact.SourceLiteral)
                Error("DIMENSION_CALLOUT_REVIEW_STALE", "复核记录的原文已改变；重新查看原图并复核，不能沿用旧记录。");
            else if (review.State != DrawingReviewState.Reviewed || review.UnresolvedTokens is null || review.UnresolvedTokens.Count > 0 ||
                string.IsNullOrWhiteSpace(review.SelectedComponent) || string.IsNullOrWhiteSpace(review.Evidence) ||
                fact.BindingEvidence is not { } evidence || !Enum.IsDefined(review.QuantityKind) ||
                review.QuantityKind != evidence.QuantityKind || review.Unit != fact.Unit ||
                !DimensionExpressions.Equal(review.Value, SourceInterpretationValidation.SourceLiteralValue(fact)))
                Error("DIMENSION_CALLOUT_REVIEW_INVALID", "逐项复核须明确所选量、原单位和标称值，提供原图解释并解决未读清符号；不能只标记已审。");
            else visualComplete = true;
        }
        var needsReview = coverage is DrawingCalloutCoverage.Partial or DrawingCalloutCoverage.Unsupported;
        if (requireReview && needsReview && !visualComplete)
            Error("DIMENSION_CALLOUT_REVIEW_REQUIRED", "此关键标注尚未被自动语法完整核对；实际查看原图并填写 callout_review。未解析不等于数值正确。");
        if (diagnostics.Any(d => d.Code != "DIMENSION_CALLOUT_REVIEW_REQUIRED")) coverage = DrawingCalloutCoverage.Conflict;
        return new(fact.Id, coverage, visualComplete, needsReview && !visualComplete,
            "nominal_quantity_and_explicit_unit_only; no_tolerance_or_manufacturing_certification", diagnostics);
    }

    public static IReadOnlyList<Assessment> Assess(DrawingPlanContext context)
    {
        var critical = context.Features.Where(f => f.Critical).SelectMany(f => f.CriticalParameters).Select(p => p.DimensionId).ToHashSet(StringComparer.Ordinal);
        return context.Dimensions.Select(f =>
        {
            if (SourceWorkflow.UsesAnnotationComponents(context.WorkflowRevision) && f.Status == DrawingFactStatus.Stated &&
                context.AnnotationLedger?.Annotations.FirstOrDefault(a => a.Id == f.SourceAnnotationId) is { } annotation)
            {
                var parsed = HoleCalloutParser.Parse(annotation.SourceLiteral, annotation.LengthUnit);
                var component = parsed.Components.FirstOrDefault(c => c.Key == f.SourceComponentKey);
                var matches = parsed.Complete && component?.Value is { } value && f.SourceLiteral == annotation.SourceLiteral &&
                    DimensionExpressions.Equal(value, SourceInterpretationValidation.SourceLiteralValue(f)) && component.Unit == f.Unit && component.QuantityKind == f.BindingEvidence?.QuantityKind;
                return new Assessment(f.Id, matches ? DrawingCalloutCoverage.Verified : DrawingCalloutCoverage.Conflict,
                    annotation.State == DrawingReviewState.Reviewed, !matches, "bounded_typed_components; retained_manufacturing_metadata_not_certified",
                    matches ? [] : [new("DIMENSION_CALLOUT_COMPONENT_CONFLICT", DiagnosticSeverity.Error, "标注分项身份或量值不符；修复整条账本。", "drawing_context.dimensions." + f.Id)]);
            }
            return Assess(f, SourceWorkflow.RequiresSourceReview(context.WorkflowRevision) && (f.Critical || critical.Contains(f.Id)));
        }).ToArray();
    }

    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingDimensionFact fact)
    {
        if (fact.Status != DrawingFactStatus.Stated) return [];
        var result = new List<ModelingDiagnostic>();
        void Error(string code, string message) => result.Add(new(code, DiagnosticSeverity.Error, message, "drawing_context.dimensions." + fact.Id));
        var literal = Callout().Match(fact.SourceLiteral.Trim());
        if (!literal.Success) return result; // Unsupported compound notes remain subject to source review.
        var symbol = literal.Groups["symbol"].Value.ToUpperInvariant();
        var quantity = fact.BindingEvidence?.QuantityKind;
        var isCount = quantity == DrawingQuantityKind.Count;
        var hasCount = literal.Groups["count"].Success;
        // A multiplier without a radius/diameter symbol could be a rectangle or a chamfer.
        if (hasCount && symbol.Length == 0) return result;
        var valueGroup = isCount && hasCount ? "count" : "value";
        if (!double.TryParse(literal.Groups[valueGroup].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        { Error("DIMENSION_LITERAL_VALUE", "原标注数值超出有限范围。"); return result; }
        if (!DimensionExpressions.Equal(value, SourceInterpretationValidation.SourceLiteralValue(fact)))
            Error("DIMENSION_LITERAL_VALUE", isCount && hasCount ? "孔/特征数量与原标注前缀不一致。" : "原标注标称值与声明值不符；公差、配合及数量前缀不能掩盖错值。");
        var leaf = fact.ParameterPath.Split('.').LastOrDefault() ?? "";
        if (!(isCount && hasCount) &&
            (symbol == "R" && (leaf.Contains("diameter", StringComparison.Ordinal) || quantity is not null and not DrawingQuantityKind.Radius) ||
             symbol is "Ø" or "⌀" or "Φ" or "∅" && (leaf.Contains("radius", StringComparison.Ordinal) || quantity is not null and not DrawingQuantityKind.Diameter)))
            Error("DIMENSION_LITERAL_KIND", "R 与直径符号必须绑定相应物理量；复合标注中的数量应单独绑定 Count。");
        var unit = literal.Groups["unit"].Value.ToLowerInvariant();
        if (!(isCount && hasCount) && ((unit == "mm" && fact.Unit != DrawingValueUnit.Millimeter) ||
            (unit == "in" && fact.Unit != DrawingValueUnit.Inch) || (unit == "m" && fact.Unit != DrawingValueUnit.Meter) ||
            (unit is "°" or "º" && fact.Unit != DrawingValueUnit.Degree)))
            Error("DIMENSION_LITERAL_UNIT", "原标注单位与尺寸事实单位不符；换算须显式派生。");
        return result;
    }

    // Full-string grammar prevents treating a depth, thread pitch or second diameter as a tolerance.
    // Fits and tolerances are retained in SourceLiteral; this check does not certify their manufacturing semantics.
    [GeneratedRegex(@"^\s*(?:(?<count>\d+)\s*[-x×]\s*)?(?<symbol>[RØ⌀φΦ∅])?\s*(?<value>[+-]?(?:\d+(?:\.\d+)?|\.\d+))\s*(?<unit>mm|in|m|°|º)?\s*(?:[A-Z]{1,2}\d{1,2})?\s*(?:(?:±|\+/-)\s*\d+(?:\.\d+)?|[+-]\s*\d+(?:\.\d+)?\s*/\s*[+-]\s*\d+(?:\.\d+)?)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Callout();

    [GeneratedRegex(@"^\s*(?:\d+\s*[-x×]\s*)?[RØ⌀φΦ∅]\s*[+-]?(?:\d+(?:\.\d+)?|\.\d+)(?:\s|[^\d.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartialCallout();
}
