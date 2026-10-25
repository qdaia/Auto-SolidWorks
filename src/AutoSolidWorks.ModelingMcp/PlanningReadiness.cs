using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;

/// <summary>Advisory grouping of existing facts and diagnostics; never modifies compilation or source evidence.</summary>
public static class PlanningReadiness
{
    public static JsonObject Collect(GenericModelDraft? draft, JsonObject compilation)
    {
        var diagnostics = compilation["diagnostics"] as JsonArray ?? [];
        var errors = diagnostics.Select((value, index) => (Value: value as JsonObject, Index: index))
            .Where(x => IsError(x.Value)).ToArray();
        var groupedErrors = new JsonArray(errors.GroupBy(x => (
            Code: x.Value!["code"]?.GetValue<string>(), Path: x.Value!["path"]?.GetValue<string>(),
            Message: x.Value!["message"]?.GetValue<string>()))
            .Select(g => (JsonNode?)new JsonObject {
                ["code"] = g.Key.Code, ["path"] = g.Key.Path, ["message"] = g.Key.Message,
                ["diagnostic_indices"] = JsonSerializer.SerializeToNode(g.Select(x => x.Index).ToArray()),
                ["suggested_actions"] = Strings(g.Select(x => x.Value!["suggested_action"]?.GetValue<string>()).OfType<string>())
            }).ToArray());

        var gaps = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (draft?.DrawingContext is { } context)
        {
            var criticalBindings = context.Features.Where(f => f.Critical).SelectMany(f => f.CriticalParameters)
                .Select(b => b.DimensionId).ToHashSet(StringComparer.Ordinal);
            foreach (var fact in context.Dimensions)
            {
                var reasons = errors.Where(e => e.Value!["path"]?.GetValue<string>() == "drawing_context.dimensions." + fact.Id)
                    .Select(e => e.Value!["code"]?.GetValue<string>())
                    .Where(code => code is "DIMENSION_LITERAL_VALUE" or "DIMENSION_LITERAL_KIND" or "DIMENSION_LITERAL_UNIT" or "DIMENSION_CROSS_VIEW_CONFLICT" ||
                        code?.StartsWith("DIMENSION_CALLOUT_", StringComparison.Ordinal) == true)
                    .OfType<string>().ToArray();
                if (reasons.Length == 0) continue;
                var gap = GetGap(gaps, "dimension-source:" + fact.Id, "dimension_source_conflict");
                Append(gap, "dimension_ids", [fact.Id]); Append(gap, "view_ids", fact.ViewIds);
                Append(gap, "source_literals", [fact.SourceLiteral]); Append(gap, "reasons", reasons);
                Append(gap, "parameter_targets", [fact.OperationId + "." + fact.ParameterPath]);
            }
            foreach (var fact in context.Dimensions.Where(d => (d.Critical || criticalBindings.Contains(d.Id))
                && d.Status is DrawingFactStatus.Unknown or DrawingFactStatus.Assumed))
            {
                // Only the same declared operation field is deduplicated; equal numeric values prove no correspondence.
                var key = !string.IsNullOrWhiteSpace(fact.OperationId) && !string.IsNullOrWhiteSpace(fact.ParameterPath)
                    ? "dimension:" + JsonSerializer.Serialize(new[] { fact.OperationId, fact.ParameterPath }) : "dimension-id:" + fact.Id;
                var gap = GetGap(gaps, key, "dimension");
                Append(gap, "dimension_ids", [fact.Id]);
                Append(gap, "view_ids", fact.ViewIds);
                Append(gap, "source_literals", [fact.SourceLiteral]);
                Append(gap, "reasons", [fact.Status == DrawingFactStatus.Unknown ? "declared_unknown" : "declared_assumed"]);
                Append(gap, "parameter_targets", [fact.OperationId + "." + fact.ParameterPath]);
                Append(gap, "feature_ids", context.Features.Where(f => f.CriticalParameters.Any(b => b.DimensionId == fact.Id)).Select(f => f.Id));
                // Unknown/assumed Value is deliberately not returned as a proposed answer.
            }
            foreach (var feature in context.Features.Where(f => f.Critical && f.Status is DrawingFactStatus.Unknown or DrawingFactStatus.Assumed))
            {
                var gap = FeatureGap(gaps, feature.Id, feature.ViewIds, feature.SourceLiteral);
                Append(gap, "reasons", [feature.Status == DrawingFactStatus.Unknown ? "declared_unknown_topology" : "declared_assumed_topology"]);
            }
            foreach (var review in context.InterpretationReviews)
            {
                var feature = context.Features.FirstOrDefault(f => f.Id == review.FeatureId && f.Critical);
                var selected = review.Hypotheses.FirstOrDefault(h => h.Id == review.SelectedHypothesisId);
                if (feature is null || selected is null) continue; // Malformed review needs agent repair, not a guessed user question.
                var contradicted = selected.Observations.Any(o => o.Contradicts);
                var ambiguous = review.Hypotheses.Any(h => h.Id != selected.Id && !h.Observations.Any(o => o.Contradicts));
                if (!contradicted && !ambiguous) continue;
                var gap = FeatureGap(gaps, feature.Id, feature.ViewIds, feature.SourceLiteral);
                if (contradicted) Append(gap, "reasons", ["declared_source_counterevidence"]);
                if (ambiguous) Append(gap, "reasons", ["unexcluded_source_alternative"]);
            }
        }
        var coverage = draft?.DrawingContext is { } sourceContext ? DimensionCalloutValidation.Assess(sourceContext) : [];
        return new JsonObject {
            ["annotation_components"] = JsonSerializer.SerializeToNode(draft?.DrawingContext?.AnnotationLedger?.Annotations.Select(a => new { a.Id, parsed = HoleCalloutParser.Parse(a.SourceLiteral, a.LengthUnit) }).ToArray() ?? [], ModelingIrJson.Options),
            ["annotation_components_section"] = "/readiness/annotation_components",
            ["callout_coverage"] = JsonSerializer.SerializeToNode(coverage, ModelingIrJson.Options),
            ["callout_coverage_count"] = coverage.Count,
            ["callout_source_review_required_count"] = coverage.Count(a => a.SourceReviewRequired),
            ["callout_coverage_section"] = "/readiness/callout_coverage",
            ["status"] = compilation["success"]?.GetValue<bool>() == true ? "ready" : "blocked",
            ["source_gap_count"] = gaps.Count, ["error_count"] = errors.Length,
            ["error_group_count"] = groupedErrors.Count,
            ["agent_source_review_required"] = gaps.Count > 0,
            ["user_questions_automatic"] = false,
            ["source_gaps"] = new JsonArray(gaps.Values.Select(g => (JsonNode?)g).ToArray()),
            ["error_groups"] = groupedErrors,
            ["source_gaps_section"] = "/readiness/source_gaps",
            ["error_groups_section"] = "/readiness/error_groups"
        };
    }

