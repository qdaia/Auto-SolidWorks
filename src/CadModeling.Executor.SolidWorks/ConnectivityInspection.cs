using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System.Runtime.InteropServices;

internal sealed partial class SolidWorksComExecutor
{
    private sealed record AxialCylinderFace(IFace2 Face, Vector3 AxisStartMm, Vector3 AxisEndMm, Vector3 Direction,
        double RadiusMm, string BodyId, bool LateralBoundaryExcluded);

    private static ConnectivityCheck InspectConnectivity(IModelDoc2 model, ConnectivityInspectionQuery query,
        IReadOnlyDictionary<string, GeometryRefResolution> resolutions, string actualModelSha256, bool modelReopened)
    {
        ConnectivityVerifier.Validate(query);
        var scoped = query.GeometryRefs.Select(reference => resolutions[reference.RefId]).ToArray();
        if (scoped.Any(item => item.Status != GeometryRefResolutionStatus.Resolved || item.Candidate is null))
            return ConnectivityVerifier.Evaluate(query.Requirement, new()
            {
                CheckId = query.QueryId, ActualModelSha256 = actualModelSha256, ModelReopened = modelReopened,
                GeometryResolutions = scoped, Complete = false, Method = "solidworks_brep_connectivity",
                Error = "One or more T06 GeometryRefs did not resolve uniquely on the inspected model."
            });
        if (model is not IPartDoc part)
            return Unsupported(query, "T08 actual connectivity inspection currently supports native part documents only.");

        try
        {
            return query.Requirement.Kind == ConnectivityKind.SolidConnection
                ? InspectSolidConnection(model, query, scoped, actualModelSha256, modelReopened)
                : InspectAxialVoid(model, part, query, scoped, actualModelSha256, modelReopened);
        }
        catch (Exception ex)
        {
            return ConnectivityVerifier.Evaluate(query.Requirement, new()
            {
                CheckId = query.QueryId, ActualModelSha256 = actualModelSha256, ModelReopened = modelReopened,
                GeometryResolutions = scoped, Complete = false, Method = "solidworks_brep_connectivity", Error = ex.Message
            });
        }
    }

    private static ConnectivityCheck InspectSolidConnection(IModelDoc2 model, ConnectivityInspectionQuery query,
        IReadOnlyList<GeometryRefResolution> scoped, string modelSha256, bool reopened)
    {
        var groupIds = new List<string>();
        foreach (var resolution in scoped)
        {
            var entity = ResolveCurrentCandidate(model, resolution);
            var body = entity switch
            {
                IBody2 bodyEntity => bodyEntity,
                IFace2 face => (IBody2)face.GetBody(),
                IEdge edge => (edge.GetTwoAdjacentFaces2() as object[] ?? []).OfType<IFace2>().Select(face => (IBody2)face.GetBody()).FirstOrDefault(),
                _ => null
            } ?? throw new InvalidOperationException("Solid-connection GeometryRef must resolve to a body, face, or edge with an owning solid body.");
            if (body.GetType() != (int)swBodyType_e.swSolidBody)
                throw new InvalidOperationException("Solid-connection evidence requires solid bodies.");
            groupIds.Add(Persistent(model, body) ?? "body-name:" + body.Name);
        }
        var observation = new ConnectivityObservation
        {
            CheckId = query.QueryId,
            ActualModelSha256 = modelSha256,
            ModelReopened = reopened,
            GeometryResolutions = scoped,
            MaterialGroupIds = groupIds,
            Complete = true,
            Method = "solidworks_owning_body_identity"
        };
        return ConnectivityVerifier.Evaluate(query.Requirement, observation);
    }

