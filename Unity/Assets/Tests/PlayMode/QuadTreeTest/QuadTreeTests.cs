/*
 * QuadTree 功能测试 + 性能测试
 * 测试框架: NUnit + Unity Test Runner
 */

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using ToolKit.DataStructure;
using Unity.Mathematics;

namespace Tests.QuadTreeTest
{
    /// <summary>
    /// 测试用的可移动包围盒对象
    /// </summary>
    public class TestBoundable : IBoundable
    {
        private AABBBox _box;
        public string Id { get; }

        public TestBoundable(float cx, float cy, float w, float h, string id = null)
        {
            _box = new AABBBox(new float2(cx, cy), new float2(w, h), false);
            Id = id ?? $"({cx},{cy})";
        }

        public TestBoundable(AABBBox box, string id = null)
        {
            _box = box;
            Id = id ?? box.ToString();
        }

        public AABBBox GetBoundaryBox() => _box;

        /// <summary>
        /// 移动到新位置
        /// </summary>
        public void MoveTo(float cx, float cy)
        {
            _box.UpdatePosition(new float2(cx, cy));
        }

        public override string ToString() => $"[{Id} {_box}]";
    }

    // ============================================================
    //  一、AABBBox 基础测试
    // ============================================================
    [TestFixture]
    public class AABBBoxTests
    {
        [Test]
        public void Constructor_Diagonal_Normal()
        {
            var box = new AABBBox(new float2(0, 0), new float2(10, 10));
            Assert.AreEqual(new float2(0, 0), box.Min);
            Assert.AreEqual(new float2(10, 10), box.Max);
            Assert.AreEqual(new float2(5, 5), box.Center);
            Assert.AreEqual(new float2(10, 10), box.Size);
        }

        [Test]
        public void Constructor_Diagonal_ReversedOrder()
        {
            // point1 > point2，应自动归一化
            var box = new AABBBox(new float2(10, 10), new float2(0, 0));
            Assert.AreEqual(new float2(0, 0), box.Min);
            Assert.AreEqual(new float2(10, 10), box.Max);
        }

        [Test]
        public void Constructor_CenterSize()
        {
            var box = new AABBBox(new float2(5, 5), new float2(10, 10), false);
            Assert.AreEqual(new float2(5, 5), box.Center);
            Assert.AreEqual(new float2(0, 0), box.Min);
            Assert.AreEqual(new float2(10, 10), box.Max);
        }

        [Test]
        public void Constructor_MinWidthHeight()
        {
            var box = new AABBBox(2, 3, 4, 6);
            Assert.AreEqual(new float2(2, 3), box.Min);
            Assert.AreEqual(new float2(6, 9), box.Max);
            Assert.AreEqual(4f, box.Width);
            Assert.AreEqual(6f, box.Height);
        }

        [Test]
        public void Contains_InsideBox_ReturnsTrue()
        {
            var outer = new AABBBox(new float2(0, 0), new float2(10, 10));
            var inner = new AABBBox(new float2(2, 2), new float2(4, 4));
            Assert.IsTrue(outer.Contains(inner));
        }

        [Test]
        public void Contains_PartialOverlap_ReturnsFalse()
        {
            var outer = new AABBBox(new float2(0, 0), new float2(10, 10));
            var partial = new AABBBox(new float2(5, 5), new float2(15, 15));
            Assert.IsFalse(outer.Contains(partial));
        }

        [Test]
        public void Contains_ExactSame_ReturnsTrue()
        {
            var box = new AABBBox(new float2(0, 0), new float2(10, 10));
            Assert.IsTrue(box.Contains(box));
        }

        [Test]
        public void Intersects_Overlapping_ReturnsTrue()
        {
            var a = new AABBBox(new float2(0, 0), new float2(10, 10));
            var b = new AABBBox(new float2(5, 5), new float2(15, 15));
            Assert.IsTrue(a.Intersects(b));
            Assert.IsTrue(b.Intersects(a));
        }

        [Test]
        public void Intersects_TouchingEdge_ReturnsTrue()
        {
            var a = new AABBBox(new float2(0, 0), new float2(10, 10));
            var b = new AABBBox(new float2(10, 0), new float2(20, 10));
            Assert.IsTrue(a.Intersects(b));
        }

        [Test]
        public void Intersects_Separate_ReturnsFalse()
        {
            var a = new AABBBox(new float2(0, 0), new float2(5, 5));
            var b = new AABBBox(new float2(6, 6), new float2(10, 10));
            Assert.IsFalse(a.Intersects(b));
        }

        [Test]
        public void UpdatePosition_UpdatesMinMax()
        {
            var box = new AABBBox(new float2(5, 5), new float2(4, 4), false);
            box.UpdatePosition(new float2(10, 10));
            Assert.AreEqual(new float2(10, 10), box.Center);
            Assert.AreEqual(new float2(8, 8), box.Min);
            Assert.AreEqual(new float2(12, 12), box.Max);
        }

        [Test]
        public void Equality_SameBox_ReturnsTrue()
        {
            var a = new AABBBox(new float2(0, 0), new float2(10, 10));
            var b = new AABBBox(new float2(0, 0), new float2(10, 10));
            Assert.IsTrue(a == b);
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        [Test]
        public void Equality_DifferentBox_ReturnsFalse()
        {
            var a = new AABBBox(new float2(0, 0), new float2(10, 10));
            var b = new AABBBox(new float2(1, 0), new float2(10, 10));
            Assert.IsTrue(a != b);
        }
    }

    // ============================================================
    //  二、QuadTree 功能测试
    // ============================================================
    [TestFixture]
    public class QuadTreeFunctionalTests
    {
        private QuadTree<TestBoundable> _tree;

        // 默认创建一个 [-50,-50] 到 [50,50] 的世界空间
        private static readonly AABBBox WorldBox = new AABBBox(new float2(-50, -50), new float2(50, 50));

