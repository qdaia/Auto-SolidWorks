using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static SavedRevisionState CaptureSavedRevisionState(IModelDoc2 model, string inputPath, string modelSha)
    {
        if (model is not IPartDoc) throw new InvalidOperationException("保存修订认证目前仅支持解析零件。");
        var document = GeometryDocumentIdentity.FromSavedPath(inputPath, modelSha);
        var entities = new List<SavedRevisionEntity>();
        foreach (var kind in new[] { EntityKind.Body, EntityKind.Face, EntityKind.Edge })
        {
            var reference = new GeometryRef { RefId = "revision-inventory", DocumentId = document.DocumentId,
                DocumentPath = inputPath, ModelSha256 = modelSha, EntityKind = kind,
                Signature = new() { EntityKind = kind } };
            foreach (var candidate in BuildGeometryCandidates(model, reference))
            {
                if (entities.Count >= 4096) throw new InvalidOperationException("保存修订认证库存超过 4096 个实体。");
                if (candidate.NativePersistentReference is not { Length: > 0 } pid)
                    throw new InvalidOperationException("修订库存实体缺持久身份。");
                int error;
                var native = model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(pid), out error);
                if (error != 0 || native is null) throw new InvalidOperationException("修订库存持久身份无法原生读回。");
                var trim = new List<double>();
                IReadOnlyList<string>? boundaryEdges = null;
                SavedSplineSurface? splineSurface = null;
                SavedSplineCurve? splineCurve = null;
                SavedSplineTrimLoop? splineLoop = null;
                if (native is IBody2 body) trim.AddRange(ToDoubles(body.GetBodyBox(), 6, "修订体范围"));
                else if (native is IFace2 face)
                {
                    trim.AddRange(ToDoubles(face.GetBox(), 6, "修订面范围"));
                    var uv = ToDoubles(face.GetUVBounds(), 4, "修订面修剪参数");
                    trim.AddRange(uv);
                    var edges = (face.GetEdges() as object[] ?? []).Cast<IEdge>().ToArray();
                    trim.Add(edges.Length);
                    boundaryEdges = edges.Select(e => Persistent(model, e)
                        ?? throw new InvalidOperationException("修订面边界缺原生持久身份。")).ToArray();
                    var surface = (ISurface)face.GetSurface();
                    if (surface.Identity() == (int)swSurfaceTypes_e.BSURF_TYPE)
                        (splineSurface, splineLoop) = CaptureSavedSplineFace(model, face, surface, boundaryEdges);
                    if (surface.IsSphere())
                    {
                        // Center and radius define the complete analytic surface;
                        // the original orientation and actual boundary identities
                        // supplement the trim bounds and full entity inventory.
                        trim.AddRange(ToDoubles(surface.SphereParams, 4, "修订解析球面参数"));
                        trim.Add(face.FaceInSurfaceSense() ? 1 : 0);
                    }
                    for (int i = 0; i <= 2; i++) for (int j = 0; j <= 2; j++)
                        trim.AddRange(ToDoubles(surface.Evaluate(uv[0] + (uv[1] - uv[0]) * i / 2,
                            uv[2] + (uv[3] - uv[2]) * j / 2, 0, 0), 3, "修订解析面采样").Take(3));
                }
                else if (native is IEdge edge)
                {
                    var data = edge.GetCurveParams3() ?? throw new InvalidOperationException("修订边缺修剪参数。");
                    trim.Add(data.UMinValue); trim.Add(data.UMaxValue); trim.Add(EdgeLengthMm(edge));
                    var curve = (ICurve)edge.GetCurve();
                    if (curve.IsBcurve()) splineCurve = CaptureSavedSplineCurve(curve);
                    for (int i = 0; i <= 4; i++)
                        trim.AddRange(ToDoubles(curve.Evaluate2(data.UMinValue + (data.UMaxValue - data.UMinValue) * i / 4, 0),
                            3, "修订解析边采样").Take(3));
                }
                else throw new InvalidOperationException("修订库存对象原生类型不符。");
                entities.Add(new(candidate, trim) { BoundaryEdgeReferences = boundaryEdges,
                    SplineSurface = splineSurface, SplineCurve = splineCurve, SplineTrimLoop = splineLoop });
            }
        }
        var parameters = new List<object>(); var visited = new HashSet<int>();
        void Visit(IFeature feature)
        {
            if (!visited.Add(feature.GetID())) return;
            if (visited.Count > 1000) throw new InvalidOperationException("修订特征库存超过 1000 项。");
            parameters.Add(new { feature.Name, Type = feature.GetTypeName2(), Suppressed = feature.IsSuppressed(),
                PersistentReference = Persistent(model, feature),
                Parents = (feature.GetParents() as object[] ?? []).OfType<IFeature>().Select(p => Persistent(model, p)).Order().ToArray(),
                Dimensions = ReadNativeFeatureDimensions(model, feature).OrderBy(d => d.Name).ToArray() });
            for (var child = feature.IGetFirstSubFeature(); child is not null; child = child.IGetNextSubFeature()) Visit(child);
        }
        for (var f = model.IFirstFeature(); f is not null; f = f.IGetNextFeature()) Visit(f);
        var mgr = model.GetEquationMgr();
        var equations = Enumerable.Range(0, mgr.GetCount()).Select(i => new { Equation = mgr.Equation[i],
            Value = mgr.Value[i], Disabled = mgr.Disabled[i], Global = mgr.GlobalVariable[i], Scope = mgr.GetConfigurationOption(i) }).ToArray();
        var geometry = MeasureGeometry(model);
        return new() { ModelSha256 = ReadInspectionHash(inputPath), Configuration = model.ConfigurationManager.ActiveConfiguration.Name,
            ParameterState = JsonSerializer.Serialize(new { Features = parameters, Equations = equations,
                Configurations = model.GetConfigurationNames(), HasDesignTable = model.Extension.HasDesignTable() }, ModelingIrJson.Options),
            Geometry = geometry, Entities = entities, Complete = true, CleanSavedModel = !model.GetSaveFlag(),
            NativeBodiesValid = RequireValidNativeBodies(model).Passed };
    }

    private static bool HasSemanticInspectionReferences(ModelInspectionRequest request) =>
        (request.GeometryReferences ?? []).Any(r => r.Semantic is not null)
        || (request.Measurements ?? []).Any(q => q.Geometry.Semantic is not null || q.SecondaryGeometry?.Semantic is not null)
        || (request.ConnectivityQueries ?? []).Any(q => q.GeometryRefs.Any(r => r.Semantic is not null));
}