    private static ConnectivityCheck InspectAxialVoid(IModelDoc2 model, IPartDoc part, ConnectivityInspectionQuery query,
        IReadOnlyList<GeometryRefResolution> scoped, string modelSha256, bool reopened)
    {
        var cylinders = scoped.Select(resolution => MeasureInteriorAxialFace(model, resolution)).ToArray();
        if (cylinders.Length == 0) return Unsupported(query, "Axial connectivity query has no cylindrical interior faces.");

        var direction = ModelVerification.Unit(cylinders[0].Direction);
        var axisOrigin = cylinders[0].AxisStartMm;
        var angularTolerance = Math.Cos(.25 * Math.PI / 180);
        var axisTolerance = query.GeometryRefs.Min(reference => reference.Signature.PositionToleranceMm);
        foreach (var cylinder in cylinders)
        {
            var current = ModelVerification.Unit(cylinder.Direction);
            if (Math.Abs(ModelVerification.Dot(direction, current)) < angularTolerance)
                throw new InvalidOperationException("Scoped cylindrical faces are not coaxial within the frozen angular threshold.");
            var delta = ModelVerification.Sub(cylinder.AxisStartMm, axisOrigin);
            var perpendicular = ModelVerification.Sub(delta, ModelVerification.Scale(direction, ModelVerification.Dot(delta, direction)));
            if (ModelVerification.Norm(perpendicular) > axisTolerance)
                throw new InvalidOperationException("Scoped cylindrical faces do not share one axial line within GeometryRef tolerance.");
        }

        var segments = cylinders.Select(cylinder =>
        {
            var a = ModelVerification.Dot(ModelVerification.Sub(cylinder.AxisStartMm, axisOrigin), direction);
            var b = ModelVerification.Dot(ModelVerification.Sub(cylinder.AxisEndMm, axisOrigin), direction);
            return (Cylinder: cylinder, Min: Math.Min(a, b), Max: Math.Max(a, b));
        }).OrderBy(item => item.Min).ToArray();
        var overallMin = segments.Min(item => item.Min);
        var overallMax = segments.Max(item => item.Max);
        var startSegment = segments.OrderBy(item => item.Min).First();
        var endSegment = segments.OrderByDescending(item => item.Max).First();
        var startPoint = ModelVerification.Add(axisOrigin, ModelVerification.Scale(direction, overallMin));
        var endPoint = ModelVerification.Add(axisOrigin, ModelVerification.Scale(direction, overallMax));
        var bodies = (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? []).OfType<IBody2>().ToArray();
        if (bodies.Length == 0) throw new InvalidOperationException("No solid bodies are available for connectivity ray probing.");

        var startEvidence = ProbeAxialEnd(model, bodies, query, startPoint, ModelVerification.Scale(direction, -1),
            startSegment.Cylinder.RadiusMm, "start");
        var endEvidence = ProbeAxialEnd(model, bodies, query, endPoint, direction, endSegment.Cylinder.RadiusMm, "end");
        var bothClosed = IsClosed(startEvidence) && IsClosed(endEvidence);
        var axialSegments = segments.Select(item => new AxialInterval(item.Min, item.Max)).ToArray();
        var segmentDetails = segments.Select((item, segmentIndex) => new AxialSegmentEvidence
        {
            SegmentId = $"segment-{segmentIndex + 1:D3}", StartMm = item.Min, EndMm = item.Max,
            RadiusMm = item.Cylinder.RadiusMm, BodyId = item.Cylinder.BodyId,
            LateralBoundaryExcluded = item.Cylinder.LateralBoundaryExcluded
        }).ToArray();
        var passage = ProbeAxialPassage(model, bodies, query, startPoint, endPoint, direction, overallMin, overallMax,
            cylinders.Min(item => item.RadiusMm), segmentDetails.All(item => item.LateralBoundaryExcluded),
            bodies.Select(body => Persistent(model, body) ?? "body-name:" + body.Name).ToArray());
        var observation = new ConnectivityObservation
        {
            CheckId = query.QueryId,
            ActualModelSha256 = modelSha256,
            ModelReopened = reopened,
            GeometryResolutions = scoped,
            AxisOriginMm = axisOrigin,
            Axis = direction,
            AxialInterval = new(overallMin, overallMax),
            AxialSegments = axialSegments,
            AxialSegmentDetails = segmentDetails,
            PassageEvidence = passage,
            StartEvidence = startEvidence,
            EndEvidence = endEvidence,
            VoidGroupIds = query.Requirement.Kind == ConnectivityKind.InternalCavity && bothClosed && passage.Complete &&
                passage.BlockedInteriorSampleCount == 0 && passage.LateralOutletExcluded ? ["bounded-void-1"] : [],
            Complete = startEvidence.Complete && endEvidence.Complete && passage.Complete,
            Method = "solidworks_trimmed_axial_walls_plus_end_and_passage_ray_entry_exit"
        };
        return ConnectivityVerifier.Evaluate(query.Requirement, observation);
    }

