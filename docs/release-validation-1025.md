# 1025 发布验证 / Release validation

发布日期 / Release date: 2026-10-25. Source baseline: local 26.10.24-vision.9. Public version: 1025; .NET package version: 1025.0.0; informational version: 1025.

| 检查 / Check | 结果 / Result |
|---|---|
| Managed build | Passed, MCP and native executor built against local Interop |
| Repository structure | Passed, 61 synthetic case definitions present |
| Versioned capability contract | Passed, 10 generated contract documents |
| Managed regression suites | 21 suites / 1431 checks passed |
| Final-review receipt regressions | 17 passed |
| Public MCP compile/schema/dry-run checks | 130 passed; native executor deliberately unavailable |
| Source/runtime identity | 223 source entries / 64 runtime files hashed |
| Native CAD execution for this release | **Not run / 未运行** |

The 17 signed-direction checks are included in the managed total. They compile the known corrected drawing and verify direction failures, without native execution. The 24 background/projection tests use a managed fake executor. One initial build lacked the explicit local SolidWorks Interop path; the corrected build and tests passed. That failed build receipt is retained locally.

本次发布不重跑SolidWorks原生建模。先前vision.9的保存、读回及方向修复证据保留原版本归属，不认证1025全图/BREP/GD&T等价、通用识图准确率或全面原生能力。21组托管回归与17项回执回归均不能替代原生建模验收。

The release package is separately checked after extraction. Publication verifies the remote commit/tree, CI, tag, release assets and complete downloaded-asset SHA-256 values. See the GitHub Actions run and published validation JSON; local installation identity is recorded separately.

[机器可读检查结果 / Validation JSON](validation-1025.json)
