# Auto SolidWorks 0.5.5-rc4 release notes

This prerelease publishes the locally validated rc4 update. It expands drawing omission review, saved-model verification, constrained repair and recovery. Independent drawing holdout acceptance and the complete performance matrix remain unfinished; this is not full product acceptance.

## Changes

- Expand the public MCP surface from 9 to 15 tools: `cad_review_drawing_coverage`, `cad_capture_projection`, `cad_capture_section`, `cad_verify_revolved_family`, `cad_verify_hole_group`, and `cad_verify_edge_treatment` are new.
- Add independent drawing candidate inventories, overlapping-region and residual-ink review. Unresolved required coverage blocks complete acceptance. OCR remains candidate evidence interpreted against the source by the agent, not certified semantic recall.
- Add identity-bound geometry references, directional measurements, bounded cavity connectivity checks, native projection/planar-section capture, and source-requirement coverage review.
- Strengthen bounded revolved-part and hole-family verification, including countersink/counterbore and blind cases plus cosmetic-thread metadata. Physical helical threads are neither generated nor certified.
- Bind repair acceptance to source revision, model/plan identity, repair scope, complete differences and checks. Reject synthetic/stale receipts and actual collateral geometry changes.
- Strengthen client disconnect, pause/reconnect, corrupt/stale checkpoint rejection and verified-prefix recovery. Numeric signed-zero fingerprint normalization preserves strings and nonzero values; old receipts may require actual re-execution.
- Add default-off internal tracing via the absolute-path `AUTO_SOLIDWORKS_TRACE_DIRECTORY` environment variable. Spans carry process and parent identities. Diagnostic failures preserve operation behavior but invalidate timing evidence.

## Validation and limits

The local rc4 passed 429 automated checks, 203 native checks, 18 installed public MCP checks, 8 actual desktop-entry checks and 6 genuine IR1.4 native compatibility checks with output-only relocation. The native groups cover inspection36, hole-family12, edge19, revolved14, recovery10, axial-hole19, hole-subtype72 and actual repair21.

These are development fixtures, adversarial inputs and bounded native execution paths, not independent drawing success rates. Release DLL/EXE bytes are preserved from the validated rc4. Public source is rebuilt and regression-tested separately; the release archive is checked through isolated installation, per-file hashes and the exact 15-tool smoke set. PDBs and proprietary SolidWorks interop DLLs are excluded; installation prepares interop from the user's licensed local SolidWorks installation.

The main-chain performance experiment used five samples per group plus one excluded warmup. Native-build medians were 14.578s for fresh MCP/executor processes, 13.692s for warm processes and 13.437s for a local dimension change followed by a full rebuild. SolidWorks/OS caches remained; nested timings overlap. There is no trace-disabled or matched v0.5.4 comparison and no claimed speedup.

Remaining work includes independent holdouts and generator/evaluator access isolation; matched observation/coverage/diff/repair timing; genuine IR1.1–1.3 archives; optional engineering-OCR comparison and viewer selection mapping. Optional additions stay disabled; existing Tesseract candidate extraction is separate. Open-model work is limited to a source preflight followed by keeping the feature disabled.

Existing modeling, assemblies, local geometry checks, drawing export and baseline recovery remain. Native feature categories remain at 36; tool-count growth is not an overall capability percentage.

## Download

[Prerelease and Windows package](https://github.com/qdaia/Auto-SolidWorks/releases/tag/v0.5.5-rc4) · [English download page](download.en.md) · [中文更新公告](release-0.5.5-rc4.zh-CN.md)

The v0.5.4 stable release remains available. This update publishes code, regressions and public documentation, without user drawings, reference CAD, native acceptance outputs, personal configuration or historical backups.
