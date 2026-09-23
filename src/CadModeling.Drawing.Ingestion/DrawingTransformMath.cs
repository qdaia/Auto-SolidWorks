using CadModeling.Drawing.Contracts;

namespace CadModeling.Drawing.Ingestion;

public sealed record ViewMapValidationIssue(string Code, string Message, string? ViewId = null, string? TransformId = null);

public static class DrawingTransformMath
{
    public static IReadOnlyList<ViewMapValidationIssue> Validate(DrawingViewMapDocument document, double tolerance = 1e-8)
    {
        ArgumentNullException.ThrowIfNull(document);
        var issues = new List<ViewMapValidationIssue>();
        if (document.Views.Select(item => item.ViewId).Distinct(StringComparer.Ordinal).Count() != document.Views.Count)
            issues.Add(new("VIEW_DUPLICATE", "View map IDs must be unique."));
        var frames = document.CoordinateFrames.ToDictionary(item => item.FrameId, StringComparer.Ordinal);
        var transforms = document.Transforms.ToDictionary(item => item.TransformId, StringComparer.Ordinal);
        foreach (var view in document.Views)
        {
            if (view.PageNumber < 1 || string.IsNullOrWhiteSpace(view.SourceRegionId))
                issues.Add(new("VIEW_SOURCE", "View requires a page and source region.", view.ViewId));
            if (!frames.TryGetValue(view.SourceFrameId, out var source) ||
                !frames.TryGetValue(view.ViewMillimeterFrameId, out var local) ||
                !frames.TryGetValue(view.ModelFrameId, out var model))
            {
                issues.Add(new("VIEW_FRAME", "View references a missing coordinate frame.", view.ViewId));
                continue;
            }
            if (local.Space != CoordinateSpace.ViewLocal || local.Unit != MeasurementUnit.Millimeter ||
                model.Space != CoordinateSpace.SolidworksModel || model.Unit != MeasurementUnit.Millimeter)
                issues.Add(new("VIEW_UNIT", "View-local and model frames must be explicit millimeter frames.", view.ViewId));
            if (!transforms.TryGetValue(view.SourceToViewTransformId, out var sourceToView) ||
                sourceToView.FromFrameId != source.FrameId || sourceToView.ToFrameId != local.FrameId)
                issues.Add(new("VIEW_SOURCE_TRANSFORM", "Source-to-view transform is missing or connects the wrong frames.", view.ViewId, view.SourceToViewTransformId));
            else if (!AreInverse(sourceToView.ForwardMatrix, sourceToView.InverseMatrix, tolerance))
                issues.Add(new("VIEW_SOURCE_ROUNDTRIP", "Source-to-view transform does not round-trip.", view.ViewId, sourceToView.TransformId));
            if (!transforms.TryGetValue(view.ViewToModelTransformId, out var viewToModel) ||
                viewToModel.FromFrameId != local.FrameId || viewToModel.ToFrameId != model.FrameId)
                issues.Add(new("VIEW_MODEL_TRANSFORM", "View-to-model transform is missing or connects the wrong frames.", view.ViewId, view.ViewToModelTransformId));
            else
            {
                if (!AreInverse(viewToModel.ForwardMatrix, viewToModel.InverseMatrix, tolerance))
                    issues.Add(new("VIEW_MODEL_ROUNDTRIP", "View-to-model transform does not round-trip.", view.ViewId, viewToModel.TransformId));
                if (!IsRigidMillimeterTransform(viewToModel.ForwardMatrix, 1e-7))
                    issues.Add(new("VIEW_MODEL_SCALE_OR_MIRROR", "View-to-model transform must be a rigid, orientation-preserving millimeter transform; free scale and reflection are forbidden.", view.ViewId, viewToModel.TransformId));
            }
            if (!double.IsFinite(view.PositionUncertaintyMm) || view.PositionUncertaintyMm < 0)
                issues.Add(new("VIEW_UNCERTAINTY", "Position uncertainty must be finite and non-negative.", view.ViewId));
        }
        return issues;
    }

    public static (double X, double Y) Transform2(IReadOnlyList<IReadOnlyList<double>> matrix, double x, double y)
    {
        RequireMatrix(matrix, 3);
        var w = matrix[2][0] * x + matrix[2][1] * y + matrix[2][2];
        if (!double.IsFinite(w) || Math.Abs(w) < 1e-15) throw new ArgumentException("Transform produced an invalid homogeneous coordinate.", nameof(matrix));
        return ((matrix[0][0] * x + matrix[0][1] * y + matrix[0][2]) / w,
            (matrix[1][0] * x + matrix[1][1] * y + matrix[1][2]) / w);
    }

