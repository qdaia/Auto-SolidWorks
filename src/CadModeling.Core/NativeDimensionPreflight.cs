using CadModeling.Ir;
namespace CadModeling.Core;

/// <summary>Validate names only when the executor owns a complete, deterministic naming contract.</summary>
public static class NativeDimensionPreflight
{
    public static IReadOnlyList<ModelingDiagnostic> Validate(GenericModelDraft draft)
    {
        var result=new List<ModelingDiagnostic>();
        var critical=draft.DrawingContext?.Features.Where(f=>f.Critical).SelectMany(f=>f.CriticalParameters).Select(p=>p.DimensionId).ToHashSet(StringComparer.Ordinal)??[];
        foreach(var operation in draft.Operations.Where(o=>o.Type==GenericOperationKind.ProfileSketch))
        {
            if(draft.DrawingContext is {} context && operation.Primitives.Count>0 &&
                operation.Primitives.All(p=>p.Construction) && context.Dimensions.Any(d=>d.OperationId==operation.Id&&(d.Critical||critical.Contains(d.Id))&&!Consumed(d,context,draft)))
                result.Add(new("SOURCE_DIMENSION_UNCONSUMED_DATUM",DiagnosticSeverity.Error,
                    "关键源尺寸绑定在没有被特征消费的独立构造草图"+operation.Name+"。该尺寸只证明辅助线长度，不能证明零件尺寸；绑定实际驱动参数及源独立几何检查。", "operations."+operation.Id));
            string[]? names=null;
            if(operation.DrivenProfile is {} profile)
                names=profile.Kind==DrivenProfileKind.RoundEar ? ["耳板宽度","耳板直段高度"] : ["外圆半径","内圆半径"];
            else if(operation.AutoDimensionPrimitives && operation.Primitives.Count>0 && operation.Primitives.All(p=>p.Type==GenericPrimitiveKind.Circle))
                names=operation.Primitives.SelectMany((p,i)=>new[]{i==0?"直径":"孔径_"+i,"定位X_"+i,"定位Y_"+i})
                    .Concat(operation.Dimensions.Select(d=>d.Name)).Concat(operation.CenterPositionDimensionName is {} alias?[alias]:Array.Empty<string>()).ToArray();
            if(names is null)continue;
            var featureName=string.IsNullOrWhiteSpace(operation.Name)?operation.Id:operation.Name;
            var fullNames=names.Select(n=>n+"@"+featureName).ToArray();
            result.Add(new("SKETCH_NATIVE_DIMENSION_NAMES",DiagnosticSeverity.Info,
                "执行器声明的尺寸名称："+string.Join("; ",fullNames)+"。圆头耳板半径由宽度/2确定，不能猜测额外半径尺寸。", "operations."+operation.Id));
            foreach(var check in draft.Verification.NativeDimensions.Where(c=>c.DimensionName.EndsWith("@"+featureName,StringComparison.Ordinal)))
                if(!fullNames.Contains(check.DimensionName,StringComparer.Ordinal))
                    result.Add(new("NATIVE_DIMENSION_NAME_UNKNOWN",DiagnosticSeverity.Error,
                        "检查"+check.Id+"引用未生成的尺寸"+check.DimensionName+"；可用名称："+string.Join("; ",fullNames)+"。按真实量义修订检查，不按相近数值猜选。","verification.native_dimensions."+check.Id));
        }
        return result;
    }

    private static bool Consumed(DrawingDimensionFact source,DrawingPlanContext context,GenericModelDraft draft)
    {
        // Ordering alone (DependsOn) does not create a geometric dependency.
        bool UsedByFeature(string id)=>draft.Operations.Any(o=>o.Id!=id &&
            (o.SketchId==id || o.Feature?.SketchId==id || o.Feature?.PathSketchId==id ||
             o.Feature?.ProfileIds.Contains(id)==true || o.Feature?.GuideIds.Contains(id)==true || o.Feature?.AxisId==id));
        if(UsedByFeature(source.OperationId))return true;
        var reached=new HashSet<string>{source.Id};var pending=new Queue<string>();pending.Enqueue(source.Id);
        while(pending.TryDequeue(out var id))
        foreach(var derived in context.Dimensions.Where(f=>f.Expression is not null&&DimensionExpressions.References(f.Expression).Contains(id)))
        {
            if(!reached.Add(derived.Id))continue;
            var target=draft.Operations.FirstOrDefault(o=>o.Id==derived.OperationId);
            if(target is not null && target.Id!=source.OperationId &&
                (target.Type!=GenericOperationKind.ProfileSketch || target.Primitives.Any(p=>!p.Construction)&&UsedByFeature(target.Id)) &&
                (derived.ParameterPath.StartsWith("frame.") || derived.ParameterPath.StartsWith("primitives.") ||
                 derived.ParameterPath.StartsWith("driven_profile.") || derived.ParameterPath is "depth_mm" or "start_offset_mm"))return true;
            pending.Enqueue(derived.Id);
        }
        return false;
    }
}
