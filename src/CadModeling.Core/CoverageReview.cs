using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ReviewEvidenceKind>))]
public enum ReviewEvidenceKind
{
    SourceFact,
    Binding,
    Measurement,
    Connectivity,
    Projection,
    Section,
    Observation
}

[JsonConverter(typeof(JsonStringEnumConverter<CoverageConclusion>))]
public enum CoverageConclusion { Passed, Failed, Unverifiable, Unsupported }

public sealed record RequiredFact
{
    public required string FactId { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string SourceFactFingerprint { get; init; }
    public required string RequirementFingerprint { get; init; }
    public IReadOnlyList<ReviewEvidenceKind> RequiredEvidenceKinds { get; init; } = [];
    public bool AllowObservationOnly { get; init; }
    public string Description { get; init; } = string.Empty;
}

public sealed record CoverageEvidence
{
    public required string EvidenceId { get; init; }
    public required string FactId { get; init; }
    public required string SourceSha256 { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string SourceFactFingerprint { get; init; }
    public required string RequirementFingerprint { get; init; }
    public string? ModelSha256 { get; init; }
    public bool ModelReopened { get; init; }
    public ReviewEvidenceKind Kind { get; init; }
    public RequirementCheckStatus Status { get; init; }
    public bool Deterministic { get; init; } = true;
    public bool ScopeBlocking { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record ReviewPacket
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string SourceSha256 { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string CandidateModelSha256 { get; init; }
    public string? CandidatePlanFingerprint { get; init; }
    public string ReviewerConfiguration { get; init; } = "source_first_deterministic_v1";
    public IReadOnlyList<RequiredFact> RequiredFacts { get; init; } = [];
    public ObservationBudget ObservationBudget { get; init; } = new();
}

public sealed record CoverageItem
{
    public required string FactId { get; init; }
    public RequirementCheckStatus Status { get; init; }
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
    public IReadOnlyList<ReviewEvidenceKind> MissingEvidenceKinds { get; init; } = [];
    public string Reason { get; init; } = string.Empty;
}

public sealed record CoverageReport
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string SourceSha256 { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string CandidateModelSha256 { get; init; }
    public string? CandidatePlanFingerprint { get; init; }
    public required string RequiredScopeFingerprint { get; init; }
    public IReadOnlyList<string> RequiredFactIds { get; init; } = [];
    public IReadOnlyList<CoverageItem> Items { get; init; } = [];
    public IReadOnlyList<string> BlockingEvidenceIds { get; init; } = [];
    public CoverageConclusion Conclusion { get; init; }
    public string ConclusionReason { get; init; } = string.Empty;
    public bool FullPass => Conclusion == CoverageConclusion.Passed;
}

public static class CoverageReviewer
{
    public static CoverageReport Review(ReviewPacket packet, IReadOnlyList<CoverageEvidence> evidence)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("review.coverage");
        Validate(packet);
        ArgumentNullException.ThrowIfNull(evidence);
        CoverageEvidence[] blockers = [];
        if (packet.RequiredFacts.Count == 0)
            return Report([], CoverageConclusion.Unverifiable,
                "源特征库存为空；空的范围永远无法产生完整的接受。");

        ValidateEvidence(evidence);
        blockers = evidence.Where(item => item.ScopeBlocking).ToArray();
        var items = packet.RequiredFacts.Select(required => Evaluate(required, packet, evidence)).ToArray();
        var blockerFailed = blockers.Any(item => item.Status == RequirementCheckStatus.Failed);
        var blockerUnsupported = blockers.Any(item => item.Status == RequirementCheckStatus.Unsupported);
        var blockerUnverifiable = blockers.Any(item =>
            !item.SourceSha256.Equals(packet.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !item.SourceRevisionId.Equals(packet.SourceRevisionId, StringComparison.Ordinal) ||
            item.ModelSha256 is { Length: > 0 } model && !model.Equals(packet.CandidateModelSha256, StringComparison.OrdinalIgnoreCase) ||
            item.Status is RequirementCheckStatus.Stale or RequirementCheckStatus.Unverifiable or RequirementCheckStatus.Ambiguous or
                           RequirementCheckStatus.Missing or RequirementCheckStatus.WrongDocument);
        var conclusion = blockerFailed || items.Any(item => item.Status == RequirementCheckStatus.Failed)
            ? CoverageConclusion.Failed
            : blockerUnsupported || items.Any(item => item.Status == RequirementCheckStatus.Unsupported)
                ? CoverageConclusion.Unsupported
                : blockerUnverifiable || items.Any(item => item.Status != RequirementCheckStatus.Passed)
                    ? CoverageConclusion.Unverifiable
                    : CoverageConclusion.Passed;
        var reason = conclusion switch
        {
            CoverageConclusion.Passed => "每个必需的源事实在其声明的有效范围内都有当前确定性的证据。",
            CoverageConclusion.Failed => "至少一个必需的源事实具有确定性的矛盾证据。",
            CoverageConclusion.Unsupported => "至少一个必需的源事实需要一个明确不支持的能力。",
            _ => "至少缺少一个必需的源事实，过时、模糊、不完整，或者无法验证。"
        };
        return Report(items, conclusion, reason);

        CoverageReport Report(IReadOnlyList<CoverageItem> reportItems, CoverageConclusion status, string why) => new()
        {
            SourceSha256 = packet.SourceSha256,
            SourceRevisionId = packet.SourceRevisionId,
            CandidateModelSha256 = packet.CandidateModelSha256,
            CandidatePlanFingerprint = packet.CandidatePlanFingerprint,
            RequiredScopeFingerprint = RequiredScopeFingerprint(packet),
            RequiredFactIds = packet.RequiredFacts.Select(item => item.FactId).ToArray(),
            Items = reportItems,
            BlockingEvidenceIds = blockers.Select(item => item.EvidenceId).ToArray(),
            Conclusion = status,
            ConclusionReason = why
        };
    }

    private static CoverageItem Evaluate(RequiredFact required, ReviewPacket packet, IReadOnlyList<CoverageEvidence> allEvidence)
    {
        var relevant = allEvidence.Where(item => !item.ScopeBlocking && item.FactId.Equals(required.FactId, StringComparison.Ordinal)).ToArray();
        var ids = relevant.Select(item => item.EvidenceId).Distinct(StringComparer.Ordinal).ToArray();
        if (relevant.Length == 0)
            return Item(RequirementCheckStatus.Unverifiable, "需要的源事实没有证据支持。", required.RequiredEvidenceKinds);

        if (!required.SourceRevisionId.Equals(packet.SourceRevisionId, StringComparison.Ordinal))
            return Item(RequirementCheckStatus.Stale, "要求的事实属于审查包来自不同的源修订版本。");
        if (relevant.Any(item => !item.SourceSha256.Equals(packet.SourceSha256, StringComparison.OrdinalIgnoreCase)))
            return Item(RequirementCheckStatus.Stale, "至少一个证据项属于不同的源图纸 SHA-256。");
        if (relevant.Any(item => !item.SourceRevisionId.Equals(packet.SourceRevisionId, StringComparison.Ordinal)))
            return Item(RequirementCheckStatus.Stale, "至少一个证据项属于较旧/不同的源修订版本。");
        if (relevant.Any(item => !item.SourceFactFingerprint.Equals(required.SourceFactFingerprint, StringComparison.OrdinalIgnoreCase)))
            return Item(RequirementCheckStatus.Stale, "至少一个证据项属于不同的源事实指纹。");
        var requiredEvidence = relevant.Where(item => required.RequiredEvidenceKinds.Contains(item.Kind)).ToArray();
        if (requiredEvidence.Any(item => !item.RequirementFingerprint.Equals(required.RequirementFingerprint, StringComparison.OrdinalIgnoreCase)))
            return Item(RequirementCheckStatus.Stale, "至少一个证据项属于不同的已锁定需求指纹。");
        if (relevant.Any(item => item.ModelSha256 is { Length: > 0 } model &&
                                 !model.Equals(packet.CandidateModelSha256, StringComparison.OrdinalIgnoreCase)))
            return Item(RequirementCheckStatus.Stale, "至少一个基于模型的证据项属于不同的模型 SHA-256。");

        var missingKinds = required.RequiredEvidenceKinds.Distinct().Where(kind => !requiredEvidence.Any(item => item.Kind == kind)).ToArray();
        if (missingKinds.Length > 0)
            return Item(RequirementCheckStatus.Unverifiable, "所需的证据能力/视图未执行。", missingKinds);

        var modelKinds = new HashSet<ReviewEvidenceKind>
        {
            ReviewEvidenceKind.Measurement, ReviewEvidenceKind.Connectivity, ReviewEvidenceKind.Projection, ReviewEvidenceKind.Section
        };
        if (!required.RequiredEvidenceKinds.Any(modelKinds.Contains))
            return Item(RequirementCheckStatus.Unverifiable,
                "源/绑定/观察证据可以确立出处或提出疑问，但自身不能单独证明候选项模型的合规性。");
        var modelEvidence = relevant.Where(item => modelKinds.Contains(item.Kind)).ToArray();
        if (modelEvidence.Length == 0 || modelEvidence.Any(item => !item.ModelReopened))
            return Item(RequirementCheckStatus.Unverifiable,
                "候选模型的合规性需要来自保存并重新打开的确切模型的证据。");

        var deterministic = relevant.Where(item => item.Deterministic && item.Kind != ReviewEvidenceKind.Observation).ToArray();
        if (deterministic.Any(item => item.Status == RequirementCheckStatus.Failed))
            return Item(RequirementCheckStatus.Failed, "确定性证据违背了来源要求；视觉/审核证据无法取代它。");
        if (deterministic.Any(item => item.Status == RequirementCheckStatus.Unsupported))
            return Item(RequirementCheckStatus.Unsupported, "一个必需的确定性能力不被支持。");
        if (deterministic.Any(item => item.Status is RequirementCheckStatus.Stale or RequirementCheckStatus.Unverifiable or
                                               RequirementCheckStatus.Ambiguous or RequirementCheckStatus.Missing or RequirementCheckStatus.WrongDocument))
            return Item(RequirementCheckStatus.Unverifiable, "确定性证据不足以认证此源要求，且不够当前和完整。");
        if (relevant.Any(item => item.Status == RequirementCheckStatus.Failed))
            return Item(RequirementCheckStatus.Failed, "证据反驳了来源要求。");
        if (relevant.Any(item => item.Status == RequirementCheckStatus.Unsupported))
            return Item(RequirementCheckStatus.Unsupported, "证据报告了一个未支持的必需能力。");
        if (relevant.Any(item => item.Status != RequirementCheckStatus.Passed))
            return Item(RequirementCheckStatus.Unverifiable, "至少一个必需的证据项无法验证/未通过。");
        if (!required.AllowObservationOnly && deterministic.Length == 0)
            return Item(RequirementCheckStatus.Unverifiable, "仅凭视觉观察可以提出疑问，但无法验证确定性的源要求。");
        return Item(RequirementCheckStatus.Passed, "当前证据覆盖了此所需的来源事实。");

        CoverageItem Item(RequirementCheckStatus status, string reason, IReadOnlyList<ReviewEvidenceKind>? missing = null) => new()
        {
            FactId = required.FactId,
            Status = status,
            EvidenceIds = ids,
            MissingEvidenceKinds = missing ?? [],
            Reason = reason
        };
    }

    public static CoverageEvidence FromMeasurement(RequirementCheck check, string evidenceId)
    {
        ArgumentNullException.ThrowIfNull(check);
        check.Evidence.TryGetValue("actual_model_sha256", out var modelSha);
        var reopened = check.Evidence.TryGetValue("model_reopened", out var reopenedText) && bool.TryParse(reopenedText, out var reopenedValue) && reopenedValue;
        return new()
        {
            EvidenceId = evidenceId,
            FactId = check.SourceFactId,
            SourceSha256 = check.SourceSha256 ?? string.Empty,
            SourceRevisionId = check.SourceRevisionId,
            SourceFactFingerprint = check.SourceFactFingerprint ?? string.Empty,
            RequirementFingerprint = check.RequirementFingerprint,
            ModelSha256 = modelSha,
            ModelReopened = reopened,
            Kind = ReviewEvidenceKind.Measurement,
            Status = check.Status,
            Message = check.Message
        };
    }

    public static CoverageEvidence FromConnectivity(ConnectivityCheck check, string evidenceId) => new()
    {
        EvidenceId = evidenceId,
        FactId = check.SourceFactId,
        SourceSha256 = check.SourceSha256 ?? string.Empty,
        SourceRevisionId = check.SourceRevisionId,
        SourceFactFingerprint = check.SourceFactFingerprint,
        RequirementFingerprint = check.RequirementFingerprint,
        ModelSha256 = check.ActualModelSha256,
        ModelReopened = check.ModelReopened,
        Kind = ReviewEvidenceKind.Connectivity,
        Status = check.Status switch
        {
            ConnectivityStatus.Passed => RequirementCheckStatus.Passed,
            ConnectivityStatus.Failed => RequirementCheckStatus.Failed,
            ConnectivityStatus.Unsupported => RequirementCheckStatus.Unsupported,
            _ => RequirementCheckStatus.Unverifiable
        },
        Message = check.Message
    };

    public static IReadOnlyList<CoverageEvidence> FromProjection(ProjectionReport report, string evidencePrefix)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(report.SourceRevisionId) || report.Requirements.Any(result =>
                string.IsNullOrWhiteSpace(result.SourceFactId) || !Hash(result.SourceFactFingerprint ?? string.Empty) ||
                !Hash(result.RequirementFingerprint ?? string.Empty)))
            return [ProjectionIdentityBlocker(report, evidencePrefix,
                "投影报告缺少归属于使用者的来源修订/事实/要求身份标识；调用者重新标识被禁止。")];
        var sourceRevisionId = report.SourceRevisionId;
        var mapped = report.Requirements.Select(result => new CoverageEvidence
        {
            EvidenceId = $"{evidencePrefix}:{result.RequirementId}",
            FactId = result.SourceFactId!,
            SourceSha256 = report.SourceSha256,
            SourceRevisionId = sourceRevisionId,
            SourceFactFingerprint = result.SourceFactFingerprint!,
            RequirementFingerprint = result.RequirementFingerprint!,
            ModelSha256 = report.ActualModelSha256,
            ModelReopened = report.ModelReopened,
            Kind = ReviewEvidenceKind.Projection,
            Status = result.Status switch
            {
                ProjectionRequirementStatus.Passed => RequirementCheckStatus.Passed,
                ProjectionRequirementStatus.Failed => RequirementCheckStatus.Failed,
                _ => RequirementCheckStatus.Unverifiable
            },
            Message = string.Join(",", result.DifferenceIds)
        }).ToList();
        if (!report.Passed)
        {
            var incomplete = report.CaptureLimitations.Count > 0 || report.CheckedRequiredPrimitiveCount < report.RequiredPrimitiveCount;
            mapped.Add(new()
            {
                EvidenceId = $"{evidencePrefix}:scope",
                FactId = "*",
                SourceSha256 = report.SourceSha256,
                SourceRevisionId = sourceRevisionId,
                SourceFactFingerprint = SourceFactIdentity(report.SourceSha256, sourceRevisionId, "*"),
                RequirementFingerprint = ProjectionScopeIdentity(report, sourceRevisionId),
                ModelSha256 = report.ActualModelSha256,
                ModelReopened = report.ModelReopened,
                Kind = ReviewEvidenceKind.Projection,
                Status = incomplete ? RequirementCheckStatus.Unverifiable : RequirementCheckStatus.Failed,
                ScopeBlocking = true,
                Message = report.Differences.Count == 0 ? "投影报告未通过。" : string.Join("; ", report.Differences.Select(item => item.Message))
            });
        }
        return mapped;
    }

    /// <summary>Compatibility overload: labels are accepted only as assertions matching identities already frozen by the producer.</summary>
    public static IReadOnlyList<CoverageEvidence> FromProjection(ProjectionReport report, string sourceRevisionId,
        IReadOnlyDictionary<string, string> requirementToFactId, string evidencePrefix)
    {
        ArgumentNullException.ThrowIfNull(report);ArgumentNullException.ThrowIfNull(requirementToFactId);
        if (!string.Equals(report.SourceRevisionId, sourceRevisionId, StringComparison.Ordinal) ||
            report.Requirements.Any(result => !requirementToFactId.TryGetValue(result.RequirementId, out var factId) ||
                                              !string.Equals(factId, result.SourceFactId, StringComparison.Ordinal)))
            throw new ArgumentException("投影适配器标签必须完全匹配生产者自有源修订和事实身份；重新标签是被禁止的。");
        return FromProjection(report, evidencePrefix);
    }

    private static CoverageEvidence ProjectionIdentityBlocker(ProjectionReport report, string evidencePrefix, string message)
    {
        var revision = string.IsNullOrWhiteSpace(report.SourceRevisionId) ? "missing-source-revision" : report.SourceRevisionId;
        return new()
        {
            EvidenceId = $"{evidencePrefix}:identity",
            FactId = "*",
            SourceSha256 = report.SourceSha256,
            SourceRevisionId = revision,
            SourceFactFingerprint = SourceFactIdentity(report.SourceSha256, revision, "*"),
            RequirementFingerprint = ProjectionScopeIdentity(report, revision),
            ModelSha256 = report.ActualModelSha256,
            ModelReopened = report.ModelReopened,
            Kind = ReviewEvidenceKind.Projection,
            Status = RequirementCheckStatus.Unverifiable,
            ScopeBlocking = true,
            Message = message
        };
    }

    public static CoverageEvidence FromSection(SectionReport report, string evidenceId) => new()
    {
        EvidenceId = evidenceId,
        FactId = report.SourceFactId,
        SourceSha256 = report.SourceSha256,
        SourceRevisionId = report.SourceRevisionId,
        SourceFactFingerprint = SourceFactIdentity(report.SourceSha256, report.SourceRevisionId, report.SourceFactId),
        RequirementFingerprint = report.SectionSpecFingerprint,
        ModelSha256 = report.ActualModelSha256,
        ModelReopened = report.ModelReopened,
        Kind = ReviewEvidenceKind.Section,
        Status = report.Status switch
        {
            SectionStatus.Passed => RequirementCheckStatus.Passed,
            SectionStatus.Failed => RequirementCheckStatus.Failed,
            SectionStatus.Unsupported => RequirementCheckStatus.Unsupported,
            _ => RequirementCheckStatus.Unverifiable
        },
        Message = string.Join("; ", report.Differences.Select(item => item.Message))
    };

    public static CoverageEvidence FromObservation(ObservationResult result, string sourceFactId, string evidenceId) => new()
    {
        EvidenceId = evidenceId,
        FactId = sourceFactId,
        SourceSha256 = result.SourceSha256,
        SourceRevisionId = result.SourceRevisionId,
        SourceFactFingerprint = SourceFactIdentity(result.SourceSha256, result.SourceRevisionId, sourceFactId),
        RequirementFingerprint = result.RequestFingerprint,
        ModelSha256 = result.ModelSha256,
        Kind = ReviewEvidenceKind.Observation,
        Deterministic = false,
        Status = result.Status switch
        {
            ObservationStatus.Completed or ObservationStatus.Reused => RequirementCheckStatus.Passed,
            ObservationStatus.Unsupported => RequirementCheckStatus.Unsupported,
            _ => RequirementCheckStatus.Unverifiable
        },
        Message = result.StopReason
    };

    public static void Validate(ReviewPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Contract != ReviewPacket.ContractVersion || !Hash(packet.SourceSha256) || !Hash(packet.CandidateModelSha256) ||
            string.IsNullOrWhiteSpace(packet.SourceRevisionId) || string.IsNullOrWhiteSpace(packet.ReviewerConfiguration) ||
            packet.CandidatePlanFingerprint is { Length: > 0 } planFingerprint && !Hash(planFingerprint))
            throw new ArgumentException("ReviewPacket 需要以源文件/模型身份版本化的源文件/模型作为基础。", nameof(packet));
        if (packet.RequiredFacts.Any(item => string.IsNullOrWhiteSpace(item.FactId) || string.IsNullOrWhiteSpace(item.SourceRevisionId) ||
                                             !Hash(item.SourceFactFingerprint) || !Hash(item.RequirementFingerprint) ||
                                             !item.SourceRevisionId.Equals(packet.SourceRevisionId, StringComparison.Ordinal)) ||
            packet.RequiredFacts.Select(item => item.FactId).Distinct(StringComparer.Ordinal).Count() != packet.RequiredFacts.Count)
            throw new ArgumentException("需要有唯一的标识符以及冻结的来源事实/需求指纹，这些指纹应在包修订版本中。", nameof(packet));
        if (packet.RequiredFacts.Any(item => item.RequiredEvidenceKinds.Count == 0 ||
                                             item.RequiredEvidenceKinds.Distinct().Count() != item.RequiredEvidenceKinds.Count))
            throw new ArgumentException("每个必需的事实必须声明至少一种非重复的证据类型；仅存在来源不足以使模型被接受。", nameof(packet));
        if (packet.ObservationBudget.MaxAttempts < 1 || packet.ObservationBudget.MaxUniqueRequests < 1 || packet.ObservationBudget.MaxRepeatsPerIdentity < 1)
            throw new ArgumentException("审查观测预算必须为正数。", nameof(packet));
    }

