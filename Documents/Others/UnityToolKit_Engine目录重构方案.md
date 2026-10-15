# UnityToolKit 目录重构方案（Engine / Runtime 分层）

> 目标：把 `Unity/Assets/UnityToolKit` 按 **侧重点** 重新分层 —— **Engine = 对引擎原有功能的拓展 / 增加**，**Runtime = 游戏开发常用业务的整理 / 封装**；模块内部功能优先（Feature-first），并消除命名空间不一致。

---

## 一、分层定义（核心）

两层的**侧重点不同**，这是划分的根本依据：

| 层 | 程序集 | 侧重点 | 判断标准（放它进来问一句） | 当前内容 |
|---|---|---|---|---|
| **Engine** | `UnityToolKit.Engine` | 对引擎**原有功能**的拓展 / 增加 | "它是在补强 / 拓展引擎本身能做的事吗？" | 扩展方法、特性、GPU 动画技术、Gizmos 调试、引擎类型助手 |
| **Runtime** | `UnityToolKit.Runtime`（新建） | 游戏开发中**常用业务**的整理 / 封装 | "它是把开发里反复出现的业务套路沉淀下来吗？" | 资源加载系统（后续：对象池、事件系统、存档、状态机、计时器等） |
| Editor | `UnityToolKit.Editor` | 仅编辑器工具 | — | 自定义 Inspector、烘焙窗口等 |
| Plugins | `UnityToolKit.Plugins` | 原生平台桥接 | — | Android / iOS |

要点：

- **Engine 面向「引擎能力」维度** —— 它让引擎多出一些原本没有 / 不好用的能力（如 GPU 蒙皮动画、可注册的 Gizmos 调试、Mesh/Vector/Material 扩展、统一时间访问）。判断时只看「它是否在拓展引擎本身」，与是否有状态、是否是大系统**无关**。
- **Runtime 面向「业务封装」维度** —— 它把游戏开发里反复要写的通用业务整理成可复用模块（资源加载、对象池、事件总线、存档、FSM……）。它建立在引擎之上，但关注点是**业务复用**，而非引擎本身。
- 两层之上是引擎无关核心库 `ToolKit/`（纯 C#，命名空间 `ToolKit.*`），Engine 与 Runtime 都构建在它之上。

---

## 二、为什么这样分（基于现状诊断）

旧 `Engine/` 把所有东西塞在一层，混了三种问题：**功能系统与代码类型桶平级**、**业务封装与引擎扩展不分**、**命名空间三种风格并存**。本方案按两条线解决：

1. **纵向分层（按侧重点）**：把「常用业务封装」（资源加载）迁到 Runtime；Engine 只留「引擎能力拓展」。
2. **横向归类（功能优先）**：每层内部按功能模块组织；零散的底层引擎助手（扩展方法、特性、类型工具）收进该层的 `Core/`。

> 关于 `Utility`：它现有命名空间是 `UnityToolKit.Runtime.Utility`，乍看像 Runtime；但逐个看内容 —— `UnityTime`（给引擎 `Time` 补编辑器模式）、`ColorUtil`（`Vector→Color` 转换，用于写顶点纹理）、`BoundsIncrement`（`Bounds` 增量计算）—— **三者都是对引擎类型的薄封装 / 补充，且目前全部只被 GPU 动画烘焙使用**，属于「引擎能力」维度而非「业务封装」。所以它应归 **Engine**，那个 `Runtime.` 命名空间是历史残留，本次一并纠正。

---

## 三、目标结构

```
UnityToolKit/
│
├── Engine/                              UnityToolKit.Engine —— 对引擎原有功能的拓展 / 增加
│   ├── UnityToolKit.Engine.asmdef
│   ├── Core/                            跨模块的底层引擎助手（无业务语义）
│   │   ├── Extensions/                  →  UnityToolKit.Engine.Extensions
│   │   │   ├── MeshExtension.cs
│   │   │   ├── VectorExtension.cs
│   │   │   └── MaterialKeywordExtension.cs      (← 从 Render 移入)
│   │   ├── Attributes/                  →  UnityToolKit.Engine.Attributes
│   │   │   └── InspectorButtonAttribute.cs
│   │   └── Utilities/                   →  UnityToolKit.Engine.Utilities      (← 从 Utility 移入并迁层)
│   │       ├── UnityTime.cs
│   │       ├── ColorUtil.cs
│   │       └── BoundsIncrement.cs
│   ├── Animation/                       →  UnityToolKit.Engine.Animation   (展平掉冗余的 GPUAnimation/ 中间层)
│   │   ├── GPUAnimDefine.cs
│   │   ├── GPUAnimUtility.cs
│   │   ├── GPUAnimationData.cs
│   │   ├── AnimationTex/
│   │   │   ├── AnimationTicker.cs
│   │   │   ├── GPUAnimationController.cs
│   │   │   └── Shader/  (GPUAnimationInclude.hlsl, GPUAnimation_SimpleLit.shader)
│   │   ├── GPUSkinnedMesh/
│   │   │   ├── GPUSkinnedMeshRenderer.cs
│   │   │   └── Shader/  (GPUSkinning.compute)
│   │   └── Docs/  (GPUAnimation_Design.md, GPU动画优化技术原理详解.md, GPU动画优化技术文档.md)
│   └── Gizmos/                          →  UnityToolKit.Engine.Gizmos
│       ├── GizmosDebugRegistry.cs
│       ├── GizmosTargetBase.cs
│       ├── IGizmosDebugTarget.cs
│       └── Docs/  (Gizmos Debug Visualizer设计文档.md)
│
├── Runtime/                             UnityToolKit.Runtime —— 游戏开发常用业务的整理 / 封装
│   ├── UnityToolKit.Runtime.asmdef      (新建)
│   └── Resource/                        →  UnityToolKit.Runtime.Resource   (← 从 Engine/ResourceSystem 迁入)
│       ├── AssetBundleLoader.cs
│       ├── ResourcesLoader.cs
│       └── GameObjectInstanceProvider.cs
│       （后续可在此并列：ObjectPool/、Event/、Save/、StateMachine/ …）
│
├── Editor/   (UnityToolKit.Editor)
└── Plugins/  (UnityToolKit.Plugins)
```