    public static (double X, double Y, double Z) Transform3(IReadOnlyList<IReadOnlyList<double>> matrix, double x, double y, double z)
    {
        RequireMatrix(matrix, 4);
        var w = matrix[3][0] * x + matrix[3][1] * y + matrix[3][2] * z + matrix[3][3];
        if (!double.IsFinite(w) || Math.Abs(w) < 1e-15) throw new ArgumentException("Transform produced an invalid homogeneous coordinate.", nameof(matrix));
        return ((matrix[0][0] * x + matrix[0][1] * y + matrix[0][2] * z + matrix[0][3]) / w,
            (matrix[1][0] * x + matrix[1][1] * y + matrix[1][2] * z + matrix[1][3]) / w,
            (matrix[2][0] * x + matrix[2][1] * y + matrix[2][2] * z + matrix[2][3]) / w);
    }

    public static bool AreInverse(IReadOnlyList<IReadOnlyList<double>> forward, IReadOnlyList<IReadOnlyList<double>> inverse, double tolerance = 1e-8)
    {
        if (!IsFiniteSquare(forward) || !IsFiniteSquare(inverse) || forward.Count != inverse.Count) return false;
        var n = forward.Count;
        for (var r = 0; r < n; r++)
        for (var c = 0; c < n; c++)
        {
            var value = 0d;
            for (var k = 0; k < n; k++) value += forward[r][k] * inverse[k][c];
            var expected = r == c ? 1d : 0d;
            if (Math.Abs(value - expected) > tolerance) return false;
        }
        return true;
    }

    public static bool IsRigidMillimeterTransform(IReadOnlyList<IReadOnlyList<double>> matrix, double tolerance = 1e-8)
    {
        if (!IsFiniteSquare(matrix) || matrix.Count != 4) return false;
        if (Math.Abs(matrix[3][0]) > tolerance || Math.Abs(matrix[3][1]) > tolerance || Math.Abs(matrix[3][2]) > tolerance || Math.Abs(matrix[3][3] - 1) > tolerance)
            return false;
        var columns = Enumerable.Range(0, 3).Select(c => new[] { matrix[0][c], matrix[1][c], matrix[2][c] }).ToArray();
        for (var i = 0; i < 3; i++)
        {
            var norm = Math.Sqrt(columns[i].Sum(value => value * value));
            if (Math.Abs(norm - 1) > tolerance) return false;
            for (var j = 0; j < i; j++)
                if (Math.Abs(columns[i].Zip(columns[j], (a, b) => a * b).Sum()) > tolerance) return false;
        }
        return Determinant3(matrix) > 0 && Math.Abs(Determinant3(matrix) - 1) <= tolerance * 4;
    }

    public static double Determinant3(IReadOnlyList<IReadOnlyList<double>> matrix)
    {
        if (matrix.Count < 3 || matrix.Take(3).Any(row => row.Count < 3)) throw new ArgumentException("A 3x3 linear block is required.", nameof(matrix));
        var a = matrix[0][0]; var b = matrix[0][1]; var c = matrix[0][2];
        var d = matrix[1][0]; var e = matrix[1][1]; var f = matrix[1][2];
        var g = matrix[2][0]; var h = matrix[2][1]; var i = matrix[2][2];
        return a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
    }

    public static IReadOnlyList<IReadOnlyList<double>> InvertAffine3(IReadOnlyList<IReadOnlyList<double>> matrix)
    {
        RequireMatrix(matrix, 3);
        if (Math.Abs(matrix[2][0]) > 1e-12 || Math.Abs(matrix[2][1]) > 1e-12 || Math.Abs(matrix[2][2] - 1) > 1e-12)
            throw new ArgumentException("Only affine 2D transforms are supported.", nameof(matrix));
        var a = matrix[0][0]; var b = matrix[0][1]; var tx = matrix[0][2];
        var c = matrix[1][0]; var d = matrix[1][1]; var ty = matrix[1][2];
        var det = a * d - b * c;
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-15) throw new ArgumentException("Transform is singular.", nameof(matrix));
        return [new[] { d / det, -b / det, (b * ty - d * tx) / det },
            new[] { -c / det, a / det, (c * tx - a * ty) / det }, new[] { 0d, 0d, 1d }];
    }

    public static IReadOnlyList<IReadOnlyList<double>> InvertRigid4(IReadOnlyList<IReadOnlyList<double>> matrix)
    {
        if (!IsRigidMillimeterTransform(matrix, 1e-6)) throw new ArgumentException("Rigid millimeter transform required.", nameof(matrix));
        var inverse = new double[4][] { new double[4], new double[4], new double[4], new double[4] };
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 3; c++) inverse[r][c] = matrix[c][r];
        for (var r = 0; r < 3; r++) inverse[r][3] = -(inverse[r][0] * matrix[0][3] + inverse[r][1] * matrix[1][3] + inverse[r][2] * matrix[2][3]);
        inverse[3][3] = 1;
        return inverse;
    }

    private static bool IsFiniteSquare(IReadOnlyList<IReadOnlyList<double>> matrix) => matrix.Count >= 2 &&
        matrix.All(row => row.Count == matrix.Count && row.All(double.IsFinite));

    private static void RequireMatrix(IReadOnlyList<IReadOnlyList<double>> matrix, int size)
    {
        if (!IsFiniteSquare(matrix) || matrix.Count != size) throw new ArgumentException($"A finite {size}x{size} matrix is required.", nameof(matrix));
    }
}
