/*
 * TraversalHelper 功能测试
 * 测试框架: NUnit + Unity Test Runner
 */

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ToolKit.DataStructure;

namespace Tests.TraversalTest
{
    /// <summary>
    /// 测试用的树节点
    /// </summary>
    public class TestTreeNode : ITraversableNode
    {
        public int Value { get; }
        private readonly List<ITraversableNode> _children = new List<ITraversableNode>();

        public int Count => _children.Count;

        public TestTreeNode(int value)
        {
            Value = value;
        }

        public ITraversableNode GetChild(int index)
        {
            if (index < 0 || index >= _children.Count)
                return null;
            return _children[index];
        }

        public void AddChild(TestTreeNode child)
        {
            _children.Add(child);
        }

        public override string ToString() => Value.ToString();
    }

    // ============================================================
    //  辅助：构建测试树
    // ============================================================
    //
    //        1
    //       / \
    //      2   3
    //     / \   \
    //    4   5   6
    //

    public static class TreeBuilder
    {
        /// <summary>
        /// 构建一棵普通二叉树用于测试
        /// </summary>
        public static TestTreeNode BuildStandardTree()
        {
            var n1 = new TestTreeNode(1);
            var n2 = new TestTreeNode(2);
            var n3 = new TestTreeNode(3);
            var n4 = new TestTreeNode(4);
            var n5 = new TestTreeNode(5);
            var n6 = new TestTreeNode(6);

            n1.AddChild(n2);
            n1.AddChild(n3);
            n2.AddChild(n4);
            n2.AddChild(n5);
            n3.AddChild(n6);

            return n1;
        }

        /// <summary>
        /// 构建只有一个根节点的树
        /// </summary>
        public static TestTreeNode BuildSingleNodeTree()
        {
            return new TestTreeNode(42);
        }

        /// <summary>
        /// 构建一棵用于中序遍历的二叉搜索树
        ///        4
        ///       / \
        ///      2   6
        ///     / \ / \
        ///    1  3 5  7
        /// </summary>
        public static TestTreeNode BuildBSTTree()
        {
            var n1 = new TestTreeNode(1);
            var n2 = new TestTreeNode(2);
            var n3 = new TestTreeNode(3);
            var n4 = new TestTreeNode(4);
            var n5 = new TestTreeNode(5);
            var n6 = new TestTreeNode(6);
            var n7 = new TestTreeNode(7);

            n4.AddChild(n2);
            n4.AddChild(n6);
            n2.AddChild(n1);
            n2.AddChild(n3);
            n6.AddChild(n5);
            n6.AddChild(n7);

            return n4;
        }
    }

    // ============================================================
    //  一、BFSFindFirst / BFSFindAll 测试
    // ============================================================

    [TestFixture]
    public class BFSTests
    {
        [Test]
        public void BFSFindAll_ReturnsAllInLevelOrder()
        {
            var root = TreeBuilder.BuildStandardTree();

            var result = TraversalHelper.BFSFindAll(root, _ => true);

            var values = result.Cast<TestTreeNode>().Select(n => n.Value).ToList();
            Assert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, values.ToArray());
        }

        [Test]
        public void BFSFindFirst_ReturnsFirstMatch()
        {
            var root = TreeBuilder.BuildStandardTree();

            // 查找值 > 1 的第一个节点（BFS 顺序应该是 2）
            var found = TraversalHelper.BFSFindFirst(root, n => ((TestTreeNode)n).Value > 1);

            Assert.IsNotNull(found);
            Assert.AreEqual(2, ((TestTreeNode)found).Value);
        }

        [Test]
        public void BFSFindFirst_NoMatch_ReturnsNull()
        {
            var root = TreeBuilder.BuildStandardTree();

            var found = TraversalHelper.BFSFindFirst(root, n => ((TestTreeNode)n).Value > 100);

            Assert.IsNull(found);
        }

        [Test]
        public void BFSFindAll_NoMatch_ReturnsEmptyList()
        {
            var root = TreeBuilder.BuildStandardTree();

            var result = TraversalHelper.BFSFindAll(root, n => ((TestTreeNode)n).Value > 100);

            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void BFSFindFirst_SingleNode_FindsSelf()
        {
            var root = TreeBuilder.BuildSingleNodeTree();

            var found = TraversalHelper.BFSFindFirst(root, _ => true);

            Assert.IsNotNull(found);
            Assert.AreEqual(42, ((TestTreeNode)found).Value);
        }

        [Test]
        public void BFSFindAll_LeafNodes()
        {
            var root = TreeBuilder.BuildStandardTree();

            var result = TraversalHelper.BFSFindAll(root, n => n.Count == 0);

            var values = result.Cast<TestTreeNode>().Select(n => n.Value).OrderBy(v => v).ToList();
            Assert.AreEqual(new[] { 4, 5, 6 }, values.ToArray());
        }

        [Test]
        public void BFSFindFirst_NullRoot_ReturnsNull()
        {
            var found = TraversalHelper.BFSFindFirst(null, _ => true);
            Assert.IsNull(found);
        }

        [Test]
        public void BFSFindAll_NullRoot_ReturnsEmptyList()
        {
            var result = TraversalHelper.BFSFindAll(null, _ => true);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }
    }

