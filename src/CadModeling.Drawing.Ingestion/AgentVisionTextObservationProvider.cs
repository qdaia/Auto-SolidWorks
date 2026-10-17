using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Providers.Abstractions;

namespace CadModeling.Drawing.Ingestion;

/// <summary>
/// Hands raster text interpretation to the connected AI. This provider performs no
/// recognition, OCR configuration checks, model loading, or external process calls.
/// Empty observations are pending evidence, never an assertion that no text exists.
/// </summary>
public sealed class AgentVisionTextObservationProvider : ITextObservationProvider
{
    public const string Revision = "ai-vision-only-1";

    public Task<ProviderObservationBatch> ObserveAsync(RasterPreprocessResult page,
        ProviderPageContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProviderObservationBatch
        {
            PageNumber = context.PageNumber,
            Modality = ObservationModality.RasterText,
            Provenance = IngestionUtilities.Provenance("agent-vision-pending", Revision,
                context.ProviderProfile + ":ocr-disabled", context.DeterministicSeed),
            Diagnostics = [new IngestionDiagnostic
            {
                Code = "ING-AI-VISION-REQUIRED",
                Severity = ContractDiagnosticSeverity.Info,
                Blocking = false,
                PageNumber = context.PageNumber,
                ProviderName = "agent-vision-pending",
                Message = "本地OCR已停用。AI必须直接查看原图及所有非空区域，识别文字、尺寸端点、视图与几何关系，再填写来源绑定和遗漏复查；空文字候选不表示图上没有文字。"
            }]
        });
    }
}
