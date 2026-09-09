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
        void Error(string text,string path) => errors.Add(new("DRAWING_BINDING",DiagnosticSeverity.Error,text,path));
        if(!Path.IsPathFullyQualified(context.SourcePath) || !File.Exists(context.SourcePath)) Error("Drawing source must be an existing absolute path.","drawing_context.source_path");
        var views=context.Views.Select(v=>v.Id).ToHashSet(StringComparer.Ordinal);
        if(views.Count!=context.Views.Count || context.Views.Any(v=>string.IsNullOrWhiteSpace(v.Id)||v.PageNumber<1)) Error("Drawing views require unique IDs and positive page numbers.","drawing_context.views");
        if(context.Dimensions.Select(d=>d.Id).Distinct(StringComparer.Ordinal).Count()!=context.Dimensions.Count) Error("Dimension facts need unique IDs.","drawing_context.dimensions");
        errors.AddRange(ValidateCoverage(context, draft.Operations.Select(o=>o.Id).ToArray(), draft.Verification));
        foreach(var fact in context.Dimensions)
        {
            var path="drawing_context.dimensions."+fact.Id;
            if(fact.Status==DrawingFactStatus.Unknown || !double.IsFinite(fact.Value) || string.IsNullOrWhiteSpace(fact.SourceLiteral))
            { Error("A bound dimension must have a finite value, source literal and known fact status.",path);continue; }
            if(fact.Status==DrawingFactStatus.Derived && string.IsNullOrWhiteSpace(fact.Derivation)) Error("Derived dimensions need the geometric derivation recorded.",path);
            if(fact.ViewIds.Any(v=>!views.Contains(v))) Error("Dimension refers to a missing drawing view.",path);
            var operation=draft.Operations.FirstOrDefault(o=>o.Id==fact.OperationId);
            if(operation is null) {Error("Dimension refers to a missing modeling operation.",path);continue;}
            var fields=fact.ParameterPath.Split('.');
            var isAngle=fields.Any(f=>f.EndsWith("_degrees",StringComparison.Ordinal)) ||
                (fields[^1]=="dimension_value" && operation.Feature?.DimensionIsAngle==true);
            var isLength=fields.Any(f=>f.EndsWith("_mm",StringComparison.Ordinal) || f is "xmm" or "ymm");
            if ((isAngle && fact.Unit!=DrawingValueUnit.Degree) || (isLength && fact.Unit is DrawingValueUnit.Degree or DrawingValueUnit.Unitless) || (fields[^1]=="count"&&fact.Unit!=DrawingValueUnit.Unitless))
                Error("Angle and length dimensions cannot be bound to each other's parameter fields.",path);
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
                Error($"Bound source dimension does not match operation '{fact.OperationId}' field '{fact.ParameterPath}' after unit conversion.",path);
        }
        return errors;
    }

    public static IReadOnlyList<ModelingDiagnostic> ValidateCoverage(DrawingPlanContext context,
        IReadOnlyList<string> operationIds, ModelVerificationSpec verification)
    {
        var errors=new List<ModelingDiagnostic>();
        void Error(string text) => errors.Add(new("DRAWING_COVERAGE",DiagnosticSeverity.Error,text,"drawing_context.features"));
        var viewIds=context.Views.Select(v=>v.Id).ToHashSet(StringComparer.Ordinal);
        var checks=verification.CylinderGroups.Select(c=>c.Id).Concat(verification.NativeDimensions.Select(c=>c.Id)).Concat(verification.Bounds.Select(c=>c.Id)).Concat(verification.SurfaceSamples.Select(c=>c.Id)).Concat(verification.BoundaryClearances.Select(c=>c.Id)).ToHashSet(StringComparer.Ordinal);
        if(context.Features.Select(f=>f.Id).Distinct(StringComparer.Ordinal).Count()!=context.Features.Count)
            Error("Source feature IDs must be unique.");
        foreach(var feature in context.Features)
        {
            if(string.IsNullOrWhiteSpace(feature.Id) || string.IsNullOrWhiteSpace(feature.SourceLiteral) ||
               feature.ViewIds.Count==0 || feature.ViewIds.Any(id=>!viewIds.Contains(id)))
                Error($"Feature '{feature.Id}' needs a source literal and existing source views.");
            if(feature.OperationIds.Count==0 || feature.OperationIds.Any(id=>!operationIds.Contains(id,StringComparer.Ordinal)))
                Error($"Feature '{feature.Id}' refers to missing operations or has no operations.");
            if(feature.Status==DrawingFactStatus.Unknown || feature.Critical && feature.Status==DrawingFactStatus.Assumed)
                Error($"Critical feature '{feature.Id}' cannot use unknown or assumed geometry.");
            if(feature.Status==DrawingFactStatus.Derived && string.IsNullOrWhiteSpace(feature.Derivation))
                Error($"Feature '{feature.Id}' needs its geometric derivation.");
            if(feature.Critical && feature.CriticalParameters.Count==0)
                Error($"Critical feature '{feature.Id}' must list its critical parameter bindings.");
            if(feature.Critical && feature.VerificationCheckIds.Count==0 || feature.VerificationCheckIds.Any(id=>!checks.Contains(id)))
                Error($"Feature '{feature.Id}' requires existing independent verification checks.");
            foreach(var binding in feature.CriticalParameters)
            {
                var fact=context.Dimensions.FirstOrDefault(d=>d.Id==binding.DimensionId);
                if(fact is null || fact.OperationId!=binding.OperationId || fact.ParameterPath!=binding.ParameterPath ||
                   !feature.OperationIds.Contains(binding.OperationId,StringComparer.Ordinal))
                    Error($"Feature '{feature.Id}' has an unbound critical parameter '{binding.OperationId}.{binding.ParameterPath}'.");
                else if(feature.Critical && (fact.Status is DrawingFactStatus.Assumed or DrawingFactStatus.Unknown ||
                        fact.ViewIds.Count==0 || !fact.ViewIds.Any(feature.ViewIds.Contains)))
                    Error($"Critical dimension '{fact.Id}' must be supported by a source view and cannot be assumed.");
            }
            if(feature.Critical)
            {
                var checkedDimensions=verification.CylinderGroups.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds)
                    .Concat(verification.NativeDimensions.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds))
                    .Concat(verification.Bounds.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds))
                    .Concat(verification.SurfaceSamples.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds))
                    .Concat(verification.BoundaryClearances.Where(c=>feature.VerificationCheckIds.Contains(c.Id)).SelectMany(c=>c.SourceDimensionIds)).ToHashSet(StringComparer.Ordinal);
                foreach(var binding in feature.CriticalParameters)
                    if(!checkedDimensions.Contains(binding.DimensionId)) Error($"Critical dimension '{binding.DimensionId}' is not covered by the feature's verification checks.");
            }
        }
        if(context.RequireCompleteBindings)
        {
            if(context.Features.Count==0) Error("Drawing modeling requires a source feature inventory. List all features and critical parameters.");
            var covered=context.Features.SelectMany(f=>f.OperationIds).ToHashSet(StringComparer.Ordinal);
            foreach(var id in operationIds.Where(id=>!covered.Contains(id))) Error($"Operation '{id}' is missing from the source feature inventory.");
            var bound=context.Features.Where(f=>f.Critical).SelectMany(f=>f.CriticalParameters).Select(b=>b.DimensionId).ToHashSet(StringComparer.Ordinal);
            foreach(var fact in context.Dimensions.Where(d=>d.Critical&&!bound.Contains(d.Id))) Error($"Critical dimension '{fact.Id}' is missing from the source feature inventory.");
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
            yield return new("DRAWING_COMPILED_CHANGED",DiagnosticSeverity.Error,"Drawing bindings or operations changed after compilation. Recompile the corrected typed draft.","drawing_binding_digest");
        if(plan.DrawingContext is { } context)
        {
            if(!File.Exists(context.SourcePath) || plan.DrawingSourceSha256!=FileHash(context.SourcePath))
                yield return new("DRAWING_SOURCE_CHANGED",DiagnosticSeverity.Error,"Source drawing changed or disappeared after compilation. Read the current source and recompile.","drawing_context.source_path");
            foreach(var error in ValidateCoverage(context,plan.Operations.Select(o=>o.Id).ToArray(),plan.Verification)) yield return error;
        }
    }
    public static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