    // ============================================================
    //  二、DFSFindFirst / DFSFindAll 测试
    // ============================================================

    [TestFixture]
    public class DFSTests
    {
        [Test]
        public void DFSFindAll_ReturnsAllInPreorder()
        {
            var root = TreeBuilder.BuildStandardTree();

            var result = TraversalHelper.DFSFindAll(root, _ => true);

            var values = result.Cast<TestTreeNode>().Select(n => n.Value).ToList();
            // DFS 前序: 1, 2, 4, 5, 3, 6
            Assert.AreEqual(new[] { 1, 2, 4, 5, 3, 6 }, values.ToArray());
        }

        [Test]
        public void DFSFindFirst_ReturnsFirstMatch()
        {
            var root = TreeBuilder.BuildStandardTree();

            var found = TraversalHelper.DFSFindFirst(root, n => ((TestTreeNode)n).Value == 5);

            Assert.IsNotNull(found);
            Assert.AreEqual(5, ((TestTreeNode)found).Value);
        }

        [Test]
        public void DFSFindFirst_NoMatch_ReturnsNull()
        {
            var root = TreeBuilder.BuildStandardTree();

            var found = TraversalHelper.DFSFindFirst(root, n => ((TestTreeNode)n).Value == 99);

            Assert.IsNull(found);
        }

        [Test]
        public void DFSFindFirst_VisitedPath_RecordsPathToTarget()
        {
            var root = TreeBuilder.BuildStandardTree();
            var path = new Stack<ITraversableNode>();

            // 查找值 == 5 的路径，应为 1 -> 2 -> 5
            var found = TraversalHelper.DFSFindFirst(root, n => ((TestTreeNode)n).Value == 5,
                visitedPath: path);

            Assert.IsNotNull(found);
            var pathValues = path.Select(n => ((TestTreeNode)n).Value).ToList();
            // Stack 是后进先出，所以 Pop 顺序是 5, 2, 1
            Assert.AreEqual(new[] { 5, 2, 1 }, pathValues.ToArray());
        }

        [Test]
        public void DFSFindFirst_StopsEarly()
        {
            var root = TreeBuilder.BuildStandardTree();
            int visitCount = 0;

            // 确认找到后立即终止，不继续遍历
            var found = TraversalHelper.DFSFindFirst(root, n =>
            {
                visitCount++;
                return ((TestTreeNode)n).Value == 4;
            });

            Assert.IsNotNull(found);
            // DFS 顺序: 1, 2, 4 — 应该在第3个节点就终止
            Assert.AreEqual(3, visitCount);
        }

        [Test]
        public void DFSFindFirst_SingleNode_FindsSelf()
        {
            var root = TreeBuilder.BuildSingleNodeTree();

            var found = TraversalHelper.DFSFindFirst(root, _ => true);

            Assert.IsNotNull(found);
            Assert.AreEqual(42, ((TestTreeNode)found).Value);
        }

        [Test]
        public void DFSFindFirst_NullRoot_ReturnsNull()
        {
            var found = TraversalHelper.DFSFindFirst(null, _ => true);
            Assert.IsNull(found);
        }

        [Test]
        public void DFSFindAll_NullRoot_ReturnsEmptyList()
        {
            var result = TraversalHelper.DFSFindAll(null, _ => true);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }
    }

    // ============================================================
    //  三、LevelOrderTraverse 测试
    // ============================================================

    [TestFixture]
    public class LevelOrderTraverseTests
    {
        [Test]
        public void LevelOrderTraverse_ReturnsList_InLevelOrder()
        {
            var root = TreeBuilder.BuildStandardTree();

            var result = TraversalHelper.LevelOrderTraverse(root);

            var values = result.Cast<TestTreeNode>().Select(n => n.Value).ToList();
            Assert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, values.ToArray());
        }

