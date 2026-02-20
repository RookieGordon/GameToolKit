/*
 * 功能描述：四叉树调试信息接口
 *           为可视化调试工具提供树结构数据的非泛型访问方式
 */

using System.Collections.Generic;

namespace ToolKit.DataStructure
{
    /// <summary>
    /// 四叉树节点调试快照
    /// </summary>
    public struct QuadTreeNodeDebugInfo
    {
        /// <summary>
        /// 节点的包围盒
        /// </summary>
        public AABBBox Box;

        /// <summary>
        /// 节点深度（根节点为0）
        /// </summary>
        public int Depth;

        /// <summary>
        /// 该节点直接存储的元素数量
        /// </summary>
        public int ValueCount;

        /// <summary>
        /// 是否为叶子节点
        /// </summary>
        public bool IsLeaf;
    }

    /// <summary>
    /// 四叉树调试信息接口（非泛型）
    /// <para>可视化调试工具通过此接口读取四叉树结构数据，无需知道元素类型T</para>
    /// </summary>
    public interface IQuadTreeDebugInfo
    {
        /// <summary>
        /// 树中存储的元素总数
        /// </summary>
        int ElementCount { get; }

        /// <summary>
        /// 根节点的包围盒范围
        /// </summary>
        AABBBox RootBox { get; }

        /// <summary>
        /// 配置的最大深度
        /// </summary>
        int ConfigMaxDepth { get; }

        /// <summary>
        /// 配置的分裂阈值
        /// </summary>
        int ConfigValueThreshold { get; }

        /// <summary>
        /// 收集所有节点的调试快照，用于可视化绘制
        /// </summary>
        /// <param name="result">输出列表，调用前会被清空</param>
        void CollectDebugNodeInfos(List<QuadTreeNodeDebugInfo> result);

        /// <summary>
        /// 收集所有元素的包围盒，用于在Scene视图中绘制元素位置
        /// </summary>
        /// <param name="result">输出列表，调用前会被清空</param>
        void CollectDebugElementBoxes(List<AABBBox> result);
    }
}
