using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static GeometryRefResolution ResolveGeometryReference(IModelDoc2 model, string inputPath, string modelSha256,
        GeometryRef reference, string? documentRevision, string? sourceRevisionId,ISemanticTopologyHistorySession? historySession=null)
    {
        var document = GeometryDocumentIdentity.FromSavedPath(inputPath, modelSha256, documentRevision) with { SourceRevisionId = sourceRevisionId };
        try
        {
            if (reference.EntityKind is EntityKind.Face or EntityKind.Edge or EntityKind.Body && model is not IPartDoc)
                return Unsupported(reference, modelSha256, "GeometryRef 面/边/体的分辨率目前仅支持原生零件文档。");
            var candidates = reference.InputToFeature is null ? BuildGeometryCandidates(model, reference) : BuildFeatureInputCandidates(model,reference);
            if(reference.Semantic is not null&&model.GetSaveFlag())
                return Unsupported(reference,modelSha256,"未保存的内存模型不能以磁盘文件哈希认证语义拓扑修订。");
            TopologyHistoryCapture? history = null;
            if (reference.Semantic is not null)
            {
                if (historySession is not null) history = historySession.Capture(reference,document);
                else
                {
                    var durable = new DurableTopologyHistoryStore(NativeTopologyHistoryRoot).Load(reference,document);
                    if (durable is not null)
                    {
                        history = durable.Capture;
                        var endpoint = history.Snapshots[^1].Document;
                        document = document with { SourceRevisionId=sourceRevisionId??endpoint.SourceRevisionId,
                            DocumentRevision=documentRevision??endpoint.DocumentRevision };
                    }
                    else history = new NativeUnchangedTopologySession(candidates,model.ConfigurationManager.ActiveConfiguration.Name).Capture(reference,document);
                }
            }
            return GeometryRefResolver.Resolve(reference, document, candidates,history);
        }
        catch (Exception ex)
        {
            return Unsupported(reference, modelSha256, "实际B-Rep候选项库存无法完成：" + ex.Message);
        }
    }

    private static IReadOnlyList<GeometryCandidate> BuildFeatureInputCandidates(IModelDoc2 model,GeometryRef reference)
    {
        GeometryRefResolver.Validate(reference);
        var feature=FindFeatureByName(model,reference.InputToFeature!);
        if(feature is null||feature.IsSuppressed())throw new InvalidOperationException("输入范围内的特征缺失或被抑制。");
        var definition=feature.GetDefinition();
        var accessed=false;
        try
        {
            accessed=definition switch
            {
                ISimpleFilletFeatureData2 fillet=>fillet.AccessSelections(model,null),
                IChamferFeatureData2 chamfer=>chamfer.AccessSelections(model,null),
                _=>false
            };
            if(!accessed)throw new InvalidOperationException("原生特征输入范围不被支持或无法访问。");
            // Inventory every edge in the input B-Rep, not just the feature's selected
            // edges, so source resolution and native driving-edge ownership stay separate.
            return BuildGeometryCandidates(model,reference).Select(c=>c with{InputToFeature=feature.Name}).ToArray();
        }
        finally
        {
            if(accessed)
            {
                if(definition is ISimpleFilletFeatureData2 fillet)fillet.ReleaseSelectionAccess();
                else if(definition is IChamferFeatureData2 chamfer)chamfer.ReleaseSelectionAccess();
                if(!model.ForceRebuild3(false))throw new InvalidOperationException("特征输入检查无法恢复最终模型。");
            }
        }
    }

    private static IReadOnlyList<GeometryCandidate> BuildGeometryCandidates(IModelDoc2 model, GeometryRef reference)
    {
        var result = new List<GeometryCandidate>();
        var index = 0;
        if (reference.EntityKind is EntityKind.Feature or EntityKind.Plane or EntityKind.Axis)
        {
            var visited = new HashSet<int>();
            void Visit(IFeature feature)
            {
                if (!visited.Add(feature.GetID())) return;
                if (visited.Count > 10000) throw new InvalidOperationException("特征库存超过10000个GeometryRef。");
                var type = feature.GetTypeName2();
                var accepted = reference.EntityKind == EntityKind.Feature || reference.EntityKind == EntityKind.Plane && type == "RefPlane" ||
                    reference.EntityKind == EntityKind.Axis && type == "RefAxis";
                if (accepted)
                {
                    result.Add(new()
                    {
                        CandidateId = $"feature-{++index:D5}",
                        NativePersistentReference = Persistent(model, feature),
                        FeatureId = feature.Name,
                        Signature = Signature(reference, reference.EntityKind, GeometryKind.Any, null, null, null, null)
                    });
                }
                for (var child = feature.IGetFirstSubFeature(); child is not null; child = child.IGetNextSubFeature()) Visit(child);
            }
            for (var feature = model.IFirstFeature(); feature is not null; feature = feature.IGetNextFeature()) Visit(feature);
            return result;
        }

        var part = (IPartDoc)model;
        var bodies = (part.GetBodies2(-1, false) as object[] ?? []).Cast<IBody2>().ToArray();
        if (bodies.Length > 10000) throw new InvalidOperationException("GeometryRef 体库存超出 10000 体的数量。");
        if (reference.EntityKind == EntityKind.Body)
        {
            foreach (var body in bodies)
            {
                var box = ToDoubles(body.GetBodyBox(), 6, "体的边界");
                var anchor = new Vector3((box[0] + box[3]) * 500, (box[1] + box[4]) * 500, (box[2] + box[5]) * 500);
                var area = (body.GetFaces() as object[] ?? []).Cast<IFace2>().Sum(face => face.GetArea()) * 1_000_000;
                result.Add(new()
                {
                    CandidateId = $"body-{++index:D5}", NativePersistentReference = Persistent(model, body), FeatureId = body.Name,
                    Signature = Signature(reference, EntityKind.Body, GeometryKind.Any, anchor, null, null, area > 0 ? area : null)
                });
            }
            return result;
        }

        if (reference.EntityKind == EntityKind.Face)
        {
            var faces = bodies.SelectMany(body => body.GetFaces() as object[] ?? []).Cast<IFace2>().ToArray();
            if (faces.Length > 50000) throw new InvalidOperationException("GeometryRef 面库存超过了 50000 面的数量。");
            foreach (var face in faces)
            {
                var surface = (ISurface)face.GetSurface();
                var kind = surface.IsPlane() ? GeometryKind.Plane : surface.IsCylinder() ? GeometryKind.Cylinder : surface.IsCone() ? GeometryKind.Cone :
                    surface.IsSphere() ? GeometryKind.Sphere : surface.IsTorus() ? GeometryKind.Torus :
                    surface.Identity() == (int)swSurfaceTypes_e.BSURF_TYPE ? GeometryKind.BSpline : GeometryKind.Any;
                Vector3? anchor = null; Vector3? direction = null; double? radius = null;
                if (kind == GeometryKind.Cylinder)
                {
                    var p = ToDoubles(surface.CylinderParams, 7, "圆柱参数");
                    var origin = new Vector3(p[0] * 1000, p[1] * 1000, p[2] * 1000);
                    direction = ModelVerification.Unit(new(p[3], p[4], p[5])); radius = p[6] * 1000;
                    var uv = ToDoubles(face.GetUVBounds(), 4, "圆柱体 UV 边界");
                    var midpoint = ToDoubles(surface.Evaluate((uv[0] + uv[1]) / 2, (uv[2] + uv[3]) / 2, 0, 0), 3, "圆柱体剪裁中点");
                    anchor = AxisProjection(origin, direction, new(midpoint[0] * 1000, midpoint[1] * 1000, midpoint[2] * 1000));
                }
                else if (kind == GeometryKind.Cone)
                {
                    var cone=MeasureCone(model,face);
                    direction=cone.Direction;
                    anchor=ModelVerification.Scale(ModelVerification.Add(cone.AxisStartMm,cone.AxisEndMm),.5);
                }
                else if (kind == GeometryKind.Plane)
                {
                    var p = ToDoubles(surface.PlaneParams, 3, "平面参数");
                    direction = ModelVerification.Unit(new(p[0], p[1], p[2]));
                    anchor = CanonicalFaceAnchor(face);
                }
                else if(kind==GeometryKind.Sphere) {var p=ReadSphereParameters(surface);anchor=p.CenterMm;radius=p.RadiusMm;}
                else anchor = CanonicalFaceAnchor(face);
                var area = face.GetArea() * 1_000_000;
                var owner=face.GetFeature() as IFeature;
                result.Add(new()
                {
                    CandidateId = $"face-{++index:D5}", NativePersistentReference = Persistent(model, face),
                    FeatureId = owner?.Name,OwnerFeaturePersistentReference=owner is null?null:Persistent(model,owner),
                    Signature = Signature(reference, EntityKind.Face, kind, anchor, direction, radius, area > 0 ? area : null)
                });
            }
            return result;
        }

        if (reference.EntityKind == EntityKind.Edge)
        {
            var edges = bodies.SelectMany(body => body.GetEdges() as object[] ?? []).Cast<IEdge>().Distinct().ToArray();
            if (edges.Length > 100000) throw new InvalidOperationException("GeometryRef 边缘库存超出 100000 边缘数量。");
            foreach (var edge in edges)
            {
                var curve = (ICurve)edge.GetCurve();
                var kind = curve.IsCircle() ? GeometryKind.Circle : curve.IsLine() ? GeometryKind.Line :
                    curve.IsBcurve() ? GeometryKind.BSpline : GeometryKind.Any;
                Vector3? anchor = null; Vector3? direction = null; double? radius = null;
                if (kind == GeometryKind.Circle)
                {
                    var p = ToDoubles(curve.CircleParams, 7, "圆的参数");
                    anchor = new(p[0] * 1000, p[1] * 1000, p[2] * 1000);
                    direction = ModelVerification.Unit(new(p[3], p[4], p[5])); radius = p[6] * 1000;
                }
                else if (kind == GeometryKind.Line)
                {
                    var p = ToDoubles(curve.LineParams, 6, "线参数");
                    direction = ModelVerification.Unit(new(p[3], p[4], p[5]));
                    anchor = CanonicalEdgeAnchor(edge) ?? new(p[0] * 1000, p[1] * 1000, p[2] * 1000);
                }
                else anchor = CanonicalEdgeAnchor(edge);
                var owners=(edge.GetTwoAdjacentFaces2() as object[]??[]).OfType<IFace2>().Select(f=>f.GetFeature()).OfType<IFeature>()
                    .DistinctBy(f=>f.GetID()).ToArray();
                var owner=owners.Length==1?owners[0]:null;
                result.Add(new()
                {
                    CandidateId = $"edge-{++index:D5}", NativePersistentReference = Persistent(model, edge),
                    FeatureId=owner?.Name,OwnerFeaturePersistentReference=owner is null?null:Persistent(model,owner),
                    OwnerFeatures=owners.Select(f=>new GeometryOwnerIdentity(f.Name,Persistent(model,f)
                        ?? throw new InvalidOperationException("相邻面来源特征缺少原生持久身份。")))
                        .OrderBy(o=>o.PersistentReference,StringComparer.Ordinal).ToArray(),
                    Signature = Signature(reference, EntityKind.Edge, kind, anchor, direction, radius, null)
                });
            }
            return result;
        }
        return result;
    }

    private static GeometrySignature Signature(GeometryRef reference, EntityKind entityKind, GeometryKind geometryKind,
        Vector3? anchor, Vector3? direction, double? radius, double? area) => new()
    {
        EntityKind = entityKind,
        GeometryKind = geometryKind,
        AnchorMm = anchor,
        Direction = direction,
        RadiusMm = radius,
        AreaMm2 = area,
        PositionToleranceMm = reference.Signature.PositionToleranceMm,
        RadiusToleranceMm = reference.Signature.RadiusToleranceMm,
        AreaToleranceMm2 = reference.Signature.AreaToleranceMm2,
        DirectionToleranceDegrees = reference.Signature.DirectionToleranceDegrees
    };

    private static Vector3 Closest(IFace2 face, Vector3 point)
    {
        var p = ToDoubles(face.GetClosestPointOn(Mm(point.X), Mm(point.Y), Mm(point.Z)), 3, "面的最近点");
        return new(p[0] * 1000, p[1] * 1000, p[2] * 1000);
    }

    private static Vector3 CanonicalFaceAnchor(IFace2 face)
    {
        var box = ToDoubles(face.GetBox(), 6, "面的边界");
        var center = new Vector3((box[0] + box[3]) * 500, (box[1] + box[4]) * 500, (box[2] + box[5]) * 500);
        return Closest(face, center);
    }

    private static Vector3 Closest(IEdge edge, Vector3 point)
    {
        var p = ToDoubles(edge.GetClosestPointOn(Mm(point.X), Mm(point.Y), Mm(point.Z)), 3, "边线最近点");
        return new(p[0] * 1000, p[1] * 1000, p[2] * 1000);
    }

    private static Vector3? CanonicalEdgeAnchor(IEdge edge)
    {
        if ((IVertex?)edge.GetStartVertex() is not { } start || (IVertex?)edge.GetEndVertex() is not { } end ||
            start.GetPoint() is not double[] a || end.GetPoint() is not double[] b || a.Length < 3 || b.Length < 3)
            return null;
        return new((a[0] + b[0]) * 500, (a[1] + b[1]) * 500, (a[2] + b[2]) * 500);
    }

    private static Vector3 AxisProjection(Vector3 origin, Vector3 direction, Vector3 point)
    {
        var delta = ModelVerification.Sub(point, origin);
        return ModelVerification.Add(origin, ModelVerification.Scale(direction, ModelVerification.Dot(delta, direction)));
    }

    private static GeometryRefResolution Unsupported(GeometryRef reference, string modelSha256, string message) => new()
    {
        Reference = reference,
        Status = GeometryRefResolutionStatus.Unsupported,
        ResolvedModelSha256 = modelSha256,
        Message = message,
        Evidence = [$"actual_model_sha256={modelSha256}"]
    };
}
