# Auto SolidWorks 1025

Release date: 2026-10-25. Synchronizes the latest local 26.10.24-vision.9 source and runtime under public version **1025**.

## Changes

- Four default stages: recognition, dimension binding, compile, model. New inputs use source-four-stage-2; explicit older contracts retain their rules.
- Source geometry proposals, batch dimension binding and local binding diagnostics; image semantics still require actual AI review.
- Degree-based Sin/Cos source expressions and page-stretch calibration from independently reviewed normal-view circles; pixels are not model dimensions.
- Signed source-angle ray to actual sketch X-axis correspondence rejects the reproduced front/back ear mirror defect, including compiled-plan validation before execution.
- Parameter ownership, source endpoints, units, native rebuild, saved readback and body-validity checks remain.

## Validation scope

Release validation covers source/contracts, managed regressions, public MCP, archive paths/hashes and fresh installed-runtime identity. See [validation scope](release-validation-1025.md) for measured results.

This release does not rerun native CAD modeling: native_execution_for_release=false. Prior vision.9 native repair evidence remains historical evidence for its exact runtime. The direction gate covers source-angle expressions driving sketch X axes. Front-basis and view labels remain reviewed inputs; no complete drawing/BREP/GD&T equivalence or general recognition accuracy is certified.

## Install

Download the Windows ZIP and SHA-256 file, verify the digest, extract, and run install.ps1. SolidWorks Interop is prepared from the user's local installation and is not redistributed. Modeling output defaults to local storage.