    private static AxialCylinderFace MeasureInteriorAxialFace(IModelDoc2 model, GeometryRefResolution resolution)
    {
        var entity = ResolveCurrentCandidate(model, resolution);
        if (entity is not IFace2 face) throw new InvalidOperationException("Axial hole/cavity GeometryRef must resolve to an actual cylindrical face.");
        var surface = (ISurface)face.GetSurface();
        if(surface.IsCone())
        {
            var cone=MeasureCone(model,face);
            if(!cone.Interior||!cone.CompleteWall)throw new InvalidOperationException("Scoped cone is not a complete interior wall: "+cone.BoundaryEvidence);
            var coneBody=(IBody2)face.GetBody();
            return new(face,cone.AxisStartMm,cone.AxisEndMm,cone.Direction,Math.Min(cone.StartRadiusMm,cone.EndRadiusMm),
                Persistent(model,coneBody)??"body-name:"+coneBody.Name,true);
        }
        if (!surface.IsCylinder()) throw new InvalidOperationException("Axial hole/cavity GeometryRef resolved to a non-cylindrical face.");
        var data = ToDoubles(surface.CylinderParams, 7, "connectivity cylinder parameters");
        var origin = new Vector3(data[0] * 1000, data[1] * 1000, data[2] * 1000);
        var direction = ModelVerification.Unit(new(data[3], data[4], data[5]));
        var radius = data[6] * 1000;
        if (!ModelVerification.Positive(radius)) throw new InvalidOperationException("Connectivity cylinder radius is invalid.");
        var uv = ToDoubles(face.GetUVBounds(), 4, "connectivity cylinder UV bounds");
        var p0 = ToDoubles(surface.Evaluate((uv[0] + uv[1]) / 2, uv[2], 0, 0), 6, "connectivity cylinder end A");
        var p1 = ToDoubles(surface.Evaluate((uv[0] + uv[1]) / 2, uv[3], 0, 0), 6, "connectivity cylinder end B");
        Vector3 AxisPoint(double[] evaluated)
        {
            var position = new Vector3(evaluated[0] * 1000, evaluated[1] * 1000, evaluated[2] * 1000);
            return ModelVerification.Add(origin, ModelVerification.Scale(direction,
                ModelVerification.Dot(ModelVerification.Sub(position, origin), direction)));
        }
        var mid = ToDoubles(surface.Evaluate((uv[0] + uv[1]) / 2, (uv[2] + uv[3]) / 2, 0, 0), 6, "connectivity cylinder normal");
        var point = new Vector3(mid[0] * 1000, mid[1] * 1000, mid[2] * 1000);
        var radial = ModelVerification.Sub(point, AxisPoint(mid));
        var normal = new Vector3(mid[3], mid[4], mid[5]);
        if (face.FaceInSurfaceSense()) normal = ModelVerification.Scale(normal, -1);
        if (!ModelVerification.Finite(normal) || ModelVerification.Norm(normal) <= 1e-12 || ModelVerification.Norm(radial) <= 1e-12 ||
            ModelVerification.Dot(normal, radial) >= 0)
            throw new InvalidOperationException("Scoped cylindrical face is not an interior void wall or has invalid normal evidence.");
        var startAxis = AxisPoint(p0); var endAxis = AxisPoint(p1);
        var body = (IBody2)face.GetBody();
        var lateralBoundaryExcluded = CylinderLateralBoundaryExcluded(face, startAxis, endAxis, direction, radius, .02);
        return new(face, startAxis, endAxis, direction, radius, Persistent(model, body) ?? "body-name:" + body.Name, lateralBoundaryExcluded);
    }

