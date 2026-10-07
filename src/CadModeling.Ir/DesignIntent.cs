using System.Text.Json.Serialization;

namespace CadModeling.Ir;

[JsonConverter(typeof(JsonStringEnumConverter<DesignExpressionKind>))]
public enum DesignExpressionKind { Literal, Variable, Add, Subtract, Multiply, Divide, Dimension, Function, Negate }

[JsonConverter(typeof(JsonStringEnumConverter<DesignFunction>))]
public enum DesignFunction { Abs, Sqrt, Sin, Cos, Tan, Asin, Acos, Atan, Exp, Log, Int, Sign, Min, Max, Power }

public sealed record DesignValue(double Value, DrawingValueUnit Unit);

/// <summary>Bounded typed arithmetic, declared variable/dimension references and fixed functions; no arbitrary native code.</summary>
public sealed record DesignExpression
{
    public required DesignExpressionKind Kind { get; init; }
    public DesignValue? Literal { get; init; }
    public string? Variable { get; init; }
    public string? DimensionName { get; init; }
    public DesignFunction? Function { get; init; }
    public IReadOnlyList<DesignExpression> Arguments { get; init; } = [];
    public DesignExpression? Left { get; init; }
    public DesignExpression? Right { get; init; }
}

public sealed record DesignGlobalVariable
{
    public required string Name { get; init; }
    public required DesignExpression Expression { get; init; }
    public bool ReplaceExisting { get; init; }
}

public sealed record DesignDimensionEquation
{
    public required string DimensionName { get; init; }
    public required DesignExpression Expression { get; init; }
    public required DesignValue ExpectedValue { get; init; }
    public bool ReplaceExisting { get; init; }
}

public sealed record DesignDimensionValue
{
    public required string DimensionName { get; init; }
    public required DesignValue Value { get; init; }
}

public sealed record DesignConfigurationVariable
{
    public required string Name { get; init; }
    public required DesignExpression Expression { get; init; }
    public required DesignValue ExpectedValue { get; init; }
}

public sealed record DesignFeatureSuppression
{
    public required string FeatureName { get; init; }
    public required bool Suppressed { get; init; }
}

public sealed record DesignConfiguration
{
    public required string Name { get; init; }
    public bool ReuseExisting { get; init; }
    // Creation copies this explicitly selected existing configuration.
    public string? CreateFromConfiguration { get; init; }
    public IReadOnlyList<DesignDimensionValue> Dimensions { get; init; } = [];
    public IReadOnlyList<DesignConfigurationVariable> GlobalVariables { get; init; } = [];
    public IReadOnlyList<DesignDimensionValue> EquationValues { get; init; } = [];
    public IReadOnlyList<DesignDimensionValue> DimensionInputValues { get; init; } = [];
    /// <summary>Ordered exact feature states; implicit changes to undeclared features are rejected.</summary>
    public IReadOnlyList<DesignFeatureSuppression> FeatureSuppression { get; init; } = [];
    public GeometryQualitySpec Geometry { get; init; } = new();
}

/// <summary>
/// Applied after geometry creation. Base globals and equations apply to all configurations;
/// variable expressions and literal dimension overrides may be scoped to a named configuration.
/// Input values and equation expected values are independent assertions, never mutations.
/// </summary>
public sealed record DesignIntentSpec
{
    public required string ActiveConfiguration { get; init; }
    public IReadOnlyList<DesignGlobalVariable> GlobalVariables { get; init; } = [];
    public IReadOnlyList<DesignDimensionEquation> Equations { get; init; } = [];
    /// <summary>Independent source values for referenced dimensions not controlled by an equation.</summary>
    public IReadOnlyList<DesignDimensionValue> DimensionInputs { get; init; } = [];
    public required IReadOnlyList<DesignConfiguration> Configurations { get; init; }
    public IReadOnlyList<string> RequireFullyDefinedSketches { get; init; } = [];
}
