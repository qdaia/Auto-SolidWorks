# Auto SolidWorks 26.10.13-opt.1

Synchronized with the locally installed version on October 14, 2026. The base/runtime revision remains `26.10.13-opt.1` / `parametric-recipes-evidence-20261013`, with `essential-final-review-1` retained. The reverted `accuracy-first-drawing-1` patch is excluded.

## Behavior and delivery

- Driving hole positions, native sketch quality readback and edit probes with actual geometry contracts.
- Bounded flange, hole-plate and Through / Blind / Counterbore hole-family recipes; explicit direction and depth contracts.
- Native capability evidence bound to task scope, runtime hashes and the SolidWorks revision.
- Source review, dimension bindings, final save/reopen, rebuild, native body validity and declared checks remain required.
- Essential final review reads one bounded summary and actually reviews a contact sheet of the isometric and necessary source views. The ordinary 30-to-60-second target is neither a guarantee nor a deadline.

## Validation scope

The installed source and cache each match the 649-file pre-patch snapshot by SHA-256. A fresh read-only MCP session reports 26.10.13-opt.1. Publication preparation checks repository structure, capability contracts, source identity, offline regressions and every packaged file. Existing evidence for 13 native scenarios and edit probes certifies only the indexed scopes, SW33.0.0 and runtime identities. This publication does not rerun native modeling or certify arbitrary drawing reconstruction, whole-model BREP equivalence or general speed improvements.

See the [original optimization record](optimization-20261013.md) for retained evidence and reproduction entry points. Download the [Windows package](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.13-opt.1/auto-solidworks-26.10.13-opt.1-windows-x64.zip) and run install.ps1 from the extracted directory. Runtime, skills, installer, notices and checksums are included; SolidWorks and Interop are supplied by the user's local installation.
