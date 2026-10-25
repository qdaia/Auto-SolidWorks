using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CadModeling.Core;
using CadModeling.Ir;

/// <summary>Deterministic authoring only. The original compiler remains authoritative.</summary>
public static class BindingSheetAssembler
{
    public const string Revision="source-binding-sheet-1";
    private static readonly JsonSerializerOptions Strict=new(ModelingIrJson.Options){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public sealed record NormalizedPath(string InputPath,string Original,string Canonical);
    public sealed record Prepared(JsonObject Draft,IReadOnlyList<NormalizedPath> NormalizedPaths,int Annotations,int Dimensions,int Anchors);
    public sealed class InputError(string path,string message):ArgumentException(message)
    { public string InputPath {get;}=path; }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition,string path,string message)
    {if(!condition)throw new InputError(path,message);}

    public static string ParameterPath(string value,string location,List<NormalizedPath> changes)
    {
        Require(!string.IsNullOrEmpty(value)&&Regex.IsMatch(value,@"\A[A-Za-z_][A-Za-z_0-9]*(?:(?:\.[A-Za-z_][A-Za-z_0-9]*)|(?:\.(?:0|[1-9][0-9]*))|(?:\[(?:0|[1-9][0-9]*)\]))*\z"),
            location,"Use an explicit parameter path, e.g. feature.hole_centers.0.xmm; no wildcards, negative or leading-zero indices.");
        var normalized=Regex.Replace(value,@"\[([0-9]+)\]",m=>"."+m.Groups[1].Value);
        if(normalized!=value)changes.Add(new(location,value,normalized));return normalized;
    }
    private static JsonArray Array(JsonObject obj,string field)
    {
        if(obj[field] is null){var made=new JsonArray();obj[field]=made;return made;}
        if(obj[field] is JsonArray array)return array;
        throw new InputError("/draft/"+field,"Expected an array.");
    }
    private static void AddString(JsonObject obj,string field,string value)
    {var array=Array(obj,field);if(!array.Any(x=>x?.GetValue<string>()==value))array.Add(value);}
    private static JsonObject Find(JsonArray array,string id,string path)
    {
        var matches=array.OfType<JsonObject>().Where(o=>o["id"]?.GetValue<string>()==id).ToArray();
        Require(matches.Length==1,path,"Reference must name exactly one declared object: "+id);return matches[0];
    }
    private static JsonNode? At(JsonNode obj,string path,string location)
    {
        JsonNode? value=obj;
        foreach(var part in path.Split('.'))
        {
            if(value is JsonObject item&&item.TryGetPropertyValue(part,out var next))value=next;
            else if(value is JsonArray array&&int.TryParse(part,out var index)&&index>=0&&index<array.Count)value=array[index];
            else throw new InputError(location,"Parameter does not exist in the declared object: "+path);
        }
        Require(value is JsonValue||value is JsonObject reference&&(reference.ContainsKey("$dimension")||reference.ContainsKey("$expression")),location,"Target must be a scalar or explicit dimension expression: "+path);
        return value;
    }
    private static BindingAttachment Merge(BindingAttachment common,BindingAttachment? specific)=>specific is null?common:new(){
        TargetBounds=specific.TargetBounds??common.TargetBounds,StartAnchorId=specific.StartAnchorId??common.StartAnchorId,
        EndAnchorId=specific.EndAnchorId??common.EndAnchorId,TargetAnchorId=specific.TargetAnchorId??common.TargetAnchorId,
        StartReference=specific.StartReference??common.StartReference,EndReference=specific.EndReference??common.EndReference,
        Trace=specific.Trace??common.Trace,Explanation=specific.Explanation??common.Explanation,ObservationIds=specific.ObservationIds??common.ObservationIds};

