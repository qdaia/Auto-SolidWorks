# Optional timing diagnostics

Set AUTO_SOLIDWORKS_TRACE_DIRECTORY to an absolute, writable local directory before starting the plugin. Tracing is disabled by default. Each process writes a separate JSONL file with monotonic inclusive wall time, parent span IDs, process identity and a write-failure count. Nested timings overlap and must not be added. Span completion is not acceptance. Any write failure invalidates timing evidence while preserving the modeling result or original exception.

Coverage includes drawing ingestion/source facts, plan compilation, native execution, feature COM calls, checkpoints, save/close/open/rebuild, inspection, projection/section and selected observation/coverage/diff/repair paths. A scoped 22-stage matrix is available. COM activation, standalone previews, assembly, drawing export and STEP import are not separately timed. See docs/release-0.6.0.en.md in the release archive.
