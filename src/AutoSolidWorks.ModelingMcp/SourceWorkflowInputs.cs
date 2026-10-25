using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;

/// <summary>Maintains only explicitly declared source consumers; never guesses identity from equal numbers.</summary>
public static class SourceWorkflowInputs
{
    public static void ApplyDefaults(JsonObject context)
    {
        context["workflow_revision"] ??= SourceWorkflow.DefaultRevision;
        var fourStage=SourceWorkflow.IsFourStage(context["workflow_revision"]?.GetValue<string>());
        foreach(var key in new[]{"require_complete_bindings","require_dimension_evidence","require_geometry_anchors"})
            if(!context.ContainsKey(key))context[key]=true;
        if(!context.ContainsKey("require_interpretation_review"))context["require_interpretation_review"]=!fourStage;
        if(SourceWorkflow.RequiresDecomposition(context["workflow_revision"]?.GetValue<string>()) && !context.ContainsKey("require_shape_decomposition"))
            context["require_shape_decomposition"]=true;
    }
    public static JsonObject Summarize(JsonObject adoption)
    {
        var summary=adoption.DeepClone().AsObject();
        if(summary["unresolved_consumers"] is JsonArray issues)
        {
            summary["unresolved_consumer_count"]=issues.Count;
            summary["unresolved_consumers_truncated"]=issues.Count>12;
            summary["unresolved_consumers"]=new JsonArray(issues.Take(12).Select(x=>x?.DeepClone()).ToArray());
        }
        return summary;
    }
    public sealed record Prepared(JsonNode Input, JsonObject Adoption);
    public static Prepared Prepare(JsonNode input, bool contextOnly = false)
    {
        var root = input.DeepClone();
        var adoption = new JsonObject { ["drawing_backed"] = false };
        if ((contextOnly ? root : root["drawing_context"]) is not JsonObject context)
            return new(root, adoption);
        ApplyDefaults(context);
        var fourStage=SourceWorkflow.IsFourStage(context["workflow_revision"]?.GetValue<string>());
        var resolved = DimensionInputResolver.Normalize(context, true).Deserialize<DrawingPlanContext>(ModelingIrJson.Options)!;
        var facts = resolved.Dimensions.GroupBy(d => d.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var targets = new List<(string Fact, JsonNode Owner, string Path, string Location, DrawingValueUnit Unit)>();
        var issues = new JsonArray();
        var converted = 0; var existing = 0; var computed = 0;
        void Issue(string code, string location, string fact) => issues.Add(new JsonObject { ["code"] = code, ["location"] = location, ["dimension_id"] = fact });
        if (!contextOnly)
        {
            if(!fourStage && root["recovery"] is JsonObject recovery && recovery["directory"] is not null && recovery["enabled"]?.GetValue<bool>() != false)
                if(!recovery.ContainsKey("auto_select_checkpoint")) recovery["auto_select_checkpoint"] = true;
            var operations = (root["operations"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
            foreach (var fact in resolved.Dimensions)
            {
                var matches = operations.Where(o => o["id"]?.GetValue<string>() == fact.OperationId).ToArray();
                if (matches.Length != 1) { Issue("consumer_operation_missing_or_ambiguous", fact.OperationId, fact.Id); continue; }
                // Operation lengths are millimetres, angles degrees, counts unitless in the typed draft.
                var unit = fact.Unit is DrawingValueUnit.Inch or DrawingValueUnit.Meter ? DrawingValueUnit.Millimeter : fact.Unit;
                targets.Add((fact.Id, matches[0], fact.ParameterPath, "operations." + fact.OperationId + "." + fact.ParameterPath, unit));
            }
            if (root["verification"] is JsonObject verification)
            {
                var checks = verification.Where(p => p.Key != "bindings" && p.Value is JsonArray)
                    .SelectMany(p => p.Value!.AsArray().OfType<JsonObject>().Select(c => (Group: p.Key, Check: c))).ToArray();
                foreach (var binding in (verification["bindings"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var id = binding["dimension_id"]!.GetValue<string>();
                    var checkId = binding["check_id"]!.GetValue<string>();
                    var path = binding["parameter_path"]!.GetValue<string>();
                    var matches = checks.Where(c => c.Check["id"]?.GetValue<string>() == checkId).ToArray();
                    if (matches.Length != 1 || !facts.TryGetValue(id, out var values) || values.Length != 1)
                    { Issue("consumer_verification_missing_or_ambiguous", checkId + "." + path, id); continue; }
                    var unit = values[0].Unit is DrawingValueUnit.Inch or DrawingValueUnit.Meter ? DrawingValueUnit.Millimeter : values[0].Unit;
                    if (matches[0].Group == "native_dimensions" && path == "value")
                        unit = Enum.Parse<DrawingValueUnit>(matches[0].Check["unit"]?.GetValue<string>() ?? "Millimeter");
                    targets.Add((id, matches[0].Check, path, "verification." + checkId + "." + path, unit));
                }
            }
        }
        foreach (var group in targets.GroupBy(t => t.Location, StringComparer.Ordinal))
        {
            var target = group.First();
            if (group.Select(t => t.Fact).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                var declared = group.Select(t => facts.TryGetValue(t.Fact,out var f) && f.Length==1 ? f[0] : null).ToArray();
                var primary = declared.Where(f=>f is not null && f.CorroboratesDimensionId is null).ToArray();
                if(primary.Length!=1 || declared.Any(f=>f is null || f.Id!=primary[0]!.Id && f.CorroboratesDimensionId!=primary[0]!.Id))
                    throw new JsonException("SOURCE_CONSUMER_AMBIGUOUS: multiple dimensions declare " + target.Location);
                target = group.First(t=>t.Fact==primary[0]!.Id);
            }
            if (!facts.TryGetValue(target.Fact, out var values) || values.Length != 1)
            { Issue("source_missing_or_ambiguous", target.Location, target.Fact); continue; }
            var fact = values[0];
            if(target.Path.Split('.')[^1] is "hole_center_count" or "expected_count" or "cone_included_angle_degrees" ||
                target.Path=="radius_mm" && (root["verification"]?["cylinder_groups"] as JsonArray)?.Any(c=>ReferenceEquals(c,target.Owner))==true)
            { computed++; continue; } // Derived structural fields are validated, never overwritten or advertised as live scalar consumers.
            var current = Field(target.Owner, target.Path);
            if (current is JsonObject reference && reference.ContainsKey("$dimension"))
            {
                if (reference["$dimension"]?.GetValue<string>() != target.Fact)
                    throw new JsonException("SOURCE_CONSUMER_ID_CONFLICT: " + target.Location);
                existing++; continue; // The strict resolver validates shape, unit and value.
            }
            if (fact.Status is not (DrawingFactStatus.Stated or DrawingFactStatus.Derived) ||
                current is not JsonValue numeric || !numeric.TryGetValue<double>(out var actual))
            { Issue("consumer_unresolved", target.Location, target.Fact); continue; }
            var expected = fact.Value * DimensionExpressions.Scale(fact.Unit) / DimensionExpressions.Scale(target.Unit);
            if (DimensionExpressions.Family(fact.Unit) != DimensionExpressions.Family(target.Unit) ||
                !double.IsFinite(expected) || Math.Abs(actual - expected) > 1e-7 * Math.Max(1, Math.Abs(expected)))
            { Issue("consumer_value_conflict", target.Location, target.Fact); continue; }
            var value = new JsonObject { ["$dimension"] = fact.Id, ["unit"] = target.Unit.ToString() };
            if (current.Parent is JsonArray array) array[array.IndexOf(current)] = value;
            else current.Parent!.AsObject()[target.Path.Split('.')[^1]] = value;
            converted++;
        }
        adoption["drawing_backed"] = true;
        adoption["workflow_revision"] = resolved.WorkflowRevision;
        adoption["source_dimensions"] = resolved.Dimensions.Count;
        adoption["formula_dimensions"] = resolved.Dimensions.Count(d => d.Expression is not null);
        adoption["geometry_anchors"] = resolved.GeometryAnchors.Count;
        adoption["anchors_required"] = resolved.RequireGeometryAnchors;
        adoption["shape_decomposition_required"] = resolved.RequireShapeDecomposition;
        adoption["shape_decomposition_features"] = resolved.ShapeDecomposition?.Features?.Count ?? 0;
        adoption["stage_declared"] = resolved.StageScope is not null;
        adoption["declared_consumers"] = targets.Select(t => t.Location).Distinct(StringComparer.Ordinal).Count();
        adoption["converted_to_references"] = converted;
        adoption["existing_references"] = existing;
        adoption["computed_consumers_validated_by_compiler"] = computed;
        adoption["unresolved_consumers"] = issues;
        adoption["status"] = "prepared_not_validated";
        return new(root, adoption);
    }

    private static JsonNode? Field(JsonNode node, string path)
    {
        JsonNode? current = node;
        foreach (var token in path.Split('.'))
            current = current switch
            {
                JsonObject obj => obj[token],
                JsonArray array when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < array.Count => array[i],
                _ => null
            };
        return current;
    }
}
