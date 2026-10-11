# 26.10.11 measurements and validation scope

[简体中文](benchmark-26.10.11.zh-CN.md) · [Machine-readable data](benchmark-26.10.11.json) · [Release notes](release-26.10.11.en.md)

Release 26.10.11 integrates the earlier 26.10.07.1 compact interface and 26.10.11.1 input reuse. Fresh release build, offline, interface and installation checks are separate from the following bounded pre-release native comparison. The comparison does not recertify all native capabilities of the release rebuild.

## Text and tokens

Earlier 26.10.07.1 measurements: default catalog 406144→10683 characters; compilation response 5836→1331; build arguments 3528→96. The input reuse workflow includes complete initial upload, three coverage reviews, compilation and one name edit/recompile: 589879→125346 (78.75% reduction). One identical hollow-shaft R5 reference correction/recompile used 1920316→1383 JSON-RPC request characters (99.928% reduction), excluding initial input storage for that local-edit measurement. Earlier figures count compact JSON arguments/results; the final figure includes RPC framing. They cannot be added as total token savings.

The 16-tool pre-release compact catalog measured 11547 characters, 97.16% below the 26.10.07 catalog's 406144. The historical 97.37% belongs to the preceding 15-tool build. **Billed tokens, model costs and isolated drawing interpretation speed were not measured.** Complete inputs, images and required evidence remain part of the workflow.

## Native drawing comparison

| Drawing | 26.10.07.1 wall time | 26.10.11.1 wall time | Outcome |
|---|---:|---:|---|
| Arched support | 9m19.2s | 3m15.4s | Both completed and passed declared checks |
| Hollow shaft | 31m05.1s | 15m43.9s | Both failed; these are not completion times |

Inputs are pinned to repository commit `409c09e8c6bbe703306f34d8e34a3aeb87e2607a`: the drawing area in frame 0 of `docs/examples/arch-support/modeling-process.gif`, and `docs/examples/hollow-shaft/drawing.pdf`. Each attempt began with a blank part and independently authored draft, without loading the other model/checkpoint. The AI conversation and warmed SolidWorks process were shared. Baseline-first debugging experience carries forward; the shaft baseline includes an unseparated user-confirmation wait. Later interpretation/authoring overlapped earlier retries, while all native requests were serialized. Wall-time differences are not attributable stable plugin speedups.

Arched support: each passed 13 declared checks, native save/reopen, nine independently imported STEP geometry checks and body validity. Both have one solid, 18 native faces, 41 edges, a 92×50×40 mm box, volume 62356.690420106555 mm³ and area 16661.99145880745 mm². Origins differ by a 20 mm Z translation. No aligned whole-solid Boolean comparison or complete drawing/GD&T certification was performed.

Hollow shaft: operation 70 could not attach a native cosmetic M20 thread at the curved entry. Neither yielded a complete accepted SLDPRT/STEP. Saved verified checkpoints contain only operations 1–16, the eight outer shaft sections. The R5 selection station discrepancy between X1555 and X1553.75 (1.25 mm) remains unresolved against the drawing. The 120° internal cone is an explicit common user assumption, absent from the source drawing; execution success did not reach final saved-reopen acceptance. Later hole groups and nominal Tr400×5 thread operations were not all executed. Historical shaft screenshots do not establish current completion.

## Validation boundary

Input reuse compares complete IR semantics, excluding only a new request ID. Unknown fields, source/recovery/compiler changes, tampering, missing candidate reviews/source bindings and failed atomic patches remain rejected. Stored inputs are marked stored_not_validated. See the [release validation record](validation-26.10.11.json) for fresh offline and installation results.

Standard thread fits, complex Boundary/G2, general changed topology and mechanism limits remain governed by the bundled capability manifest. Private runtime logs, full local native evidence and personal configuration stay local.
