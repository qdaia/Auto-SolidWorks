using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CadModeling.Core;
using CadModeling.Ir;

/// <summary>Normalizes a copy; immutable submitted input retains references for subsequent patches.</summary>
public static class DimensionInputResolver
{
    private static readonly JsonSerializerOptions Strict = new(ModelingIrJson.Options)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static JsonNode Normalize(JsonNode input, bool contextOnly)
    {
        var root = input.DeepClone();
        var contextNode = contextOnly ? root : root["drawing_context"];
        if (contextNode is null) return root;
        // Formula-owned values are calculated, not another editable copy of the same fact.
        if (contextNode["dimensions"] is JsonArray dims)
            foreach (var dim in dims.OfType<JsonObject>())
                if (dim["expression"] is not null && !dim.ContainsKey("value")) dim["value"] = 0;
        var context = contextNode.Deserialize<DrawingPlanContext>(Strict) ?? throw new JsonException("图纸上下文不能为空。");
        if (context.Dimensions.Any(d=>d.Expression is not null) || context.DimensionEquations.Count>0)
            context = DimensionExpressions.Resolve(context);
        if (contextOnly) return JsonSerializer.SerializeToNode(context, ModelingIrJson.Options)!;
        root["drawing_context"] = JsonSerializer.SerializeToNode(context, ModelingIrJson.Options);
        var facts = context.Dimensions.GroupBy(f=>f.Id).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
        var count = 0;
        JsonNode? Walk(JsonNode? node, string path, int depth, string field="")
        {
            if (++count > 100000 || depth > 64) throw new JsonException("DIMENSION_REFERENCE_LIMIT: 输入过大。");
            if (node is JsonObject reference && reference.ContainsKey("$dimension"))
            {
                        if (reference.Count != 2 || reference["$dimension"] is not JsonValue idNode || !idNode.TryGetValue<string>(out var id) ||
                            reference["unit"] is not JsonValue unitNode || !unitNode.TryGetValue<string>(out var unitName) ||
                            !Enum.TryParse<DrawingValueUnit>(unitName, false, out var unit) || !Enum.IsDefined(unit) || unit.ToString()!=unitName)
                            throw new JsonException("DIMENSION_REFERENCE_SHAPE: 引用必须只有 $dimension 和枚举字符串 unit。");
                        if (!facts.TryGetValue(id, out var matches) || matches.Length != 1)
                            throw new JsonException("DIMENSION_REFERENCE_ID: 缺失或不唯一的尺寸 " + id);
                        var f = matches[0];
                        if (f.Status is not (DrawingFactStatus.Stated or DrawingFactStatus.Derived) || !double.IsFinite(f.Value))
                            throw new JsonException("DIMENSION_REFERENCE_UNRESOLVED: " + id);
                        if (DimensionExpressions.Family(unit)!=DimensionExpressions.Family(f.Unit))
                            throw new JsonException("DIMENSION_REFERENCE_UNIT: 引用与源尺寸量纲不同。");
                        if ((field.EndsWith("_mm",StringComparison.Ordinal) || field.EndsWith("xmm",StringComparison.Ordinal) || field.EndsWith("ymm",StringComparison.Ordinal) || field.EndsWith("zmm",StringComparison.Ordinal) || path.Contains("_mm",StringComparison.Ordinal)) && unit != DrawingValueUnit.Millimeter ||
                            field.EndsWith("_degrees",StringComparison.Ordinal) && unit != DrawingValueUnit.Degree ||
                            (field == "count" || field.EndsWith("_count",StringComparison.Ordinal)) && unit != DrawingValueUnit.Unitless)
                            throw new JsonException("DIMENSION_REFERENCE_TARGET_UNIT: 目标字段单位不匹配 " + path + "/" + field);
                        var result = f.Value * DimensionExpressions.Scale(f.Unit) / DimensionExpressions.Scale(unit);
                        if (!double.IsFinite(result)) throw new JsonException("DIMENSION_REFERENCE_NONFINITE: 换算溢出。");
                        return JsonValue.Create(result);
            }
            if (node is JsonObject obj)
                foreach(var pair in obj.ToArray())
                { var child=Walk(pair.Value,path+"/"+pair.Key,depth+1,pair.Key); if(!ReferenceEquals(child,pair.Value))obj[pair.Key]=child; }
            else if (node is JsonArray array)
                for(var i=0;i<array.Count;i++)
                { var child=Walk(array[i],path+"/"+i,depth+1,field); if(!ReferenceEquals(child,array[i]))array[i]=child; }
            return node;
        }
        // Source facts/evidence are never rewritten from operation values.
        var resolvedContext = root["drawing_context"]!.DeepClone();
        root.AsObject().Remove("drawing_context");
        Walk(root,"",0);
        root["drawing_context"] = resolvedContext;
        return root;
    }
}
