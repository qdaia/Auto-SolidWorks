> 本地尺寸绑定修订：dimension-evidence-1。新读图任务要求对象、基准和端点证据，检查量义、原文、跨视图冲突及派生依赖。详见 [识图与尺寸绑定](skills/auto-solidworks/references/dimension-evidence.md)。盲测识图准确率与本轮原生建模尚未验证。

> 当前识图方式：ai-vision-only-1。本地OCR已停用，AI直接识别原图文字、尺寸与视图关系；无需配置或安装OCR。原图、来源绑定和最终建模验收仍必须完成。

# Optional timing diagnostics

Set AUTO_SOLIDWORKS_TRACE_DIRECTORY to an absolute, writable local directory before starting the plugin. Tracing is disabled by default. Each process writes a separate JSONL file with monotonic inclusive wall time, parent span IDs, process identity and a write-failure count. Nested timings overlap and must not be added. Span completion is not acceptance. Any write failure invalidates timing evidence while preserving the modeling result or original exception.

Coverage includes drawing ingestion/source facts, plan compilation, native execution, feature COM calls, checkpoints, save/close/open/rebuild, inspection, projection/section and selected observation/coverage/diff/repair paths. A scoped 22-stage matrix is available. COM activation, standalone previews, assembly, drawing export and STEP import are not separately timed. See docs/release-0.6.0.en.md in the release archive.
