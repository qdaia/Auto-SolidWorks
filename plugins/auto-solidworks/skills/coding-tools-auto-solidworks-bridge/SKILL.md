---
name: coding-tools-auto-solidworks-bridge
description: Connect coding-tools to the local Auto SolidWorks MCP when both coding-tools and Auto SolidWorks are involved, especially when Auto SolidWorks cad_* tools are not directly exposed in the current tool list. Use this connection skill before declaring Auto SolidWorks unavailable. It covers workspace/link selection, plugin discovery, Windows environment recovery, stdio MCP startup, JSON-RPC initialization and response parsing only; it contains no CAD modeling guidance.
---

# coding-tools → Auto SolidWorks Bridge

Use this skill only to establish or recover the connection from coding-tools to the local Auto SolidWorks MCP server. Once the Auto SolidWorks `cad_*` tools are reachable, stop using this skill and continue with the main Auto SolidWorks skill.

## Mandatory trigger

Load this skill before connection work when any of the following is true:

- The user explicitly selects or mentions both coding-tools and Auto SolidWorks.
- Auto SolidWorks is selected, but its `cad_*` tools are absent from the current tool list while coding-tools is available.
- A previous direct Auto SolidWorks connection failed and coding-tools can reach the user's local workspace.

Do not interpret an absent `cad_*` tool list as proof that Auto SolidWorks is unavailable until this bridge path has been checked.

## Connection workflow

1. Call coding-tools `server_info`.
2. Select the coding-tools link whose workspace is the intended local workspace returned by `server_info`. Do not assume another host's path.
3. Locate the Auto SolidWorks plugin from the workspace rather than assuming that the current release folder name will never change. Prefer a directory containing all of:
   - `plugins/auto-solidworks/.codex-plugin/plugin.json`
   - `plugins/auto-solidworks/scripts/run-auto-solidworks-mcp.ps1`
   - `plugins/auto-solidworks/runtime/mcp/AutoSolidWorks.ModelingMcp.exe`
4. If the workspace has no plugin, locate the active installed Auto SolidWorks cache using the local Codex plugin listing. Read its manifest and build identity to confirm the active version; do not fall back to an older development checkout merely because it exists.
5. Start the MCP through its launcher:
   `scripts/run-auto-solidworks-mcp.ps1`
   Do not treat `AutoSolidWorks.ModelingMcp.exe` as an ordinary one-shot CLI. It is a stdio MCP server and needs a live stdin/stdout transport.
6. When starting it through coding-tools `exec_command`, explicitly provide these Windows environment values if they are absent from the sandbox:
   - `LOCALAPPDATA=<actual local app data directory on this host>`
   - `USERPROFILE=<actual user profile directory on this host>`
7. Do not use `tty=true`. The verified coding-tools Windows runtime has no ConPTY support and returns `TTY_UNSUPPORTED`. Use ordinary pipes.
8. Keep stdin/stdout open and perform the MCP handshake in this order:
   - JSON-RPC `initialize`
   - `notifications/initialized`
   - optionally `tools/list`
   - then `tools/call`
9. The default tool profile is compact. If the task requires projection/section capture or T17-T19 specialized acceptance tools, set `AUTO_SOLIDWORKS_TOOL_PROFILE=full` in the new MCP child process environment before initialization; this preserves all original tool names. Keep this separate from any unknown or pending native request; changing tool profiles never authorizes replay.

10. For `tools/call`, send:
   `{"jsonrpc":"2.0","id":N,"method":"tools/call","params":{"name":"<tool>","arguments":{...}}}`
11. Parse a successful tool result from `structuredContent` first. If it is absent, parse the JSON string in `content[0].text`.
12. Close stdin and terminate or await the MCP child process cleanly when the task is finished.

## Known failure signatures

### LOCALAPPDATA is null

Symptom:

`Join-Path ... LOCALAPPDATA ... parameter is null`

Meaning: the coding-tools sandbox did not inherit the Windows user environment. This does not mean Auto SolidWorks is broken.

Recovery: restart the MCP process with explicit `LOCALAPPDATA` and `USERPROFILE`.

### MCP starts and immediately exits

Symptom: logs show the server started, then completed reading messages and shut down.

Meaning: stdin reached EOF because the MCP process was launched without a persistent client transport.

Recovery: launch it as a child process with stdin/stdout pipes and send the MCP initialization sequence.

### TTY_UNSUPPORTED

Meaning: the Windows coding-tools build cannot create a ConPTY session.

Recovery: retry with `tty=false` or omit the tty option.

### Auto SolidWorks tools missing from the ChatGPT tool list

Meaning: direct tool exposure failed or was not attached to the current runtime; it does not establish that the local plugin is absent.

Recovery: use this coding-tools stdio bridge before reporting the plugin unavailable.

## Reference implementation

When transport details are uncertain, read:

`Auto-SolidWorks-Release-0.6.0/tests/smoke.py`

Its subprocess + queue + JSON-RPC implementation is the verified reference for:

- launching `run-auto-solidworks-mcp.ps1`
- maintaining stdin/stdout
- correlating JSON-RPC request IDs
- sending `initialize`
- sending `notifications/initialized`
- calling `tools/call`
- decoding `structuredContent` / text fallback
- shutting down the child process

If the release directory changes, locate the corresponding `tests/smoke.py` in the active Auto SolidWorks source or release tree.

## Scope boundary

This is a connection skill, not a modeling skill.

- Do not include CAD dimensions, feature-planning heuristics, drawing interpretation, or geometry decisions here.
- A temporary Python process may act only as MCP transport or orchestration.
- Do not use Python, PowerShell, C#, VBA, COM automation, macros, or arbitrary SolidWorks API calls to replace Auto SolidWorks modeling tools.
- Once MCP connection succeeds, hand control back to the main Auto SolidWorks skill and its `cad_*` tools.

