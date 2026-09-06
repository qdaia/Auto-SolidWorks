using System.Text.Json;
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
            if ((isAngle && fact.Unit!=DrawingValueUnit.Degree) || (isLength && fact.Unit==DrawingValueUnit.Degree))
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
}