    private static bool CylinderLateralBoundaryExcluded(IFace2 face, Vector3 axisStartMm, Vector3 axisEndMm, Vector3 axisDirection,
        double radiusMm, double toleranceMm)
    {
        var edges = (face.GetEdges() as object[] ?? []).OfType<IEdge>().ToArray();
        if (edges.Length == 0) return false;
        var axis = ModelVerification.Unit(axisDirection);
        var startStation = ModelVerification.Dot(axisStartMm, axis);
        var endStation = ModelVerification.Dot(axisEndMm, axis);
        var min = Math.Min(startStation, endStation);
        var max = Math.Max(startStation, endStation);
        var hasMinEndBoundary = false;
        var hasMaxEndBoundary = false;
        foreach (var edge in edges)
        {
            var curve = (ICurve?)edge.GetCurve();
            if (curve is null) return false;
            if (curve.IsLine())
            {
                var p = ToDoubles(curve.LineParams, 6, "cylinder boundary line");
                var d = ModelVerification.Unit(new Vector3(p[3], p[4], p[5]));
                if (Math.Abs(ModelVerification.Dot(axis, d)) < Math.Cos(.25 * Math.PI / 180)) return false;
                // An axial trim edge is only a harmless periodic seam / split-cylinder edge when both sides
                // are supported by the same coaxial cylindrical wall. A slot lip has a planar/non-cylinder
                // neighbour and therefore cannot be promoted to "lateral outlet excluded".
                var adjacent = (edge.GetTwoAdjacentFaces2() as object[] ?? []).OfType<IFace2>().ToArray();
                if (adjacent.Length != 2 || adjacent.Any(item => !MatchesCylinderSupport(item, axisStartMm, axis, radiusMm, toleranceMm)))
                    return false;
                continue;
            }
            if (curve.IsCircle())
            {
                var p = ToDoubles(curve.CircleParams, 7, "cylinder boundary circle");
                var center = new Vector3(p[0] * 1000, p[1] * 1000, p[2] * 1000);
                var circleAxis = ModelVerification.Unit(new Vector3(p[3], p[4], p[5]));
                if (Math.Abs(ModelVerification.Dot(axis, circleAxis)) < Math.Cos(.25 * Math.PI / 180)) return false;
                if (!double.IsFinite(p[6]) || Math.Abs(p[6] * 1000 - radiusMm) > toleranceMm) return false;
                var station = ModelVerification.Dot(center, axis);
                if (Math.Min(Math.Abs(station - min), Math.Abs(station - max)) > toleranceMm) return false;
                var nearestEnd = Math.Abs(station - startStation) <= Math.Abs(station - endStation) ? axisStartMm : axisEndMm;
                var delta = ModelVerification.Sub(center, nearestEnd);
                var perpendicular = ModelVerification.Sub(delta, ModelVerification.Scale(axis, ModelVerification.Dot(delta, axis)));
                if (ModelVerification.Norm(perpendicular) > toleranceMm) return false;
                if (Math.Abs(station - min) <= toleranceMm) hasMinEndBoundary = true;
                if (Math.Abs(station - max) <= toleranceMm) hasMaxEndBoundary = true;
                continue;
            }
            return false; // trimmed by a non-axial/non-end curve: branch/side outlet cannot be excluded
        }
        return hasMinEndBoundary && hasMaxEndBoundary;

        static bool MatchesCylinderSupport(IFace2 adjacentFace, Vector3 axisPointMm, Vector3 axis, double expectedRadiusMm, double toleranceMm)
        {
            var surface = (ISurface?)adjacentFace.GetSurface();
            if (surface is null || !surface.IsCylinder()) return false;
            var p = ToDoubles(surface.CylinderParams, 7, "adjacent cylinder parameters");
            var direction = ModelVerification.Unit(new Vector3(p[3], p[4], p[5]));
            if (Math.Abs(ModelVerification.Dot(axis, direction)) < Math.Cos(.25 * Math.PI / 180)) return false;
            var radius = p[6] * 1000;
            if (!double.IsFinite(radius) || Math.Abs(radius - expectedRadiusMm) > toleranceMm) return false;
            var supportPoint = new Vector3(p[0] * 1000, p[1] * 1000, p[2] * 1000);
            var delta = ModelVerification.Sub(supportPoint, axisPointMm);
            var perpendicular = ModelVerification.Sub(delta, ModelVerification.Scale(axis, ModelVerification.Dot(delta, axis)));
            return ModelVerification.Norm(perpendicular) <= toleranceMm;
        }
    }

