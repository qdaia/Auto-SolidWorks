# Auto SolidWorks `26.10.11` release notes

Released on October 11, 2026. Version numbers use `YY.MM.DD` (year/month/day).

[简体中文](release-26.10.11.zh-CN.md) | **English**

## Changes

- **Reduce repeated token input**: integrates the earlier compact tool catalog, on-demand contracts, plan handles, default summaries and paginated results, avoiding repeated expansion of large schemas for the model and duplicate full-plan/IR responses.
- **Reuse complete inputs**: adds `cad_store_model_input` to store complete drawing contexts and drafts locally. Coverage review and compilation can reference them through `drawingContextId` / `draftId`.
- **Atomic incremental edits**: uses `baseInputId` and the JSON Patch operations add / replace / remove / test to correct local content and produce a new immutable input. Failed edits preserve the original.
- **Preserve validation requirements**: checks source, content, compiler and recovery identities before reuse. After restoring complete inputs, the server continues the original compilation, coverage and geometry validation. Complete final receipts can be reused; new requirements, changed files and STEP exports still require independent checks.
- **Unified installation version**: merges the optimized workflow into Auto SolidWorks `26.10.11`, with 16 default tools and 21 full-profile tools, retaining specialized inspection endpoints.

## Measured changes in token input burden

The table measures character counts for compact JSON requests/responses. The first three rows come from the earlier same-input interface comparison between `26.10.07` and `26.10.07.1`. This release inherits those mechanisms; adding tools changes the catalog length.

| Scenario | Characters before optimization | Characters after optimization | Reduction |
|---|---:|---:|---:|
| Earlier optimization: default tool catalog | 406,144 | 10,683 | 97.37% |
| Earlier optimization: compilation response | 5,836 | 1,331 | 77.19% |
| Earlier optimization: build-call arguments | 3,528 | 96 | 97.28% |
| Input reuse: initial complete upload, 3 reviews, compilation and one correction/recompilation | 589,879 | 125,346 | 78.75% |
| Hollow shaft: the same R5 reference correction and recompilation, measured as JSON-RPC request characters | 1,920,316 | 1,383 | 99.928% |

These results show a substantial reduction in repeated text input, reducing the work required to generate and transmit large JSON payloads. **Billed tokens were not recorded; character reductions cannot be presented as equivalent token or cost savings.** The initial complete input still needs to be uploaded. Images, context, system prompts and tool responses also affect total tokens.

In the same-conversation arched-support comparison, wall time decreased from 9 minutes 19.2 seconds to 3 minutes 15.4 seconds. Both versions passed 13 declared checks, native save/reopen checks and independent STEP inspection. This single observation includes the effects of warm-up, baseline debugging and experience shared within the conversation, and is not a stable speedup rate.

## Download

[Windows package](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.11/auto-solidworks-26.10.11-windows-x64.zip) · [SHA-256](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.11/auto-solidworks-26.10.11-windows-x64.zip.sha256) · [English installation guide](download.en.md)
