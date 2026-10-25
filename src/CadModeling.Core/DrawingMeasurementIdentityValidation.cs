using CadModeling.Ir;
namespace CadModeling.Core;

/// <summary>Object and consumer identity checks supplement user-supplied measurement labels.
/// Equal values alone never establish a cross-view correspondence.</summary>
public static class DrawingMeasurementIdentityValidation
{
    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext context)
    {
        var errors=new List<ModelingDiagnostic>();
        var anchors=context.GeometryAnchors.GroupBy(a=>a.Id).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
        var measurements=new List<(DrawingDimensionFact Fact,string Signature,Vector3? Direction)>();
        foreach(var f in context.Dimensions)
        {
            if(f.Status!=DrawingFactStatus.Stated||f.BindingEvidence is not {} e||f.Unit is not(DrawingValueUnit.Millimeter or DrawingValueUnit.Inch or DrawingValueUnit.Meter))continue;
            if(e.QuantityKind==DrawingQuantityKind.Linear&&e.Trace?.ModelDirection is {} direction&&ModelVerification.Finite(direction)&&ModelVerification.Norm(direction)>1e-9&&
                e.StartAnchorId is {} startId&&e.EndAnchorId is {} endId&&anchors.TryGetValue(startId,out var start)&&anchors.TryGetValue(endId,out var end)&&start.ViewId==e.ViewId&&end.ViewId==e.ViewId&&start.PhysicalEntityId!=end.PhysicalEntityId)
                measurements.Add((f,System.Text.Json.JsonSerializer.Serialize(new[]{start.PhysicalEntityId,end.PhysicalEntityId}.Order(StringComparer.Ordinal)),ModelVerification.Unit(direction)));
            else if(e.QuantityKind is DrawingQuantityKind.Diameter or DrawingQuantityKind.Radius&&e.TargetAnchorId is {} id&&anchors.TryGetValue(id,out var target)&&target.ViewId==e.ViewId&&DrawingCircularProjectionValidation.CircularTarget(context,target) is {} circle)
                measurements.Add((f,circle.PhysicalEntityId,null));
        }
        foreach(var group in measurements.Where(x=>x.Direction is not null).GroupBy(x=>x.Signature,StringComparer.Ordinal))
        {
            var prior=new List<(DrawingDimensionFact Fact,string Signature,Vector3? Direction)>();
            foreach(var item in group)
            {
                var match=prior.FirstOrDefault(x=>Math.Abs(ModelVerification.Dot(x.Direction!,item.Direction!))>1-1e-7);
                if(match.Fact is not null&&!DimensionExpressions.Equal(Value(match.Fact),Value(item.Fact)))
                    errors.Add(new("ANCHOR_LINEAR_CONFLICT",DiagnosticSeverity.Error,"The same physical endpoints and model measurement direction disagree with dimension "+match.Fact.Id+"; changing measurement_id cannot hide the conflict.","drawing_context.dimensions."+item.Fact.Id));
                else prior.Add(item);
            }
        }
        foreach(var group in measurements.GroupBy(x=>(x.Fact.OperationId,x.Fact.ParameterPath,x.Fact.BindingEvidence!.QuantityKind)))
        {
            var first=group.First();
            foreach(var item in group.Skip(1))
                if(item.Fact.BindingEvidence!.ViewId!=first.Fact.BindingEvidence!.ViewId&&item.Signature!=first.Signature)
                    errors.Add(new("DIMENSION_CONSUMER_OWNERSHIP",DiagnosticSeverity.Error,"The same parameter is bound to different physical source objects across views ("+first.Fact.Id+"). Equal values do not establish ownership; explicitly declare the shared geometry or a derived dependency.","drawing_context.dimensions."+item.Fact.Id));
        }
        foreach(var f in context.Dimensions.Where(f=>f.CorroboratesDimensionId is not null))
        {
            var primary=context.Dimensions.Where(p=>p.Id==f.CorroboratesDimensionId).ToArray();
            var m=measurements.Where(x=>x.Fact.Id==f.Id).ToArray();
            var p=measurements.Where(x=>x.Fact.Id==f.CorroboratesDimensionId).ToArray();
            if(primary.Length!=1 || primary[0].CorroboratesDimensionId is not null || f.Id==primary[0].Id ||
                f.Status!=DrawingFactStatus.Stated || primary[0].Status!=DrawingFactStatus.Stated ||
                f.OperationId!=primary[0].OperationId || f.ParameterPath!=primary[0].ParameterPath ||
                m.Length!=1 || p.Length!=1 || m[0].Signature!=p[0].Signature ||
                f.BindingEvidence!.ViewId==primary[0].BindingEvidence!.ViewId ||
                f.BindingEvidence!.QuantityKind!=primary[0].BindingEvidence!.QuantityKind ||
                !DimensionExpressions.Equal(Value(f),Value(primary[0])) ||
                (m[0].Direction is {} md && (p[0].Direction is not {} pd || Math.Abs(ModelVerification.Dot(md,pd))<1-1e-7)))
                errors.Add(new("DIMENSION_CORROBORATION_INVALID",DiagnosticSeverity.Error,"Corroboration requires one stated primary dimension, the same consumer and physical measurement, consistent value and direction, and a different view.","drawing_context.dimensions."+f.Id));
        }
        return errors;
    }
    private static double Value(DrawingDimensionFact f)=>SourceInterpretationValidation.SourceLiteralValue(f)*DimensionExpressions.Scale(f.Unit);
}
