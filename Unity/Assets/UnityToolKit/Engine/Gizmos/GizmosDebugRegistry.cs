/*
 * 功能描述：Gizmos调试可视化注册表
 *           管理所有调试目标的注册与注销，维护可视化状态
 *           作为Runtime静态管理器，不依赖UnityEditor
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityToolKit.Engine.Gizmos
{
    /// <summary>
    /// 调试条目，包含目标对象的可视化状态
    /// </summary>
    [Serializable]
    public class GizmosDebugEntry
    {
        /// <summary>
        /// 唯一编号
        /// </summary>
        public int Id;

        /// <summary>
        /// 调试目标引用
        /// </summary>
        public IGizmosDebugTarget Target;

        /// <summary>
        /// 分配的颜色
        /// </summary>
        public Color Color;

        /// <summary>
        /// 是否启用可视化
        /// </summary>
        public bool Enabled = true;

        public GizmosDebugEntry(int id, IGizmosDebugTarget target, Color color)
        {
            Id = id;
            Target = target;
            Color = color;
            Enabled = true;
        }
    }

    /// <summary>
    /// Gizmos调试可视化注册表（全局静态管理器）
    /// <para>所有调试目标通过此注册表注册/注销，Editor端读取注册表进行可视化</para>
    /// </summary>
    public static class GizmosDebugRegistry
    {
        private static readonly Dictionary<int, GizmosDebugEntry> _entries = new Dictionary<int, GizmosDebugEntry>();
        private static int _nextId = 1;

        /// <summary>
        /// 全局开关
        /// </summary>
        public static bool GlobalEnabled { get; set; } = true;

        /// <summary>
        /// 3D标签字体大小
        /// </summary>
        public static int LabelFontSize { get; set; } = 14;

        /// <summary>
        /// 无形对象占位Gizmo的尺寸
        /// </summary>
        public static float GizmoSize { get; set; } = 0.5f;

        /// <summary>
        /// 是否显示包围盒线框
        /// </summary>
        public static bool ShowBounds { get; set; } = true;

        /// <summary>
        /// 是否显示连接线（从对象到其标签）
        /// </summary>
        public static bool ShowConnectors { get; set; } = true;

        /// <summary>
        /// 注册表变更事件（注册、注销、清除时触发）
        /// </summary>
        public static event Action OnRegistryChanged;

        /// <summary>
        /// 颜色调板，自动循环分配
        /// </summary>
        public static readonly Color[] ColorPalette = new Color[]
        {
            new Color(0.40f, 0.70f, 1.00f, 1f), // 天蓝
            new Color(1.00f, 0.55f, 0.30f, 1f), // 橙色
            new Color(0.45f, 0.90f, 0.45f, 1f), // 绿色
            new Color(1.00f, 0.45f, 0.70f, 1f), // 粉色
            new Color(1.00f, 0.95f, 0.40f, 1f), // 黄色
            new Color(0.70f, 0.50f, 1.00f, 1f), // 紫色
            new Color(0.20f, 0.95f, 0.85f, 1f), // 青色
            new Color(1.00f, 0.35f, 0.35f, 1f), // 红色
            new Color(0.80f, 0.80f, 0.40f, 1f), // 橄榄
            new Color(0.60f, 0.85f, 1.00f, 1f), // 浅蓝
        };

        /// <summary>
        /// 所有已注册条目（只读视图）
        /// </summary>
        public static IReadOnlyDictionary<int, GizmosDebugEntry> Entries => _entries;

        /// <summary>
        /// 已注册条目数量
        /// </summary>
        public static int Count => _entries.Count;

        /// <summary>
        /// 注册调试目标
        /// </summary>
        /// <param name="target">实现了 IGizmosDebugTarget 的对象</param>
        /// <returns>分配的唯一编号ID</returns>
        public static int Register(IGizmosDebugTarget target)
        {
            if (target == null)
            {
                Debug.LogWarning("[GizmosDebug] 尝试注册空目标");
                return -1;
            }

            int id = _nextId++;
            var color = ColorPalette[(id - 1) % ColorPalette.Length];
            _entries[id] = new GizmosDebugEntry(id, target, color);
            OnRegistryChanged?.Invoke();
            return id;
        }

        /// <summary>
        /// 注销调试目标
        /// </summary>
        /// <param name="id">注册时返回的ID</param>
        public static void Unregister(int id)
        {
            if (_entries.Remove(id))
            {
                OnRegistryChanged?.Invoke();
            }
        }

        /// <summary>
        /// 清除所有已注册条目，重置ID计数
        /// </summary>
        public static void Clear()
        {
            _entries.Clear();
            _nextId = 1;
            OnRegistryChanged?.Invoke();
        }

        /// <summary>
        /// 清理已失效的条目（目标被销毁或不再有效）
        /// </summary>
        public static void Cleanup()
        {
            var toRemove = new List<int>();
            foreach (var kvp in _entries)
            {
                if (kvp.Value.Target == null || !kvp.Value.Target.IsAlive)
                    toRemove.Add(kvp.Key);
            }

            foreach (var id in toRemove)
                _entries.Remove(id);

            if (toRemove.Count > 0)
                OnRegistryChanged?.Invoke();
        }

        /// <summary>
        /// 设置条目的启用/禁用状态
        /// </summary>
        public static void SetEnabled(int id, bool enabled)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                entry.Enabled = enabled;
            }
        }

        /// <summary>
        /// 设置条目的自定义颜色
        /// </summary>
        public static void SetColor(int id, Color color)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                entry.Color = color;
            }
        }

        /// <summary>
        /// 切换全部条目的启用状态
        /// </summary>
        public static void SetAllEnabled(bool enabled)
        {
            foreach (var kvp in _entries)
            {
                kvp.Value.Enabled = enabled;
            }
        }

        /// <summary>
        /// 尝试获取指定ID的条目
        /// </summary>
        public static bool TryGetEntry(int id, out GizmosDebugEntry entry)
        {
            return _entries.TryGetValue(id, out entry);
        }
    }
}
