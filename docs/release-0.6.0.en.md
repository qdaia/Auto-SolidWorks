# Auto SolidWorks 0.6.0 release notes

Released 2026-09-25.

0.6.0 packages the current optimized version under a final release number. Modeling and verification algorithms are unchanged from 0.5.5-rc4. This release updates runtime/plugin version identifiers and download entry points, and adds the latest scoped evidence. The version number does not certify arbitrary complex drawings or long-running reliability.

## Cumulative changes since 0.5.4

- Expand the public tool set from 9 to 15: drawing coverage review, native projection/section capture, and bounded revolved-part, hole-group and edge-treatment verification.
- Preserve source facts, critical dimension bindings, raw drawing details and omission-candidate accounting. OCR remains auxiliary to agent visual interpretation.
- Strengthen geometry identity, directional measurements, through/blind connectivity, before/after differences and constrained repair, including unaffected requirements.
- Retain verified-prefix recovery, native save/close/reopen checks and controlled dimension-drive/restore verification.
- Add optional stage timing without reducing required checks. Diagnostics remain disabled by default.

## Fresh 0.6.0 release checks

The rebuilt release passed 428 source regressions and 9 public smoke checks, confirming all 15 tools and matching runtime/plugin versions. The ZIP passed per-file hashing, isolated installation and installed smoke checks; all 77 locally installed files match their source. A fresh process from the installed 0.6.0 cache built a synthetic bored plate, saved and reopened it, checked projection and section geometry, read back its STEP export, and drove/restored a native dimension from 10 to 11 to 10 mm without changing the source model file hash. These synthetic checks do not replace independent drawing acceptance.

## Evidence and measured scope

The functionally equivalent candidate was measured on 2026-09-24 across 22 distinct stages: fresh processes, warmed processes and local changes, n=5 per workload/condition. Two workloads produced 30 measured samples, 3 excluded warmups and 570 passing assertions: 31 workload/stage pairs and 93 condition cells. See [sanitized raw timings and call counts](performance-0.6.0.csv). These timings belong to the prior candidate; the 0.6.0 rebuild, installation and native smoke results are reported separately in this Release.

The main workload is a synthetic bored plate. The review/repair workload performs actual native fillet repair and bore-preservation checks. Fresh processes do not mean cold OS/SolidWorks caches. Local changes use full rebuilds. Nested timings overlap. With n=5, p95 is a local diagnostic estimate. No controlled speedup over 0.5.4 is claimed.

One opened pin drawing passed a diagnostic replay: saved native readback, 8 declared geometry checks, 14 source/reference projection segments and a native length-drive/restore probe. This is scoped nominal-geometry evidence, excluded from the independent holdout score. Private drawings, reference CAD and local test artifacts are not distributed.

## Known limits

- The seven-case independent drawing trial was invalidated by execution interruption and missing isolation-continuity evidence. Independent acceptance is incomplete; no pass rate is reported.
- COM activation, standalone preview rendering, assembly, drawing export and STEP import lack separate timing cells. Long-duration reliability and all-API performance are not certified.
- Genuine IR 1.1–1.3 archive compatibility remains untested. Optional enhanced OCR and viewer-selection mapping remain disabled.
- Threads use nominal holes and cosmetic annotations, not physical helical geometry. Full GD&T, roughness and physical material/heat-treatment properties are not certified.

## Installation

[Windows 0.6.0 ZIP](https://github.com/qdaia/Auto-SolidWorks/releases/download/v0.6.0/auto-solidworks-0.6.0-windows-x64.zip) · [SHA-256](https://github.com/qdaia/Auto-SolidWorks/releases/download/v0.6.0/auto-solidworks-0.6.0-windows-x64.zip.sha256) · [English installation](download.en.md) · [中文更新公告](release-0.6.0.zh-CN.md)

Requires Windows x64, a licensed local SolidWorks installation, .NET 9 Windows Desktop Runtime x64 and a plugin-capable Codex CLI. Extract the Windows ZIP, run install.ps1, then open a new Codex task. Proprietary SolidWorks interop files are obtained from the user's local installation and are not distributed.
