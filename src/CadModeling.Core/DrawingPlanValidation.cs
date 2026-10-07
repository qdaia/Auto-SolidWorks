using System.Text.Json;
using System.Security.Cryptography;
using CadModeling.Ir;
namespace CadModeling.Core;

public static class DrawingPlanValidation
{
    public static IReadOnlyList<ModelingDiagnostic> Validate(GenericModelDraft draft)
    {
        if(draft.DrawingContext is not { } context) return [];
        var errors=new List<ModelingDiagnostic>();
        errors.AddRange(DrawingOmissionValidation.Diagnostics(context));
        void Error(string text,string path) => errors.Add(new("DRAWING_BINDING",DiagnosticSeverity.Error,text,path));
        if(!Path.IsPathFullyQualified(context.SourcePath) || !File.Exists(context.SourcePath)) Error("绘制源必须是一个现有的绝对路径。","drawing_context.source_path");
        var views=context.Views.Select(v=>v.Id).ToHashSet(StringComparer.Ordinal);
        if(views.Count!=context.Views.Count || context.Views.Any(v=>string.IsNullOrWhiteSpace(v.Id)||v.PageNumber<1)) Error("绘制视图需要独特的ID和正数页码。","drawing_context.views");
        if(context.Dimensions.Select(d=>d.Id).Distinct(StringComparer.Ordinal).Count()!=context.Dimensions.Count) Error("尺寸特性需要唯一的标识符。","drawing_context.dimensions");
        errors.AddRange(ValidateCoverage(context, draft.Operations.Select(o=>o.Id).ToArray(), draft.Verification));
        foreach(var fact in context.Dimensions)
        {
            var path="drawing_context.dimensions."+fact.Id;
            if(fact.Status==DrawingFactStatus.Unknown || !double.IsFinite(fact.Value) || string.IsNullOrWhiteSpace(fact.SourceLiteral))
            { Error("一个边界尺寸必须有有限的值，来源常量和已知事实状态。",path);continue; }
            if(fact.Status==DrawingFactStatus.Derived && string.IsNullOrWhiteSpace(fact.Derivation)) Error("导出的尺寸需要记录几何 推导过程。",path);
            if(fact.ViewIds.Any(v=>!views.Contains(v))) Error("尺寸是指缺少的视图。",path);
            var operation=draft.Operations.FirstOrDefault(o=>o.Id==fact.OperationId);
            if(operation is null) {Error("尺寸是指一个缺失的建模操作。",path);continue;}
            var fields=fact.ParameterPath.Split('.');
            var isAngle=fields.Any(f=>f.EndsWith("_degrees",StringComparison.Ordinal)) ||
                (fields[^1]=="dimension_value" && operation.Feature?.DimensionIsAngle==true);
            var isLength=fields.Any(f=>f.EndsWith("_mm",StringComparison.Ordinal) || f is "xmm" or "ymm");
            if ((isAngle && fact.Unit!=DrawingValueUnit.Degree) || (isLength && fact.Unit is DrawingValueUnit.Degree or DrawingValueUnit.Unitless) || ((fields[^1]=="count"||fields[^1].EndsWith("_count",StringComparison.Ordinal))&&fact.Unit!=DrawingValueUnit.Unitless))
                Error("角度和长度尺寸不能绑定到彼此的参数字段中。",path);
            var element=JsonSerializer.SerializeToElement(operation,ModelingIrJson.Options);
            var found=true;
            foreach(var key in fact.ParameterPath.Split('.'))
            {
                if(element.ValueKind==JsonValueKind.Object && element.TryGetProperty(key,out var next)) element=next;
                else if(element.ValueKind==JsonValueKind.Array && int.TryParse(key,out var index) && index>=0 && index<element.GetArrayLength()) element=element[index];
                else {found=false;break;}
            }
            var value=fact.Value*(fact.Unit==DrawingValueUnit.Inch?25.4:fact.Unit==DrawingValueUnit.Meter?1000:1);
            if(!found || element.ValueKind!=JsonValueKind.Number || !element.TryGetDouble(out var actual) || Math.Abs(actual-value)>1e-7*Math.Max(1,Math.Abs(value)))
                Error($"边界源尺寸在单位转换后不匹配操作 '{fact.OperationId}' 的 '{fact.ParameterPath}' 字段。",path);
        }
        return errors;
    }