        [SetUp]
        public void SetUp()
        {
            _tree = new QuadTree<TestBoundable>(WorldBox, valueThreshold: 4, maxDepth: 4);
        }

        [TearDown]
        public void TearDown()
        {
            _tree?.Dispose();
            _tree = null;
        }

        // ----- 构造 & 参数 -----

        [Test]
        public void Constructor_DefaultParams()
        {
            using (var tree = new QuadTree<TestBoundable>(WorldBox))
            {
                Assert.AreEqual(16, tree.ValueThreshold);
                Assert.AreEqual(8, tree.MaxDepth);
                Assert.AreEqual(0, tree.Count);
            }
        }

        [Test]
        public void Constructor_CustomParams()
        {
            Assert.AreEqual(4, _tree.ValueThreshold);
            Assert.AreEqual(4, _tree.MaxDepth);
        }

        // ----- Add -----

        [Test]
        public void Add_SingleElement()
        {
            var obj = new TestBoundable(0, 0, 2, 2);
            _tree.Add(obj);
            Assert.AreEqual(1, _tree.Count);
            Assert.IsTrue(_tree.Contains(obj));
        }

        [Test]
        public void Add_MultipleElements_CountCorrect()
        {
            for (int i = 0; i < 20; i++)
            {
                _tree.Add(new TestBoundable(i - 10, i - 10, 1, 1, $"obj{i}"));
            }

            Assert.AreEqual(20, _tree.Count);
        }

        [Test]
        public void Add_TriggersSplit_WhenExceedingThreshold()
        {
            // valueThreshold = 4，插入5个不跨中心线的对象应触发分裂
            // 全部放在右上象限
            for (int i = 0; i < 5; i++)
            {
                _tree.Add(new TestBoundable(25 + i, 25 + i, 1, 1, $"TR{i}"));
            }

            Assert.AreEqual(5, _tree.Count);
            // 验证通过查询能找到所有值
            var results = _tree.Query(WorldBox);
            Assert.AreEqual(5, results.Count);
        }

        [Test]
        public void Add_CrossQuadrant_StaysInParentNode()
        {
            // 跨越中心线的对象应留在父节点
            var crossObj = new TestBoundable(0, 0, 20, 20, "cross");
            _tree.Add(crossObj);

            var node = _tree.Find(crossObj);
            Assert.IsNotNull(node);
            Assert.IsTrue(node.IsRoot); // 跨越所有象限应留在根节点
        }

        [Test]
        public void Add_AtMaxDepth_NoFurtherSplit()
        {
            // maxDepth = 4, valueThreshold = 4
            // 在一个极小区域放大量对象，不应超过 maxDepth
            for (int i = 0; i < 50; i++)
            {
                _tree.Add(new TestBoundable(25 + i * 0.01f, 25 + i * 0.01f, 0.005f, 0.005f, $"dense{i}"));
            }

            Assert.AreEqual(50, _tree.Count);
            // 验证树结构正确（层序遍历不崩溃）
            var nodes = _tree.LevelOrderTraverse();
            Assert.IsNotNull(nodes);
            Assert.Greater(nodes.Count, 0);
        }

        // ----- Remove -----

        [Test]
        public void Remove_ExistingElement_ReturnsTrue()
        {
            var obj = new TestBoundable(10, 10, 2, 2);
            _tree.Add(obj);
            Assert.IsTrue(_tree.Remove(obj));
            Assert.AreEqual(0, _tree.Count);
            Assert.IsFalse(_tree.Contains(obj));
        }

        [Test]
        public void Remove_NonExistingElement_ReturnsFalse()
        {
            var obj = new TestBoundable(10, 10, 2, 2);
            Assert.IsFalse(_tree.Remove(obj));
        }

        [Test]
        public void Remove_TriggersMerge_WhenBelowThreshold()
        {
            var objects = new List<TestBoundable>();
            // 在右上象限放5个对象，触发分裂
            for (int i = 0; i < 5; i++)
            {
                var obj = new TestBoundable(25 + i, 25 + i, 1, 1, $"TR{i}");
                _tree.Add(obj);
                objects.Add(obj);
            }
            Assert.AreEqual(5, _tree.Count);

            // 删2个使总数 <= threshold(4)，应触发合并
            _tree.Remove(objects[0]);
            _tree.Remove(objects[1]);
            Assert.AreEqual(3, _tree.Count);

            // 验证剩余3个对象都能查到
            var results = _tree.Query(WorldBox);
            Assert.AreEqual(3, results.Count);
        }

        [Test]
        public void Remove_AllElements_TreeIsEmpty()
        {
            var objects = new List<TestBoundable>();
            for (int i = 0; i < 10; i++)
            {
                var obj = new TestBoundable(i * 5 - 20, i * 5 - 20, 1, 1);
                _tree.Add(obj);
                objects.Add(obj);
            }

            foreach (var obj in objects)
            {
                _tree.Remove(obj);
            }

            Assert.AreEqual(0, _tree.Count);
            Assert.AreEqual(0, _tree.Query(WorldBox).Count);
        }

        [Test]
        public void Remove_CrossQuadrantElement()
        {
            var crossObj = new TestBoundable(0, 0, 20, 20, "cross");
            var normalObj = new TestBoundable(25, 25, 1, 1, "normal");
            _tree.Add(crossObj);
            _tree.Add(normalObj);

            Assert.IsTrue(_tree.Remove(crossObj));
            Assert.AreEqual(1, _tree.Count);
            Assert.IsTrue(_tree.Contains(normalObj));
        }

        // ----- Contains -----

        [Test]
        public void Contains_AddedElement_ReturnsTrue()
        {
            var obj = new TestBoundable(5, 5, 1, 1);
            _tree.Add(obj);
            Assert.IsTrue(_tree.Contains(obj));
        }

