using CadModeling.Ir;

namespace CadModeling.Core;

public enum BoundaryCurveRepresentation { Feature, Sketch, SingleSketchSegment }
public sealed record BoundaryCurveBinding(string SourceId, string FeatureReference,
    IReadOnlyList<string> WholeCurveAliases, IReadOnlyList<string> SegmentReferences);
public sealed record BoundarySelection(string Reference, int Mark);
public sealed record BoundaryCurveState(string Reference, BoundaryCurveRepresentation Representation,
    string? OwnerSketchReference, int? OwnerSketchSegmentCount, int Tangency, double DraftAngle,
    bool ReverseDraft, double? TangentLength, bool? ReverseTangent, bool? TangentApplyAll);
public sealed record BoundaryDirectionState(bool CompleteInventory, int Influence, bool? TrimByD1,
    IReadOnlyList<BoundaryCurveState> Curves);
public sealed record BoundaryDefinitionSnapshot(string FeatureReference, bool SurfaceBodiesOnly,
    IReadOnlyList<BoundaryDirectionState> Directions);
public sealed record BoundaryDefinitionRequest(IReadOnlyList<BoundaryCurveBinding> Profiles,
    IReadOnlyList<BoundaryCurveBinding> Guides, SurfaceEndCondition Start, SurfaceEndCondition End, bool Merge);
public sealed record BoundaryDefinitionReceipt(BoundaryDefinitionRequest Request, BoundaryDefinitionSnapshot Snapshot);
public sealed record BoundaryCreationParameters(short Type, short ProfileCount, short GuideCount, bool HasCenterline,
    double TessellationFactor, bool WantsSolid, bool Merge, bool FeatureScope, bool AutoSelect,
    bool Thin, bool ForceNonRational, bool CreateSolid);

/// <summary>Exact native creation/readback adapter, also executable with an offline fake port.</summary>
public interface IBoundarySurfaceSession
{
    void ClearSelection();
    bool Select(string sourceId, int mark, bool append);
    IReadOnlyList<BoundarySelection> CaptureSelection();
    void SetCurveData(short direction, short index, short tangency, double draft, double tangentLength, bool applyAll);
    void SetDirectionData(short direction, int influence, short trim, bool closed, bool split);
    bool Create(BoundaryCreationParameters parameters);
    BoundaryDefinitionSnapshot CaptureDefinition();
}

