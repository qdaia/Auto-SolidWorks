# Auto SolidWorks 26.10.13-opt.1

2026 年 10 月 14 日同步本地已安装版本。插件基础版本及运行库修订保持 `26.10.13-opt.1` / `parametric-recipes-evidence-20261013`，保留 `essential-final-review-1` 收尾流程；不包含已撤销的 `accuracy-first-drawing-1` 全图纸流程补丁。

## 行为与交付

- 可驱动孔位、原生草图质量读回和带实际几何合同的编辑试算。
- 法兰、孔板和 Through / Blind / Counterbore 孔族配方，方向及深度合同。
- 原生能力证据绑定任务范围、运行库哈希和 SolidWorks 修订号。
- 来源复查、尺寸绑定、最终保存重开、重建、实体有效性及声明检查继续保留。
- 必要收尾流程读取一次摘要，实际查看等轴测及覆盖源特征的必要视图合成图；30至60秒是流程目标，不是保证或截止。

## 验证边界

本次同步核对安装源和缓存各649个文件与回退前目标备份的SHA-256，独立MCP只读查询返回26.10.13-opt.1。发布准备执行仓库、能力合同、来源身份、离线回归和包内逐文件校验。既有13个原生场景及编辑试算只认证随包证据索引列出的范围、SW33.0.0与运行库身份；本次发布没有重新执行原生建模，不认证任意图纸重建、完整BREP等价或通用提速。

此前优化验证及复跑入口见[优化记录](optimization-20261013.md)。下载[Windows安装包](https://github.com/qdaia/Auto-SolidWorks/releases/download/v26.10.13-opt.1/auto-solidworks-26.10.13-opt.1-windows-x64.zip)，在解压目录运行install.ps1。运行程序、技能、安装脚本、许可与校验清单随包提供；SolidWorks和Interop由使用者的本机安装提供。