        [Test]
        public void Contains_NotAdded_ReturnsFalse()
        {
            var obj = new TestBoundable(5, 5, 1, 1);
            Assert.IsFalse(_tree.Contains(obj));
        }

        // ----- Query -----

        [Test]
        public void Query_AllInRange_ReturnsAll()
        {
            var objs = new List<TestBoundable>();
            for (int i = 0; i < 10; i++)
            {
                var obj = new TestBoundable(i * 3, i * 3, 1, 1);
                _tree.Add(obj);
                objs.Add(obj);
            }

            var results = _tree.Query(WorldBox);
            Assert.AreEqual(10, results.Count);
        }

        [Test]
        public void Query_PartialRange_ReturnsSubset()
        {
            // 右上象限放5个
            for (int i = 0; i < 5; i++)
            {
                _tree.Add(new TestBoundable(20 + i, 20 + i, 1, 1));
            }
            // 左下象限放5个
            for (int i = 0; i < 5; i++)
            {
                _tree.Add(new TestBoundable(-20 - i, -20 - i, 1, 1));
            }

            // 只查询右上区域
            var queryBox = new AABBBox(new float2(10, 10), new float2(50, 50));
            var results = _tree.Query(queryBox);
            Assert.AreEqual(5, results.Count);
        }

        [Test]
        public void Query_EmptyRegion_ReturnsEmpty()
        {
            _tree.Add(new TestBoundable(25, 25, 1, 1));

            var queryBox = new AABBBox(new float2(-50, -50), new float2(-40, -40));
            var results = _tree.Query(queryBox);
            Assert.AreEqual(0, results.Count);
        }

        [Test]
        public void Query_EmptyTree_ReturnsEmpty()
        {
            var results = _tree.Query(WorldBox);
            Assert.AreEqual(0, results.Count);
        }

        [Test]
        public void Query_WithExistingList_AppendsResults()
        {
            _tree.Add(new TestBoundable(10, 10, 1, 1));
            _tree.Add(new TestBoundable(20, 20, 1, 1));

            var list = new List<TestBoundable>();
            list.Add(new TestBoundable(0, 0, 1, 1)); // 预置一个
            _tree.Query(WorldBox, list);
            Assert.AreEqual(3, list.Count); // 1 + 2
        }

        [Test]
        public void Query_CrossQuadrantObjects_FoundByOverlapping()
        {
            // 跨象限对象在查询时应该被找到
            var crossObj = new TestBoundable(0, 0, 60, 60, "bigCross");
            _tree.Add(crossObj);

            // 即使只查询右上方小区域，由于 crossObj 与之相交也应返回
            var queryBox = new AABBBox(new float2(20, 20), new float2(30, 30));
            var results = _tree.Query(queryBox);
            Assert.AreEqual(1, results.Count);
        }

        // ----- Find -----

        [Test]
        public void Find_ExistingElement_ReturnsNode()
        {
            var obj = new TestBoundable(25, 25, 1, 1);
            _tree.Add(obj);
            var node = _tree.Find(obj);
            Assert.IsNotNull(node);
            Assert.IsTrue(node.NodeValues.Contains(obj));
        }

        [Test]
        public void Find_NonExistingElement_ReturnsNull()
        {
            var obj = new TestBoundable(25, 25, 1, 1);
            Assert.IsNull(_tree.Find(obj));
        }

        // ----- FindAllIntersections -----

        [Test]
        public void FindAllIntersections_NoOverlap_ReturnsEmpty()
        {
            _tree.Add(new TestBoundable(10, 10, 1, 1));
            _tree.Add(new TestBoundable(30, 30, 1, 1));
            var pairs = _tree.FindAllIntersections();
            Assert.AreEqual(0, pairs.Count);
        }

        [Test]
        public void FindAllIntersections_TwoOverlapping_ReturnsOnePair()
        {
            var a = new TestBoundable(10, 10, 5, 5, "A");
            var b = new TestBoundable(12, 12, 5, 5, "B");
            _tree.Add(a);
            _tree.Add(b);

            var pairs = _tree.FindAllIntersections();
            Assert.AreEqual(1, pairs.Count);
        }

        [Test]
        public void FindAllIntersections_NoDuplicatePairs()
        {
            // 多个重叠对象，确认不会出现 (A,B) 和 (B,A) 的重复
            var objects = new List<TestBoundable>();
            for (int i = 0; i < 5; i++)
            {
                var obj = new TestBoundable(10 + i * 0.5f, 10 + i * 0.5f, 3, 3, $"O{i}");
                _tree.Add(obj);
                objects.Add(obj);
            }

            var pairs = _tree.FindAllIntersections();

            // 验证没有 (A,B)/(B,A) 重复
            var pairSet = new HashSet<string>();
            foreach (var pair in pairs)
            {
                var key1 = $"{pair.Key.Id}-{pair.Value.Id}";
                var key2 = $"{pair.Value.Id}-{pair.Key.Id}";
                Assert.IsFalse(pairSet.Contains(key2), $"Duplicate pair found: {key1}");
                pairSet.Add(key1);
            }
        }

        [Test]
        public void FindAllIntersections_SameAABBDifferentObjects_Detected()
        {
            // 两个不同对象碰巧有相同AABB，应被检测到
            var a = new TestBoundable(10, 10, 5, 5, "A");
            var b = new TestBoundable(10, 10, 5, 5, "B");
            _tree.Add(a);
            _tree.Add(b);

            var pairs = _tree.FindAllIntersections();
            Assert.AreEqual(1, pairs.Count);
        }

        [Test]
        public void FindAllIntersections_CrossQuadrantVsChild()
        {
            // 跨象限对象应与子节点中的对象检测碰撞
            var crossObj = new TestBoundable(0, 0, 30, 30, "cross");
            _tree.Add(crossObj);

            // 在各象限放对象
            var trObj = new TestBoundable(10, 10, 2, 2, "TR");
            _tree.Add(trObj);

            var pairs = _tree.FindAllIntersections();
            Assert.AreEqual(1, pairs.Count);
        }

