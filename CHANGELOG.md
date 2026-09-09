# Changelog

## 0.5.4 — Native drawing export and measured verification

- Add the ninth MCP tool, `cad_export_drawing`, for first-angle A3 SLDDRW/PDF export, four views, a parameter schedule, source hash preservation and native reopen checks.
- Recover omitted feature dimensions, deduplicate parameters and distinguish millimeters, degrees and unitless pattern counts.
- Preserve rotated PDF dimension labels, OCR conflict evidence and coordinate-frame boundaries.
- Include the previously local 0.5.2/0.5.3 source binding, measured cylinder and local surface checks, checkpoint recovery and chamfer parameter fixes.
- Add focused core and drawing regressions; retain the existing public synthetic modeling cases.
- Ship separate Chinese/English documentation, complete example assets and a version-independent isolated installer verification script.

The 14-reference development corpus uses model-derived drawings; it does not establish blind drawing-to-model reconstruction accuracy. Native export currently supports saved parts, A3 sheets, recognized English/Chinese standard views and up to 44 source parameters.

## 0.5.1 — First public GitHub release

- Publish the local typed SolidWorks MCP server and executor source, with eight public modeling tools.
- Include native part and assembly workflows, model inspection, image/PDF input and STEP/STL output.
- Add Chinese installation and build instructions, MIT license and third-party notices.
- Build the runtime from source and distribute a Windows ZIP with a local Codex installer and SHA-256 checksums.
- Discover local SolidWorks interop components during installation; proprietary interop DLLs are not redistributed.
- Keep source, tests and plugin instructions in Git. Keep runtime binaries in Releases, and retain private drawings, CAD files, backups and historical machine reports outside the public repository.
- Correct the public smoke test to expect the current eight tools and decouple public tests from private fixtures.

Known limitations and the exact release validation scope are documented in [docs/validation.md](docs/validation.md).
