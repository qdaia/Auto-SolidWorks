"""Validate the versioned capability contract and its generated skill/document summaries. Never starts CAD."""
import argparse
import json
import re
from pathlib import Path
import xml.etree.ElementTree as ET
import hashlib

root=Path(__file__).resolve().parents[1]
plugin=root/'plugins/auto-solidworks'
manifest_path=plugin/'skills/auto-solidworks/references/capability-manifest.json'
manifest=json.loads(manifest_path.read_text(encoding='utf-8'))
parser=argparse.ArgumentParser();parser.add_argument('--write-docs',action='store_true');parser.add_argument('--runtime',action='store_true');args=parser.parse_args()
start='<!-- AUTO-SOLIDWORKS-CONTRACT:BEGIN -->'
end='<!-- AUTO-SOLIDWORKS-CONTRACT:END -->'
docs={
    root/'README.md':(True,'plugins/auto-solidworks/skills/auto-solidworks/references/capability-manifest.json'),
    root/'README.en.md':(False,'plugins/auto-solidworks/skills/auto-solidworks/references/capability-manifest.json'),
    plugin/'README.md':(True,'skills/auto-solidworks/references/capability-manifest.json'),
    plugin/'skills/auto-solidworks/SKILL.md':(False,'references/capability-manifest.json'),
}
for name in ('surface-modeling.md','gordon-surface.md','reference-drawing-export.md','local-geometry-verification.md','root-hardening.md','engineering-reliability.md'):
    docs[manifest_path.parent/name]=(name=='engineering-reliability.md','capability-manifest.json')
errors=[]
version=manifest['version']; revision=manifest['revision']
digest=hashlib.sha256(manifest_path.read_bytes()).hexdigest()
for p,value in ((plugin/'.codex-plugin/plugin.json',json.loads((plugin/'.codex-plugin/plugin.json').read_text(encoding='utf-8'))['version']),
                (root/'src/AutoSolidWorks.ModelingMcp/AutoSolidWorks.ModelingMcp.csproj',ET.parse(root/'src/AutoSolidWorks.ModelingMcp/AutoSolidWorks.ModelingMcp.csproj').getroot().findtext('.//Version'))):
    if value!=version:errors.append(f'Version drift: {p} = {value}, manifest = {version}')