    private static AxialPassageEvidence ProbeAxialPassage(IModelDoc2 model, IReadOnlyList<IBody2> bodies,
        ConnectivityInspectionQuery query, Vector3 startMm, Vector3 endMm, Vector3 direction,
        double coverageStartMm, double coverageEndMm, double apertureRadiusMm,
        bool lateralOutletExcluded, IReadOnlyList<string> bodyIds)
    {
        var axis = ModelVerification.Unit(direction);
        var sampleCount = Math.Max(3, query.EndProbeCount);
        var span = ModelVerification.Dot(ModelVerification.Sub(endMm, startMm), axis);
        if (!double.IsFinite(span) || span <= query.ProbeInsetMm * 2)
            return Incomplete("Axial passage interval is too short for bounded interior probes.");
        var helper = Math.Abs(axis.Z) < .9 ? new Vector3(0, 0, 1) : new Vector3(0, 1, 0);
        var radialU = ModelVerification.Unit(Cross(axis, helper));
        var radialV = ModelVerification.Unit(Cross(axis, radialU));
        var bases = new List<Vector3>(); var baseValues = new List<double>(); var directions = new List<double>();
        for (var i = 0; i < sampleCount; i++)
        {
            var radial = new Vector3(0, 0, 0);
            if (i > 0)
            {
                var angle = 2 * Math.PI * (i - 1) / Math.Max(1, sampleCount - 1);
                radial = ModelVerification.Add(ModelVerification.Scale(radialU, apertureRadiusMm * .25 * Math.Cos(angle)),
                    ModelVerification.Scale(radialV, apertureRadiusMm * .25 * Math.Sin(angle)));
            }
            var point = ModelVerification.Add(ModelVerification.Add(startMm, ModelVerification.Scale(axis, query.ProbeInsetMm)), radial);
            bases.Add(point);
            baseValues.AddRange([point.X / 1000, point.Y / 1000, point.Z / 1000]);
            directions.AddRange([axis.X, axis.Y, axis.Z]);
        }
        // Explicitly request normals so the documented hit layout is always nine doubles.
        var options = (int)(swRayPtsOpts_e.swRayPtsOptsENTRY_EXIT | swRayPtsOpts_e.swRayPtsOptsNORMALS);
        // SOLIDWORKS expects SAFEARRAY(IDispatch), not a VARIANT array of RCWs.
        var count = model.Extension.RayIntersections(bodies.Select(body => new DispatchWrapper(body)).ToArray(), baseValues.ToArray(), directions.ToArray(), options,
            0, query.NumericalToleranceMm / 1000, true);
        if (count < 0) return Incomplete("SOLIDWORKS returned a negative axial-passage ray count.");
        var values = count == 0 ? [] : ToDoubles(model.GetRayIntersectionsPoints(), count * 9, "axial passage ray intersections");
        if (values.Length != count * 9) return Incomplete("Axial-passage ray payload size is inconsistent.");
        var blocked = new HashSet<int>();
        var interiorLimit = span - Math.Max(query.ProbeInsetMm + query.Requirement.SealDetectionThresholdMm, query.NumericalToleranceMm * 4);
        for (var hitIndex = 0; hitIndex < count; hitIndex++)
        {
            var offset = hitIndex * 9;
            _ = ExactIndex(values[offset], bodies.Count, "body index");
            var rayIndex = ExactIndex(values[offset + 1], sampleCount, "ray index");
            var type = (int)Math.Round(values[offset + 2]);
            var point = new Vector3(values[offset + 3] * 1000, values[offset + 4] * 1000, values[offset + 5] * 1000);
            var distance = ModelVerification.Dot(ModelVerification.Sub(point, bases[rayIndex]), axis);
            // Any entry/exit boundary hit inside the declared interior span proves the probe ray intersects material.
            // Counting EXIT as well as ENTER also catches a probe base that unexpectedly started inside residual material.
            if (type != 0 && distance > query.NumericalToleranceMm && distance < interiorLimit)
                blocked.Add(rayIndex);
        }
        return new()
        {
            SampleCount = sampleCount,
            BlockedInteriorSampleCount = blocked.Count,
            Complete = true,
            CoverageStartMm = coverageStartMm,
            CoverageEndMm = coverageEndMm,
            BodyScopeComplete = true,
            LateralOutletExcluded = lateralOutletExcluded,
            BodyIds = bodyIds,
            NumericalUncertaintyMm = query.NumericalToleranceMm,
            Method = "solidworks_multi_ray_axial_passage_entry_detection"
        };

        AxialPassageEvidence Incomplete(string message) => new()
        {
            SampleCount = sampleCount,
            Complete = false,
            CoverageStartMm = coverageStartMm,
            CoverageEndMm = coverageEndMm,
            BodyScopeComplete = bodyIds.Count == bodies.Count && bodyIds.Count > 0,
            LateralOutletExcluded = false,
            BodyIds = bodyIds,
            NumericalUncertaintyMm = query.NumericalToleranceMm,
            Method = "solidworks_multi_ray_axial_passage:" + message
        };
    }