    public static JsonObject Summarize(JsonObject readiness)
    {
        var result = CompactResponses.Summarize(readiness);
        var annotations = readiness["annotation_components"] as JsonArray ?? [];
        result["annotation_components_count"] = annotations.Count;
        result["annotation_components_truncated"] = annotations.Count > 12;
        result["annotation_components"] = new JsonArray(annotations.Take(12).Select(v => v?.DeepClone()).ToArray());
        var coverage = readiness["callout_coverage"] as JsonArray ?? [];
        result["callout_coverage_truncated"] = coverage.Count > 12;
        result["callout_coverage"] = new JsonArray(coverage.Take(12).Select(v => v?.DeepClone()).ToArray());
        foreach (var key in new[] { "source_gaps", "error_groups" })
        {
            var entries = (JsonArray)readiness[key]!;
            result[key + "_truncated"] = entries.Count > 12;
            result[key] = new JsonArray(entries.Take(12).OfType<JsonObject>().Select(entry => {
                var item = CompactResponses.Summarize(entry);
                foreach (var pair in entry.Where(p => p.Value is JsonArray && p.Key != "source_literals"))
                {
                    var values = (JsonArray)pair.Value!;
                    item[pair.Key] = new JsonArray(values.Take(12).Select(v => v?.DeepClone()).ToArray());
                    item[pair.Key + "_truncated"] = values.Count > 12;
                }
                return (JsonNode?)item;
            }).ToArray());
        }
        return result;
    }

    private static bool IsError(JsonObject? diagnostic)
    {
        if (diagnostic?["severity"] is not JsonValue value) return false;
        return value.TryGetValue<int>(out var numeric) ? numeric == 2
            : value.TryGetValue<string>(out var text) && text.Equals("Error", StringComparison.OrdinalIgnoreCase);
    }
    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Distinct(StringComparer.Ordinal).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    private static JsonObject GetGap(Dictionary<string, JsonObject> gaps, string key, string kind)
    {
        if (gaps.TryGetValue(key, out var existing)) return existing;
        var gap = new JsonObject { ["key"] = key, ["kind"] = kind,
            ["resolution_route"] = "agent_source_review_then_user_if_still_blocked" };
        gaps.Add(key, gap);
        return gap;
    }
    private static JsonObject FeatureGap(Dictionary<string, JsonObject> gaps, string id, IReadOnlyList<string> views, string literal)
    {
        var gap = GetGap(gaps, "feature:" + id, "feature_interpretation");
        Append(gap, "feature_ids", [id]); Append(gap, "view_ids", views); Append(gap, "source_literals", [literal]);
        return gap;
    }
    private static void Append(JsonObject gap, string name, IEnumerable<string> values)
    {
        var existing = (gap[name] as JsonArray)?.Select(v => v!.GetValue<string>()) ?? [];
        gap[name] = Strings(existing.Concat(values));
    }
}