for path,(zh,link) in docs.items():
    if manifest.get('local_integration') and path in (root/'README.md', root/'README.en.md'):
        body=(f'版本：`{version}`。通过 `cad_get_capabilities` 查询当前功能、输入约束和验证范围，或阅读[随包能力清单]({link})。本版完成重新构建、1,101 项离线检查及安装后的 128 项公开接口检查；原生案例证据按其原始修订记录。' if zh else f'Version: `{version}`. Query `cad_get_capabilities` for available features, input constraints and validation scope, or read the [bundled capability manifest]({link}). This release has a fresh build, 1,101 offline checks and 128 post-install public-interface checks; native fixture evidence remains tied to its original revision.')
    elif manifest.get('local_integration') and zh:
        body=f'当前本地整合版本：`{version}`；修订：`{revision}`。整合 `.34.validation` 已实现的优化，保留原有建模与工程图能力；本轮验证范围为重新构建、离线回归及公开 MCP 编译／预演，不启动 SolidWorks。B01–B06 工程任务保持暂停且未全部完成。原生成功与失败按原工程修订和限定案例保留，不自动认证此版本；复杂 Boundary、Curvature Fill／G2、一般拓扑历史、标准螺纹配合及机构限位／耦合仍有缺口。详见[随包能力清单]({link})及 `cad_get_capabilities`。'
    elif manifest.get('local_integration'):
        body=f'Current local integration version: `{version}`; revision: `{revision}`. Incorporates the implemented `.34.validation` optimizations and preserves existing modeling/drawing capabilities. This integration is checked by a fresh build, offline regressions and public MCP compile/dry-run without starting SOLIDWORKS. B01-B06 engineering remains paused and incomplete. Native successes and failures remain bound to their original revisions and bounded fixtures; this version is not natively recertified. Complex Boundary, Curvature Fill/G2, general changed topology, standard thread fits and mechanism limits/couplings remain open. Read the [bundled capability manifest]({link}) and `cad_get_capabilities`.'
    elif manifest.get('native_validation_session') and zh:
        body=f'当前本地源码版本：`{version}`；修订：`{revision}`。本轮继续离线与真实 SolidWorks 验收，状态为 `{manifest["current_native_acceptance"]}`。已实现配置表达式、特征抑制及 B01–B06 核心；实际成功范围以当前构建身份绑定的验收记录为准。历史成功不自动认证当前版本。设计表、曲面支撑观测、原生变化拓扑历史、实体螺纹和活动机构适配仍有开放边界。详见[随包能力清单]({link})及 `cad_get_capabilities`。'
    elif manifest.get('native_validation_session'):
        body=f'Current local source version: `{version}`; revision: `{revision}`. Offline and native SOLIDWORKS validation continues with status `{manifest["current_native_acceptance"]}`. Configuration expressions, feature suppression and B01-B06 cores are implemented; successful scope is bound to the current build identity and retained acceptance records. Historical successes do not recertify this revision. Design tables, surface support observations, changed native topology history, physical threads and moving-mechanism adapters still have open boundaries. Read the [bundled capability manifest]({link}) and `cad_get_capabilities`.'
    elif manifest.get('design_intent_authoring',{}).get('feature_suppression') and zh:
        body=f'当前本地源码版本：`{version}`；修订：`{revision}`。本轮离线增加配置级特征抑制／解除抑制、完整特征清单、持久身份及保存回执核对，拒绝未声明连带变化和跨配置泄漏。配置变量、固定函数、尺寸引用和既有 B01–B06 核心保留。验证覆盖编译、独立数值、模拟端口及公开 MCP 试运行；SolidWorks 原生验收未运行。设计表、原生全局单位／函数语义校准、曲面观测、变化拓扑历史、实体螺纹和活动机构适配仍开放。详见[随包能力清单]({link})及 `cad_get_capabilities`。'
    elif manifest.get('design_intent_authoring',{}).get('feature_suppression'):
        body=f'Current local source version: `{version}`; revision: `{revision}`. This offline update adds configuration feature suppression/unsuppression, complete feature inventories, persistent identity and saved receipts, rejecting undeclared cascades and cross-configuration leakage. Configuration expressions and existing B01-B06 cores remain. Compilation, independent numeric checks, fake ports and public MCP dry-run are the evidence scope; native acceptance is not run. Design tables, native global units/function calibration, surface observations, changed topology history, physical threads and moving-mechanism adapters remain open. Read the [bundled capability manifest]({link}) and `cad_get_capabilities`.'
    elif manifest.get('design_intent_authoring',{}).get('configuration_expressions') and zh:
        body=f'当前本地源码版本：`{version}`；修订：`{revision}`。本轮离线增加配置变量、15 个固定函数、尺寸引用联合依赖、输入继承预检、角度模式与自动求解顺序检查，并限制表达式展开长度。既有 Boundary、Fill、语义拓扑、文本与运动核心保留。验证覆盖编译、独立数值、模拟端口及公开 MCP 试运行；SolidWorks 原生验收未运行。原生全局变量单位与函数语义尚未校准，特征抑制、设计表及其他 B01–B06 缺口仍开放。详见[随包能力清单]({link})及 `cad_get_capabilities`。'
    elif manifest.get('design_intent_authoring',{}).get('configuration_expressions'):
        body=f'Current local source version: `{version}`; revision: `{revision}`. This offline update adds configuration variables, 15 fixed functions, a joint variable/dimension dependency graph, input inheritance preflight, angular/automatic-solve-order checks and bounded rendering. Existing Boundary, Fill, semantic topology, text and mobility cores remain. Validation covers compilation, independent numeric checks, fake ports and public MCP dry-run. Native acceptance is not run; raw global units and native function semantics remain uncalibrated. Feature suppression, design tables and other B01-B06 gaps remain open. Read the [bundled capability manifest]({link}) and `cad_get_capabilities`.'
    elif manifest.get('boundary_surface_contract') and zh:
        body=f'当前本地源码版本：`{version}`；修订：`{revision}`。本轮离线把 Boundary 实际选择身份／标记／顺序、原生曲面工厂参数、曲线族完整来源和端部／可用方向控制、保存回执统一到核心执行端口，并验证跨平面弯曲和三维网络角点及失败路径。填充支撑、语义拓扑、参数配置、文本与自由度修复保留。验证仅包含编译、核心、模拟接口和 MCP 试运行，SolidWorks 原生验收未运行。第二方向裁剪观测、接口影响枚举分歧、实际内交叉／连接器及其他 B01–B06 任务仍开放；不以 Fill 或四角匹配替代真实 Boundary／G1／G2 成功。详见[随包能力清单]({link})及 `cad_get_capabilities`。'
    elif manifest.get('boundary_surface_contract'):
        body=f'Current local source version: `{version}`; revision: `{revision}`. This offline update unifies Boundary actual selection identity/mark/order, exact surface factory arguments, complete source curves, retained end/available direction controls and saved receipts in one core port. Bowed cross-plane and spatial corner/failure fixtures are checked offline. Fill support, semantic topology, design intent, text and mobility cores remain. Validation covers compilation, core, fake ports and MCP dry-run; native acceptance was not run. Second-direction trim observation, API influence encoding discrepancy, internal intersections/connectors and other B01-B06 tasks remain open. Fill or corner checks do not certify actual Boundary/G1/G2 success. Read the [bundled capability manifest]({link}) and `cad_get_capabilities`.'
    elif manifest.get('fill_surface_contract') and zh:
        body=f'当前本地源码版本：`{version}`；修订：`{revision}`。本轮离线补齐填充边界的支撑面及来源特征合同、连续性设置提交后重新读回、保存定义回执与原生身份等价核对。语义拓扑、参数配置、文本盲孔抽壳与一般自由度核心保留。验证仅包含编译、核心、模拟接口和 MCP 编译／预演；本版未做 SolidWorks 原生验收。两个来源面时的逐边 G2 支撑观测、变更拓扑历史及其余 B01–B06 任务仍开放；控制值保留不能证明 G2 几何连续。历史原生证据属于原版本，工程图导出不在修复范围。详见[随包能力清单]({link})及 `cad_get_capabilities`。'
    elif manifest.get('fill_surface_contract'):
        body=f'Current local source version: `{version}`; revision: `{revision}`. This offline update adds per-boundary support/owner contracts, fresh readback after contact setter/commit and saved definition identity receipts. Semantic topology, design intent, bounded text and general mobility cores remain. Validation covers compilation, core, fake ports and MCP compile/dry-run only; native acceptance was not run. Two-source-face G2 support observation, changed topology history and other B01-B06 tasks remain open. Retained controls do not prove geometric G2. Historical native evidence belongs to its original revisions and drawing export is outside scope. Read the [bundled capability manifest]({link}) and `cad_get_capabilities`.'
    elif manifest.get('semantic_topology') and zh:
        body=f'当前本地源码版本：`{version}`；修订：`{revision}`。本轮离线增加源语义、根实体／来源特征身份、完整拓扑变更历史及修复／验收回执重验。参数配置、盲孔抽壳文本与一般自由度核心保留。验证仅包含编译、核心、模拟接口和 MCP 编译／预演；本版未做 SolidWorks 原生验收。原生端尚缺跨重建／保存的变更历史提供者，不能声称真实语义重绑定已完成；标准实体螺纹和其他 B01–B06 边界仍开放。历史原生证据属于原版本，工程图导出不在修复范围。详见[随包能力清单]({link})及 `cad_get_capabilities`。'
    elif manifest.get('semantic_topology'):
        body=f'Current local source version: `{version}`; revision: `{revision}`. This offline update adds source semantics, root/owner identity, complete topology history and receipt replay before repair/acceptance. Parameter/configuration, blind-hole/shell text and general mobility core repairs are retained. Validation covers compilation, core, fake ports and MCP compile/dry-run only; native acceptance was not run. A durable native changed-history provider remains unavailable, so actual semantic rebinding is not claimed. Physical threads and other B01-B06 boundaries remain open; historical native evidence belongs to its original revisions and drawing export is outside scope. Read the [bundled capability manifest]({link}) and `cad_get_capabilities`.'
    elif manifest.get('offline_modeling_boundaries') and zh:
        body=f'当前本地源码版本：`{version}`；修订：`{revision}`。本轮离线增加明确中心盲孔、向内抽壳文本及独立几何合同，以及一般瞬时相对自由度的完整运动基、坐标／身份／保存读回合同。方程与配置修复保留。验证仅包含编译、核心、模拟接口和 MCP 编译／预演；本版 SolidWorks 原生验收未运行。活动机构的原生完整运动基仍不可用，语义拓扑与标准实体螺纹等任务仍开放。历史原生证据属于原版本，工程图导出不在修复范围。详见[随包能力清单]({link})及 `cad_get_capabilities`。'
    elif manifest.get('offline_modeling_boundaries'):
        body=f'Current local source version: `{version}`; revision: `{revision}`. This offline update adds bounded center blind-hole/inward-shell text and independent geometry contracts, plus complete relative instantaneous mobility bases and coordinate/identity/saved-readback contracts. Equation/configuration repairs are retained. Validation covers compilation, core, fake ports and MCP compile/dry-run only; native acceptance was not run. Complete native moving-mechanism bases, semantic topology and standard physical threads remain open. Historical native evidence belongs to its original revisions; drawing export is outside this repair scope. Read the [bundled capability manifest]({link}) and `cad_get_capabilities`.'
    elif manifest.get('design_intent_authoring') and zh:
        body=f'当前本地源码版本：`{version}`；修订：`{revision}`。本轮离线增加方程／全局变量／配置的类型化合同、依赖与单位检查、配置尺寸隔离及保存读回执行代码。验证仅包含编译、核心和模拟原生接口测试；本版未执行 SolidWorks 原生建模、保存重开或几何验收。此前 `.4` 等版本原生证据按原版本保留，不代表本版验证。工程图导出不在修复范围。详见[随包能力清单]({link})及 `cad_get_capabilities`。'
    elif manifest.get('design_intent_authoring'):
        body=f'Current local source version: `{version}`; revision: `{revision}`. This offline update adds typed equations/global variables/configurations, dependency and unit checks, configuration isolation and saved-readback execution code. Validation consists of compilation, core and fake-native-port tests only. Native authoring, save/reopen and geometry acceptance were not run for this version. Prior native evidence remains attached to its original versions, including `.4`; drawing export is outside this repair scope. Read the [bundled capability manifest]({link}) and `cad_get_capabilities`.'
    elif manifest.get('engineering_reliability') and zh:
        body=f'当前本地版本：`{version}`；修订：`{revision}`。建模可靠性修复覆盖原生参数单位、最终零件重开与实体故障检查、板件／圆柱驱动尺寸、独立牙深和装配干涉拒绝。本次追加高级原生定义保存读回、持久引用的原生等价比较及独立接缝 G2 采样测量；此前配合与三维草图修复继续保留。原生验收只覆盖能力清单声明的案例；Boundary 创建和 Curvature Fill 约束仍未通过，工程图未在本轮验收。能力与证据范围见[随包能力清单]({link})及 `cad_get_capabilities`。'
    elif manifest.get('engineering_reliability'):
        body=f'Current local version: `{version}`; revision: `{revision}`. Modeling reliability covers native parameter units, final part reopen and body faults, driving template dimensions, separate thread depth and assembly interference rejection. This update adds saved advanced-definition readback, native persistent-ID equality and independent sampled G2 seam measurements; prior mate and spatial sketch fixes are retained. Native validation covers only declared fixtures; Boundary creation and Curvature Fill controls still fail, and drawing export is not recertified. Read the [bundled capability manifest]({link}) and `cad_get_capabilities` for evidence and limits.'
    elif manifest.get('localization') and zh:
        body=f'当前本地版本：`{version}`；修订：`{revision}`。默认简体中文：设计树生成名称、工程图、工具说明及诊断。协议标识和显式用户名称保持原值，旧文件不自动迁移。本轮中文化验收范围以[随包能力清单]({link})和 `cad_get_capabilities` 为准；此前复杂建模结果属于 `0.6.0+modeling.20261001.4`，不代表本轮重新验收。'
    elif manifest.get('localization'):
        body=f'Current local version: `{version}`; revision: `{revision}`. Generated feature names, drawing text, tool descriptions and diagnostics default to Simplified Chinese. Protocol identifiers and explicit user names remain invariant; existing artifacts are not migrated. Read the [bundled capability manifest]({link}) and `cad_get_capabilities` for this localization validation scope. Earlier complex-modeling evidence belongs to `0.6.0+modeling.20261001.4` and is not recertified by this update.'
    elif manifest.get('complex_modeling') and zh:
        body=f'当前本地版本：`{version}`；修订：`{revision}`。本轮增加中心线放样、导向影响范围、端部切向长度、三维角点校验和裁剪到加厚的体归属衔接。五个新复杂案例通过原生创建与保存后读回；此前模型未重跑。新 Boundary 案例未通过，G2 未重新认证。能力与具体证据范围以[随包能力清单]({link})和 `cad_get_capabilities` 为准。'
    elif manifest.get('complex_modeling'):
        body=f'Current local version: `{version}`; revision: `{revision}`. This revision adds centerline lofts, guide influence, tangent lengths, spatial corner checks and trim-to-thicken body ownership. Five new complex fixtures passed native creation and saved readback; previous fixtures were not rerun. The new Boundary fixture failed and G2 was not recertified. Read the [bundled capability manifest]({link}) and `cad_get_capabilities` for exact evidence and limits.'
    elif manifest.get('modeling_upgrade') and zh:
        body=f'当前本地版本：`{version}`；修订：`{revision}`。本轮新增高级特征控制、三维曲线、原生螺旋线、实体选择过滤与边/接缝验证，并对声明的建模样例运行原生创建、重建、保存后读回。Curvature 填充尚未通过原生约束读回；工程图导出未在本轮验收。能力与具体证据范围以[随包能力清单]({link})和 `cad_get_capabilities` 为准。'
    elif manifest.get('modeling_upgrade'):
        body=f'Current local version: `{version}`; revision: `{revision}`. This upgrade adds advanced feature controls, spatial curves, native helices, entity filters and edge/seam verification, with native creation/rebuild/saved readback for the declared modeling fixtures. Curvature Fill has not passed native constraint readback. Drawing export was not recertified in this round. Read the [bundled capability manifest]({link}) and `cad_get_capabilities` for exact evidence and limits.'
    elif zh:
        body=f'当前本地版本：`{version}`；修订：`{revision}`。本轮只进行源码、离线和接口模拟验证，SolidWorks 原生验收未运行。文本单位/需求检查、整件几何清单、可配置工程图分页、执行截止与回执均已加入源码。历史原生测试结果属于其注明的旧版本。能力与验收状态以[随包能力清单]({link})和 `cad_get_capabilities` 为准。'
    else:
        body=f'Current local version: `{version}`; revision: `{revision}`. This revision is validated by source/offline/managed-adapter checks; native SolidWorks acceptance is not run. It adds text quantity/requirement checks, whole-model inventories, configurable drawing pagination, execution deadlines and receipts. Historical native evidence belongs to its stated earlier version. Read the [bundled capability manifest]({link}) and `cad_get_capabilities` for the authoritative scope.'
    expected=start+'\n'+body+'\n'+f'<!-- Capability manifest SHA256: {digest} -->'+'\n'+end
    if not path.is_file():errors.append(f'Missing contract document: {path}');continue
    content=path.read_text(encoding='utf-8')
    existing=re.search(re.escape(start)+r'.*?'+re.escape(end),content,re.S)
    if args.write_docs:
        if existing:content=content[:existing.start()]+expected+content[existing.end():]
        else:
            at=content.find('\n',content.find('# Auto SolidWorks')) if path.name=='SKILL.md' else content.find('\n')
            content=content[:at+1]+'\n'+expected+'\n'+content[at+1:]
        path.write_text(content,encoding='utf-8')
    elif existing is None or existing.group()!=expected:errors.append(f'Document contract drift: {path}; run --write-docs')
