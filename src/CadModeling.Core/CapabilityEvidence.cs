using System.Security.Cryptography;
using System.Text.Json.Nodes;
namespace CadModeling.Core;
public static class CapabilityEvidence
{
    public static JsonObject Read(string packageRoot,string runtimeVersion)
    {
        var result=new JsonObject{["status"]="not_run",["runtime_version"]=runtimeVersion,["task_families"]=new JsonArray()};
        var path=Path.Combine(packageRoot,"skills","auto-solidworks","references","current-native-evidence.json");
        if(!File.Exists(path))return result;
        try
        {
            var index=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if(index["schema_version"]?.GetValue<int>()!=1||index["runtime_version"]?.GetValue<string>()!=runtimeVersion)
                throw new InvalidDataException("证据版本与当前运行时不一致。");
            var runtimes=index["runtime_hashes"]?.AsObject()??throw new InvalidDataException("缺少运行库身份。");
            foreach(var required in new[]{"runtime/mcp/AutoSolidWorks.ModelingMcp.dll","runtime/mcp/CadModeling.Core.dll",
                "runtime/mcp/CadModeling.Ir.dll","runtime/executor/CadModeling.Executor.SolidWorks.dll","runtime/executor/CadModeling.Core.dll","runtime/executor/CadModeling.Ir.dll"})
                if(runtimes[required] is null)throw new InvalidDataException("证据运行库身份不完整。");
            foreach(var pair in runtimes)Verify(packageRoot,pair.Key,pair.Value!.GetValue<string>());
            if(index["task_families"] is not JsonArray inputFamilies||inputFamilies.Count==0||inputFamilies.Any(f=>f is not JsonObject))
                throw new InvalidDataException("任务证据列表缺失或格式错误。");
            var families=new JsonArray();
            foreach(var family in inputFamilies.OfType<JsonObject>())
            {
                var item=family.DeepClone().AsObject();
                try
                {
                    if(item["scope"] is not JsonArray scope||scope.Count==0||string.IsNullOrWhiteSpace(item["solidworks_revision"]?.GetValue<string>()))
                        throw new InvalidDataException("缺少任务范围或原生版本。");
                    if(item["files"] is not JsonArray files||files.Count<2||files.Any(f=>f is not JsonObject))
                        throw new InvalidDataException("任务证据库存不完整。");
                    foreach(var file in files.OfType<JsonObject>())
                        Verify(packageRoot,Required(file,"path"),Required(file,"sha256"));
                    var receiptPath=SafePath(packageRoot,Required(item,"inspection_path"));
                    var receipt=JsonNode.Parse(File.ReadAllText(receiptPath))?.AsObject()??throw new InvalidDataException("回执为空。");
                    var receiptEntry=files.OfType<JsonObject>().Any(f=>f["path"]?.GetValue<string>()==Required(item,"inspection_path"));
                    if(!receiptEntry||receipt["success"]?.GetValue<bool>()!=true||receipt["model_reopened"]?.GetValue<bool>()!=true||
                        receipt["capture_complete"]?.GetValue<bool>()!=true||receipt["body_validity"]?["passed"]?.GetValue<bool>()!=true||
                        receipt["rebuild_succeeded"]?.GetValue<bool>()!=true||receipt["geometry_status"]?.GetValue<string>()!="passed"||
                        receipt["verification"]?["passed"]?.GetValue<bool>()!=true||receipt["verification"]?["whole_model_inventory_checked"]?.GetValue<bool>()!=true||
                        receipt["parametric_status"]?.GetValue<string>()!="passed")
                        throw new InvalidDataException("原生验收回执不完整或未通过。");
                    var model=Required(item,"model_path");
                    Verify(packageRoot,model,Required(receipt,"model_sha256"));
                    if(!files.OfType<JsonObject>().Any(f=>f["path"]?.GetValue<string>()==model))
                        throw new InvalidDataException("模型未进入证据库存。");
                    if(receipt["editability_probes"] is JsonArray probes&&probes.Any(p=>p?["passed"]?.GetValue<bool>()!=true))
                        throw new InvalidDataException("编辑探针未通过。");
                    item["status"]="native_verified_scoped";
                }
                catch(Exception error) when(error is InvalidDataException or IOException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
                { item["status"]="stale_or_unverifiable";item["reason"]=error.Message; }
                families.Add(item);
            }
            result["task_families"]=families;
            result["status"]=families.Any(f=>f?["status"]?.GetValue<string>()=="native_verified_scoped")?"scoped_native_verified":"not_run";
            result["limits"]="只认证列出的任务、检查范围和SolidWorks修订号；不继承上游或历史版本的认证。";
        }
        catch(Exception error) when(error is InvalidDataException or IOException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { result["status"]="stale_or_unverifiable";result["reason"]=error.Message; }
        return result;
    }
    private static string SafePath(string root,string relative)
    {
        if(Path.IsPathFullyQualified(relative))throw new InvalidDataException("证据必须使用包内相对路径。");
        var target=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!target.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("证据路径越界。");
        return target;
    }
    private static string Required(JsonObject node,string key) =>
        node[key] is JsonValue value&&value.TryGetValue<string>(out var text)&&!string.IsNullOrWhiteSpace(text)
            ?text:throw new InvalidDataException("缺少证据字段："+key);
    private static void Verify(string root,string relative,string expected)
    {
        var target=SafePath(root,relative);
        if(!File.Exists(target)||!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).Equals(expected,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("证据文件或运行库身份已变："+relative);
    }
}
