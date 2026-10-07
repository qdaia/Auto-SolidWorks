using System.Text.Json.Serialization;
using CadModeling.Ir;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<AssemblyMotionKind>))]
public enum AssemblyMotionKind { Translation, Rotation, Screw }

/// <summary>装配坐标系中的瞬时相对运动。螺旋导程使用 mm/rad。</summary>
public sealed record AssemblyMotion
{
    public AssemblyMotionKind Kind { get; init; }
    public required Vector3 Direction { get; init; }
    public Vector3? PointMm { get; init; }
    public double? PitchMmPerRadian { get; init; }
}

public sealed record AssemblyMobilityRequirement
{
    public required string ComponentId { get; init; }
    public required string RelativeToComponentId { get; init; }
    public required string SourceLiteral { get; init; }
    public IReadOnlyList<AssemblyMotion> ExpectedBasis { get; init; } = [];
    public Vector3 EvaluationPointMm { get; init; } = new(0, 0, 0);
    public double CharacteristicLengthMm { get; init; } = 100;
    public double SubspaceTolerance { get; init; } = 1e-6;
}

public sealed record AssemblyMobilitySpec
{
    public IReadOnlyList<AssemblyMobilityRequirement> Requirements { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<AssemblyMobilityEvidenceState>))]
public enum AssemblyMobilityEvidenceState { Unavailable, Incomplete, Complete }

/// <summary>Complete 必须是完整的相对运动基；不能由预期配合数量或未文档化返回码推断。</summary>
public sealed record AssemblyMobilityObservation
{
    public required string ComponentId { get; init; }
    public required string RelativeToComponentId { get; init; }
    public required string ComponentIdentity { get; init; }
    public required string ReferenceIdentity { get; init; }
    public required string Configuration { get; init; }
    public required string EvidenceSource { get; init; }
    public AssemblyMobilityEvidenceState State { get; init; }
    public bool InAssemblyCoordinates { get; init; }
    public bool RegularConfiguration { get; init; }
    public int? DegreesOfFreedom { get; init; }
    public IReadOnlyList<AssemblyMotion> Basis { get; init; } = [];
    public NativeConcentricTreeReadback? ConcentricTreeReadback { get; init; }
}

public sealed record AssemblyMobilityReceipt(
    string ComponentId, string RelativeToComponentId, string ComponentIdentity, string ReferenceIdentity,
    string Configuration, int DegreesOfFreedom, double SubspaceDistance,
    string EvidenceSource, IReadOnlyList<AssemblyMotion> ObservedBasis)
{
    public NativeConcentricTreeReadback? ConcentricTreeReadback { get; init; }
}

/// <summary>只读端口；不能通过临时固定组件或抑制配合改变被验收机构。</summary>
public interface IAssemblyMobilitySession
{
    string Configuration { get; }
    IReadOnlyDictionary<string, string> ComponentIdentities { get; }
    AssemblyMobilityObservation Inspect(string componentId, string relativeToComponentId);
}

public static class AssemblyMobilityContract
{
    private const double RankTolerance = 1e-8;

