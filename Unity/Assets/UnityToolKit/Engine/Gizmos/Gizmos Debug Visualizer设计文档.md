# 1. 概述

一套通用的 Scene 视图调试可视化工具，能够将场景中**任意对象**（有形/无形）以统一方式呈现、区分、交互，并通过 EditorWindow 控制面板集中管理。

# 2. 系统架构

```
┌──────────────────────── Runtime 层 ────────────────────────┐
│                                                            │
│  IGizmosDebugTarget          接口：定义可视化目标的契约     │
│         ▲                                                  │
│         │ 实现                                             │
│  GizmosTargetBase            MonoBehaviour便捷基类          │
│                              (自动注册/注销、Renderer检测)  │
│                                                            │
│  GizmosDebugRegistry         全局静态注册表                 │
│  ┌─────────────────────────────────────────┐               │
│  │ Dictionary<int, GizmosDebugEntry>       │               │
│  │ - 自动分配编号 + 颜色                    │               │
│  │ - 全局/单项开关                          │               │
│  │ - 失效条目清理                           │               │
│  │ - 事件通知 OnRegistryChanged             │               │
│  └─────────────────────────────────────────┘               │
└────────────────────────────────────────────────────────────┘

┌──────────────────────── Editor 层 ─────────────────────────┐
│                                                            │
│  GizmosDebugSceneView        Scene视图绘制器               │
│  ┌─────────────────────────────────────────┐               │
│  │ - 3D标签 [#01] Name（带背景色）          │               │
│  │ - 包围盒线框                             │               │
│  │ - 无形对象：十字星占位Gizmo              │               │
│  │ - 点击交互 → 信息浮窗                    │               │
│  │ - 标签连接线                             │               │
│  └─────────────────────────────────────────┘               │
│                                                            │
│  GizmosDebugWindow           EditorWindow控制面板          │
│  ┌─────────────────────────────────────────┐               │
│  │ - 工具栏：全局开关/清理/重置/刷新        │               │
│  │ - 显示设置：字体/大小/线框/连接线        │               │
│  │ - 搜索栏 + 有形/无形过滤Toggle           │               │
│  │ - 条目列表：开关/颜色/定位/Ping          │               │
│  │ - 详情面板：属性+交互按钮                │               │
│  │ - Footer：统计信息                       │               │
│  └─────────────────────────────────────────┘               │
└────────────────────────────────────────────────────────────┘
```

# 3. 核心数据模型

## 3.1 `IGizmosDebugTarget` 接口

| 属性/方法 | 类型 | 说明 |
|---|---|---|
| `DebugDisplayName` | `string` | 3D标签显示名称 |
| `DebugWorldPosition` | `Vector3` | 世界空间锚点 |
| `IsTangible` | `bool` | 是否有形（有Renderer） |
| `DebugBounds` | `Bounds` | 调试包围盒 |
| `DebugDetailInfo` | `string` | 点击交互时显示的详细信息 |
| `IsAlive` | `bool` | 是否仍然有效 |
| `OnDebugInteract()` | `void` | 点击触发的交互回调 |
| `OnDrawDebugGizmos()` | `void` | 自定义Gizmo扩展绘制（默认空实现） |

## 3.2 `GizmosDebugEntry` 数据条目

| 字段 | 说明 |
|---|---|
| `Id` | 自增唯一编号（从1开始） |
| `Target` | `IGizmosDebugTarget` 引用 |
| `Color` | 分配的显示颜色（10色调板循环） |
| `Enabled` | 是否启用可视化显示 |

## 3.3 `GizmosDebugRegistry` 全局参数

| 参数 | 默认值 | 说明 |
|---|---|---|
| `GlobalEnabled` | `true` | 全局显示开关 |
| `LabelFontSize` | `14` | 3D标签字体大小 |
| `GizmoSize` | `0.5` | 无形对象占位Gizmo半径 |
| `ShowBounds` | `true` | 是否显示包围盒线框 |
| `ShowConnectors` | `true` | 是否显示标签连接线 |

# 4. 功能详细设计

## 4.1 显示开关系统

```
全局开关 (GlobalEnabled)
 ├── 条目开关 (Entry.Enabled)  ← 每个目标独立控制
 ├── 全选/全不选  ← 批量操作
 └── EditorWindow关闭 ≠ 关闭显示（SceneView绘制独立于Window）
```

- **全局开关**：EditorWindow 工具栏左侧眼睛图标，一键开关全部可视化
- **单项开关**：条目列表每行左侧 Toggle
- **批量操作**：搜索栏下方"全选"/"全不选"按钮

## 4.2 对象区分系统

每个注册目标获得：

1. **唯一编号** `[#01]` `[#02]` — 全局自增，不会重复
2. **指定颜色** — 从10色调板自动分配，也可在面板中自定义
3. **3D标签** — Scene 视图中显示 `[#01] 对象名` 的带背景色文字标签，悬浮于对象包围盒上方
4. **类型标识** — 无形对象标签旁显示 `◆` 菱形图标，面板中显示`[有形]`/`[无形]`颜色标签