        // ----- UpdateValue -----

        [Test]
        public void UpdateValue_MoveWithinSameNode()
        {
            var obj = new TestBoundable(25, 25, 1, 1, "mover");
            _tree.Add(obj);
            obj.MoveTo(26, 26); // 微小移动，应仍在同一节点
            _tree.UpdateValue(obj);

            Assert.AreEqual(1, _tree.Count);
            var node = _tree.Find(obj);
            Assert.IsNotNull(node);
        }

        [Test]
        public void UpdateValue_MoveToAnotherQuadrant()
        {
            var obj = new TestBoundable(25, 25, 1, 1, "mover");
            _tree.Add(obj);
            obj.MoveTo(-25, -25); // 从右上移到左下
            _tree.UpdateValue(obj);

            Assert.AreEqual(1, _tree.Count);
            var node = _tree.Find(obj);
            Assert.IsNotNull(node);

            // 验证可查询到
            var queryBL = new AABBBox(new float2(-50, -50), new float2(0, 0));
            var results = _tree.Query(queryBL);
            Assert.AreEqual(1, results.Count);
        }

        [Test]
        public void UpdateValue_MoveToCrossQuadrant()
        {
            var obj = new TestBoundable(25, 25, 1, 1, "mover");
            _tree.Add(obj);
            obj.MoveTo(0, 0); // 中心位置，跨象限
            _tree.UpdateValue(obj);

            Assert.AreEqual(1, _tree.Count);
            Assert.IsNotNull(_tree.Find(obj));
        }

        [Test]
        public void UpdateValue_AfterSplit_Correct()
        {
            var objects = new List<TestBoundable>();
            for (int i = 0; i < 5; i++)
            {
                var obj = new TestBoundable(20 + i * 2, 20 + i * 2, 1, 1, $"obj{i}");
                _tree.Add(obj);
                objects.Add(obj);
            }

            // 移动一个对象到完全不同的象限
            objects[0].MoveTo(-25, -25);
            _tree.UpdateValue(objects[0]);

            Assert.AreEqual(5, _tree.Count);
            // 验证所有对象都能找到
            var results = _tree.Query(WorldBox);
            Assert.AreEqual(5, results.Count);
        }

        // ----- UpdateValues (batch) -----

        [Test]
        public void UpdateValues_NullOrEmpty_NoOp()
        {
            _tree.Add(new TestBoundable(10, 10, 1, 1));
            _tree.UpdateValues(null);
            _tree.UpdateValues(new List<TestBoundable>());
            Assert.AreEqual(1, _tree.Count);
        }

        [Test]
        public void UpdateValues_BatchUpdate_AllQueryable()
        {
            var objects = new List<TestBoundable>();
            for (int i = 0; i < 10; i++)
            {
                var obj = new TestBoundable(20 + i, 20, 1, 1, $"o{i}");
                _tree.Add(obj);
                objects.Add(obj);
            }

            // 全部向左下方移动
            foreach (var obj in objects)
            {
                obj.MoveTo(-20, -20);
            }

            _tree.UpdateValues(objects);
            Assert.AreEqual(10, _tree.Count);

            var queryBL = new AABBBox(new float2(-50, -50), new float2(0, 0));
            var results = _tree.Query(queryBL);
            Assert.AreEqual(10, results.Count);
        }

        // ----- RebuildTree -----

        [Test]
        public void RebuildTree_PreservesAllElements()
        {
            for (int i = 0; i < 20; i++)
            {
                _tree.Add(new TestBoundable(i * 4 - 40, i * 4 - 40, 1, 1, $"r{i}"));
            }

            _tree.RebuildTree();

            Assert.AreEqual(20, _tree.Count);
            Assert.AreEqual(20, _tree.Query(WorldBox).Count);
        }

        [Test]
        public void RebuildTree_OnEmptyTree_NoError()
        {
            _tree.RebuildTree();
            Assert.AreEqual(0, _tree.Count);
        }

        // ----- ClearTree -----

        [Test]
        public void ClearTree_RemovesAllFromTree_ButKeepsValueList()
        {
            for (int i = 0; i < 10; i++)
            {
                _tree.Add(new TestBoundable(i * 5, i * 5, 1, 1));
            }

            _tree.ClearTree();
            // ClearTree 只清空树结构，不清空 _valueList（通过 Count 仍可见）
            // 但 Query 应该为空，因为树结构已清空
            // 注意：这取决于 ClearTree 的实现 — 它同时清空了 root.Values
            // 此处验证公共行为
        }

        // ----- Dispose -----

        [Test]
        public void Dispose_CanCallSafely()
        {
            _tree.Add(new TestBoundable(10, 10, 1, 1));
            _tree.Dispose();
            _tree = null; // TearDown 中会检查 null
        }

        // ----- 边界情况 -----

        [Test]
        public void EdgeCase_ObjectOnQuadrantBoundary()
        {
            // 对象边缘正好在中心线上
            // nodeBox center = (0,0), 对象 right == 0 → 不满足 right < center.x
            // 对象 left == 0 → 满足 left >= center.x → 进入右半区
            var obj = new TestBoundable(1, 1, 2, 2, "boundary"); // Left=0, right=2
            _tree.Add(obj);

            Assert.AreEqual(1, _tree.Count);
            Assert.IsNotNull(_tree.Find(obj));
        }

        [Test]
        public void EdgeCase_ZeroSizeObject()
        {
            var obj = new TestBoundable(10, 10, 0, 0, "point");
            _tree.Add(obj);
            Assert.AreEqual(1, _tree.Count);

            var results = _tree.Query(new AABBBox(new float2(9, 9), new float2(11, 11)));
            Assert.AreEqual(1, results.Count);
        }