设计要点：

- **Engine 内用 `Core/` 消除混轴。** Extensions / Attributes / Utilities 都是被各模块复用的底层引擎助手，收进 `Core/`；顶层只剩 `Core` + 功能模块（Animation、Gizmos），分类轴单一。
- **`Render` 撤销。** 唯一文件 `MaterialKeywordExtension` 本质是扩展方法，并入 `Core/Extensions`。
- **`Utility` → `Engine/Core/Utilities`。** 内容是引擎类型助手，归 Engine；命名空间从 `Runtime.Utility` 纠正为 `Engine.Utilities`。
- **`Animation` 展平一层。** 现命名空间已是 `...Engine.Animation`（未含 `GPUAnimation`），去掉冗余中间目录后命名空间无需改动，只是文件上移。
- **`ResourceSystem` → `Runtime/Resource`。** 这是典型的「常用业务封装」，迁层 + 改名；无任何外部引用，改动安全。Runtime 目前只此一个模块，但它确立了「业务层」，后续对象池、事件、存档等都进这一层。
- **文档收进各模块 `Docs/`，顶层不再散落 `.md`。**

---

## 四、命名空间映射

| 涉及文件 | 旧命名空间 | 新命名空间 |
|---|---|---|
| `MeshExtension` / `VectorExtension` | `UnityToolKit.Engine.Extension` | `UnityToolKit.Engine.Extensions` |
| `MaterialKeywordExtension` | `UnityToolKit.Engine.Render` | `UnityToolKit.Engine.Extensions` |
| `InspectorButtonAttribute` | `UnityToolKit.Engine`（裸） | `UnityToolKit.Engine.Attributes` |
| `UnityTime` / `ColorUtil` / `BoundsIncrement` | `UnityToolKit.Runtime.Utility` | `UnityToolKit.Engine.Utilities` |
| `Animation/*`（6 个文件） | `UnityToolKit.Engine.Animation` | **不变** |
| `Gizmos/*` | `UnityToolKit.Engine.Gizmos` | **不变** |
| `AssetBundleLoader` / `ResourcesLoader` / `GameObjectInstanceProvider` | `UnityToolKit.Engine.ResourceSystem` | `UnityToolKit.Runtime.Resource` |

> `Core/` 仅作分组容器、不计入命名空间（故是 `Engine.Utilities` 而非 `Engine.Core.Utilities`），与 `Extensions`/`Attributes` 规则一致。

---

## 五、程序集（asmdef）改动

1. **新建 `Runtime/UnityToolKit.Runtime.asmdef`。**
   - `name` = `UnityToolKit.Runtime`，`rootNamespace` = `UnityToolKit.Runtime`。
   - `references`：复制 `Engine.asmdef` 里那 3 个 ToolKit 核心程序集的 GUID（`Resource` 依赖 `ToolKit.Tools.Common`）。
   - `includePlatforms` 留空（运行时全平台）。

2. **`Engine/UnityToolKit.Engine.asmdef`：不变。**

3. **`Editor/UnityToolKit.Editor.asmdef`：不变。**
   - Editor 用到的 `UnityTime`/`ColorUtil`/`BoundsIncrement` 随 Utility 回到 Engine，而 Editor 本就引用 Engine；Editor 不使用 `Resource`。因此 **Editor 无需引用新的 Runtime 程序集**。

依赖方向（核实后）：

```
            ToolKit 核心 (引擎无关)
            ▲                ▲
            │                │
   UnityToolKit.Engine   UnityToolKit.Runtime      ← 两者互相独立，无耦合
            ▲
            │
   UnityToolKit.Editor                              ← 仅引用 Engine（不依赖 Runtime）
```