    public static Prepared Prepare(JsonNode draftInput,JsonElement sheetInput,BindingWorkspace workspace)
    {
        // Work on a deep copy; even a late invalid group cannot change its base input.
        var draft=draftInput.DeepClone() as JsonObject??throw new InputError("/draft","Expected object.");
        foreach(var name in new[]{"name","source_text"})
            Require(draft[name] is JsonValue value&&value.TryGetValue<string>(out var text)&&!string.IsNullOrWhiteSpace(text),"/draft/"+name,"Required nonempty string: "+name);
        Require(draft["drawing_context"] is JsonObject,"/draft/drawing_context","Source context is required.");
        Require(draft["operations"] is JsonArray,"/draft/operations","Explicit real operations are required.");
        SourceWorkflowInputs.ApplyDefaults(draft["drawing_context"]!.AsObject());
        var fourStage=SourceWorkflow.IsFourStage(draft["drawing_context"]?["workflow_revision"]?.GetValue<string>());
        if(fourStage && draft["verification"] is null)draft["verification"]=new JsonObject();
        Require(draft["verification"] is JsonObject,"/draft/verification","Expected independent verification object for this workflow.");
        var context=draft["drawing_context"]!.AsObject();var operations=draft["operations"]!.AsArray();var verification=draft["verification"]!.AsObject();
        var features=Array(context,"features");var bindings=Array(verification,"bindings");var changes=new List<NormalizedPath>();
        var sheet=sheetInput.Deserialize<BindingSheet>(Strict)??throw new InputError("/sheet","Expected object.");
        Require(sheet.Groups is {Count: >0 and <=100},"/sheet/groups","Provide 1..100 explicit source groups.");
        int annotationCount=0,dimensionCount=0,anchorCount=0;
        for(var gi=0;gi<sheet.Groups.Count;gi++)
        {
            var group=sheet.Groups[gi];var gp="/sheet/groups/"+gi;
            Require(group is not null,gp,"Group cannot be null.");
            Require(group.Annotations is not null&&group.Anchors is not null,gp,"anchors and annotations must be arrays, not null.");
            for(var i=0;i<group.Anchors.Count;i++)
                Require(group.Anchors[i] is {Bounds:not null,SourcePath:not null,ObservationIds:not null,CandidateIds:not null},gp+"/anchors/"+i,"Anchor and its bounds/collections cannot be null.");
            var feature=Find(features,group.FeatureId,gp+"/feature_id");
            Require(feature["critical"] is null||feature["critical"]!.GetValue<bool>(),gp+"/feature_id","Binding sheet requires a critical source feature.");
            var critical=Array(feature,"critical_parameters");var annotations=new List<BindingAnnotationInput>();
            for(var ai=0;ai<group.Annotations.Count;ai++)
            {
                var a=group.Annotations[ai];var ap=gp+"/annotations/"+ai;
                Require(a is {TextBounds:not null,Attachment:not null,Dimensions:not null},ap,"Annotation, text_bounds, attachment and dimensions cannot be null.");
                Require(a.LengthUnit is DrawingValueUnit.Millimeter or DrawingValueUnit.Meter or DrawingValueUnit.Inch,ap+"/length_unit","Annotation length unit must be Millimeter, Meter or Inch.");
                Find(operations,a.OperationId,ap+"/operation_id");
                Require((feature["operation_ids"] as JsonArray??[]).Any(x=>x?.GetValue<string>()==a.OperationId),ap+"/operation_id","Annotation operation must belong to its declared source feature.");
                var parsed=HoleCalloutParser.Parse(a.SourceLiteral,a.LengthUnit);
                Require(parsed.Complete,ap+"/source_literal","Unparsed/ambiguous source literal. Preserve and review it; do not discard suffix: "+parsed.UnresolvedSuffix);
                Require(!string.IsNullOrWhiteSpace(a.Evidence),ap+"/evidence","Explicit source review evidence is required; state still defaults to Unresolved.");
                var numeric=parsed.Components.Where(c=>c.QuantityKind is not null).ToDictionary(c=>c.Key);
                Require(a.Dimensions.All(d=>d is {ComponentKey:not null,Checks:not null}),ap+"/dimensions","Dimensions, component_key and checks cannot be null.");
                Require(a.Dimensions.Count==numeric.Count&&a.Dimensions.Select(d=>d.ComponentKey).Distinct().Count()==a.Dimensions.Count&&a.Dimensions.All(d=>numeric.ContainsKey(d.ComponentKey)),ap+"/dimensions",
                    "Bind each numeric component exactly once. Expected keys: "+string.Join(", ",numeric.Keys));
                var values=new List<BindingValueInput>();
                for(var di=0;di<a.Dimensions.Count;di++)
                {
                    var d=a.Dimensions[di];var dp=ap+"/dimensions/"+di;var part=numeric[d.ComponentKey];
                    Require(part.Value is not null,dp+"/component_key","Component has no finite numeric value.");
                    var op=d.OperationId??a.OperationId;var operation=Find(operations,op,dp+"/operation_id");
                    Require((feature["operation_ids"] as JsonArray??[]).Any(x=>x?.GetValue<string>()==op),dp+"/operation_id","Operation must already belong to the explicitly selected source feature.");
                    var path=ParameterPath(d.ParameterPath,dp+"/parameter_path",changes);At(operation,path,dp+"/parameter_path");
                    var attachment=Merge(a.Attachment,d.Attachment);
                    Require(attachment.TargetBounds is not null,dp+"/attachment/target_bounds","Provide the observed target bounds in unscaled crop pixels.");
                    Require(!string.IsNullOrWhiteSpace(attachment.Explanation),dp+"/attachment/explanation","Provide the explicit attachment/datum explanation.");
                    Require(fourStage || d.Checks.Count>0,dp+"/checks","Declare at least one independent source check and its exact parameter slot; expectations are never copied from operations.");
                    values.Add(new(){Id=d.Id,ComponentKey=d.ComponentKey,Value=part.Value!.Value,Unit=part.Unit,QuantityKind=part.QuantityKind!.Value,
                        OperationId=op,ParameterPath=path,MeasurementId=d.MeasurementId,TargetBounds=attachment.TargetBounds!,
                        StartAnchorId=attachment.StartAnchorId,EndAnchorId=attachment.EndAnchorId,TargetAnchorId=attachment.TargetAnchorId,
                        StartReference=attachment.StartReference??attachment.StartAnchorId,EndReference=attachment.EndReference??attachment.EndAnchorId,
                        Trace=attachment.Trace,AttachmentExplanation=attachment.Explanation!,ObservationIds=attachment.ObservationIds??[]});
                    var existing=critical.OfType<JsonObject>().Where(x=>x["dimension_id"]?.GetValue<string>()==d.Id).ToArray();
                    Require(existing.Length==0||existing.Length==1&&existing[0]["operation_id"]?.GetValue<string>()==op&&existing[0]["parameter_path"]?.GetValue<string>()==path,dp+"/id","Critical parameter binding conflicts with the declared source dimension.");
                    if(existing.Length==0)critical.Add(JsonSerializer.SerializeToNode(new CriticalParameterBinding{OperationId=op,ParameterPath=path,DimensionId=d.Id},ModelingIrJson.Options));
                    foreach(var (target,ti) in d.Checks.Select((x,i)=>(x,i)))
                    {
                        var tp=dp+"/checks/"+ti;Require(target is not null,tp,"Check target cannot be null.");var checkPath=ParameterPath(target.ParameterPath,tp+"/parameter_path",changes);
                        var checkObjects=verification.Where(p=>p.Key!="bindings"&&p.Value is JsonArray).SelectMany(p=>p.Value!.AsArray().OfType<JsonObject>()).Where(x=>x["id"]?.GetValue<string>()==target.CheckId).ToArray();
                        Require(checkObjects.Length==1,tp+"/check_id","Check must already exist exactly once in independent verification: "+target.CheckId);
                        At(checkObjects[0],checkPath,tp+"/parameter_path");
                        var conflicts=bindings.OfType<JsonObject>().Where(x=>x["check_id"]?.GetValue<string>()==target.CheckId&&x["parameter_path"]?.GetValue<string>()==checkPath).ToArray();
                        Require(conflicts.All(x=>x["dimension_id"]?.GetValue<string>()==d.Id),tp,"A verification slot is already bound to a different source fact; do not collapse equal values.");
                        if(conflicts.Length==0)bindings.Add(new JsonObject{["dimension_id"]=d.Id,["check_id"]=target.CheckId,["parameter_path"]=checkPath});
                        AddString(checkObjects[0],"source_dimension_ids",d.Id);AddString(feature,"verification_check_ids",target.CheckId);
                    }
                }
                annotations.Add(new(){Id=a.Id,SourceLiteral=a.SourceLiteral,OperationId=a.OperationId,TextBounds=a.TextBounds,LengthUnit=a.LengthUnit,State=a.State,Evidence=a.Evidence,
                    LeaderTrace=a.LeaderTrace,LeaderTargetAnchorId=a.LeaderTargetAnchorId,Dimensions=values,
                    RetainedComponents=parsed.Components.Where(c=>c.QuantityKind is null).Select(c=>new DrawingAnnotationBinding{ComponentKey=c.Key,RetainedLiteral=c.Literal,Evidence=a.Evidence}).ToArray()});
            }
            try
            {
                var added=SourceBindingTools.ApplyBatch(draft,"draft",workspace,new BindingBatch{FrameId=group.FrameId,ViewId=group.ViewId,FeatureId=group.FeatureId,Datum=group.Datum,Anchors=group.Anchors,Annotations=annotations});
                annotationCount+=added.Annotations;dimensionCount+=added.Dimensions;anchorCount+=added.Anchors;
            }
            catch(ArgumentException e) when(e is not InputError){throw new InputError(gp,e.Message);}
        }
        return new(draft,changes,annotationCount,dimensionCount,anchorCount);
    }
}