public static partial class BoundarySurfaceContract
{
    private static void Require(bool ok,string code,string text)
    {if(!ok)throw new InvalidOperationException(code+"："+text);}
    private static bool Identity(string? value)=>!string.IsNullOrWhiteSpace(value) && value.Length<=4096;
    private static void Unique(IReadOnlyList<string> values,Func<string,string,bool> same)
    {
        Require(values.All(Identity),"BOUNDARY_IDENTITY_INVALID","曲线身份缺失或超限。");
        for(int i=0;i<values.Count;i++)Require(!values.Take(i).Any(v=>same(v,values[i])),
            "BOUNDARY_IDENTITY_AMBIGUOUS","原生曲线身份重复。");
    }
    public static void ValidateRequest(BoundaryDefinitionRequest request,Func<string,string,bool> same)
    {
        Require(request.Profiles.Count is >=2 and <=128 && request.Guides.Count<=128
            && Enum.IsDefined(request.Start) && Enum.IsDefined(request.End),
            "BOUNDARY_REQUEST_INVALID","曲线族数量或端条件无效。");
        var curves=request.Profiles.Concat(request.Guides).ToArray();
        Require(curves.Select(c=>c.SourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count()==curves.Length,
            "BOUNDARY_SOURCE_AMBIGUOUS","曲线来源 ID 在方向间重复。");
        Unique(curves.Select(c=>c.FeatureReference).ToArray(),same);
        foreach(var curve in curves)
        {
            Require(Identity(curve.SourceId) && curve.WholeCurveAliases.Count is >=1 and <=2 && curve.SegmentReferences.Count<=4096
                && curve.WholeCurveAliases.Any(r=>same(r,curve.FeatureReference)),
                "BOUNDARY_IDENTITY_INVALID","源特征身份或完整曲线别名库存不符。");
            Unique(curve.WholeCurveAliases,same);Unique(curve.SegmentReferences,same);
        }
        for(int i=0;i<curves.Length;i++)for(int j=i+1;j<curves.Length;j++)
            Require(!curves[i].WholeCurveAliases.Any(a=>curves[j].WholeCurveAliases.Any(b=>same(a,b))),
                "BOUNDARY_IDENTITY_AMBIGUOUS","不同来源特征不能共享同一完整曲线身份。");
    }
    private static bool Matches(BoundaryCurveBinding expected,BoundaryCurveState actual,Func<string,string,bool> same)=>
        actual.Representation switch
        {
            BoundaryCurveRepresentation.Feature=>same(expected.FeatureReference,actual.Reference),
            BoundaryCurveRepresentation.Sketch=>expected.WholeCurveAliases.Any(a=>same(a,actual.Reference)),
            BoundaryCurveRepresentation.SingleSketchSegment=>actual.OwnerSketchSegmentCount==1 && expected.SegmentReferences.Count==1
                && actual.OwnerSketchReference is not null && expected.WholeCurveAliases.Any(a=>same(a,actual.OwnerSketchReference))
                && same(expected.SegmentReferences[0],actual.Reference),
            _=>false
        };
    public static int ExpectedTangency(BoundaryDefinitionRequest request,int direction,int index,int count)
    {
        var condition=direction==0 && index==0?request.Start:direction==0 && index==count-1?request.End:SurfaceEndCondition.None;
        return condition==SurfaceEndCondition.NormalToProfile?1:0;
    }
    public static void VerifyDeclared(BoundaryDefinitionSnapshot actual,BoundaryDefinitionRequest request,Func<string,string,bool> same)
    {
        ValidateRequest(request,same);
        Require(Identity(actual.FeatureReference) && actual.SurfaceBodiesOnly && actual.Directions.Count==2,
            "BOUNDARY_DEFINITION_UNVERIFIABLE","输出特征身份或曲面体类型无法确认。");
        for(int direction=0;direction<2;direction++)
        {
            var inputs=direction==0?request.Profiles:request.Guides;var state=actual.Directions[direction];
            Require(state.CompleteInventory && state.Curves.Count==inputs.Count,
                "BOUNDARY_INVENTORY_MISMATCH","原生曲线族库存与请求不符。");
            Require(state.Influence==32 && (direction==0 && request.Guides.Count>0 ? state.TrimByD1==false : state.TrimByD1 is null),
                "BOUNDARY_DIRECTION_MISMATCH","全局影响或可用的方向 1 裁剪设置未保留。");
            Unique(state.Curves.Select(c=>c.Reference).ToArray(),same);
            for(int i=0;i<inputs.Count;i++)
            {
                var curve=state.Curves[i];var tangency=ExpectedTangency(request,direction,i,inputs.Count);
                Require(Enum.IsDefined(curve.Representation) && Matches(inputs[i],curve,same),
                    "BOUNDARY_CURVE_IDENTITY","曲线身份、完整曲线表示或顺序与源请求不同。");
                Require(curve.Tangency==tangency,"BOUNDARY_END_CONDITION","原生端条件降级或移到错误曲线。");
                Require(double.IsFinite(curve.DraftAngle) && Math.Abs(curve.DraftAngle)<=1e-10 && !curve.ReverseDraft,
                    "BOUNDARY_DRAFT_MISMATCH","曲线意外保留了拔模或反向控制。");
                if(tangency==1)
                    Require(curve.TangentLength is { } length && double.IsFinite(length) && Math.Abs(length-1)<=1e-10
                        && curve.ReverseTangent==false && (request.Guides.Count>0 || curve.TangentApplyAll==true),
                        "BOUNDARY_TANGENT_MISMATCH","原生端部切向长度、方向或单方向统一控制未保留。");
                else
                    Require(curve.TangentLength is null && curve.ReverseTangent is null && curve.TangentApplyAll is null,
                        "BOUNDARY_CONTROL_UNAVAILABLE","None 端条件不应读取不可用的切向控制。");
            }
        }
    }
    public static BoundaryDefinitionReceipt Execute(IBoundarySurfaceSession session,BoundaryDefinitionRequest request,Func<string,string,bool> same)
    {
        ValidateRequest(request,same);session.ClearSelection();
        var ordered=request.Profiles.Concat(request.Guides).ToArray();var marks=request.Profiles.Select((_,i)=>SelectionMark(0,i,request.Profiles.Count))
            .Concat(request.Guides.Select((_,i)=>SelectionMark(1,i,request.Guides.Count))).ToArray();
        for(int i=0;i<ordered.Length;i++)Require(session.Select(ordered[i].SourceId,marks[i],i>0),
            "BOUNDARY_SELECTION_FAILED","选择请求曲线失败；未调用曲面工厂。");
        var selected=session.CaptureSelection();
        Require(selected.Count==ordered.Length,"BOUNDARY_SELECTION_CONTRACT","实际选择库存数量不同；未创建曲面。");
        for(int i=0;i<ordered.Length;i++)Require(Identity(selected[i].Reference) && selected[i].Mark==marks[i]
            && ordered[i].WholeCurveAliases.Any(a=>same(a,selected[i].Reference)),
            "BOUNDARY_SELECTION_CONTRACT","实际选择身份、顺序或方向标记不符；未创建曲面。");
        for(short direction=0;direction<2;direction++)
        {
            var count=direction==0?request.Profiles.Count:request.Guides.Count;
            for(short i=0;i<count;i++)session.SetCurveData(direction,i,(short)ExpectedTangency(request,direction,i,count),0,1,true);
            // 32 is the installed swBoundaryBossCurve_GlobalInfluence enum and the
            // official Boundary example's value. Method prose in 2025 also lists 0:
            // retain the enum/example path and certify by definition readback, not prose.
            session.SetDirectionData(direction,32,0,false,false);
        }
        var parameters=new BoundaryCreationParameters(2,(short)request.Profiles.Count,(short)request.Guides.Count,false,1,false,
            request.Merge,false,true,false,true,false);
        Require(session.Create(parameters),"BOUNDARY_NATIVE_CREATION","原生 Boundary 工厂未返回特征；不替换为 Fill。");
        var snapshot=session.CaptureDefinition();VerifyDeclared(snapshot,request,same);return new(request,snapshot);
    }
    public static void VerifyRetained(BoundaryDefinitionReceipt receipt,BoundaryDefinitionSnapshot actual,Func<string,string,bool> same)
    {
        VerifyDeclared(receipt.Snapshot,receipt.Request,same);VerifyDeclared(actual,receipt.Request,same);
        Require(same(receipt.Snapshot.FeatureReference,actual.FeatureReference),"SAVED_BOUNDARY_IDENTITY","保存后 Boundary 特征身份变化。");
    }
}
