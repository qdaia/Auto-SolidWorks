using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using CadModeling.Core;
using CadModeling.Ir;

public sealed record ExecutionStatusSummary(string? RequestId, bool Pending, bool PauseAccepted, bool OutcomeUnknown,
    bool DeadlineExceeded, bool ServerReached, string? Error, string? ErrorCode, bool? Success, string Status,
    string? NativePath, string? ArtifactId, string? ArtifactSha256, string? ArtifactPath, string? ArtifactError);

public static class CompactResponses
{
    public static void ValidateDetail(string detail)
    { if (detail is not ("summary" or "full")) throw new ArgumentException("responseDetail 必须是 summary 或 full。"); }

    public static object Present(CompactArtifactStore store, object result, string detail)
    {
        ValidateDetail(detail);
        var (receipt, artifactError) = TrySave(store, result);
        if (detail == "full") return result;
        var node = JsonSerializer.SerializeToNode(result, ModelingIrJson.Options) as JsonObject ?? new();
        var summary = Summarize(node);
        summary["artifact_id"] = receipt?.ArtifactId; summary["artifact_sha256"] = receipt?.Sha256;
        summary["artifact_path"] = receipt?.Path; summary["artifact_error"] = artifactError; summary["response_detail"] = "summary";
        summary["full_drawing_equivalence"] = false;
        return summary;
    }

    public static ExecutionStatusSummary Status(CompactArtifactStore store, ExecutorServiceResponse result)
    {
        var (receipt, artifactError) = TrySave(store, result);
        var unknown = result.OutcomeUnknown || result.Execution?.OutcomeUnknown == true || result.Inspection?.OutcomeUnknown == true || result.Assembly?.OutcomeUnknown == true
            || result.Drawing?.OutcomeUnknown == true || result.Projection?.OutcomeUnknown == true || result.Section?.OutcomeUnknown == true || result.Observation?.OutcomeUnknown == true;
        var success = result.Execution?.Success ?? result.Inspection?.Success ?? result.Assembly?.Success ?? result.Drawing?.Success;
        if ((result.Pending || unknown) && success == true) success = null;
        var status = result.Pending ? "pending" : unknown ? "outcome_unknown" : result.Error is not null ? "error"
            : result.Execution?.Status ?? (success is not null ? "completed" : result.PauseAccepted ? "pause_requested" : "status_unavailable");
        return new(result.RequestId ?? result.Execution?.RequestId ?? result.Inspection?.RequestId ?? result.Assembly?.RequestId,
            result.Pending, result.PauseAccepted, unknown, result.DeadlineExceeded, result.ServerReached,
            result.Error, result.ErrorCode, success, status, result.Execution?.NativePath ?? result.Assembly?.NativePath,
            receipt?.ArtifactId, receipt?.Sha256, receipt?.Path, artifactError);
    }

    private static (ArtifactReceipt? Receipt, string? Error) TrySave(CompactArtifactStore store, object result)
    {
        try { return (store.SaveResult(result), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return (null, "完整结果保存失败；原始执行状态保留，请勿因此重放修改：" + error.Message); }
    }

    public static JsonObject Summarize(JsonObject node)
    {
        var summary = new JsonObject();
        // Preserve ALL scalar state, including failure/incompleteness flags; omit only bulky containers.
        foreach (var pair in node)
            if (pair.Value is not (JsonArray or JsonObject))
            {
                var text = pair.Value is JsonValue scalar && scalar.TryGetValue<string>(out var str) ? str : null;
                if (text is { Length: > 1200 } && pair.Key is "message" or "suggested_action" or "source_text")
                { summary[pair.Key] = text[..1200]; summary[pair.Key + "_truncated"] = true; }
                else summary[pair.Key] = pair.Value?.DeepClone();
            }
            else if (pair.Value is JsonArray list) summary[pair.Key + "_count"] = list.Count;
        foreach (var key in new[] { "geometry", "body_validity" })
            if (node[key] is { } value) summary[key] = value.DeepClone();
        foreach (var key in new[] { "drawing", "recovery", "source_manifest", "execution", "inspection", "assembly" })
            if (node[key] is JsonObject nested) summary[key] = Summarize(nested);
        if (node["verification"] is JsonObject verification)
        {
            var checks = verification["checks"] as JsonArray;
            summary["verification"] = new JsonObject {
                ["passed"] = verification["passed"]?.DeepClone(), ["scope"] = verification["scope"]?.DeepClone(),
                ["whole_model_inventory_checked"] = verification["whole_model_inventory_checked"]?.DeepClone(),
                ["check_count"] = checks?.Count ?? 0,
                ["failed_count"] = checks?.Count(c => c?["passed"]?.GetValue<bool>() == false) ?? 0 };
            if (checks is not null)
            {
                var failed = checks.OfType<JsonObject>().Where(c => c["passed"]?.GetValue<bool>() == false).ToArray();
                summary["verification"]!["failed_checks"] = new JsonArray(failed.Take(12).Select(c => (JsonNode?)Summarize(c)).ToArray());
                summary["verification"]!["failed_checks_truncated"] = failed.Length > 12;
            }
        }
        foreach (var key in new[] { "diagnostics", "evidence", "issues", "checks" })
        {
            if (node[key] is not JsonArray entries) continue;
            var actionable = entries.Where(e => e is JsonObject && (e["passed"]?.GetValue<bool>() == false
                || key is "diagnostics" or "issues" || e["code"]?.GetValue<string>()?.Contains("UNAVAILABLE", StringComparison.Ordinal) == true)).ToArray();
            summary[key + "_actionable_count"] = actionable.Length;
            summary[key + "_truncated"] = actionable.Length > 12;
            summary[key] = new JsonArray(actionable.Take(12).Select(e => (JsonNode?)Summarize((JsonObject)e!)).ToArray());
        }
        if (node["pages"] is JsonArray pages)
        {
            summary["pages"] = new JsonArray(pages.Take(12).OfType<JsonObject>().Select(p => (JsonNode?)Summarize(p)).ToArray());
            summary["pages_truncated"] = pages.Count > 12;
        }
        return summary;
    }
}
