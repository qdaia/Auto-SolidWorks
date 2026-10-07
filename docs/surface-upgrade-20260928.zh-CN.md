# 本地曲面建模升级：2026-09-28

本次按用户要求不打开 SolidWorks，不执行原生建模验收。开发版标识为 `0.6.0+surface.20260928.1`；代码、运行时、技能及样例均在本地维护，不发布到 GitHub。

## 网络项目候选与采用决策

| 项目 | 实际查阅内容 | 本次采用 | 未采用/未验证 |
|---|---|---|---|
| [CurvesWB](https://github.com/tomate44/CurvesWB) | `freecad/Curves/gordon.py` 的 `compute_intersections`、曲线方向和相交条件 | 两方向显式输入、连接预条件、模型空间容差；实现本地有限四角检查 | 未复制 Gordon、样条重参数化或曲面插值算法；未安装运行 FreeCAD；并非已实现 Gordon 曲面 |
| [CadQuery](https://github.com/CadQuery/cadquery/blob/master/cadquery/occ_impl/shapes.py) | `Face.makeNSidedSurface` 的边界、内部约束、连续性及容差接口 | 填充边界与内部约束分离；暴露明确控制，限制为接触填充 | 未引入 OCC/CadQuery 运行依赖；没有实现相邻支持面的 G1/G2 约束 |
| [BrepGen](https://github.com/samxuxiang/BrepGen) | 官方 README、依赖、数据处理/训练流程 | 记录为生成式 B-Rep 研究候选 | 未下载权重、训练或复现；未将研究模型当作精确参数建模执行器 |
| [ABC-1M / Hugging Face](https://huggingface.co/datasets/ADSKAILab/ABC-1M) | 官方数据卡及文件目录 | 记录为后续独立曲面测试数据候选 | 未下载大数据、未作真实样本验收；不得计入本次通过数 |

CurvesWB 自述为实验性工作台；外部项目的能力声明不等于此插件具备该能力。本次独立编写 C# 适配与验证代码，没有复制第三方几何算法或重新分发其代码。后续若引入代码/权重，应单独核对其许可及部署要求。

## 实际新增能力

1. **SurfaceBoundary**：原生边界曲面，按顺序传入两方向曲线；支持方向 1 首末轮廓的 None / NormalToProfile 控制。
2. **SurfaceFill**：原生接触填充，边界草图与内部约束草图独立传入；分辨率 1–3、优化开关。
3. **SurfaceSweep**：开放/闭合截面沿路径扫描，支持引导线、随路径/保持法向、方向与平滑面合并设置。
4. **SurfaceOffset**：显式面查询、毫米到米转换、反向等距；距离为零时复制面。
5. **预检查**：新曲面和已有曲面放样检查引用类型、先后顺序和依赖；新曲面拒绝重复或混用曲线。缝合拒绝负公差和超范围公差。
6. **四角连接断言**：针对显式坐标系中的 2×2 端点网络，可选择检查模型空间间隙、端点方向和退化角点；坐标可能被求解器改变时拒绝离线认证。不自动移动或吸附曲线。

参数及四份 JSON 草案见 [技能曲面说明](../plugins/auto-solidworks/skills/auto-solidworks/references/surface-modeling.md)。用户的尺寸仍需显式给出；纯曲面草案需声明零实体及预期曲面体数量。

## API 依据与执行边界

调用签名在本机 SolidWorks Interop DLL 上经反射及编译核对；读取 DLL 不启动 SolidWorks。

- [InsertNetBlend2 / 官方边界曲面示例](https://help.solidworks.com/2025/english/api/sldworksapi/Insert_Solid_Body_Boundary_Surface_Feature_Example_CSharp.htm)；本次设置 surface 类型并关闭实体生成。
- [SetNetBlendDirectionData](https://help.solidworks.com/2018/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IFeatureManager~SetNetBlendDirectionData.html)：采用文档定义的全局影响、开放、不修剪方向。
- [InsertFillSurface2](https://help.solidworks.com/2021/English/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IFeatureManager~InsertFillSurface2.html)：边界接触与内部约束分开设置。
- [InsertSweepSurface3](https://help.solidworks.com/2018/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IFeatureManager~InsertSweepSurface3.html)：采用既有执行器所使用的旧扫描 API 家族；官方已标记该方法为旧接口，当前本机签名存在，实际运行仍待原生验收。
- [InsertOffsetSurface](https://help.solidworks.com/2020/english/api/sldworksapi/solidworks.interop.sldworks~solidworks.interop.sldworks.imodeldoc2~insertoffsetsurface.html?format=P&value=)：使用已有模型 API，并保留外层新特征身份检查。

## 验证范围

新增 `SurfaceRegression` 检查编译、JSON 往返、三维间隙、旋转坐标系、错误引用、篡改依赖及非法参数。`SurfaceAdapterRegression` 直接调用生产适配器并用托管接口代理检查选择顺序、标记、参数及单位；这不是原生 COM 验收。`surface_smoke.py` 只允许能力查询、编译和显式 `dryRun=true`，不会调用 health、inspect 或 native build。

本次不验证：SolidWorks 生成成功率、原生特征类型、保存重开、STEP 回读、外形/斑马条纹、G1/G2 连续性、任意 NURBS 或训练模型重建。填充仅为接触条件；NormalToProfile 也不代表与相邻面连续。dry-run 通过不得标记为原生建模通过。

后续授权原生验收时，应逐个运行四个样例并保存真实特征树、重建状态、曲面/实体数、重开证据及导出检查；复杂引导网络、自交、相切和曲率连续性应另设独立用例。