    private static AxialOpeningEvidence ProbeAxialEnd(IModelDoc2 model, IReadOnlyList<IBody2> bodies,
        ConnectivityInspectionQuery query, Vector3 endpointMm, Vector3 outwardDirection, double apertureRadiusMm, string endId)
    {
        var outward = ModelVerification.Unit(outwardDirection);
        var inward = ModelVerification.Scale(outward, -1);
        var helper = Math.Abs(outward.Z) < .9 ? new Vector3(0, 0, 1) : new Vector3(0, 1, 0);
        var radialU = ModelVerification.Unit(Cross(outward, helper));
        var radialV = ModelVerification.Unit(Cross(outward, radialU));
        var basePoints = new List<double>(); var directions = new List<double>();
        var basesMm = new List<Vector3>();
        for (var i = 0; i < query.EndProbeCount; i++)
        {
            var radial = new Vector3(0, 0, 0);
            if (i > 0)
            {
                var angle = 2 * Math.PI * (i - 1) / Math.Max(1, query.EndProbeCount - 1);
                radial = ModelVerification.Add(ModelVerification.Scale(radialU, apertureRadiusMm * .35 * Math.Cos(angle)),
                    ModelVerification.Scale(radialV, apertureRadiusMm * .35 * Math.Sin(angle)));
            }
            var baseMm = ModelVerification.Add(ModelVerification.Add(endpointMm, ModelVerification.Scale(inward, query.ProbeInsetMm)), radial);
            basesMm.Add(baseMm);
            basePoints.AddRange([baseMm.X / 1000, baseMm.Y / 1000, baseMm.Z / 1000]);
            directions.AddRange([outward.X, outward.Y, outward.Z]);
        }
        // Explicitly request normals so the documented hit layout is always nine doubles.
        var options = (int)(swRayPtsOpts_e.swRayPtsOptsENTRY_EXIT | swRayPtsOpts_e.swRayPtsOptsNORMALS);
        var intersectionCount = model.Extension.RayIntersections(bodies.Select(body => new DispatchWrapper(body)).ToArray(), basePoints.ToArray(), directions.ToArray(),
            options, 0, query.NumericalToleranceMm / 1000, true);
        if (intersectionCount < 0) return Incomplete("SOLIDWORKS returned a negative ray-intersection count.");
        var values = intersectionCount == 0 ? [] : ToDoubles(model.GetRayIntersectionsPoints(), intersectionCount * 9, "connectivity ray intersections");
        if (values.Length != intersectionCount * 9) return Incomplete("Ray-intersection point payload size does not match the reported hit count.");

        var hits = Enumerable.Range(0, intersectionCount).Select(hitIndex =>
        {
            var offset = hitIndex * 9;
            _ = ExactIndex(values[offset], bodies.Count, "body index");
            var rayIndex = ExactIndex(values[offset + 1], query.EndProbeCount, "ray index");
            var type = (int)Math.Round(values[offset + 2]);
            var point = new Vector3(values[offset + 3] * 1000, values[offset + 4] * 1000, values[offset + 5] * 1000);
            var distance = ModelVerification.Dot(ModelVerification.Sub(point, basesMm[rayIndex]), outward);
            return (RayIndex: rayIndex, Type: type, DistanceMm: distance);
        }).Where(hit => hit.DistanceMm >= -query.NumericalToleranceMm).GroupBy(hit => hit.RayIndex)
            .ToDictionary(group => group.Key, group => group.OrderBy(hit => hit.DistanceMm).ToArray());

        var material = 0; var open = 0; var thicknesses = new List<double>(); var complete = true;
        var immediateWindow = query.ProbeInsetMm + query.Requirement.SealDetectionThresholdMm;
        var enterFlag = (int)swRayPtsResults_e.swRayPtsResultsENTER;
        var exitFlag = (int)swRayPtsResults_e.swRayPtsResultsEXIT;
        for (var rayIndex = 0; rayIndex < query.EndProbeCount; rayIndex++)
        {
            var rayHits = hits.GetValueOrDefault(rayIndex) ?? [];
            var firstEnter = rayHits.FirstOrDefault(hit => (hit.Type & enterFlag) != 0 && hit.DistanceMm <= immediateWindow + query.NumericalToleranceMm);
            if ((firstEnter.Type & enterFlag) == 0)
            {
                if (rayHits.Any(hit => hit.DistanceMm <= immediateWindow + query.NumericalToleranceMm && (hit.Type & exitFlag) != 0)) complete = false;
                else open++;
                continue;
            }
            var exit = rayHits.FirstOrDefault(hit => hit.DistanceMm > firstEnter.DistanceMm + query.NumericalToleranceMm && (hit.Type & exitFlag) != 0);
            if ((exit.Type & exitFlag) == 0) { complete = false; continue; }
            var thickness = exit.DistanceMm - firstEnter.DistanceMm;
            if (!double.IsFinite(thickness) || thickness <= query.NumericalToleranceMm) { complete = false; continue; }
            material++; thicknesses.Add(thickness);
        }
        return new()
        {
            EndId = endId,
            SampleCount = query.EndProbeCount,
            MaterialSampleCount = material,
            VoidSampleCount = open,
            Complete = complete && material + open == query.EndProbeCount,
            ProbeOffsetMm = query.ProbeInsetMm,
            MaterialThicknessBeyondMm = material == query.EndProbeCount && thicknesses.Count == material ? thicknesses.Min() : null,
            NumericalUncertaintyMm = query.NumericalToleranceMm,
            Method = "solidworks_ray_intersections_entry_exit"
        };

        AxialOpeningEvidence Incomplete(string error) => new()
        {
            EndId = endId, SampleCount = query.EndProbeCount, Complete = false, ProbeOffsetMm = query.ProbeInsetMm,
            NumericalUncertaintyMm = query.NumericalToleranceMm, Method = "solidworks_ray_intersections_entry_exit:" + error
        };
    }

