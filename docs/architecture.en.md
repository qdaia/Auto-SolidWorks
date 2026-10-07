# Architecture

Auto SolidWorks separates input interpretation, parametric planning, native execution and result inspection. Requirements can be reviewed before CAD execution and compared with observed results afterward.

| Layer | Responsibility |
| --- | --- |
| Plugin skills and MCP | Collect drawings/dimensions, query capabilities, compile plans and invoke modeling/inspection |
| Modeling IR | Represent operation dependencies, units, configurations, geometry references, outputs and acceptance requirements |
| CadModeling.Core | Validate inputs, compile features, check geometry contracts and manage recovery/receipts |
| SolidWorks executor | Call local SolidWorks from a separate Windows process, creating native features and saving/reopening/readback |
| Local Gordon tool | Generate NURBS outputs using pinned curve-network algorithms and FreeCAD / OpenCascade |

Build identity binds production-source, capability-manifest and runtime hashes. A native execution lease limits concurrent access; receipts distinguish completion, failure and unknown outcomes. Checkpoints and persistent references determine whether a recovery prefix remains valid.

See the [capability manifest](../plugins/auto-solidworks/skills/auto-solidworks/references/capability-manifest.json) for available operations and their evidence scope.
