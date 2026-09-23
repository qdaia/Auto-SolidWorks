using CadModeling.Drawing.Contracts;
using CadModeling.Ir;

namespace CadModeling.Drawing.Ingestion;

/// <summary>
/// Compatibility adapter from the versioned view/source contracts into the existing typed-plan context.
/// Conflicted/unknown view mappings are never silently converted into executable plan metadata.
/// </summary>
public static class DrawingPlanContextAdapter
{
    public static DrawingPlanContext ApplyViewMap(DrawingPlanContext context, DrawingViewMapDocument viewMap)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(viewMap);
        if (viewMap.Status == DocumentStatus.Conflict || viewMap.Views.Any(item => item.Status == ViewMapStatus.Conflict))
            throw new InvalidOperationException("A conflicted drawing view map cannot be compiled into a modeling plan.");
        var unresolved = viewMap.Views.Where(item => item.Status != ViewMapStatus.Resolved).Select(item => item.ViewId).ToArray();
        if (unresolved.Length > 0)
            throw new InvalidOperationException($"Only resolved drawing views can be compiled into a modeling plan. Unresolved views: {string.Join(", ", unresolved)}.");
        if (viewMap.ProjectionConvention is ProjectionConvention.Unknown or ProjectionConvention.Mirrored or ProjectionConvention.ReferenceArrow)
            throw new InvalidOperationException($"Projection convention '{viewMap.ProjectionConvention}' is not supported by the stage-1 plan adapter.");
        var views = viewMap.Views.Select(item => new DrawingPlanView
        {
            Id = item.ViewId,
            Kind = ToPlanView(item.ViewType),
            PageNumber = item.PageNumber,
            RegionId = item.SourceRegionId,
            Status = item.Fact.Status switch
            {
                FactStatus.Stated => DrawingFactStatus.Stated,
                FactStatus.Derived => DrawingFactStatus.Derived,
                FactStatus.Assumed => DrawingFactStatus.Assumed,
                _ => DrawingFactStatus.Unknown
            }
        }).ToArray();
        return context with
        {
            Projection = viewMap.ProjectionConvention == ProjectionConvention.ThirdAngle ? DrawingProjection.ThirdAngle : DrawingProjection.FirstAngle,
            ProjectionStatus = viewMap.ProjectionFact.Status switch
            {
                FactStatus.Stated => DrawingFactStatus.Stated,
                FactStatus.Derived => DrawingFactStatus.Derived,
                FactStatus.Assumed => DrawingFactStatus.Assumed,
                _ => DrawingFactStatus.Unknown
            },
            Views = views
        };
    }

    private static ModelDrawingView ToPlanView(DrawingViewType view) => view switch
    {
        DrawingViewType.Front => ModelDrawingView.Front,
        DrawingViewType.Top => ModelDrawingView.Top,
        DrawingViewType.Left => ModelDrawingView.Left,
        DrawingViewType.Right => ModelDrawingView.Right,
        DrawingViewType.Rear => ModelDrawingView.Rear,
        DrawingViewType.Bottom => ModelDrawingView.Bottom,
        _ => throw new InvalidOperationException($"View role '{view}' is not supported by the stage-1 typed-plan adapter.")
    };
}