    private static object ResolveCurrentCandidate(IModelDoc2 model, GeometryRefResolution resolution)
    {
        var encoded = resolution.Candidate?.NativePersistentReference;
        if (string.IsNullOrWhiteSpace(encoded)) throw new InvalidOperationException("Resolved GeometryRef candidate has no native persistent reference for B-Rep inspection.");
        int state = 0;
        var entity = model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(encoded), out state);
        if (state != 0 || entity is null) throw new InvalidOperationException("Resolved GeometryRef candidate became stale before connectivity inspection.");
        return entity;
    }

    private static ConnectivityCheck Unsupported(ConnectivityInspectionQuery query, string message) => new()
    {
        RequirementId = query.Requirement.RequirementId,
        SourceFactId = query.Requirement.SourceFactId,
        SourceRevisionId = query.Requirement.SourceRevisionId,
        SourceFactFingerprint = query.Requirement.SourceFactFingerprint,
        RequirementFingerprint = ConnectivityVerifier.Fingerprint(query.Requirement),
        ActualModelSha256 = new string('0', 64),
        ModelReopened = false,
        Kind = query.Requirement.Kind,
        Status = ConnectivityStatus.Unsupported,
        Message = message,
        Evidence = new Dictionary<string, string> { ["method"] = "solidworks_brep_connectivity" }
    };

    private static bool IsClosed(AxialOpeningEvidence evidence) => evidence.Complete && evidence.MaterialSampleCount == evidence.SampleCount &&
        evidence.SampleCount >= 3 && evidence.MaterialThicknessBeyondMm is { } thickness && thickness > evidence.NumericalUncertaintyMm;

    private static int ExactIndex(double value, int count, string name)
    {
        if (!double.IsFinite(value) || Math.Abs(value - Math.Round(value)) > 1e-9 || value < 0 || value >= count)
            throw new InvalidOperationException($"SOLIDWORKS returned invalid {name}.");
        return (int)Math.Round(value);
    }

    private static Vector3 Cross(Vector3 a, Vector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