        [Test]
        public void LevelOrderTraverse_ActionIsCalledForEachNode()
        {
            var root = TreeBuilder.BuildStandardTree();
            var handled = new List<int>();

            TraversalHelper.LevelOrderTraverse(root, n => handled.Add(((TestTreeNode)n).Value));

            Assert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, handled.ToArray());
        }

        [Test]
        public void LevelOrderTraverse_SingleNode()
        {
            var root = TreeBuilder.BuildSingleNodeTree();

            var result = TraversalHelper.LevelOrderTraverse(root);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(42, ((TestTreeNode)result[0]).Value);
        }

        [Test]
        public void LevelOrderTraverse_NullRoot_ReturnsEmptyList()
        {
            var result = TraversalHelper.LevelOrderTraverse(null);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void LevelOrderTraverse_NullRoot_ActionDoesNotThrow()
        {
            Assert.DoesNotThrow(() => TraversalHelper.LevelOrderTraverse(null, n => { }));
        }
    }

    // ============================================================
    //  四、PreorderTraverse 测试
    // ============================================================

    [TestFixture]
    public class PreorderTraverseTests
    {
        [Test]
        public void PreorderTraverse_ReturnsList_InPreorder()
        {
            var root = TreeBuilder.BuildStandardTree();

            var result = TraversalHelper.PreorderTraverse(root);

            var values = result.Cast<TestTreeNode>().Select(n => n.Value).ToList();
            // 前序: 根左右 => 1, 2, 4, 5, 3, 6
            Assert.AreEqual(new[] { 1, 2, 4, 5, 3, 6 }, values.ToArray());
        }

        [Test]
        public void PreorderTraverse_ActionIsCalledInOrder()
        {
            var root = TreeBuilder.BuildStandardTree();
            var handled = new List<int>();

            TraversalHelper.PreorderTraverse(root, n => handled.Add(((TestTreeNode)n).Value));

            Assert.AreEqual(new[] { 1, 2, 4, 5, 3, 6 }, handled.ToArray());
        }

        [Test]
        public void PreorderTraverse_NullNode_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => TraversalHelper.PreorderTraverse(null, n => { }));
        }

        [Test]
        public void PreorderTraverse_NullNode_ReturnsEmptyList()
        {
            var result = TraversalHelper.PreorderTraverse(null);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void PreorderTraverse_SingleNode()
        {
            var root = TreeBuilder.BuildSingleNodeTree();

            var result = TraversalHelper.PreorderTraverse(root);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(42, ((TestTreeNode)result[0]).Value);
        }
    }

    // ============================================================
    //  五、InorderTraverse 测试
    // ============================================================

    [TestFixture]
    public class InorderTraverseTests
    {
        [Test]
        public void InorderTraverse_BST_VisitsInSortedOrder()
        {
            var root = TreeBuilder.BuildBSTTree();

            var result = TraversalHelper.InorderTraverse(root);

            var values = result.Cast<TestTreeNode>().Select(n => n.Value).ToList();
            // BST 中序遍历应产生升序序列: 1, 2, 3, 4, 5, 6, 7
            Assert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7 }, values.ToArray());
        }

        [Test]
        public void InorderTraverse_NullNode_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => TraversalHelper.InorderTraverse(null, n => { }));
        }

        [Test]
        public void InorderTraverse_NullNode_ReturnsEmptyList()
        {
            var result = TraversalHelper.InorderTraverse(null);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void InorderTraverse_SingleNode()
        {
            var root = TreeBuilder.BuildSingleNodeTree();

            var result = TraversalHelper.InorderTraverse(root);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(42, ((TestTreeNode)result[0]).Value);
        }

        [Test]
        public void InorderTraverse_LeafNode_CountZero_DoesNotThrow()
        {
            // 叶子节点 Count=0，确保不会越界
            var leaf = new TestTreeNode(99);

            var result = TraversalHelper.InorderTraverse(leaf);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(99, ((TestTreeNode)result[0]).Value);
        }

        [Test]
        public void InorderTraverse_NodeWithOneChild_DoesNotThrow()
        {
            // 只有左子节点，Count=1，确保不会越界调用 GetChild(1)
            var parent = new TestTreeNode(10);
            var leftChild = new TestTreeNode(5);
            parent.AddChild(leftChild);

            var result = TraversalHelper.InorderTraverse(parent);

            var values = result.Cast<TestTreeNode>().Select(n => n.Value).ToList();
            // 中序: 左(5), 根(10)
            Assert.AreEqual(new[] { 5, 10 }, values.ToArray());
        }
    }

    // ============================================================
    //  六、PostorderTraverse 测试
    // ============================================================

    [TestFixture]
    public class PostorderTraverseTests
    {
        [Test]
        public void PostorderTraverse_ReturnsList_InPostorder()
        {
            var root = TreeBuilder.BuildStandardTree();

            var result = TraversalHelper.PostorderTraverse(root);

            var values = result.Cast<TestTreeNode>().Select(n => n.Value).ToList();
            // 后序: 左右根 => 4, 5, 2, 6, 3, 1
            Assert.AreEqual(new[] { 4, 5, 2, 6, 3, 1 }, values.ToArray());
        }

        [Test]
        public void PostorderTraverse_ActionIsCalledInOrder()
        {
            var root = TreeBuilder.BuildStandardTree();
            var handled = new List<int>();

            TraversalHelper.PostorderTraverse(root, n => handled.Add(((TestTreeNode)n).Value));

            Assert.AreEqual(new[] { 4, 5, 2, 6, 3, 1 }, handled.ToArray());
        }

        [Test]
        public void PostorderTraverse_NullNode_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => TraversalHelper.PostorderTraverse(null, n => { }));
        }

        [Test]
        public void PostorderTraverse_NullNode_ReturnsEmptyList()
        {
            var result = TraversalHelper.PostorderTraverse(null);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void PostorderTraverse_SingleNode()
        {
            var root = TreeBuilder.BuildSingleNodeTree();

            var result = TraversalHelper.PostorderTraverse(root);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(42, ((TestTreeNode)result[0]).Value);
        }
    }
}
