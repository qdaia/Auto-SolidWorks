using System.Security.Cryptography;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private sealed record CapturedSectionLoop(IReadOnlyList<string> PrimitiveIds, double AreaMm2,
        ProjectionPointMm ProbePoint, Func<ProjectionPointMm, bool> Contains);

    public async Task<SectionCaptureResult> CaptureSectionAsync(SectionCaptureRequest request, CancellationToken cancellationToken = default)
    {
        await _serialGate.WaitAsync(cancellationToken);
        try { return await _sta.InvokeAsync(() => CaptureSectionOnSta(request), cancellationToken); }
        finally { _serialGate.Release(); }
    }

    private static SectionCaptureResult CaptureSectionOnSta(SectionCaptureRequest request)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("native.section");
        SldWorks? app = null;
        IModelDoc2? model = null;
        var ownedModel = false;
        var stage = "validation";
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            SectionVerifier.Validate(request.Spec);
            if (request.Spec.Type != SectionType.FullPlane)
                return new(false, "validation: only a single complete planar section is supported.");
            if (!Path.IsPathFullyQualified(request.NativePath) || !File.Exists(request.NativePath) ||
                !Path.GetExtension(request.NativePath).Equals(".sldprt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Section capture requires an existing absolute .SLDPRT path.");
            if (!double.IsFinite(request.EndpointToleranceMm) || request.EndpointToleranceMm <= 0 ||
                !double.IsFinite(request.SheetMarginMm) || request.SheetMarginMm <= 0)
                throw new ArgumentException("Section capture tolerances/margins must be finite and positive.");

            var modelHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.NativePath)));
            app = (SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application", true)!)!;
            int errors = 0, warnings = 0;
            model = (IModelDoc2?)app.GetOpenDocumentByName(request.NativePath);
            if (model is null)
            {
                model = (IModelDoc2?)PerformanceTrace.Measure("native.open", () => app.OpenDoc6(request.NativePath, (int)swDocumentTypes_e.swDocPART,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings));
                ownedModel = model is not null && Path.GetFullPath(model.GetPathName()).Equals(Path.GetFullPath(request.NativePath), StringComparison.OrdinalIgnoreCase);
            }
            if (model is null || model is not IPartDoc part ||
                !Path.GetFullPath(model.GetPathName()).Equals(Path.GetFullPath(request.NativePath), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Could not reopen exact saved native part for section capture (errors={errors}).");
            if (model.GetSaveFlag()) throw new InvalidOperationException("Section capture refuses unsaved in-memory model state.");

            stage = "freeze section frame";
            var normal = ModelVerification.Unit(request.Spec.PlaneNormal);
            var xAxis = ModelVerification.Unit(request.Spec.InPlaneXDirection);
            var yAxis = ModelVerification.Unit(SectionCross(normal, xAxis));
            if (Math.Abs(ModelVerification.Dot(normal, xAxis)) > 1e-6 || ModelVerification.Norm(yAxis) <= 1e-12)
                throw new InvalidOperationException("Section frame is not a valid right-handed in-plane basis.");
            var originMm = request.Spec.PlaneOriginMm;
            var bodies = (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? []).OfType<IBody2>().ToArray();
            if (bodies.Length == 0) throw new InvalidOperationException("Section capture found no solid bodies.");
            var (uMin, uMax, vMin, vMax) = SectionBounds(bodies, originMm, xAxis, yAxis, request.SheetMarginMm);
            var modeler = (IModeler)app.GetModeler();
            var primitives = new List<ProjectionPrimitive>();
            var limitations = new List<string>();
            var primitiveIndex = 0;

            stage = "intersect actual B-Rep with fixed plane";
            foreach (var body in bodies)
            {
                IBody2? target = null;
                IBody2? sheet = null;
                ISurface? plane = null;
                try
                {
                    target = (IBody2?)body.Copy2(true) ?? throw new IOException("Could not copy solid body for non-mutating section capture.");
                    plane = (ISurface?)modeler.CreatePlanarSurface2(
                        new[] { originMm.X / 1000, originMm.Y / 1000, originMm.Z / 1000 },
                        new[] { normal.X, normal.Y, normal.Z }, new[] { xAxis.X, xAxis.Y, xAxis.Z })
                        ?? throw new IOException("Could not create temporary section plane.");
                    sheet = (IBody2?)modeler.CreateSheetFromSurface(plane, new[] { uMin / 1000, uMax / 1000, vMin / 1000, vMax / 1000 })
                        ?? throw new IOException("Could not create bounded temporary section sheet.");
                    var raw = target.GetIntersectionEdges2(sheet, false) as object[] ?? [];
                    if (raw.Length == 0) continue;
                    if (raw.Length % 2 != 0)
                    {
                        limitations.Add($"Intersection edge payload for body '{body.Name}' is not target/tool paired.");
                        continue;
                    }
                    for (var i = 0; i < raw.Length; i += 2)
                    {
                        if (raw[i] is not IEdge edge)
                        {
                            limitations.Add($"Intersection record {i / 2} for body '{body.Name}' has no target-body edge.");
                            continue;
                        }
                        var primitive = SectionPrimitive(edge, originMm, normal, xAxis, yAxis, ref primitiveIndex, limitations);
                        if (primitive is not null) AddSectionPrimitive(primitives, primitive, request.EndpointToleranceMm);
                    }
                }
                finally
                {
                    ReleaseCom(sheet);
                    ReleaseCom(plane);
                    ReleaseCom(target);
                }
            }
            if (primitives.Count == 0) limitations.Add("The fixed section plane produced no supported line/circle boundary primitives.");

            stage = "assemble material and void loops";
            var capturedLoops = BuildSectionLoops(primitives, request.EndpointToleranceMm, limitations);
            var loops = new List<SectionLoop>();
            for (var i = 0; i < capturedLoops.Count; i++)
            {
                var loop = capturedLoops[i];
                var depth = capturedLoops.Where((other, index) => index != i && other.AreaMm2 > loop.AreaMm2 + request.EndpointToleranceMm &&
                    other.Contains(loop.ProbePoint)).Count();
                loops.Add(new()
                {
                    LoopId = $"model-section-loop-{i + 1:D4}",
                    IsVoid = depth % 2 == 1,
                    PrimitiveIds = loop.PrimitiveIds,
                    AreaMm2 = loop.AreaMm2
                });
            }
            if (loops.Count == 0) limitations.Add("No closed supported section loops could be assembled from the actual intersection edges.");
            if (modelHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.NativePath))))
                throw new IOException("Native model changed while the section was captured.");
            var complete = limitations.Count == 0;
            var snapshot = new SectionSnapshot
            {
                SectionId = request.Spec.SectionId,
                SourceSha256 = request.Spec.SourceSha256,
                ViewId = request.Spec.ViewId,
                CoordinateFrameId = request.Spec.CoordinateFrameId,
                Type = SectionType.FullPlane,
                PlaneOriginMm = originMm,
                PlaneNormal = normal,
                ViewingDirection = ModelVerification.Unit(request.Spec.ViewingDirection),
                InPlaneXDirection = xAxis,
                NativeModelSha256 = modelHash,
                ModelReopened = ownedModel,
                CaptureComplete = complete,
                CaptureLimitations = limitations,
                BoundaryPrimitives = primitives,
                Loops = loops
            };
            return new(true, complete
                ? $"Captured {primitives.Count} section boundaries and {loops.Count} closed loops from the reopened actual B-Rep."
                : $"Section B-Rep intersection completed with {limitations.Count} fail-closed limitation(s).")
            {
                Complete = complete,
                Limitations = limitations,
                Snapshot = snapshot
            };
        }
        catch (Exception ex)
        {
            return new(false, stage + ": " + ex.Message);
        }
        finally
        {
            try { if (ownedModel && model is not null && app is not null) PerformanceTrace.Measure("native.close", () => app.CloseDoc(model.GetTitle())); } catch (System.Runtime.InteropServices.COMException) { }
            ReleaseCom(app);
        }
    }

    private static ProjectionPrimitive? SectionPrimitive(IEdge edge, Vector3 originMm, Vector3 normal, Vector3 xAxis, Vector3 yAxis,
        ref int index, List<string> limitations)
    {
        var curve = (ICurve?)edge.GetCurve();
        if (curve is null) { limitations.Add("A section intersection edge exposed no underlying curve."); return null; }
        ProjectionPointMm Project(IReadOnlyList<double> point) => SectionProject(new(point[0] * 1000, point[1] * 1000, point[2] * 1000), originMm, xAxis, yAxis);
        if (curve.IsLine())
        {
            if ((IVertex?)edge.GetStartVertex() is not { } a || (IVertex?)edge.GetEndVertex() is not { } b ||
                a.GetPoint() is not double[] pa || b.GetPoint() is not double[] pb)
            { limitations.Add("A section line edge has incomplete endpoints."); return null; }
            var start = Project(pa); var end = Project(pb);
            if (Distance(start, end) <= 1e-9) { limitations.Add("A section line edge collapsed in the fixed section frame."); return null; }
            return new() { Id = $"section-line-{++index:D4}", Kind = ProjectionPrimitiveKind.Line, Start = start, End = end, LineStyle = "visible" };
        }
        if (curve.IsCircle())
        {
            var p = ToDoubles(curve.CircleParams, 7, "section circle parameters");
            var circleNormal = ModelVerification.Unit(new Vector3(p[3], p[4], p[5]));
            if (Math.Abs(ModelVerification.Dot(circleNormal, normal)) < Math.Cos(.25 * Math.PI / 180))
            { limitations.Add("A section circular edge is not coplanar with the frozen section plane."); return null; }
            var center = Project(p);
            var parameters = (ICurveParamData)edge.GetCurveParams3();
            var startRaw = parameters.StartPoint as double[]; var endRaw = parameters.EndPoint as double[];
            if (startRaw is not { Length: >= 3 } || endRaw is not { Length: >= 3 })
            { limitations.Add("A section circular edge has incomplete trimmed endpoints."); return null; }
            var start = Project(startRaw); var end = Project(endRaw); var radius = p[6] * 1000;
            if (!double.IsFinite(radius) || radius <= 0) { limitations.Add("A section circular edge has invalid radius."); return null; }
            if (Distance(start, end) <= 1e-6)
                return new() { Id = $"section-circle-{++index:D4}", Kind = ProjectionPrimitiveKind.Circle, Center = center, RadiusMm = radius, LineStyle = "visible" };
            var delta = parameters.UMaxValue - parameters.UMinValue;
            if (!double.IsFinite(delta) || Math.Abs(delta) <= 1e-12 || Math.Abs(delta) >= Math.PI * 2 - 1e-7)
            { limitations.Add("A section arc has invalid parameter extent."); return null; }
            return new()
            {
                Id = $"section-arc-{++index:D4}", Kind = ProjectionPrimitiveKind.Arc, Center = center, Start = start, End = end,
                RadiusMm = radius, SweepDegrees = delta * 180 / Math.PI * (ModelVerification.Dot(circleNormal, normal) >= 0 ? 1 : -1), LineStyle = "visible"
            };
        }
        limitations.Add("A section intersection contains a spline/ellipse/free curve outside the first T10 production scope.");
        return null;
    }

    private static IReadOnlyList<CapturedSectionLoop> BuildSectionLoops(IReadOnlyList<ProjectionPrimitive> primitives, double tolerance,
        List<string> limitations)
    {
        var loops = new List<CapturedSectionLoop>();
        foreach (var circle in primitives.Where(item => item.Kind == ProjectionPrimitiveKind.Circle))
        {
            var probe = new ProjectionPointMm(circle.Center.X + circle.RadiusMm * .75, circle.Center.Y);
            loops.Add(new([circle.Id], Math.PI * circle.RadiusMm * circle.RadiusMm, probe,
                point => Distance(point, circle.Center) < circle.RadiusMm - tolerance));
        }
        if (primitives.Any(item => item.Kind == ProjectionPrimitiveKind.Arc))
            limitations.Add("Section loop assembly currently does not certify loops containing trimmed circular arcs; arc boundaries remain captured but the section is incomplete.");

        var remaining = primitives.Where(item => item.Kind == ProjectionPrimitiveKind.Line).ToList();
        while (remaining.Count > 0)
        {
            var first = remaining[0]; remaining.RemoveAt(0);
            var ids = new List<string> { first.Id };
            var points = new List<ProjectionPointMm> { first.Start, first.End };
            var failed = false;
            while (Distance(points[^1], points[0]) > tolerance)
            {
                var tail = points[^1];
                var matches = remaining.Select((line, index) => (line, index, ds: Distance(tail, line.Start), de: Distance(tail, line.End)))
                    .Where(item => Math.Min(item.ds, item.de) <= tolerance).ToArray();
                if (matches.Length != 1) { failed = true; break; }
                var next = matches[0]; remaining.RemoveAt(next.index); ids.Add(next.line.Id);
                points.Add(next.ds <= next.de ? next.line.End : next.line.Start);
            }
            if (failed || points.Count < 4)
            {
                limitations.Add("Section line intersections cannot be assembled into a unique closed loop.");
                continue;
            }
            points.RemoveAt(points.Count - 1);
            var doubleArea = 0d;
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i]; var q = points[(i + 1) % points.Count]; doubleArea += p.X * q.Y - q.X * p.Y;
            }
            if (!double.IsFinite(doubleArea) || Math.Abs(doubleArea) <= 1e-12)
            { limitations.Add("A section line loop has degenerate area."); continue; }
            var area = Math.Abs(doubleArea) / 2;
            var centroid = SectionPolygonCentroid(points, doubleArea / 2);
            var probe = new ProjectionPointMm(points[0].X * .99 + centroid.X * .01, points[0].Y * .99 + centroid.Y * .01);
            loops.Add(new(ids, area, probe, point => SectionPointInPolygon(point, points)));
        }
        return loops;
    }

    private static (double UMin, double UMax, double VMin, double VMax) SectionBounds(IReadOnlyList<IBody2> bodies,
        Vector3 originMm, Vector3 xAxis, Vector3 yAxis, double marginMm)
    {
        var uMin = double.PositiveInfinity; var uMax = double.NegativeInfinity;
        var vMin = double.PositiveInfinity; var vMax = double.NegativeInfinity;
        foreach (var body in bodies)
        {
            var box = ToDoubles(body.GetBodyBox(), 6, "section body box");
            foreach (var x in new[] { box[0], box[3] }) foreach (var y in new[] { box[1], box[4] }) foreach (var z in new[] { box[2], box[5] })
            {
                var delta = ModelVerification.Sub(new Vector3(x * 1000, y * 1000, z * 1000), originMm);
                var u = ModelVerification.Dot(delta, xAxis); var v = ModelVerification.Dot(delta, yAxis);
                uMin = Math.Min(uMin, u); uMax = Math.Max(uMax, u); vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v);
            }
        }
        if (!new[] { uMin, uMax, vMin, vMax }.All(double.IsFinite)) throw new InvalidOperationException("Section body bounds are invalid.");
        return (uMin - marginMm, uMax + marginMm, vMin - marginMm, vMax + marginMm);
    }

    private static ProjectionPointMm SectionProject(Vector3 pointMm, Vector3 originMm, Vector3 xAxis, Vector3 yAxis)
    {
        var delta = ModelVerification.Sub(pointMm, originMm);
        return new(ModelVerification.Dot(delta, xAxis), ModelVerification.Dot(delta, yAxis));
    }

    private static Vector3 SectionCross(Vector3 a, Vector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static ProjectionPointMm SectionPolygonCentroid(IReadOnlyList<ProjectionPointMm> points, double signedArea)
    {
        var x = 0d; var y = 0d;
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i]; var q = points[(i + 1) % points.Count]; var cross = p.X * q.Y - q.X * p.Y;
            x += (p.X + q.X) * cross; y += (p.Y + q.Y) * cross;
        }
        return new(x / (6 * signedArea), y / (6 * signedArea));
    }

    private static bool SectionPointInPolygon(ProjectionPointMm point, IReadOnlyList<ProjectionPointMm> polygon)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static void AddSectionPrimitive(List<ProjectionPrimitive> primitives, ProjectionPrimitive candidate, double tolerance)
    {
        bool Same(ProjectionPrimitive a, ProjectionPrimitive b) => a.Kind == b.Kind && a.Kind switch
        {
            ProjectionPrimitiveKind.Line => (Distance(a.Start, b.Start) <= tolerance && Distance(a.End, b.End) <= tolerance) ||
                (Distance(a.Start, b.End) <= tolerance && Distance(a.End, b.Start) <= tolerance),
            ProjectionPrimitiveKind.Circle => Distance(a.Center, b.Center) <= tolerance && Math.Abs(a.RadiusMm - b.RadiusMm) <= tolerance,
            ProjectionPrimitiveKind.Arc => Distance(a.Center, b.Center) <= tolerance && Math.Abs(a.RadiusMm - b.RadiusMm) <= tolerance &&
                ((Distance(a.Start, b.Start) <= tolerance && Distance(a.End, b.End) <= tolerance) ||
                 (Distance(a.Start, b.End) <= tolerance && Distance(a.End, b.Start) <= tolerance)),
            _ => false
        };
        if (!primitives.Any(existing => Same(existing, candidate))) primitives.Add(candidate);
    }
}