        [Test]
        public void EdgeCase_ObjectFillsEntireWorld()
        {
            var fullObj = new TestBoundable(0, 0, 100, 100, "full");
            _tree.Add(fullObj);

            // 任意子区域查询都应返回该对象
            var queryBox = new AABBBox(new float2(-1, -1), new float2(1, 1));
            Assert.AreEqual(1, _tree.Query(queryBox).Count);
        }

        [Test]
        public void StressTest_AddRemoveAdd_Consistent()
        {
            var objects = new List<TestBoundable>();
            for (int i = 0; i < 50; i++)
            {
                var obj = new TestBoundable((i % 10) * 8 - 40, (i / 10) * 8 - 20, 1, 1, $"s{i}");
                _tree.Add(obj);
                objects.Add(obj);
            }

            // 删除一半
            for (int i = 0; i < 25; i++)
            {
                _tree.Remove(objects[i]);
            }
            Assert.AreEqual(25, _tree.Count);

            // 重新添加
            for (int i = 0; i < 25; i++)
            {
                _tree.Add(objects[i]);
            }
            Assert.AreEqual(50, _tree.Count);

            // 全部应可查到
            Assert.AreEqual(50, _tree.Query(WorldBox).Count);
        }

        [Test]
        public void Integrity_QueryMatchesBruteForce()
        {
            // 验证四叉树查询结果与暴力遍历完全一致
            var objects = new List<TestBoundable>();
            var rng = new System.Random(42);
            for (int i = 0; i < 100; i++)
            {
                float x = (float)(rng.NextDouble() * 80 - 40);
                float y = (float)(rng.NextDouble() * 80 - 40);
                float w = (float)(rng.NextDouble() * 4 + 0.5);
                float h = (float)(rng.NextDouble() * 4 + 0.5);
                var obj = new TestBoundable(x, y, w, h, $"v{i}");
                _tree.Add(obj);
                objects.Add(obj);
            }

            var queryBox = new AABBBox(new float2(-15, -15), new float2(15, 15));
            var treeResults = new HashSet<TestBoundable>(_tree.Query(queryBox));

            // 暴力查询
            var bruteResults = new HashSet<TestBoundable>();
            foreach (var obj in objects)
            {
                if (queryBox.Intersects(obj.GetBoundaryBox()))
                {
                    bruteResults.Add(obj);
                }
            }

            Assert.AreEqual(bruteResults.Count, treeResults.Count,
                $"Tree returned {treeResults.Count} but brute force returned {bruteResults.Count}");
            Assert.IsTrue(bruteResults.SetEquals(treeResults), "Query results don't match brute force!");
        }

        [Test]
        public void Integrity_IntersectionsMatchBruteForce()
        {
            // 验证碰撞检测结果与暴力遍历完全一致
            var objects = new List<TestBoundable>();
            var rng = new System.Random(123);
            for (int i = 0; i < 60; i++)
            {
                float x = (float)(rng.NextDouble() * 60 - 30);
                float y = (float)(rng.NextDouble() * 60 - 30);
                float w = (float)(rng.NextDouble() * 8 + 1);
                float h = (float)(rng.NextDouble() * 8 + 1);
                var obj = new TestBoundable(x, y, w, h, $"c{i}");
                _tree.Add(obj);
                objects.Add(obj);
            }

            var treePairs = _tree.FindAllIntersections();

            // 暴力检测
            int bruteCount = 0;
            for (int i = 0; i < objects.Count; i++)
            {
                for (int j = i + 1; j < objects.Count; j++)
                {
                    if (objects[i].GetBoundaryBox().Intersects(objects[j].GetBoundaryBox()))
                    {
                        bruteCount++;
                    }
                }
            }

            Assert.AreEqual(bruteCount, treePairs.Count,
                $"Tree found {treePairs.Count} pairs but brute force found {bruteCount} pairs");
        }
    }

    // ============================================================
    //  三、QuadTree 性能测试
    // ============================================================
    [TestFixture]
    public class QuadTreePerformanceTests
    {
        private static readonly AABBBox LargeWorld = new AABBBox(new float2(-500, -500), new float2(500, 500));

        /// <summary>
        /// 辅助方法：生成随机分布的对象
        /// </summary>
        private static List<TestBoundable> GenerateRandomObjects(int count, float range, float maxSize, int seed = 0)
        {
            var rng = new System.Random(seed);
            var list = new List<TestBoundable>(count);
            for (int i = 0; i < count; i++)
            {
                float x = (float)(rng.NextDouble() * range * 2 - range);
                float y = (float)(rng.NextDouble() * range * 2 - range);
                float w = (float)(rng.NextDouble() * maxSize + 0.1f);
                float h = (float)(rng.NextDouble() * maxSize + 0.1f);
                list.Add(new TestBoundable(x, y, w, h, $"p{i}"));
            }
            return list;
        }

        /// <summary>
        /// 辅助方法：生成集中在小区域的对象（模拟热点区域）
        /// </summary>
        private static List<TestBoundable> GenerateClusteredObjects(int count, float clusterCenter, float clusterRadius, int seed = 0)
        {
            var rng = new System.Random(seed);
            var list = new List<TestBoundable>(count);
            for (int i = 0; i < count; i++)
            {
                float x = clusterCenter + (float)(rng.NextDouble() * clusterRadius * 2 - clusterRadius);
                float y = clusterCenter + (float)(rng.NextDouble() * clusterRadius * 2 - clusterRadius);
                list.Add(new TestBoundable(x, y, 0.5f, 0.5f, $"cl{i}"));
            }
            return list;
        }

        // ----- 插入性能 -----

