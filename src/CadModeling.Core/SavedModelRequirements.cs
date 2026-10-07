using CadModeling.Ir;
namespace CadModeling.Core;

public static class SavedModelRequirements
{
    public static void Validate(ModelingPlan plan,ModelInspection saved)
    {
        var features=saved.Features??[];
        var dimensions=features.SelectMany(f=>f.Dimensions).GroupBy(d=>d.Name,StringComparer.Ordinal)
            .ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
        var expected=new Dictionary<string,(double Value,DrawingValueUnit? Unit,bool Angle)>(StringComparer.Ordinal);
        var equationTargets=new HashSet<string>(StringComparer.Ordinal);
        foreach(var sketch in plan.Operations.OfType<ProfileSketchOperation>())
        {
            if(sketch.RequireFullyDefined && features.SingleOrDefault(f=>f.Name==sketch.Name)?.SketchConstraintStatus!=3)
                throw new InvalidOperationException("SAVED_SKETCH_UNDERDEFINED: 最终保存的草图未完全定义："+sketch.Name+"（状态="+features.SingleOrDefault(f=>f.Name==sketch.Name)?.SketchConstraintStatus+"）。");
            if(!sketch.AutoDimensionPrimitives)continue;
            for(int i=0;i<sketch.Primitives.Count;i++)
                switch(sketch.Primitives[i])
                {
                    case CircleProfile circle:
                        expected[(i==0?"直径":"孔径_"+i)+"@"+sketch.Name]=(circle.DiameterMm,DrawingValueUnit.Millimeter,false);break;
                    case CenteredRectangleProfile rectangle:
                        expected["宽度_"+i+"@"+sketch.Name]=(rectangle.WidthMm,DrawingValueUnit.Millimeter,false);
                        expected["高度_"+i+"@"+sketch.Name]=(rectangle.HeightMm,DrawingValueUnit.Millimeter,false);break;
                }
        }
        // When a parameter is edited more than once, the last operation defines its saved value.
        foreach(var operation in plan.Operations.OfType<NativeFeatureOperation>().Where(o=>o.Options.Kind==NativeFeatureKind.SetDimension))
        {
            var o=operation.Options;
            expected[o.DimensionName!]=(o.DimensionValue,o.DimensionUnit,o.DimensionIsAngle);
        }
        // Explicit final active-configuration targets supersede primitive initialization.
        // The executor independently verifies all configurations and saved equation definitions.
        if (plan.DesignIntent is { } intent)
        {
            var compiled = DesignIntentContract.Compile(intent);
            foreach (var eq in compiled.ForConfiguration(intent.ActiveConfiguration).Where(e => !e.GlobalVariable))
            {
                expected[eq.Target] = (eq.Value.Value, eq.Value.Unit, eq.Value.Unit == DrawingValueUnit.Degree);
                equationTargets.Add(eq.Target);
            }
            foreach (var dim in intent.Configurations.Single(c => c.Name == intent.ActiveConfiguration).Dimensions)
                expected[dim.DimensionName] = (dim.Value.Value, dim.Value.Unit, dim.Value.Unit == DrawingValueUnit.Degree);
        }
        foreach(var (name,request) in expected)
        {
            if(!dimensions.TryGetValue(name,out var actual) || !(actual.Driving is true || equationTargets.Contains(name) && actual.EquationControlled))
                throw new InvalidOperationException("SAVED_DIMENSION_MISSING: 最终文件缺少可驱动的请求参数："+name);
            var kind=actual.ParameterType switch
            {
                "swDimensionParamTypeDoubleLinear"=>NativeDimensionParameterKind.Length,
                "swDimensionParamTypeDoubleAngular"=>NativeDimensionParameterKind.Angle,
                "swDimensionParamTypeInteger"=>NativeDimensionParameterKind.Integer,
                _=>throw new InvalidOperationException("最终文件参数类型无法验证："+name)
            };
            if(!NativeDimensionValues.Matches(actual.SystemValue,NativeDimensionValues.ToSystemValue(request.Value,kind,request.Unit,request.Angle),kind))
                throw new InvalidOperationException("SAVED_DIMENSION_MISMATCH: 最终文件未保留请求的参数值："+name);
        }
        foreach(var operation in plan.Operations.OfType<NativeFeatureOperation>().Where(o=>o.Options.Kind==NativeFeatureKind.Hole&&o.Options.HoleKind==HoleKind.Tapped))
        {
            var o=operation.Options;
            for(int i=0;i<o.HoleCenters.Count;i++)
            {
                var thread=saved.CosmeticThreads.SingleOrDefault(t=>t.FeatureName==operation.Name+"_装饰螺纹"+i);
                var through=o.ThroughAll&&o.ThreadDepthMm is null;
                bool Close(double actual,double value)=>double.IsFinite(actual)&&Math.Abs(actual-value)<=1e-6;
                if(thread is null || !thread.Complete || thread.Designation!=o.ThreadDesignation || thread.ThroughAll!=through
                    || !Close(thread.MajorDiameterMm,o.ThreadMajorDiameterMm) || !Close(thread.DrillRadiusMm,o.DiameterMm/2)
                    || !through&&!Close(thread.BlindDepthMm,o.ThreadDepthMm??o.DepthMm))
                    throw new InvalidOperationException("SAVED_THREAD_MISMATCH: 最终文件装饰螺纹规格或深度不符合请求。");
            }
        }
    }
}
