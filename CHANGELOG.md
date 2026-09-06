# Changelog

## 0.5.1 — First public GitHub release

- Publish the local typed SolidWorks MCP server and executor source, with eight public modeling tools.
- Include native part and assembly workflows, model inspection, image/PDF input and STEP/STL output.
- Add Chinese installation and build instructions, MIT license and third-party notices.
- Build the runtime from source and distribute a Windows ZIP with a local Codex installer and SHA-256 checksums.
- Discover local SolidWorks interop components during installation; proprietary interop DLLs are not redistributed.
- Keep source, tests and plugin instructions in Git. Keep runtime binaries in Releases, and retain private drawings, CAD files, backups and historical machine reports outside the public repository.
- Correct the public smoke test to expect the current eight tools and decouple public tests from private fixtures.

Known limitations and the exact release validation scope are documented in [docs/validation.md](docs/validation.md).
