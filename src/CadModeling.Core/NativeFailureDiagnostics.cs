using System.Globalization;
namespace CadModeling.Core;

public static class NativeFailureDiagnostics
{
    public static (string Code, string Message, string Hint) SketchStatus(int status) => status switch
    {
        4 => ("SKETCH_OVER_CONSTRAINED", "草图过约束（原生状态=4）。", "核对已有驱动尺寸和固定关系，撤回重复约束；不要继续补尺寸。"),
        _ => ("SKETCH_NOT_FULLY_DEFINED", "草图未通过完全定义验收（原生状态=" + status.ToString(CultureInfo.InvariantCulture) + "）。", "读取实际约束状态及驱动尺寸后修订草图；不要猜测尺寸名称。")
    };
    public static string RequirementCode(ModelVerificationResult result, bool saved = false) => result.Checks.Any(c => !c.Passed && c.Status == "mismatch")
        ? saved ? "SAVED_REQUIREMENT_MISMATCH" : "SOURCE_REQUIREMENT_MISMATCH"
        : saved ? "SAVED_REQUIREMENT_UNVERIFIABLE" : "SOURCE_REQUIREMENT_UNVERIFIABLE";
}