> Engine 与 Runtime 当前互不依赖。若日后某个 Runtime 业务模块要用 Engine 的扩展，只允许 `Runtime → Engine` 单向引用，**绝不可反向**（引擎扩展层在下，业务封装层在上）。

---

## 六、受影响的引用（需同步修改）

| 改动 | 需要更新的文件 | 操作 |
|---|---|---|
| `Engine.Extension` → `Engine.Extensions` | `Editor/GPUAnimation/AnimationBakerWindow_BoneBaker.cs`、`AnimatonBakerWindow_VertexBaker.cs` | 改 `using` |
| `Engine.Render` → `Engine.Extensions` | `Engine/Animation/.../GPUAnimationController.cs`、`Engine/Animation/GPUAnimUtility.cs` | 改 `using` |
| `InspectorButton` 移入 `Engine.Attributes` | `Editor/Common/CustomInspector.cs`、`Engine/Animation/.../GPUAnimationController.cs` | 新增 `using UnityToolKit.Engine.Attributes;` |
| `Runtime.Utility` → `Engine.Utilities` | `Editor/GPUAnimation/GPUAnimationControllerEditor.cs`、`AnimatonBakerWindow_VertexBaker.cs`、`AnimationBakerWindow_BoneBaker.cs` | 改 `using`（Editor 已引用 Engine，无 asmdef 改动） |
| `ResourceSystem` → `Runtime.Resource` | 仅这 3 个文件自身的 `namespace` 行（**无外部引用方**） | 改 `namespace` |

> 注意 `GPUAnimationController.cs` 用到 `InspectorButton`：它现处于 `Engine.Animation`，过去借「外层命名空间」自动看到裸 `Engine` 下的特性；移入 `Engine.Attributes` 后不再自动可见，**必须显式加 `using`**。

---

## 七、迁移操作步骤（Unity 安全搬运）

1. **关闭 Unity 编辑器**，避免搬运中触发资源重导入。
2. **`.meta` 必须随文件同名同移。** 每个 `.cs`/`.shader`/`.hlsl`/`.compute` 及每个文件夹都有配对 `.meta`（含 GUID）。用 `git mv` 连 `.meta` 一起移动，GUID 不变，材质/Prefab 引用不丢。`Engine/` 下约 39 个 `.meta`。
3. **新建 `Runtime/Resource/` 文件夹**与 `UnityToolKit.Runtime.asmdef`（见第五节）；在 `Engine/Core/` 下建 `Extensions`/`Attributes`/`Utilities`。
4. **改命名空间与 `using`**：按第四、六节两表批量替换。
5. **删除清空后的旧文件夹**（`Extension`/`Render`/`Attribute`/`Utility`/`ResourceSystem`/`Animation/GPUAnimation` 中间层）连同 `.meta`。
6. **重开 Unity**：等待编译，确认 0 报错；若有 GUID 丢失警告，检查是否漏移 `.meta`。
7. **冒烟测试**：GPU 动画烘焙窗口、资源加载、Gizmos 调试各跑一遍。

> 建议在独立分支操作，把「纯文件移动 + asmdef」与「改命名空间」拆成两个 commit，便于 review 与回滚。

---

## 八、附带发现（本次范围外，建议后续）

1. **`Plugins/ImagePicker` 命名空间混用**：同模块内 9 处 `ToolKit.Tools.ImagePicker` 与 6 处 `UnityToolKit.Plugins.ImagePicker` 并存，建议统一为 `UnityToolKit.Plugins.ImagePicker`。
2. **`Editor/GPUAnimation/VertexAnimationTextureBaker.cs` 用了 `namespace InstancedVAT`**，与 `UnityToolKit.Editor.*` 体系不一致，建议归正。
3. **`Editor/` 子目录与功能层镜像**：可把 `Editor/GPUAnimation` 改为 `Editor/Animation`，与 Engine 模块同名，便于对照查找。

---

## 九、重构前后对比（顶层一览）

```
重构前                              重构后
UnityToolKit/                       UnityToolKit/
└── Engine/                         ├── Engine/        拓展/增加引擎能力
    ├── Animation/                  │   ├── Core/      (Extensions, Attributes, Utilities)
    ├── Attribute/      ┐           │   ├── Animation/
    ├── Extension/      │ 类型桶     │   └── Gizmos/
    ├── Render/         │ 与功能     ├── Runtime/       常用业务整理/封装  (新层)
    ├── Utility/        ┘ 混杂       │   └── Resource/  (← Engine 迁出；后续并列 池/事件/存档…)
    ├── Gizmos/                      ├── Editor/
    └── ResourceSystem/             └── Plugins/
（Runtime/ 为空目录）
```

顶层从「单层混杂」变为「**Engine 引擎扩展层 + Runtime 业务封装层**」两个侧重点清晰的层；每层内部 `Core` 地基 + 功能模块，分类轴单一、可预期。新增能力时只需先问一句「这是拓展引擎，还是封装业务？」即可定位归属。
