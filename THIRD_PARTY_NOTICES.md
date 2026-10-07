# Third-party notices

The local Gordon surface addition bundles unmodified [CurvesWB](https://github.com/tomate44/CurvesWB) source at commit `e47b47927f59c87b93a1820c9f6ad4b4dc187076` under `plugins/auto-solidworks/surface/CurvesWB/`. The workbench uses LGPL-2.1-or-later (`LICENSE-CODE` and original per-file headers); the DLR TiGL-derived Gordon/BSpline algorithms use Apache-2.0 (`LICENSE-TIGL` and original per-file headers). Both license texts and original source/attributions are preserved. This does not relicense those files under the project's MIT license. FreeCAD/OpenCascade and their dependencies are separate local installations, not bundled in the plugin ZIP.

Auto SolidWorks source is MIT licensed. Dependencies retain their own licenses. This list is generated from the built MCP dependency manifest and local NuGet metadata. Upstream license and notice texts are preserved under `third-party/`.

| Package | Version | License | Copyright |
|---|---|---|---|
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Caching.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Configuration.Binder | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Configuration.CommandLine | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Configuration.FileExtensions | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Configuration.Json | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Configuration.UserSecrets | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Configuration | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.DependencyInjection | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Diagnostics | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.FileProviders.Physical | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.FileSystemGlobbing | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Hosting | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Logging.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Logging.Configuration | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Logging.Console | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Logging.Debug | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Logging.EventLog | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Logging.EventSource | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Logging | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Options.ConfigurationExtensions | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Options | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Primitives | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| ModelContextProtocol.Core | 2.0.0 | Apache-2.0 | © Model Context Protocol a Series of LF Projects, LLC. |
| ModelContextProtocol | 2.0.0 | Apache-2.0 | © Model Context Protocol a Series of LF Projects, LLC. |
| PdfPig | 0.1.16 | Apache-2.0 |  |
| System.Diagnostics.DiagnosticSource | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| System.Diagnostics.EventLog | 9.0.8 | MIT | © Microsoft Corporation. All rights reserved. |
| System.IO.Pipelines | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| System.Net.ServerSentEvents | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| System.Text.Encodings.Web | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| System.Text.Json | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |

## License texts

- [dotnet-LICENSE.txt](third-party/dotnet-LICENSE.txt)
- [dotnet-NOTICES.txt](third-party/dotnet-NOTICES.txt)
- [mcp-LICENSE.txt](third-party/mcp-LICENSE.txt)
- [mcp-NOTICES.txt](third-party/mcp-NOTICES.txt)
- [pdfpig-LICENSE.txt](third-party/pdfpig-LICENSE.txt)
- [pdfpig-NOTICES.txt](third-party/pdfpig-NOTICES.txt)

## External local dependencies

SolidWorks and its interop assemblies are external proprietary dependencies. The public ZIP does not redistribute them. The installer copies the required interop assemblies from the user's own local SolidWorks installation.

.NET Windows Desktop Runtime, Tesseract, language data and Poppler are installed separately and are not bundled.

Dependency versions and license-document provenance are recorded in [dependencies.json](third-party/dependencies.json).
