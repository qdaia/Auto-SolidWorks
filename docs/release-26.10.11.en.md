# Auto SolidWorks `26.10.11` release notes

Released on October 11, 2026. Version numbers use `YY.MM.DD` (year/month/day).

[简体中文](release-26.10.11.zh-CN.md) | **English**

## Changes

- **Lower repeated token input**: includes the earlier compact catalog, on-demand contracts, plan handles, default summaries and paginated results. Large schemas and duplicate plan/IR payloads no longer need to be expanded on every call.
- **Complete local input reuse**: `cad_store_model_input` stores complete drawing contexts and drafts. Coverage review and compilation accept `drawingContextId` / `draftId`.
- **Atomic incremental edits**: `baseInputId` and JSON Patch add / replace / remove / test produce a new immutable input; failed edits preserve the original.
- **Preserved validation**: source, content, compiler and recovery identities are checked. Restored complete inputs still pass the original compiler and coverage/geometry validation. Complete final receipts can be reused; changed files, new requirements and STEP exports still require checks.
- **Unified installation**: the optimized workflow is now Auto SolidWorks `26.10.11`, with 16 default tools and 21 full-profile tools, including specialized inspection endpoints.

## Measured reduction in token input burden

The figures measure serialized JSON characters. The first three are the earlier same-input `26.10.07` → `26.10.07.1` comparison; this release inherits the mechanisms, while the added tool changes catalog length.

| Scenario | Before, characters | After, characters | Reduction |
|---|---:|---:|---:|
| Earlier optimization: default tool catalog | 406,144 | 10,683 | 97.37% |
| Earlier optimization: compilation response | 5,836 | 1,331 | 77.19% |
| Earlier optimization: build-call arguments | 3,528 | 96 | 97.28% |
| Input reuse: initial complete upload, 3 reviews, compilation and one edit/recompile | 589,879 | 125,346 | 78.75% |
| Hollow shaft: same R5 reference edit/recompile, JSON-RPC request characters | 1,920,316 | 1,383 | 99.928% |

The measurements show less repeated text and less large-JSON generation/transmission. **Billed model tokens were not recorded; character reductions are not equivalent token or cost savings.** Initial complete input is still transmitted. Images, history, system prompts and responses also contribute to total tokens.

In one same-session arched-support comparison, wall time changed from 9m19.2s to 3m15.4s. Both passed 13 declared checks, native save/reopen and independent STEP inspection. Warm-up, baseline debugging and shared conversational experience prevent attribution as a stable plugin speedup. Both hollow-shaft attempts stopped at a curved-entry M20 native cosmetic thread and produced no complete deliverable. See [validation and quality boundaries](benchmark-26.10.11.en.md).

## Download

[Windows package](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.11/auto-solidworks-26.10.11-windows-x64.zip) · [SHA-256](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.11/auto-solidworks-26.10.11-windows-x64.zip.sha256) · [English installation guide](download.en.md)
