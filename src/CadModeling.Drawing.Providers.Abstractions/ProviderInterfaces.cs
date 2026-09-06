namespace CadModeling.Drawing.Providers.Abstractions;

public interface IPdfNativeObservationProvider
{
    Task<int> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default);
    Task<NativePdfPageObservation> ObservePageAsync(ProviderPageContext context, CancellationToken cancellationToken = default);
}

public interface IPdfRasterizationProvider
{
    Task<RasterPage> RenderPageAsync(ProviderPageContext context, CancellationToken cancellationToken = default);
}

public interface IRasterInputProvider
{
    Task<int> GetPageCountAsync(string imagePath, DrawingInputKind inputKind, CancellationToken cancellationToken = default);
    Task<RasterPage> LoadPageAsync(ProviderPageContext context, CancellationToken cancellationToken = default);
}

public interface IRasterPreprocessor
{
    Task<RasterPreprocessResult> PreprocessAsync(RasterPage page, string profile, ProviderPageContext context, CancellationToken cancellationToken = default);
}

public interface ILayoutObservationProvider
{
    Task<ProviderObservationBatch> ObserveAsync(RasterPreprocessResult page, ProviderPageContext context, CancellationToken cancellationToken = default);
}

public interface ITextObservationProvider
{
    Task<ProviderObservationBatch> ObserveAsync(RasterPreprocessResult page, ProviderPageContext context, CancellationToken cancellationToken = default);
}

public interface IPrimitiveObservationProvider
{
    Task<ProviderObservationBatch> ObserveAsync(RasterPreprocessResult page, ProviderPageContext context, CancellationToken cancellationToken = default);
}

public interface IObservationFusionService
{
    ObservationFusionResult Fuse(int pageNumber, IReadOnlyList<ProviderObservationBatch> batches);
}

public interface IEngineeringDrawingIngestionService
{
    Task<DrawingIngestionResponse> IngestAsync(DrawingIngestionRequest request, CancellationToken cancellationToken = default);
}