## 4.3 无形对象交互系统

无形对象（`IsTangible = false`）特有的可视化和交互：

| 元素 | 说明 |
|---|---|
| **十字星占位** | 三轴交叉线段，标示世界空间位置 |
| **线框球** | 三个正交圆环，增强空间感知 |
| **点击手柄** | `Handles.Button` 不可见手柄，点击区域覆盖Gizmo范围 |
| **悬停高亮** | 鼠标靠近时显示白色高亮环 |
| **信息浮窗** | 点击后在Scene视图弹出详情面板（名称、类型、详细信息、关闭按钮） |
| **交互回调** | 再次点击同一目标触发 `OnDebugInteract()`，并切换浮窗显示 |
| **Hierarchy联动** | 点击后自动在Hierarchy中选中关联的GameObject |

## 4.4 EditorWindow 控制面板

从菜单 **Tools > ToolKit > Gizmos Debug Visualizer** 打开。

```
┌────────────────────────────────────────┐
│ 👁 │ Gizmos Debug │      清理│重置│🔄 │  ← 工具栏
├────────────────────────────────────────┤
│ ▼ 显示设置                             │
│   标签字体大小  [====●=======] 14      │
│   无形对象Gizmo大小 [=●=======] 0.5    │
│   ☑ 显示包围盒线框                     │
│   ☑ 显示标签连接线                     │
├────────────────────────────────────────┤
│ [🔍 搜索...           ] [有形] [无形]  │
│                          全选 │ 全不选  │
├────────────────────────────────────────┤
│ ▼ 调试目标 (5)                         │
│ ┌────────────────────────────────────┐ │
│ │ ☑ ▎#01 Player        [有形]       │ │
│ │   🎨 (1.0, 2.0, 3.0)    📷 🔗 ❓  │ │
│ ├────────────────────────────────────┤ │
│ │ ☑ ▎#02 Waypoint_A    [无形]       │ │
│ │   🎨 (5.0, 0.0, 8.0)    📷    ❓  │ │
│ ├────────────────────────────────────┤ │
│ │ ... 更多条目 ...                   │ │
│ └────────────────────────────────────┘ │
├────────────────────────────────────────┤
│ 详情 - [#02] Waypoint_A           ×   │
│ ┌────────────────────────────────────┐ │
│ │ 类型: 无形对象                     │ │
│ │ 世界位置: (5.0, 0.0, 8.0)         │ │
│ │ 包围盒: center/size               │ │
│ │ 详细信息:                          │ │
│ │ 这是路径导航点A...                  │ │
│ │ [触发交互]  [聚焦Scene]            │ │
│ └────────────────────────────────────┘ │
├────────────────────────────────────────┤
│    总计:5 | 有形:2 | 无形:3 | 启用:5  │
└────────────────────────────────────────┘
```

## 4.5 注册/注销流程

```
OnEnable                              OnDisable
   │                                     │
   ▼                                     ▼
GizmosDebugRegistry.Register(this)   GizmosDebugRegistry.Unregister(id)
   │                                     │
   ├─ 分配唯一ID                          ├─ 从字典移除
   ├─ 分配颜色                            └─ 触发 OnRegistryChanged
   ├─ 加入 Dictionary
   └─ 触发 OnRegistryChanged
          │
          ▼
   EditorWindow.Repaint()
   SceneView 下一帧绘制新条目
```

# 5. 文件清单

| 文件 | 层级 | 职责 |
|---|---|---|
| IGizmosDebugTarget.cs | Runtime | 可视化目标接口 |
| GizmosDebugRegistry.cs | Runtime | 全局注册表+状态管理 |
| GizmosTargetBase.cs | Runtime | MonoBehaviour便捷基类 |
| GizmosDebugSceneView.cs | Editor | Scene视图3D绘制+交互 |
| GizmosDebugWindow.cs | Editor | EditorWindow控制面板 |
| UnityToolKit.Editor.Gizmos.asmdef | Editor | Editor程序集定义 |

# 6. 使用方式

**方式A — 挂载组件（零代码）：** 将 `GizmosTargetBase` 拖到 GameObject，自动判定有形/无形

**方式B — 实现接口（完全自定义）：** 在任意 MonoBehaviour 上实现 `IGizmosDebugTarget`，在 `OnEnable`/`OnDisable` 中调用 `Register`/`Unregister`

**方式C — 非MonoBehaviour对象：** 纯数据对象也可实现接口并手动注册，需自行在合适时机注销

# 7. 扩展点

| 扩展方向 | 方式 |
|---|---|
| 自定义Gizmo形状 | 重写 `OnDrawDebugGizmos()` |
| 自定义交互行为 | 重写 `OnDebugInteract()` |
| 自定义详情 | 重写 `DebugDetailInfo` getter |
| 分组/标签 | 扩展接口添加 `DebugGroup` 属性，Window中按组折叠 |
| 持久化设置 | 用 `EditorPrefs` 存储 Registry 的全局参数 |
| Runtime HUD | 基于 Registry 数据在 Game 视图叠加 UI |

Made changes.