    public static void Validate(AssemblyPlan plan)
    {
        if (plan.Mobility is null) return;
        var requirements = plan.Mobility.Requirements;
        if (requirements is null || requirements.Count is 0 or > 256)
            throw Error("自由度合同需要 1..256 个组件需求。");
        if (requirements.Any(r => r is null) || requirements.Select(r => r.ComponentId).Distinct(StringComparer.Ordinal).Count() != requirements.Count)
            throw Error("每个被检查组件需要唯一的自由度需求。");
        var components = plan.Components.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var r in requirements)
        {
            if (string.IsNullOrWhiteSpace(r.ComponentId) || string.IsNullOrWhiteSpace(r.RelativeToComponentId)
                || r.ComponentId == r.RelativeToComponentId || !components.ContainsKey(r.ComponentId) || !components.ContainsKey(r.RelativeToComponentId))
                throw Error("相对运动需要两个不同且存在的组件 ID。");
            if (string.IsNullOrWhiteSpace(r.SourceLiteral)) throw Error("自由度需求必须保留独立的源设计意图。");
            if (!Finite(r.EvaluationPointMm) || !double.IsFinite(r.CharacteristicLengthMm)
                || r.CharacteristicLengthMm is < 1e-6 or > 1e9 || !double.IsFinite(r.SubspaceTolerance)
                || r.SubspaceTolerance is < 1e-10 or > 1e-3)
                throw Error("自由度比较点、特征长度或子空间容差无效。");
            var basis = Basis(r.ExpectedBasis, r);
            // 两个固定组件之间不能有相对运动；整体完全约束要求也不允许声明活动机构。
            if (basis.Count > 0 && (plan.RequireFullyConstrainedComponents
                || components[r.ComponentId].Fixed && components[r.RelativeToComponentId].Fixed))
                throw Error("声明的相对运动与固定状态／完全约束要求矛盾。");
        }
    }

    public static IReadOnlyList<AssemblyMobilityReceipt> Verify(AssemblyPlan plan, IAssemblyMobilitySession session)
    {
        Validate(plan);
        if (plan.Mobility is null) return [];
        if (string.IsNullOrWhiteSpace(session.Configuration)) throw Error("观测需要实际装配配置身份。");
        var configuration = session.Configuration;
        var identities = session.ComponentIdentities.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var relevant = plan.Mobility.Requirements.SelectMany(r => new[] { r.ComponentId, r.RelativeToComponentId }).Distinct().ToArray();
        foreach (var id in relevant)
            if (!identities.TryGetValue(id, out var identity) || string.IsNullOrWhiteSpace(identity))
                throw Error("组件身份观测缺失：" + id);
        if (relevant.Select(id => identities[id]).Distinct(StringComparer.Ordinal).Count() != relevant.Length)
            throw Error("不同组件实例不能共用同一持久身份。");
        var receipts = new List<AssemblyMobilityReceipt>();
        foreach (var r in plan.Mobility.Requirements)
        {
            var actual = session.Inspect(r.ComponentId, r.RelativeToComponentId);
            if (actual is null || actual.State != AssemblyMobilityEvidenceState.Complete)
                throw Error("ASSEMBLY_MOBILITY_UNVERIFIABLE：缺少完整相对自由度观测，不能把未知视为零。");
            if (actual.ComponentId != r.ComponentId || actual.RelativeToComponentId != r.RelativeToComponentId
                || actual.ComponentIdentity != identities[r.ComponentId] || actual.ReferenceIdentity != identities[r.RelativeToComponentId]
                || actual.Configuration != configuration || string.IsNullOrWhiteSpace(actual.EvidenceSource))
                throw Error("自由度观测的组件归属、配置或证据身份不一致。");
            if (!actual.InAssemblyCoordinates || !actual.RegularConfiguration)
                throw Error("自由度观测必须来自装配坐标系中的非奇异配置。");
            var expectedBasis = Basis(r.ExpectedBasis, r);
            var actualBasis = Basis(actual.Basis, r);
            if(actual.ConcentricTreeReadback is { } tree)
            {
                var certifiedTreeBasis=Basis(ConcentricTreeMobilityContract.Basis(tree,r.ComponentId,r.RelativeToComponentId),r);
                if(!tree.ComponentIds.ToHashSet(StringComparer.Ordinal).SetEquals(identities.Keys)
                    ||actual.EvidenceSource!="native_complete_grounded_concentric_tree_relative_spatial_basis"
                    ||Distance(certifiedTreeBasis,actualBasis)>r.SubspaceTolerance)
                    throw Error("原生开链库存、证据来源或重新计算的运动基不一致。");
            }
            if (actual.DegreesOfFreedom != actualBasis.Count || expectedBasis.Count != actualBasis.Count)
                throw Error("ASSEMBLY_MOBILITY_MISMATCH：完整运动基的秩／数量与设计意图不符。");
            var residual = Distance(expectedBasis, actualBasis);
            if (residual > r.SubspaceTolerance)
                throw Error("ASSEMBLY_MOBILITY_MISMATCH：平移方向、旋转轴线或螺旋耦合不符。");
            receipts.Add(new(r.ComponentId, r.RelativeToComponentId, actual.ComponentIdentity, actual.ReferenceIdentity,
                configuration, actualBasis.Count, residual, actual.EvidenceSource, actual.Basis.ToArray())
                { ConcentricTreeReadback=actual.ConcentricTreeReadback });
        }
        if (session.Configuration != configuration || relevant.Any(id => !session.ComponentIdentities.TryGetValue(id, out var value) || value != identities[id]))
            throw Error("自由度检查期间装配配置或组件身份发生改变。");
        return receipts;
    }

    public static IReadOnlyList<AssemblyMobilityReceipt> VerifySaved(AssemblyPlan plan, IAssemblyMobilitySession session,
        IReadOnlyList<AssemblyMobilityReceipt> created, Func<string,string,bool>? sameIdentity = null)
    {
        sameIdentity ??= (a,b) => a == b;
        var saved = Verify(plan, session);
        if (created.Count != saved.Count || created.Select(x => x.ComponentId).Distinct().Count() != created.Count)
            throw Error("保存前的自由度证据数量／身份不完整。");
        foreach (var r in plan.Mobility?.Requirements ?? [])
        {
            var before = created.SingleOrDefault(x => x.ComponentId == r.ComponentId);
            var after = saved.Single(x => x.ComponentId == r.ComponentId);
            if (before is null || before.RelativeToComponentId != after.RelativeToComponentId || before.Configuration != after.Configuration
                || !sameIdentity(before.ComponentIdentity,after.ComponentIdentity) || !sameIdentity(before.ReferenceIdentity,after.ReferenceIdentity)
                || before.DegreesOfFreedom != after.DegreesOfFreedom || string.IsNullOrWhiteSpace(before.EvidenceSource)
                || before.DegreesOfFreedom != Basis(before.ObservedBasis, r).Count
                || Distance(Basis(before.ObservedBasis, r), Basis(after.ObservedBasis, r)) > r.SubspaceTolerance)
                throw Error("保存重开后的组件身份、配置或自由度改变。");
        }
        return saved;
    }

    // 用旋量 [ω, v/L] 比较完整子空间，而不是逐个匹配 API 给出的任意基。
    // v = ω × (评估点 - 轴线上一点) + pitch*ω。纯平移的 ω=0。
    private static List<double[]> Basis(IReadOnlyList<AssemblyMotion> modes, AssemblyMobilityRequirement r)
    {
        if (modes is null || modes.Count > 6) throw Error("运动基最多六维且不能缺失。");
        var result = new List<double[]>();
        foreach (var mode in modes)
        {
            if (mode is null || !Enum.IsDefined(mode.Kind) || !Finite(mode.Direction)) throw Error("运动类型或方向无效。");
            var axis = new[] { mode.Direction.X, mode.Direction.Y, mode.Direction.Z };
            var n = Norm(axis);
            if (!double.IsFinite(n) || n < 1e-12) throw Error("运动方向不能是零轴线或溢出数值范围。");
            axis = axis.Select(x => x / n).ToArray();
            double[] twist;
            if (mode.Kind == AssemblyMotionKind.Translation)
            {
                if (mode.PointMm is not null || mode.PitchMmPerRadian is not null) throw Error("纯平移不能携带被忽略的旋转中心或导程。");
                twist = [0, 0, 0, ..axis];
            }
            else
            {
                if (mode.PointMm is null || !Finite(mode.PointMm)
                    || mode.Kind == AssemblyMotionKind.Rotation && mode.PitchMmPerRadian is not null
                    || mode.Kind == AssemblyMotionKind.Screw && (mode.PitchMmPerRadian is null || !double.IsFinite(mode.PitchMmPerRadian.Value) || mode.PitchMmPerRadian.Value == 0))
                    throw Error("旋转需明确轴线位置；螺旋需明确有限非零导程；纯旋转不接受导程。");
                var d = new[] { r.EvaluationPointMm.X - mode.PointMm.X, r.EvaluationPointMm.Y - mode.PointMm.Y, r.EvaluationPointMm.Z - mode.PointMm.Z };
                var p = mode.PitchMmPerRadian ?? 0;
                twist = [..axis, (axis[1]*d[2]-axis[2]*d[1]+p*axis[0])/r.CharacteristicLengthMm,
                    (axis[2]*d[0]-axis[0]*d[2]+p*axis[1])/r.CharacteristicLengthMm,
                    (axis[0]*d[1]-axis[1]*d[0]+p*axis[2])/r.CharacteristicLengthMm];
            }
            if (twist.Any(x => !double.IsFinite(x))) throw Error("运动旋量超出可验证数值范围。");
            var scale = Norm(twist);
            if (!double.IsFinite(scale) || scale == 0) throw Error("运动旋量尺度无效。");
            twist = twist.Select(x => x / scale).ToArray();
            // 两次正交化避免近相关运动产生伪造的额外自由度。
            for (var pass = 0; pass < 2; pass++)
                foreach (var q in result) SubtractProjection(twist, q);
            n = Norm(twist);
            if (!double.IsFinite(n) || n < RankTolerance) throw Error("运动基重复、相关或病态，不能按条目数计算自由度。");
            result.Add(twist.Select(x => x / n).ToArray());
        }
        return result;
    }

    private static double Distance(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        if (a.Count != b.Count) return double.PositiveInfinity;
        // 正交投影矩阵距离 / sqrt(2) = sqrt(sum(sin(principal_angle)^2))。
        // 此度量不依赖 API 选择哪组基；逐个向量的最大残差在临界容差处会依赖基。
        var sum = 0d;
        for (var i = 0; i < 6; i++)
            for (var j = 0; j < 6; j++)
            {
                var d = a.Sum(q => q[i]*q[j]) - b.Sum(q => q[i]*q[j]);
                sum += d*d;
            }
        return Math.Sqrt(sum / 2);
    }
    private static void SubtractProjection(double[] v, double[] q)
    {
        var dot = v.Zip(q, (x, y) => x * y).Sum();
        for (var i = 0; i < v.Length; i++) v[i] -= dot * q[i];
    }
    private static double Norm(double[] v)
    {
        var maximum = v.Select(Math.Abs).Max();
        return maximum == 0 ? 0 : maximum * Math.Sqrt(v.Sum(x => Math.Pow(x / maximum, 2)));
    }
    private static bool Finite(Vector3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
    private static ArgumentException Error(string message) => new("ASSEMBLY_MOBILITY_CONTRACT：" + message);
}
