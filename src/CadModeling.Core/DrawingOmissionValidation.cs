using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CadModeling.Ir;

namespace CadModeling.Core;

public sealed record DrawingOmissionIssue(string Code, string Message, string? CandidateId = null,
    string? RegionId = null, int? PageNumber = null, DrawingReviewBox? Bounds = null);

public sealed record DrawingOmissionResult
{
    public bool Passed => Issues.Count == 0;
    public string Status => Passed ? "declared_review_complete" : "review_incomplete";
    public bool FullDrawingEquivalence => false;
    public string Scope => "Checks ingestion inventory accounting and declared visual review; does not prove detector recall or that an agent actually viewed every crop.";
    public int TotalCandidates { get; init; }
    public int AccountedCandidates { get; init; }
    public int RequiredRegions { get; init; }
    public int ReviewedRegions { get; init; }
    public IReadOnlyList<DrawingOmissionIssue> Issues { get; init; } = [];
}

public static partial class DrawingOmissionValidation
{
    public const long MaximumInventoryBytes = 32 * 1024 * 1024;

    public static DrawingOmissionInventory Load(string path, string? expectedHash = null)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidDataException("Use the absolute omission_inventory_path returned by cad_read_drawing.");
        if (new FileInfo(path).Length > MaximumInventoryBytes) throw new InvalidDataException("Omission inventory exceeds 32 MiB.");
        var bytes = File.ReadAllBytes(path);
        if (expectedHash is not null && !SameHash(expectedHash, Convert.ToHexString(SHA256.HashData(bytes))))
            throw new InvalidDataException("Omission inventory changed. Preserve the original inventory and reread the source if needed.");
        var inventory = JsonSerializer.Deserialize<DrawingOmissionInventory>(bytes, ModelingIrJson.Options)
            ?? throw new InvalidDataException("Empty omission inventory.");
        if (inventory.SchemaVersion != "1.0" || inventory.Producer != "auto-solidworks-omission-v1" ||
            inventory.TotalPages is < 1 or > 50 || inventory.Candidates.Count > 50000 || inventory.Regions.Count > 5000 ||
            inventory.Pages.Count != inventory.TotalPages)
            throw new InvalidDataException("Invalid or unsupported omission inventory structure.");
        if (!Unique(inventory.Pages.Select(p => p.PageNumber.ToString(CultureInfo.InvariantCulture))) ||
            inventory.Pages.Any(p => p.PageNumber < 1 || p.PageNumber > inventory.TotalPages) ||
            !Unique(inventory.Candidates.Select(c => c.Id)) || !Unique(inventory.Regions.Select(r => r.Id)))
            throw new InvalidDataException("Inventory page, candidate and region IDs must be unique and valid.");
        var regions = inventory.Regions.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var candidateIds = inventory.Candidates.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (inventory.Candidates.Any(c => !Enum.IsDefined(c.Kind) || !ValidBox(c.Bounds) || c.PageNumber < 1 || c.PageNumber > inventory.TotalPages ||
            c.NumericValue is { } n && !double.IsFinite(n) || c.Multiplicity is < 1 ||
            c.SecondaryValue is { } s && !double.IsFinite(s) ||
            c.ReviewRegionIds.Count == 0 || c.ReviewRegionIds.Any(id => !regions.TryGetValue(id, out var r) || r.PageNumber != c.PageNumber) ||
            c.RelatedCandidateIds.Any(id => !candidateIds.Contains(id))))
            throw new InvalidDataException("Invalid candidate geometry, page, quantities or references.");
        if (inventory.Regions.Any(r => !ValidBox(r.Bounds) || r.PageNumber < 1 || r.PageNumber > inventory.TotalPages ||
            r.PixelWidth < 1 || r.PixelHeight < 1 || r.InkPixels < 0 || r.ResidualInkPixels < 0 || r.ResidualInkPixels > r.InkPixels))
            throw new InvalidDataException("Invalid review region.");
        if (!Path.IsPathFullyQualified(inventory.SourcePath) || !File.Exists(inventory.SourcePath) ||
            !SameHash(inventory.SourceSha256, DrawingPlanValidation.FileHash(inventory.SourcePath)))
            throw new InvalidDataException("Source drawing changed or is unavailable.");
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var page in inventory.Pages.Where(p => p.Ingested))
        {
            CheckArtifact(root, page.ObservationPath, page.ObservationSha256);
            if (!inventory.Regions.Any(r => r.PageNumber == page.PageNumber && r.Purpose == "overview"))
                throw new InvalidDataException("An ingested page has no full-page review region.");
        }
        foreach (var region in inventory.Regions) CheckArtifact(root, region.ImagePath, region.ImageSha256);
        return inventory;
    }

    public static DrawingOmissionResult Evaluate(DrawingPlanContext context)
    {
        try
        {
            if (context.OmissionReview is not { } review)
                return Failed("DRAWING_OMISSION_MISSING", "Drawing plans require omission_review from cad_read_drawing. Read the source and account for candidates and review regions before compiling.");
            if (!IsHash(review.InventorySha256)) return Failed("DRAWING_OMISSION_INTEGRITY", "InventorySha256 must be the hash returned by ingestion.");
            var inventory = Load(review.InventoryPath, review.InventorySha256);
            if (!Path.IsPathFullyQualified(context.SourcePath) || !string.Equals(Path.GetFullPath(context.SourcePath),
                Path.GetFullPath(inventory.SourcePath), StringComparison.OrdinalIgnoreCase))
                return Failed("DRAWING_OMISSION_SOURCE", "The review inventory belongs to another source path.");
            return EvaluateLoaded(context, inventory, review);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException or InvalidOperationException or FormatException or OverflowException)
        {
            return Failed("DRAWING_OMISSION_INTEGRITY", "Cannot verify omission evidence: " + ex.Message);
        }
    }

    private static DrawingOmissionResult EvaluateLoaded(DrawingPlanContext context, DrawingOmissionInventory inventory, DrawingOmissionReview review)
    {
        if(review.Candidates.Any(c=>c.CandidateIds.Count>0 && !string.IsNullOrEmpty(c.CandidateId)))
            return Failed("DRAWING_OMISSION_IDS","Supply exactly one of candidate_id or an explicit candidate_ids batch.");
        var expanded=review.Candidates.SelectMany(c=>c.CandidateIds.Count==0?new[]{c}:c.CandidateIds.Select(id=>c with{CandidateId=id,CandidateIds=[]})).ToArray();
        review=review with{Candidates=expanded};
        var issues = new List<DrawingOmissionIssue>();
        void Issue(string code, string text, DrawingOmissionCandidate? c = null) =>
            issues.Add(new(code, text, c?.Id, null, c?.PageNumber, c?.Bounds));
        if (!inventory.CompleteExtraction) Issue("DRAWING_OMISSION_EXTRACTION", "Extraction was incomplete or capped. Resolve the ingestion limitations before claiming coverage.");
        foreach (var page in inventory.Pages.Where(p => !p.Ingested))
            issues.Add(new("DRAWING_OMISSION_PAGE", "Source page was not ingested. Read all source pages; no unexamined page is silently excluded.", PageNumber: page.PageNumber));
        if (!Unique(review.Candidates.Select(c => c.CandidateId)) || !Unique(review.Regions.Select(r => r.RegionId)) ||
            !Unique(review.CrossViewChecks.Select(c => c.Id)) || !Unique(review.AdditionalFindings.Select(f => f.Id)) ||
            !Unique(context.Features.Select(f => f.Id)) || !Unique(context.Dimensions.Select(d => d.Id)) || !Unique(context.Views.Select(v => v.Id)))
            return Failed("DRAWING_OMISSION_IDS", "Review, feature, dimension and view IDs must be nonempty and unique.");
        var candidates = inventory.Candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var decisions = review.Candidates.ToDictionary(c => c.CandidateId, StringComparer.Ordinal);
        var features = context.Features.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var dimensions = context.Dimensions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var views = context.Views.ToDictionary(v => v.Id, StringComparer.Ordinal);
        if(context.Views.Any(v=>v.PageNumber<1||v.PageNumber>inventory.TotalPages))
            Issue("DRAWING_OMISSION_VIEW","A declared source view is outside the source page range.");
        var regionReviews = review.Regions.ToDictionary(r => r.RegionId, StringComparer.Ordinal);
        foreach (var id in decisions.Keys.Where(id => !candidates.ContainsKey(id))) Issue("DRAWING_OMISSION_UNKNOWN_ID", "Review names a candidate absent from the immutable inventory: " + id);
        foreach (var r in review.Regions.Where(r => !inventory.Regions.Any(i => i.Id == r.RegionId)))
            issues.Add(new("DRAWING_OMISSION_UNKNOWN_ID", "Review names a region absent from the inventory.", RegionId: r.RegionId));
        var accounted = 0;
        foreach (var candidate in inventory.Candidates)
        {
            var before = issues.Count;
            if (!decisions.TryGetValue(candidate.Id, out var decision) || decision.Disposition == DrawingCandidateDisposition.Unresolved)
            { Issue("DRAWING_OMISSION_UNACCOUNTED", "Candidate has no resolved explanation. Inspect its original crop.", candidate); continue; }
            if (!Enum.IsDefined(decision.Disposition) || string.IsNullOrWhiteSpace(decision.Rationale))
                Issue("DRAWING_OMISSION_EXPLANATION", "Every disposition requires an explicit source-based rationale.", candidate);
            if (decision.CorrectedLiteral is not null && (string.IsNullOrWhiteSpace(decision.CorrectedLiteral) || string.IsNullOrWhiteSpace(decision.CorrectionReason)))
                Issue("DRAWING_OMISSION_CORRECTION", "An OCR correction needs the actual source literal and a reason; keep the original candidate intact.", candidate);
            if (candidate.Conflict && (decision.CorrectedLiteral is null || string.IsNullOrWhiteSpace(decision.CorrectionReason)))
                Issue("DRAWING_OMISSION_CONFLICT", "Conflicting detections require a recorded source-image reading and explanation.", candidate);
            if (decision.FeatureIds.Any(id => !features.ContainsKey(id)) || decision.DimensionIds.Any(id => !dimensions.ContainsKey(id)))
                Issue("DRAWING_OMISSION_TARGET", "Candidate refers to a feature or dimension missing from the plan.", candidate);
            switch (decision.Disposition)
            {
                case DrawingCandidateDisposition.Feature:
                    if (decision.FeatureIds.Count == 0) Issue("DRAWING_OMISSION_TARGET", "Feature disposition needs target feature IDs.", candidate);
                    foreach (var featureId in decision.FeatureIds.Where(features.ContainsKey))
                        if (!features[featureId].ViewIds.Any(id => views.TryGetValue(id, out var v) && v.PageNumber == candidate.PageNumber))
                            Issue("DRAWING_OMISSION_VIEW", "The mapped feature has no supporting view on this candidate's page.", candidate);
                    foreach (var dimId in decision.DimensionIds.Where(dimensions.ContainsKey))
                    {
                        var fact = dimensions[dimId];
                        if (!decision.FeatureIds.Any(id => features.TryGetValue(id, out var f) && f.OperationIds.Contains(fact.OperationId, StringComparer.Ordinal)))
                            Issue("DRAWING_OMISSION_BINDING", "Candidate dimension does not belong to its target feature's operations.", candidate);
                        if (!fact.ObservationIds.Any(candidate.ObservationIds.Contains))
                            Issue("DRAWING_OMISSION_BINDING", "Mapped dimensions must cite the candidate's source observation IDs.", candidate);
                    }
                    CheckQuantities(candidate, decision, dimensions, Issue);
                    break;
                case DrawingCandidateDisposition.NonModel:
                    if (decision.FeatureIds.Count > 0 || decision.DimensionIds.Count > 0 || decision.DuplicateOf is not null ||
                        decision.NonModelCategory is not ("border" or "title_block" or "dimension_graphic" or "centerline" or "hatch" or "note" or "noise"))
                        Issue("DRAWING_OMISSION_NONMODEL", "NonModel requires one supported classification and no modeling/duplicate targets.", candidate);
                    if (candidate.DimensionKind is not null && decision.NonModelCategory is not ("title_block" or "note" or "noise"))
                        Issue("DRAWING_OMISSION_NONMODEL", "A numeric annotation cannot be dismissed as a line, hatch or border.", candidate);
                    break;
                case DrawingCandidateDisposition.Duplicate:
                    if (decision.FeatureIds.Count > 0 || decision.DimensionIds.Count > 0 || decision.DuplicateOf is null || decision.DuplicateOf == candidate.Id ||
                        !candidates.TryGetValue(decision.DuplicateOf, out var original) || !decisions.TryGetValue(decision.DuplicateOf, out var originalDecision) ||
                        originalDecision.Disposition is not (DrawingCandidateDisposition.Feature or DrawingCandidateDisposition.NonModel))
                        Issue("DRAWING_OMISSION_DUPLICATE", "Duplicate must point directly to another accounted candidate; chains and cycles are rejected.", candidate);
                    else if (candidate.PageNumber != original.PageNumber || candidate.Kind != original.Kind ||
                        Overlap(candidate.Bounds, original.Bounds) < 0.2 ||
                        candidate.Kind == DrawingCandidateKind.Annotation && Normalize(decision.CorrectedLiteral ?? candidate.Literal) != Normalize(originalDecision.CorrectedLiteral ?? original.Literal) &&
                        !(original.Provider=="spatial-annotation-assembler"&&candidate.ObservationIds.Count>0&&candidate.ObservationIds.All(original.ObservationIds.Contains)&&original.RelatedCandidateIds.Contains(candidate.Id)))
                        Issue("DRAWING_OMISSION_DUPLICATE", "Duplicate candidates must overlap on the same page and agree in kind and resolved text.", candidate);
                    break;
            }
            if (issues.Count == before) accounted++;
        }
        var reviewed = 0;
        foreach (var region in inventory.Regions)
        {
            if (!regionReviews.TryGetValue(region.Id, out var inspected) || inspected.State != DrawingReviewState.Reviewed || string.IsNullOrWhiteSpace(inspected.Findings))
                issues.Add(new("DRAWING_OMISSION_REGION", "Inspect the raw full-page/detail image and record findings, including unrecognized ink.", RegionId: region.Id, PageNumber: region.PageNumber, Bounds: region.Bounds));
            else if (inspected.FeatureIds.Any(id => !features.ContainsKey(id)))
                issues.Add(new("DRAWING_OMISSION_TARGET", "Region review found a feature absent from the plan.", RegionId: region.Id, PageNumber: region.PageNumber, Bounds: region.Bounds));
            else reviewed++;
        }
        foreach (var check in review.CrossViewChecks)
        {
            if (check.State != DrawingReviewState.Reviewed || string.IsNullOrWhiteSpace(check.Evidence) || check.ViewIds.Distinct().Count() < 2 ||
                check.ViewIds.Any(id => !views.ContainsKey(id)) || check.FeatureIds.Count == 0 || check.FeatureIds.Any(id => !features.ContainsKey(id)))
                Issue("DRAWING_OMISSION_CROSS_VIEW", "Unresolved, conflicting or invalid cross-view check: " + check.Id);
            else foreach (var fid in check.FeatureIds)
                if (!check.ViewIds.All(features[fid].ViewIds.Contains))
                    Issue("DRAWING_OMISSION_CROSS_VIEW", "Cross-view evidence includes a view not declared by its feature: " + fid);
        }
        foreach (var feature in context.Features)
        {
            if (!review.Candidates.Any(c => c.Disposition == DrawingCandidateDisposition.Feature && c.FeatureIds.Contains(feature.Id)) &&
                !review.AdditionalFindings.Any(f => f.State == DrawingReviewState.Reviewed && f.FeatureIds.Contains(feature.Id)))
                Issue("DRAWING_OMISSION_FEATURE_EVIDENCE", "Feature has no candidate or localized additional visual finding: " + feature.Id);
            if (feature.ViewIds.Distinct().Count() > 1 && !review.CrossViewChecks.Any(c => c.State == DrawingReviewState.Reviewed &&
                c.FeatureIds.Contains(feature.Id) && feature.ViewIds.All(c.ViewIds.Contains)))
                Issue("DRAWING_OMISSION_CROSS_VIEW", "Feature needs an explicit consistency review across all its declared source views: " + feature.Id);
        }
        foreach (var finding in review.AdditionalFindings)
            if (finding.State != DrawingReviewState.Reviewed || string.IsNullOrWhiteSpace(finding.Description) || !ValidBox(finding.Bounds) ||
                finding.PageNumber < 1 || finding.PageNumber > inventory.TotalPages || finding.FeatureIds.Count == 0 || finding.FeatureIds.Any(id => !features.ContainsKey(id)))
                issues.Add(new("DRAWING_OMISSION_FINDING", "Additional source finding remains unresolved or is missing from the model: " + finding.Id,
                    PageNumber: finding.PageNumber, Bounds: finding.Bounds));
        return new() { TotalCandidates = inventory.Candidates.Count, AccountedCandidates = accounted, RequiredRegions = inventory.Regions.Count,
            ReviewedRegions = reviewed, Issues = issues };
    }

    private static void CheckQuantities(DrawingOmissionCandidate c, DrawingCandidateReview decision,
        IReadOnlyDictionary<string, DrawingDimensionFact> dimensions, Action<string, string, DrawingOmissionCandidate?> issue)
    {
        var kind = c.DimensionKind; var value = c.NumericValue; var multiplicity = c.Multiplicity; var secondary=c.SecondaryValue;
        if (decision.CorrectedLiteral is { } literal)
        {
            var match = Quantity().Match(literal.Trim().Replace('×', 'x').Replace('X', 'x'));
            if (match.Success)
            {
                value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
                multiplicity = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture) : 1;
                secondary=match.Groups["secondary"].Success?double.Parse(match.Groups["secondary"].Value,CultureInfo.InvariantCulture):null;
                kind = match.Groups["angle"].Success ? secondary.HasValue?"chamfer":"angle" : "linear";
            }
            else if (c.NumericValue is not null)
            { issue("DRAWING_OMISSION_CORRECTION", "A corrected quantitative feature annotation must remain a parseable quantity; unresolved text must not be dropped.", c); return; }
        }
        if (value is null) return;
        var facts = decision.DimensionIds.Where(dimensions.ContainsKey).Select(id => dimensions[id]).ToArray();
        bool Equal(double a, double b) => Math.Abs(a - b) <= 1e-7 * Math.Max(1, Math.Abs(b));
        // Candidate numbers are in the source's stated unit. Compare literal values, never infer physical sizes from pixels.
        var plainCount=kind=="linear"&&Regex.IsMatch(decision.CorrectedLiteral??c.Literal,@"^\s*\d+\s*$",RegexOptions.CultureInvariant);
        if (!facts.Any(f => Equal(f.Value, value.Value) && (f.Unit != DrawingValueUnit.Unitless||plainCount) &&
            ((kind is "angle") == (f.Unit == DrawingValueUnit.Degree))))
            issue("DRAWING_OMISSION_QUANTITY", "Annotation value has no matching source dimension binding; inspect its literal, unit and targets.", c);
        if (multiplicity is > 1 && !facts.Any(f => f.Unit == DrawingValueUnit.Unitless && Equal(f.Value, multiplicity.Value)))
            issue("DRAWING_OMISSION_COUNT", "Repeated-feature annotation requires an explicit Unitless count dimension bound to the model and its verification.", c);
        if(secondary is { } sv && !facts.Any(f=>Equal(f.Value,sv) && (kind=="chamfer"?f.Unit==DrawingValueUnit.Degree:f.Unit!=DrawingValueUnit.Degree&&f.Unit!=DrawingValueUnit.Unitless)))
            issue("DRAWING_OMISSION_QUANTITY", "Compound annotation's secondary value (pitch, angle or size) has no matching dimension binding.", c);
    }

    public static IEnumerable<ModelingDiagnostic> Diagnostics(DrawingPlanContext context)
    {
        if (context.OmissionReview is null && !context.RequireCompleteBindings)
        {
            yield return new("DRAWING_OMISSION_PARTIAL", DiagnosticSeverity.Warning,
                "Explicitly partial drawing contract: omission review is absent; no completeness claim is supported.", "drawing_context.omission_review");
            yield break;
        }
        var result = Evaluate(context);
        foreach (var issue in result.Issues.Take(100))
            yield return new(issue.Code, DiagnosticSeverity.Error, issue.Message,
                "drawing_context.omission_review." + (issue.CandidateId ?? issue.RegionId ?? "inventory"));
        if (result.Issues.Count > 100)
            yield return new("DRAWING_OMISSION_MORE", DiagnosticSeverity.Error,
                $"{result.Issues.Count - 100} additional issues. Use cad_review_drawing_coverage for paginated candidates and findings.", "drawing_context.omission_review");
    }

    private static void CheckArtifact(string root, string? path, string? hash)
    {
        if (path is null || !Path.IsPathFullyQualified(path) || !Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !IsHash(hash) || !File.Exists(path) || !SameHash(hash!, DrawingPlanValidation.FileHash(path)))
            throw new InvalidDataException("A source observation or review image is missing, changed or outside the ingestion directory.");
    }
    private static DrawingOmissionResult Failed(string code, string message) => new() { Issues = [new(code, message)] };
    private static bool Unique(IEnumerable<string> ids) { var a = ids.ToArray(); return a.All(id => !string.IsNullOrWhiteSpace(id)) && a.Distinct(StringComparer.Ordinal).Count() == a.Length; }
    public static bool ValidBox(DrawingReviewBox b) => b is not null && double.IsFinite(b.Left + b.Top + b.Right + b.Bottom) && b.Left >= 0 && b.Top >= 0 && b.Right <= 1 && b.Bottom <= 1 && b.Right > b.Left && b.Bottom > b.Top;
    private static bool IsHash(string? h) => h is { Length: 64 } && h.All(Uri.IsHexDigit);
    private static bool SameHash(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c))).Replace('Ø', '⌀').Replace('Φ', '⌀').Replace('φ', '⌀').ToUpperInvariant();
    private static double Overlap(DrawingReviewBox a, DrawingReviewBox b)
    {
        var intersection = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return intersection / Math.Max(1e-12, Math.Min((a.Right-a.Left)*(a.Bottom-a.Top), (b.Right-b.Left)*(b.Bottom-b.Top)));
    }
    [GeneratedRegex(@"^(?:(?<count>\d+)\s*[-x]\s*(?=[Ø⌀φΦ∅RM]))?[Ø⌀φΦ∅RM]?\s*(?<value>\d+(?:\.\d+)?)(?:\s*x\s*(?<secondary>\d+(?:\.\d+)?))?\s*(?<angle>[°º])?(?:mm|倒圆角|圆角|倒角)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Quantity();
}
