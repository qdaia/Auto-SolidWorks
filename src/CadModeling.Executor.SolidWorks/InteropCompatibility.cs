using SolidWorks.Interop.sldworks;

internal sealed partial class SolidWorksComExecutor
{
    // A side-by-side installation may register a different default application.
    private static Type? ApplicationType(bool throwOnError)
    {
        var major = typeof(ISldWorks).Assembly.GetName().Version!.Major;
        return Type.GetTypeFromProgID($"SldWorks.Application.{major}", throwOnError);
    }

    private static bool? ConcentricRotationLock(IFeature feature)
    {
#if SW_BEFORE_CONCENTRIC_DEFINITION
        return null;
#else
        return feature.GetDefinition() is IConcentricMateFeatureData mate ? mate.LockRotation : null;
#endif
    }

    private static IBody2? CopySectionBody(IBody2 body)
    {
#if SW_BEFORE_2020
        throw new NotSupportedException("SW_API_UNAVAILABLE: 此构建的截面检查需要 SolidWorks 2020 或更新版本的交线接口。");
#else
        return body.Copy2(true) as IBody2;
#endif
    }

    private static object[] SectionIntersectionEdges(IBody2 body, IBody2 sheet)
    {
#if SW_BEFORE_2020
        throw new NotSupportedException("SW_API_UNAVAILABLE: 缺少成对的目标体和工具体交线接口。");
#else
        return body.GetIntersectionEdges2(sheet, false) as object[] ?? [];
#endif
    }

    private static int HighPrecisionIntersections(IModelDoc2 model, object bodies, double[] origins,
        double[] directions, int options, double radius, double tolerance, bool highPrecision)
    {
#if SW_BEFORE_HIGH_PRECISION_RAYS
        throw new NotSupportedException("SW_API_UNAVAILABLE: 高精度射线检查需要 SolidWorks 2018 SP2 或更新版本。");
#else
        return model.Extension.RayIntersections(bodies, origins, directions, options, radius, tolerance, highPrecision);
#endif
    }
}