    public static IReadOnlyList<ModelingDiagnostic> ValidateCoverage(DrawingPlanContext context,
        IReadOnlyList<string> operationIds, ModelVerificationSpec verification)
    {
        var errors=new List<ModelingDiagnostic>();
        void Error(string text) => errors.Add(new("DRAWING_COVERAGE",DiagnosticSeverity.Error,text,"drawing_context.features"));
        var viewIds=context.Views.Select(v=>v.Id).ToHashSet(StringComparer.Ordinal);
        var checks=verification.CylinderGroups.Select(c=>c.Id).Concat(verification.NativeDimensions.Select(c=>c.Id)).Concat(verification.Bounds.Select(c=>c.Id)).Concat(verification.SurfaceSamples.Select(c=>c.Id)).Concat(verification.BoundaryClearances.Select(c=>c.Id)).Concat(verification.WholeModelChecks.Select(c=>c.Id)).Concat(verification.EdgeShapes.Select(c=>c.Id)).Concat(verification.SurfaceContinuity.Select(c=>c.Id)).ToHashSet(StringComparer.Ordinal);
        if(context.Features.Select(f=>f.Id).Distinct(StringComparer.Ordinal).Count()!=context.Features.Count)
            Error("源特征ID必须唯一。");
        foreach(var feature in context.Features)
        {
            if(string.IsNullOrWhiteSpace(feature.Id) || string.IsNullOrWhiteSpace(feature.SourceLiteral) ||
               feature.ViewIds.Count==0 || feature.ViewIds.Any(id=>!viewIds.Contains(id)))
                Error($"特征 '{feature.Id}' 需要一个源字面值和现有的源视图。");
            if(feature.OperationIds.Count==0 || feature.OperationIds.Any(id=>!operationIds.Contains(id,StringComparer.Ordinal)))
                Error($"特征 '{feature.Id}' 涉及缺少的操作或没有操作。");
            if(feature.Status==DrawingFactStatus.Unknown || feature.Critical && feature.Status==DrawingFactStatus.Assumed)
                Error($"关键特征 '{feature.Id}' 无法使用未知或假设的几何形状。");
            if(feature.Status==DrawingFactStatus.Derived && string.IsNullOrWhiteSpace(feature.Derivation))
                Error($"特征 '{feature.Id}' 需要其几何推导。");
            if(feature.Critical && feature.CriticalParameters.Count==0)
                Error($"关键特征 '{feature.Id}' 必须列出其关键参数绑定。");
            if(feature.Critical && feature.VerificationCheckIds.Count==0 || feature.VerificationCheckIds.Any(id=>!checks.Contains(id)))
                Error($"特征 '{feature.Id}' 需要现有的独立验证检查。");
            foreach(var binding in feature.CriticalParameters)
            {
                var fact=context.Dimensions.FirstOrDefault(d=>d.Id==binding.DimensionId);
                if(fact is null || fact.OperationId!=binding.OperationId || fact.ParameterPath!=binding.ParameterPath ||
                   !feature.OperationIds.Contains(binding.OperationId,StringComparer.Ordinal))
                    Error($"特征 '{feature.Id}' 有一个未绑定的关键参数 '{binding.OperationId}.{binding.ParameterPath}'.");
                else if(feature.Critical && (fact.Status is DrawingFactStatus.Assumed or DrawingFactStatus.Unknown ||
                        fact.ViewIds.Count==0 || !fact.ViewIds.Any(feature.ViewIds.Contains)))
                    Error($"关键尺寸 '{fact.Id}' 必须由源视图支撑，不能假设。");
            }
            if(feature.Critical)
            {
                var checkedDimensions=verification.CylinderGroups.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds)
                    .Concat(verification.NativeDimensions.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds))
                    .Concat(verification.Bounds.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds))
                    .Concat(verification.SurfaceSamples.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds))
                    .Concat(verification.BoundaryClearances.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds))
                    .Concat(verification.WholeModelChecks.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds))
                    .Concat(verification.EdgeShapes.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds))
                    .Concat(verification.SurfaceContinuity.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds)).ToHashSet(StringComparer.Ordinal);
                foreach(var binding in feature.CriticalParameters)
                    if(!checkedDimensions.Contains(binding.DimensionId)) Error($"关键尺寸 '{binding.DimensionId}' 未被特征的验证检查所覆盖。");
            }
        }
        if(context.RequireCompleteBindings)
        {
            if(context.Features.Count==0) Error("绘制建模需要源特征库存。列出所有特征和关键参数。");
            var covered=context.Features.SelectMany(f=>f.OperationIds).ToHashSet(StringComparer.Ordinal);
            foreach(var id in operationIds.Where(id=>!covered.Contains(id))) Error($"操作 '{id}' 缺少于源特征库存。");
            var bound=context.Features.Where(f=>f.Critical).SelectMany(f=>f.CriticalParameters).Select(b=>b.DimensionId).ToHashSet(StringComparer.Ordinal);
            foreach(var fact in context.Dimensions.Where(d=>d.Critical&&!bound.Contains(d.Id))) Error($"关键尺寸 '{fact.Id}' 缺少源特征库存。");
        }
        return errors;
    }

    // Integrity against accidental edits between compile and execute; this is not an approval token.
    public static string Digest(ModelingPlan plan) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new {plan.DrawingContext,plan.Operations,plan.Verification,plan.DrawingSourceSha256},ModelingIrJson.Options)));

    public static IEnumerable<ModelingDiagnostic> ValidateCompiled(ModelingPlan plan)
    {
        if(plan.DrawingContext is null && plan.DrawingBindingDigest is null) yield break;
        if(plan.DrawingContext is null || plan.DrawingBindingDigest!=Digest(plan))
            yield return new("DRAWING_COMPILED_CHANGED",DiagnosticSeverity.Error,"绘制绑定或操作在编译后更改。请重新编译修正后的类型化草案。","drawing_binding_digest");
        if(plan.DrawingContext is { } context)
        {
            if(!File.Exists(context.SourcePath) || plan.DrawingSourceSha256!=FileHash(context.SourcePath))
                yield return new("DRAWING_SOURCE_CHANGED",DiagnosticSeverity.Error,"源图在编译后更改或消失。读取当前源图并重新编译。","drawing_context.source_path");
            foreach(var error in ValidateCoverage(context,plan.Operations.Select(o=>o.Id).ToArray(),plan.Verification)) yield return error;
            foreach(var error in DrawingOmissionValidation.Diagnostics(context)) yield return error;
        }
    }
    public static string FileHash(string path)
    {
        using var stream=File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
