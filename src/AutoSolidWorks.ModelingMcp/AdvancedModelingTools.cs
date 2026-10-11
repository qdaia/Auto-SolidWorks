using System.ComponentModel;
using System.IO;
using CadModeling.Core;
using CadModeling.Ir;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class AdvancedModelingTools
{
    [McpServerTool(Name="cad_capture_projection",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("从已保存的原生零件在临时工程图中提取 T09 可见／隐藏投影图元；需要已锁定的源 SHA 和视图坐标系。提取快照用于与源图纸比较，不代表图纸等价性验收通过。")]
    public static Task<ProjectionCaptureResult> CaptureProjection(IModelingExecutor executor,ProjectionCaptureRequest request,CancellationToken cancellationToken=default)=>
        executor.CaptureProjectionAsync(request,cancellationToken);

    [McpServerTool(Name="cad_capture_section",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("按已锁定的剖面定义从已保存的原生零件提取 T10 有界剖面轮廓。不支持或不完整的提取结果不能认证剖面等价性。")]
    public static Task<SectionCaptureResult> CaptureSection(IModelingExecutor executor,SectionCaptureRequest request,CancellationToken cancellationToken=default)=>
        executor.CaptureSectionAsync(request,cancellationToken);

    [McpServerTool(Name="cad_verify_revolved_family",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("将 T17 有限旋转类轮廓与实际已保存并重开的原生模型核对。检查绑定独立地将每个源台阶事实映射到必要的原生直径／轴向长度尺寸；探测请求不能自行定义必要尺寸集合。T08/T09 输入必须由对应生成器产生。")]
    public static Task<Stage4SavedModelAcceptance<RevolvedFamilyObservation>> VerifyRevolvedFamily(
        Stage4SavedModelAcceptanceWorkflow workflow,string nativePath,RevolvedFamilyProfile profile,RevolvedFamilyInspectionBinding binding,Stage4EvidenceBundle evidence,CancellationToken cancellationToken=default)=>
        workflow.VerifyRevolvedAsync(AbsolutePath(nativePath),profile,binding,evidence,cancellationToken);

    [McpServerTool(Name="cad_verify_hole_group",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("将 T18 有限孔组与实际已保存并重开的原生模型核对，逐一匹配测得的孔实例与原生阵列。解析锥形沉孔需要完整剪裁圆锥边界；装饰螺纹需要原生规格、尺寸、深度／模式和入口绑定。缺失或有歧义的采集结果保持不可验证。需要逐孔 T08 和已锁定的 T09 证据；不认证实体螺旋螺纹。")]
    public static Task<Stage4SavedModelAcceptance<HoleGroupObservation>> VerifyHoleGroup(
        Stage4SavedModelAcceptanceWorkflow workflow,string nativePath,HoleGroupProfile profile,HoleGroupInspectionBinding binding,Stage4EvidenceBundle evidence,CancellationToken cancellationToken=default)=>
        workflow.VerifyHoleGroupAsync(AbsolutePath(nativePath),profile,binding,evidence,cancellationToken);

    [McpServerTool(Name="cad_verify_edge_treatment",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("将 T19 轮廓圆弧／实体圆角／倒角与实际已保存并重开的原生模型核对。读取命名原生尺寸和 T06 几何引用；修复候选必须提供完整 T14 尝试／执行／覆盖／检查包，以及由最终验收重新计算的 T13 差异。")]
    public static Task<Stage4SavedModelAcceptance<EdgeTreatmentObservation>> VerifyEdgeTreatment(
        Stage4SavedModelAcceptanceWorkflow workflow,string nativePath,EdgeTreatmentIntent intent,EdgeTreatmentInspectionBinding binding,Stage4RepairEvidenceBundle? repair=null,CancellationToken cancellationToken=default)=>
        workflow.VerifyEdgeTreatmentAsync(AbsolutePath(nativePath),intent,binding,repair,cancellationToken);

    private static string AbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("请使用绝对输出路径。");
        return Path.GetFullPath(path);
    }
}