    private static void ValidateEvidence(IReadOnlyList<CoverageEvidence> evidence)
    {
        if (evidence.Any(item => string.IsNullOrWhiteSpace(item.EvidenceId) || string.IsNullOrWhiteSpace(item.FactId) ||
                                 !Hash(item.SourceSha256) || string.IsNullOrWhiteSpace(item.SourceRevisionId) ||
                                 !Hash(item.SourceFactFingerprint) || !Hash(item.RequirementFingerprint) ||
                                 item.ModelSha256 is { Length: > 0 } model && !Hash(model) ||
                                 item.Kind is ReviewEvidenceKind.Measurement or ReviewEvidenceKind.Connectivity or ReviewEvidenceKind.Projection or ReviewEvidenceKind.Section or ReviewEvidenceKind.Observation &&
                                 !Hash(item.ModelSha256 ?? string.Empty)) ||
            evidence.Any(item => item.ScopeBlocking && item.FactId != "*") ||
            evidence.Select(item => item.EvidenceId).Distinct(StringComparer.Ordinal).Count() != evidence.Count)
            throw new ArgumentException("覆盖证据的ID、源身份和可选的模型哈希必须有效且唯一。", nameof(evidence));
    }

    public static string SourceFactIdentity(string sourceSha256, string sourceRevisionId, string factId)
    {
        if (!Hash(sourceSha256) || string.IsNullOrWhiteSpace(sourceRevisionId) || string.IsNullOrWhiteSpace(factId))
            throw new ArgumentException("源事实标识需要源 SHA、修订版和事实 ID。");
        return Digest("source-fact", sourceSha256.ToUpperInvariant(), sourceRevisionId, factId);
    }

