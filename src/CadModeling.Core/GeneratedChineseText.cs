namespace CadModeling.Core;

/// <summary>Default text for newly generated CAD artifacts. Protocol identifiers remain invariant.</summary>
public static class GeneratedChineseText
{
    public const string Language = "zh-CN";
    public const string DrawingFont = "Microsoft YaHei";
    public const string ViewsSheet = "视图";
    public static string ParameterSheet(int page) => page == 1 ? "参数表" : $"参数表_{page}";
    public static string ViewName(string orientation) => orientation switch
    {
        "Front" => "主视图", "Top" => "俯视图", "Left" => "左视图",
        "Right" => "右视图", "Bottom" => "仰视图", "Back" => "后视图",
        "Isometric" => "等轴测图", _ => throw new ArgumentException("未知的标准视图方向。", nameof(orientation))
    };
    public static string ModelName(string template) => template switch
    {
        "rectangular_plate" => "矩形板", "cylinder" => "圆柱体",
        "fork_bracket" => "叉形支架", "variable_l_channel" => "变截面L形槽",
        _ => template
    };
    public static string ModelTemplate(string name) => name switch
    {
        "矩形板" => "rectangular_plate", "圆柱体" => "cylinder",
        "叉形支架" => "fork_bracket", "变截面L形槽" => "variable_l_channel",
        _ => name
    };
    public static string ParameterUnit(string unit) => unit switch
    {
        "Millimeter" => "毫米", "Degree" => "度", "Unitless" => "个",
        _ => throw new ArgumentException("未知的参数单位。", nameof(unit))
    };
    public static string ParameterLabel(string nativeName, string unit, int ordinal)
    {
        // A display label must not rename dimensions in the source model. The exact
        // native parameter path is retained in DrawingExportResult.SourceDimensions.
        var label = System.Text.RegularExpressions.Regex.Replace(nativeName, @"\bD\d+(?:@|$)", "");
        if (!System.Text.RegularExpressions.Regex.IsMatch(label, "[A-Za-z]")) return nativeName;
        var kind = unit switch { "Millimeter" => "长度参数", "Degree" => "角度参数", _ => "计数参数" };
        return kind + "_" + ordinal.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
    }
    public static string FeatureName(GenericOperationDraft operation, int ordinal) =>
        (operation.Type switch
        {
            GenericOperationKind.ProfileSketch => "轮廓草图",
            GenericOperationKind.ExtrudeBoss => "拉伸凸台",
            GenericOperationKind.ExtrudeCut => "拉伸切除",
            _ => NativeFeatureName(operation.Feature?.Kind)
        }) + "_" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string NativeFeatureName(CadModeling.Ir.NativeFeatureKind? kind) => kind switch
    {
        CadModeling.Ir.NativeFeatureKind.ReferencePlane => "基准面", CadModeling.Ir.NativeFeatureKind.ReferenceAxis => "基准轴",
        CadModeling.Ir.NativeFeatureKind.Chamfer => "倒角", CadModeling.Ir.NativeFeatureKind.Fillet => "圆角",
        CadModeling.Ir.NativeFeatureKind.RevolveBoss => "旋转凸台", CadModeling.Ir.NativeFeatureKind.RevolveCut => "旋转切除",
        CadModeling.Ir.NativeFeatureKind.Shell => "抽壳", CadModeling.Ir.NativeFeatureKind.Draft => "拔模",
        CadModeling.Ir.NativeFeatureKind.Mirror => "镜像", CadModeling.Ir.NativeFeatureKind.LinearPattern => "线性阵列",
        CadModeling.Ir.NativeFeatureKind.CircularPattern => "圆周阵列", CadModeling.Ir.NativeFeatureKind.Combine => "组合",
        CadModeling.Ir.NativeFeatureKind.MoveBody => "移动实体", CadModeling.Ir.NativeFeatureKind.LoftBoss => "放样凸台",
        CadModeling.Ir.NativeFeatureKind.LoftCut => "放样切除", CadModeling.Ir.NativeFeatureKind.SweepBoss => "扫描凸台",
        CadModeling.Ir.NativeFeatureKind.SweepCut => "扫描切除", CadModeling.Ir.NativeFeatureKind.Rib => "筋",
        CadModeling.Ir.NativeFeatureKind.Hole => "孔", CadModeling.Ir.NativeFeatureKind.SheetMetalBase => "基体法兰",
        CadModeling.Ir.NativeFeatureKind.EdgeFlange => "边线法兰", CadModeling.Ir.NativeFeatureKind.Flatten => "展开",
        CadModeling.Ir.NativeFeatureKind.WeldmentMember => "结构构件", CadModeling.Ir.NativeFeatureKind.TrimWeldment => "焊件剪裁",
        CadModeling.Ir.NativeFeatureKind.SurfaceExtrude => "拉伸曲面", CadModeling.Ir.NativeFeatureKind.SurfaceLoft => "放样曲面",
        CadModeling.Ir.NativeFeatureKind.SurfaceTrim => "剪裁曲面", CadModeling.Ir.NativeFeatureKind.SurfaceKnit => "缝合曲面",
        CadModeling.Ir.NativeFeatureKind.Thicken => "加厚", CadModeling.Ir.NativeFeatureKind.SetDimension => "修改尺寸",
        CadModeling.Ir.NativeFeatureKind.Suppress => "压缩特征", CadModeling.Ir.NativeFeatureKind.Restore => "解除压缩",
        CadModeling.Ir.NativeFeatureKind.Split => "分割", CadModeling.Ir.NativeFeatureKind.ThinExtrude => "薄壁拉伸",
        CadModeling.Ir.NativeFeatureKind.SurfacePlanar => "平面曲面", CadModeling.Ir.NativeFeatureKind.SketchPattern => "草图驱动阵列",
        CadModeling.Ir.NativeFeatureKind.SurfaceBoundary => "边界曲面", CadModeling.Ir.NativeFeatureKind.SurfaceFill => "填充曲面",
        CadModeling.Ir.NativeFeatureKind.SurfaceSweep => "扫描曲面", CadModeling.Ir.NativeFeatureKind.SurfaceOffset => "等距曲面",
        CadModeling.Ir.NativeFeatureKind.SpatialCurve => "空间曲线", CadModeling.Ir.NativeFeatureKind.Helix => "螺旋线",
        _ => "特征"
    };
}