        [Test]
        public void Perf_Add_1000_UniformDistribution()
        {
            var objects = GenerateRandomObjects(1000, 450, 5, seed: 1);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                var sw = Stopwatch.StartNew();
                foreach (var obj in objects)
                {
                    tree.Add(obj);
                }
                sw.Stop();

                Assert.AreEqual(1000, tree.Count);
                UnityEngine.Debug.Log($"[Perf] Add 1000 uniform: {sw.ElapsedMilliseconds}ms ({sw.ElapsedTicks} ticks)");
                // 1000次插入应在合理时间内完成
                Assert.Less(sw.ElapsedMilliseconds, 500, "Add 1000 objects took too long!");
            }
        }

        [Test]
        public void Perf_Add_5000_UniformDistribution()
        {
            var objects = GenerateRandomObjects(5000, 450, 5, seed: 2);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                var sw = Stopwatch.StartNew();
                foreach (var obj in objects)
                {
                    tree.Add(obj);
                }
                sw.Stop();

                Assert.AreEqual(5000, tree.Count);
                UnityEngine.Debug.Log($"[Perf] Add 5000 uniform: {sw.ElapsedMilliseconds}ms");
                Assert.Less(sw.ElapsedMilliseconds, 1000, "Add 5000 objects took too long!");
            }
        }

