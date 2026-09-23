# Optional timing diagnostics

Set AUTO_SOLIDWORKS_TRACE_DIRECTORY to an absolute, writable local directory before starting the plugin. Tracing is disabled by default. Each process writes a separate JSONL file with monotonic inclusive wall time, parent span IDs, process identity and a write-failure count. Nested timings overlap and must not be added. Span completion is not acceptance. Any write failure invalidates timing evidence while preserving the modeling result or original exception.

Coverage includes drawing ingestion/source facts, plan compilation, native execution, feature COM calls, checkpoints, save/close/open/rebuild, inspection, projection/section and selected observation/coverage/diff/repair paths. Full matching benchmarks for every stage remain incomplete. See docs/release-0.5.5-rc4.en.md in the release archive.
