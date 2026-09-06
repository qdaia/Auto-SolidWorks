using System.Globalization;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Providers.Abstractions;

namespace CadModeling.Drawing.Ingestion;

public sealed class ObservationFusionService : IObservationFusionService
{
    public ObservationFusionResult Fuse(int pageNumber, IReadOnlyList<ProviderObservationBatch> batches)
    {
        var regions = batches.SelectMany(batch => batch.SourceRegions).GroupBy(region => region.RegionId, StringComparer.Ordinal)
            .Select(group => group.First()).OrderBy(region => region.RegionId, StringComparer.Ordinal).ToArray();
        var observations = batches.SelectMany(batch => batch.Observations)
            .OrderBy(observation => observation.ObservationId, StringComparer.Ordinal).ToList();
        var diagnostics = batches.SelectMany(batch => batch.Diagnostics).ToList();

        var nativeText = observations.Where(IsNativeText).ToArray();
        var rasterText = observations.Where(IsRasterText).ToArray();
        foreach (var native in nativeText)
        foreach (var raster in rasterText)
        {
            if (!TryBox(native, out var nativeBox) || !TryBox(raster, out var rasterBox) || IntersectionOverUnion(nativeBox, rasterBox) < 0.2) continue;
            if (string.IsNullOrWhiteSpace(native.RawLiteral) || string.IsNullOrWhiteSpace(raster.RawLiteral)) continue;
            if (Normalize(native.RawLiteral) == Normalize(raster.RawLiteral))
            {
                diagnostics.Add(new() { Code = "ING-FUSION-CORROBORATED", Severity = ContractDiagnosticSeverity.Info,
                    Message = $"Native and raster text observations corroborate without deduplication: {native.ObservationId}, {raster.ObservationId}.", PageNumber = pageNumber });
                continue;
            }
            var conflictId = IngestionUtilities.StableId("conflict", native.ObservationId, raster.ObservationId);
            Replace(observations, native with { EvidenceStatus = EvidenceStatus.Conflict, CandidateProperties = Add(native.CandidateProperties, "conflict_group", conflictId) });
            Replace(observations, raster with { EvidenceStatus = EvidenceStatus.Conflict, CandidateProperties = Add(raster.CandidateProperties, "conflict_group", conflictId) });
            diagnostics.Add(new() { Code = "ING-FUSION-TEXT-CONFLICT", Severity = ContractDiagnosticSeverity.Warning,
                Message = $"Overlapping native and raster text differ and were both retained in conflict group {conflictId}.", PageNumber = pageNumber });
        }

        var views = batches.SelectMany(batch => batch.ViewRegions).GroupBy(view => view.ViewRegionId, StringComparer.Ordinal)
            .Select(group => {
                var view=group.First(); var region=regions.First(r=>r.RegionId==view.SourceRegionId);
                var x0=region.Polygon.Min(p=>p.X);var x1=region.Polygon.Max(p=>p.X);var y0=region.Polygon.Min(p=>p.Y);var y1=region.Polygon.Max(p=>p.Y);
                var ids=observations.Where(o=>o.Geometry.Count>0 && o.Geometry[0].CoordinateFrameId==region.Polygon[0].CoordinateFrameId &&
                    o.Geometry.Average(p=>p.X)>=x0 && o.Geometry.Average(p=>p.X)<=x1 && o.Geometry.Average(p=>p.Y)>=y0 && o.Geometry.Average(p=>p.Y)<=y1).Select(o=>o.ObservationId).ToArray();
                return view with { ObservationIds=ids };
            }).OrderBy(view => view.ViewRegionId, StringComparer.Ordinal).ToArray();
        return new()
        {
            SourceRegions = regions,
            ViewRegions = views,
            Observations = observations.OrderBy(item => item.ObservationId, StringComparer.Ordinal).ToArray(),
            Diagnostics = diagnostics
        };
    }

    private static bool IsNativeText(ObservationEntity item) => item.Kind == ObservationKind.Text &&
        item.CandidateProperties.TryGetValue("modality", out var modality) && modality == "native_pdf";
    private static bool IsRasterText(ObservationEntity item) => item.Kind == ObservationKind.Text &&
        item.CandidateProperties.TryGetValue("modality", out var modality) && modality != "native_pdf";
    private static string Normalize(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
    private static void Replace(List<ObservationEntity> items, ObservationEntity replacement)
    {
        var index = items.FindIndex(item => item.ObservationId == replacement.ObservationId);
        if (index >= 0) items[index] = replacement;
    }
    private static IReadOnlyDictionary<string, string> Add(IReadOnlyDictionary<string, string> source, string key, string value)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in source) result[item.Key] = item.Value;
        result[key] = value;
        return result;
    }
    private static bool TryBox(ObservationEntity item, out double[] box)
    {
        box = [];
        if (!item.CandidateProperties.TryGetValue("fusion_bbox", out var text)) return false;
        var values = text.Split(',');
        if (values.Length != 4) return false;
        var parsed = new double[4];
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i])) return false;
        box = parsed;
        return true;
    }
    private static double IntersectionOverUnion(double[] a, double[] b)
    {
        var left = Math.Max(a[0], b[0]); var top = Math.Max(a[1], b[1]);
        var right = Math.Min(a[2], b[2]); var bottom = Math.Min(a[3], b[3]);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var areaA = Math.Max(0, a[2] - a[0]) * Math.Max(0, a[3] - a[1]);
        var areaB = Math.Max(0, b[2] - b[0]) * Math.Max(0, b[3] - b[1]);
        var union = areaA + areaB - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}