if args.runtime:
    receipt_path=plugin/'runtime/build-identity.json'
    if not receipt_path.is_file():errors.append('Missing runtime build identity; rebuild before packaging.')
    else:
        receipt=json.loads(receipt_path.read_text(encoding='utf-8-sig'))
        current={p.relative_to(root).as_posix():hashlib.sha256(p.read_bytes()).hexdigest()
                 for p in (root/'src').rglob('*') if p.is_file() and p.suffix in ('.cs','.csproj','.props','.targets') and not {'bin','obj'}&set(p.parts)}
        for name in ('Directory.Build.props','Directory.Build.targets','global.json','NuGet.Config'):
            path=root/name
            if path.is_file():current[name]=hashlib.sha256(path.read_bytes()).hexdigest()
        current[manifest_path.relative_to(root).as_posix()]=digest
        if receipt.get('source_hashes')!=current:errors.append('Runtime/source identity drift; rebuild before packaging.')
        for relative,expected_hash in receipt.get('runtime_hashes',{}).items():
            path=plugin/relative
            if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest()!=expected_hash:errors.append('Runtime binary identity drift: '+relative)
        if not receipt.get('runtime_hashes'):errors.append('Empty runtime identity; rebuild before packaging.')
if errors:raise SystemExit('\n'.join(errors))
print(json.dumps({'version':version,'revision':revision,'contract_documents':len(docs),'status':'passed'}))
