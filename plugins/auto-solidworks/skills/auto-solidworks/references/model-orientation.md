# 原图方向与草图方向绑定

`source-four-stage-2` 在已有四步流程的尺寸绑定中检查方向。源角度通过表达式驱动实际草图 `frame.x_direction` 时，填写：

```json
"model_orientation": {
  "front_right": {"x": 1, "y": 0, "z": 0},
  "front_up": {"x": 0, "y": 1, "z": 0},
  "state": "Reviewed",
  "evidence": "说明主视图左右、上下与模型坐标的关系，并核对俯视方向",
  "direction_bindings": [
    {"dimension_id": "source_angle", "operation_id": "ear_sketch", "use_end_ray": true}
  ]
}
```

两个基向量须为单位正交向量。令 T=front_right×front_up，则标准视图的（向右，向上）为：Front=(R,U)、Top=(R,-T)、Left=(T,U)、Right=(-T,U)、Bottom=(R,T)、Rear=(-R,U)。第一角和第三角只改变视图排布，不改变这些方向。

每条绑定选择角度路径末端（use_end_ray=true，默认）或始端（false）相对angular_vertex的射线，要求它沿实际草图的正X轴。若草图使用反向局部坐标，应先按真实几何调整草图坐标和参数表达式，不能翻改源图射线来绕过诊断。耳板与孔草图分别有角度依赖时分别绑定。绑定必须属于源角度所指的同一特征。

检查从原页坐标和已验证的page_metrics恢复有方向的射线，与实际草图X轴做带符号点积比较，容差2度。编译后执行验证再次检查实际操作；仅修改摘要不能绕过。缺失映射、反向或镜像方向会阻断编译。

边界：此检查覆盖源角度表达式驱动的草图X轴；不证明主视图基和视图标签本身识读正确，不检查任意轮廓方向、绝对位置或全图等价。辅助/剖切视图方向目前不能用于此绑定。旧版显式工作流保留原合同，只有新上下文默认使用source-four-stage-2。