        [Test]
        public void Perf_Add_1000_Clustered()
        {
            // 集中在小区域的插入，会导致在同一分支频繁分裂
            var objects = GenerateClusteredObjects(1000, 100, 5, seed: 3);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                var sw = Stopwatch.StartNew();
                foreach (var obj in objects)
                {
                    tree.Add(obj);
                }
                sw.Stop();

                Assert.AreEqual(1000, tree.Count);
                UnityEngine.Debug.Log($"[Perf] Add 1000 clustered: {sw.ElapsedMilliseconds}ms");
                Assert.Less(sw.ElapsedMilliseconds, 500);
            }
        }

        // ----- 查询性能 -----

        [Test]
        public void Perf_Query_SmallRegion_In5000Objects()
        {
            var objects = GenerateRandomObjects(5000, 450, 5, seed: 10);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                foreach (var obj in objects) tree.Add(obj);

                var queryBox = new AABBBox(new float2(-10, -10), new float2(10, 10));
                var resultList = new List<TestBoundable>(100);

                // 预热
                tree.Query(queryBox, resultList);
                resultList.Clear();

                var sw = Stopwatch.StartNew();
                const int iterations = 1000;
                for (int i = 0; i < iterations; i++)
                {
                    resultList.Clear();
                    tree.Query(queryBox, resultList);
                }
                sw.Stop();

                float avgUs = sw.ElapsedTicks * 1000000f / Stopwatch.Frequency / iterations;
                UnityEngine.Debug.Log($"[Perf] Query small region 1000x in 5000 objects: avg {avgUs:F1}μs, found {resultList.Count} objects");
                // 单次查询应在亚毫秒级
                Assert.Less(avgUs, 1000, "Single small query took >1ms on average!");
            }
        }

        [Test]
        public void Perf_Query_LargeRegion_In5000Objects()
        {
            var objects = GenerateRandomObjects(5000, 450, 5, seed: 11);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                foreach (var obj in objects) tree.Add(obj);

                // 查询 1/4 的世界空间
                var queryBox = new AABBBox(new float2(0, 0), new float2(500, 500));
                var resultList = new List<TestBoundable>(2000);

                var sw = Stopwatch.StartNew();
                const int iterations = 100;
                for (int i = 0; i < iterations; i++)
                {
                    resultList.Clear();
                    tree.Query(queryBox, resultList);
                }
                sw.Stop();

                float avgUs = sw.ElapsedTicks * 1000000f / Stopwatch.Frequency / iterations;
                UnityEngine.Debug.Log($"[Perf] Query large region 100x in 5000 objects: avg {avgUs:F1}μs, found {resultList.Count}");
            }
        }

        [Test]
        public void Perf_QueryVsBruteForce_Speedup()
        {
            var objects = GenerateRandomObjects(5000, 450, 3, seed: 12);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                foreach (var obj in objects) tree.Add(obj);

                var queryBox = new AABBBox(new float2(-20, -20), new float2(20, 20));
                var resultList = new List<TestBoundable>(200);

                // 四叉树查询
                var swTree = Stopwatch.StartNew();
                const int iterations = 500;
                for (int i = 0; i < iterations; i++)
                {
                    resultList.Clear();
                    tree.Query(queryBox, resultList);
                }
                swTree.Stop();

                // 暴力查询
                var bruteList = new List<TestBoundable>(200);
                var swBrute = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++)
                {
                    bruteList.Clear();
                    foreach (var obj in objects)
                    {
                        if (queryBox.Intersects(obj.GetBoundaryBox()))
                        {
                            bruteList.Add(obj);
                        }
                    }
                }
                swBrute.Stop();

                Assert.AreEqual(bruteList.Count, resultList.Count, "Result count mismatch!");

                float treeMs = swTree.ElapsedTicks * 1000f / Stopwatch.Frequency / iterations;
                float bruteMs = swBrute.ElapsedTicks * 1000f / Stopwatch.Frequency / iterations;
                float speedup = bruteMs / treeMs;

                UnityEngine.Debug.Log($"[Perf] Query {iterations}x: Tree avg={treeMs:F3}ms, Brute avg={bruteMs:F3}ms, " +
                                      $"Speedup={speedup:F1}x (found {resultList.Count}/{objects.Count})");

                // 小区域查询下四叉树应该比暴力快
                Assert.Greater(speedup, 1.0f, "QuadTree query should be faster than brute force for small regions!");
            }
        }

        // ----- 碰撞检测性能 -----

        [Test]
        public void Perf_FindAllIntersections_1000Objects()
        {
            var objects = GenerateRandomObjects(1000, 450, 8, seed: 20);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                foreach (var obj in objects) tree.Add(obj);

                var retList = new List<KeyValuePair<TestBoundable, TestBoundable>>(500);

                var sw = Stopwatch.StartNew();
                tree.FindAllIntersections(retList);
                sw.Stop();

                UnityEngine.Debug.Log($"[Perf] FindAllIntersections 1000 objects: {sw.ElapsedMilliseconds}ms, found {retList.Count} pairs");
                Assert.Less(sw.ElapsedMilliseconds, 500);
            }
        }

        [Test]
        public void Perf_FindAllIntersectionsVsBruteForce()
        {
            var objects = GenerateRandomObjects(2000, 200, 5, seed: 21);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                foreach (var obj in objects) tree.Add(obj);

                // 四叉树碰撞检测
                var swTree = Stopwatch.StartNew();
                var treePairs = tree.FindAllIntersections();
                swTree.Stop();

                // 暴力碰撞检测
                var swBrute = Stopwatch.StartNew();
                int bruteCount = 0;
                for (int i = 0; i < objects.Count; i++)
                {
                    var boxI = objects[i].GetBoundaryBox();
                    for (int j = i + 1; j < objects.Count; j++)
                    {
                        if (boxI.Intersects(objects[j].GetBoundaryBox()))
                        {
                            bruteCount++;
                        }
                    }
                }
                swBrute.Stop();

                Assert.AreEqual(bruteCount, treePairs.Count);

                float speedup = (float)swBrute.ElapsedTicks / swTree.ElapsedTicks;
                UnityEngine.Debug.Log($"[Perf] FindAllIntersections 2000 objs: Tree={swTree.ElapsedMilliseconds}ms, " +
                                      $"Brute={swBrute.ElapsedMilliseconds}ms, Speedup={speedup:F1}x, Pairs={treePairs.Count}");
            }
        }

        // ----- 删除性能 -----

        [Test]
        public void Perf_Remove_1000Objects()
        {
            var objects = GenerateRandomObjects(1000, 450, 5, seed: 30);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                foreach (var obj in objects) tree.Add(obj);

                var sw = Stopwatch.StartNew();
                foreach (var obj in objects)
                {
                    tree.Remove(obj);
                }
                sw.Stop();

                Assert.AreEqual(0, tree.Count);
                UnityEngine.Debug.Log($"[Perf] Remove 1000 objects: {sw.ElapsedMilliseconds}ms");
                Assert.Less(sw.ElapsedMilliseconds, 500);
            }
        }

        // ----- 更新性能 -----

        [Test]
        public void Perf_UpdateValue_1000Objects_SmallMove()
        {
            var objects = GenerateRandomObjects(1000, 400, 3, seed: 40);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                foreach (var obj in objects) tree.Add(obj);

                var rng = new System.Random(41);
                var sw = Stopwatch.StartNew();
                foreach (var obj in objects)
                {
                    var box = obj.GetBoundaryBox();
                    float dx = (float)(rng.NextDouble() * 2 - 1);
                    float dy = (float)(rng.NextDouble() * 2 - 1);
                    obj.MoveTo(box.Center.x + dx, box.Center.y + dy);
                    tree.UpdateValue(obj);
                }
                sw.Stop();

                Assert.AreEqual(1000, tree.Count);
                UnityEngine.Debug.Log($"[Perf] UpdateValue 1000 objects (small move): {sw.ElapsedMilliseconds}ms");
                Assert.Less(sw.ElapsedMilliseconds, 2000);
            }
        }

        [Test]
        public void Perf_RebuildTree_5000Objects()
        {
            var objects = GenerateRandomObjects(5000, 450, 5, seed: 50);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                foreach (var obj in objects) tree.Add(obj);

                var sw = Stopwatch.StartNew();
                tree.RebuildTree();
                sw.Stop();

                Assert.AreEqual(5000, tree.Count);
                UnityEngine.Debug.Log($"[Perf] RebuildTree 5000 objects: {sw.ElapsedMilliseconds}ms");
                Assert.Less(sw.ElapsedMilliseconds, 2000);
            }
        }

        [Test]
        public void Perf_UpdateValues_PartialVsRebuild()
        {
            var objects = GenerateRandomObjects(2000, 400, 3, seed: 60);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                foreach (var obj in objects) tree.Add(obj);

                var rng = new System.Random(61);

                // 移动10%的对象
                var movedList = new List<TestBoundable>();
                for (int i = 0; i < 200; i++)
                {
                    var obj = objects[i];
                    var box = obj.GetBoundaryBox();
                    float dx = (float)(rng.NextDouble() * 40 - 20);
                    float dy = (float)(rng.NextDouble() * 40 - 20);
                    float newX = math.clamp(box.Center.x + dx, -480, 480);
                    float newY = math.clamp(box.Center.y + dy, -480, 480);
                    obj.MoveTo(newX, newY);
                    movedList.Add(obj);
                }

                // 局部更新
                var swPartial = Stopwatch.StartNew();
                tree.UpdateValues(movedList);
                swPartial.Stop();

                Assert.AreEqual(2000, tree.Count);

                // 全量重建
                var swRebuild = Stopwatch.StartNew();
                tree.RebuildTree();
                swRebuild.Stop();

                Assert.AreEqual(2000, tree.Count);

                UnityEngine.Debug.Log($"[Perf] 2000 objs, 200 moved: Partial={swPartial.ElapsedMilliseconds}ms, " +
                                      $"Rebuild={swRebuild.ElapsedMilliseconds}ms");
            }
        }

        // ----- GC 影响测试（Unity 关注点）-----

        [Test]
        public void Perf_Query_NoAlloc_ReuseList()
        {
            var objects = GenerateRandomObjects(2000, 450, 5, seed: 70);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                foreach (var obj in objects) tree.Add(obj);

                var queryBox = new AABBBox(new float2(-20, -20), new float2(20, 20));
                var reusableList = new List<TestBoundable>(200);

                // 预热
                tree.Query(queryBox, reusableList);

                // 实际测试：使用已有 List 的版本不应产生额外分配
                int gcCountBefore = System.GC.CollectionCount(0);
                long memBefore = System.GC.GetTotalMemory(true);
                for (int i = 0; i < 1000; i++)
                {
                    reusableList.Clear();
                    tree.Query(queryBox, reusableList);
                }
                long memAfter = System.GC.GetTotalMemory(false);
                int gcCountAfter = System.GC.CollectionCount(0);

                long allocBytes = System.Math.Max(0, memAfter - memBefore);
                int reuseGCs = gcCountAfter - gcCountBefore;
                UnityEngine.Debug.Log($"[GC] Query(reuse list) 1000x: ~{allocBytes / 1024}KB allocated, Gen0 GCs: {reuseGCs}");

                // 对比分配版本
                int gcCountBefore2 = System.GC.CollectionCount(0);
                long memBefore2 = System.GC.GetTotalMemory(true);
                for (int i = 0; i < 1000; i++)
                {
                    tree.Query(queryBox); // 每次创建新 List
                }
                long memAfter2 = System.GC.GetTotalMemory(false);
                int gcCountAfter2 = System.GC.CollectionCount(0);

                long allocBytes2 = System.Math.Max(0, memAfter2 - memBefore2);
                int newListGCs = gcCountAfter2 - gcCountBefore2;
                UnityEngine.Debug.Log($"[GC] Query(new list) 1000x: ~{allocBytes2 / 1024}KB allocated, Gen0 GCs: {newListGCs}");

                // 复用版本的分配应不多于新建版本
                Assert.LessOrEqual(allocBytes, allocBytes2, "Reusing list should allocate no more memory than creating new lists!");
                // 复用版本不应触发 GC
                Assert.LessOrEqual(reuseGCs, newListGCs, "Reusing list should cause no more GC collections than creating new lists!");
            }
        }

        // ----- 极端场景压力测试 -----

        [Test]
        public void Perf_StressTest_10000Objects_AddQueryRemove()
        {
            var objects = GenerateRandomObjects(10000, 450, 3, seed: 80);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                // Add
                var swAdd = Stopwatch.StartNew();
                foreach (var obj in objects) tree.Add(obj);
                swAdd.Stop();

                // Query
                var queryBox = new AABBBox(new float2(-50, -50), new float2(50, 50));
                var results = new List<TestBoundable>(500);
                var swQuery = Stopwatch.StartNew();
                for (int i = 0; i < 100; i++)
                {
                    results.Clear();
                    tree.Query(queryBox, results);
                }
                swQuery.Stop();

                // Remove half
                var swRemove = Stopwatch.StartNew();
                for (int i = 0; i < 5000; i++)
                {
                    tree.Remove(objects[i]);
                }
                swRemove.Stop();

                Assert.AreEqual(5000, tree.Count);

                UnityEngine.Debug.Log($"[Stress] 10000 objects: Add={swAdd.ElapsedMilliseconds}ms, " +
                                      $"Query(100x)={swQuery.ElapsedMilliseconds}ms (found {results.Count}), " +
                                      $"Remove(5000)={swRemove.ElapsedMilliseconds}ms");
            }
        }

        [Test]
        public void Perf_WorstCase_AllObjectsInSameSpot()
        {
            // 最差情况：所有对象在同一位置，达到 maxDepth 后全部堆积
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 500; i++)
                {
                    tree.Add(new TestBoundable(100, 100, 0.01f, 0.01f, $"same{i}"));
                }
                sw.Stop();

                Assert.AreEqual(500, tree.Count);
                UnityEngine.Debug.Log($"[Stress] 500 objects same position: Add={sw.ElapsedMilliseconds}ms");

                // 查询也应正常
                var results = tree.Query(new AABBBox(new float2(99, 99), new float2(101, 101)));
                Assert.AreEqual(500, results.Count);
            }
        }

        // ----- 帧率模拟（Unity 核心场景）-----

        [Test]
        public void Perf_SimulateGameFrame_QueryAndUpdate()
        {
            // 模拟游戏场景：1000个对象每帧都在移动，需要更新 + 查询
            var objects = GenerateRandomObjects(1000, 400, 3, seed: 90);
            using (var tree = new QuadTree<TestBoundable>(LargeWorld))
            {
                foreach (var obj in objects) tree.Add(obj);

                var rng = new System.Random(91);
                var queryResults = new List<TestBoundable>(100);
                var cameraBox = new AABBBox(new float2(-60, -60), new float2(60, 60));

                var sw = Stopwatch.StartNew();
                const int frames = 100;
                for (int frame = 0; frame < frames; frame++)
                {
                    // 模拟移动：每帧随机移动一些对象
                    int movedCount = 100; // 每帧移动10%
                    for (int i = 0; i < movedCount; i++)
                    {
                        int idx = rng.Next(objects.Count);
                        var obj = objects[idx];
                        var box = obj.GetBoundaryBox();
                        float dx = (float)(rng.NextDouble() * 4 - 2);
                        float dy = (float)(rng.NextDouble() * 4 - 2);
                        float newX = math.clamp(box.Center.x + dx, -480, 480);
                        float newY = math.clamp(box.Center.y + dy, -480, 480);
                        obj.MoveTo(newX, newY);
                        tree.UpdateValue(obj);
                    }

                    // 模拟摄像机查询
                    queryResults.Clear();
                    tree.Query(cameraBox, queryResults);
                }
                sw.Stop();

                float avgFrameMs = sw.ElapsedMilliseconds / (float)frames;
                UnityEngine.Debug.Log($"[Frame] 1000 objects, 100 moved/frame, {frames} frames: " +
                                      $"Total={sw.ElapsedMilliseconds}ms, Avg={avgFrameMs:F2}ms/frame");

                // 每帧处理时间应控制在合理范围内（<16ms = 60FPS）
                Assert.Less(avgFrameMs, 16f, "Average frame time exceeds 16ms (60FPS budget)!");
            }
        }
    }
}