    public static string RequiredScopeFingerprint(ReviewPacket packet)
    {
        Validate(packet);
        var facts = packet.RequiredFacts.OrderBy(item => item.FactId, StringComparer.Ordinal).Select(item => string.Join(",",
            item.FactId, item.SourceRevisionId, item.SourceFactFingerprint.ToUpperInvariant(), item.RequirementFingerprint.ToUpperInvariant(),
            string.Join("+", item.RequiredEvidenceKinds.OrderBy(kind => kind.ToString(), StringComparer.Ordinal)), item.AllowObservationOnly.ToString().ToLowerInvariant()));
        return Digest("coverage-scope-v1", packet.SourceSha256.ToUpperInvariant(), packet.SourceRevisionId, string.Join(";", facts));
    }

    public static string ProjectionRequirementIdentity(ProjectionReport report, string sourceRevisionId, string requirementId)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!string.Equals(report.SourceRevisionId, sourceRevisionId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(requirementId))
            throw new ArgumentException("投影要求的标识必须使用生产者所有源修订版和要求ID。");
        var result = report.Requirements.SingleOrDefault(item => item.RequirementId.Equals(requirementId, StringComparison.Ordinal));
        if (result is null || !Hash(result.RequirementFingerprint ?? string.Empty))
            throw new ArgumentException("投影报告不携带冻结所有者拥有要求指纹。");
        return result.RequirementFingerprint!;
    }

    private static string ProjectionScopeIdentity(ProjectionReport report, string sourceRevisionId) =>
        Digest("projection-scope", report.Contract, report.SourceSha256.ToUpperInvariant(), sourceRevisionId,
            report.SourceViewId, report.CoordinateFrameId, report.RequiredPrimitiveCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static string Digest(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", values))));
    private static bool Hash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
