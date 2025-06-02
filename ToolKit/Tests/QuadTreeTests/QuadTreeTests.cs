using System.Reflection;
using Test;
using Unity.Mathematics;
using ToolKit.DataStructure;
using ToolKit.Tools;
using Xunit.Abstractions;
using Random = System.Random;

/*
 * 四叉树有如下规律：
 *  1、以正方形地图为例子，假设地图半长为2^x，
 *     如果一个正方形Value在Depth=0层，那么其值的范围是[0, 2^x],
 *     如果Value在Depth=1层，那么其值的范围是[0, 2^(x-1))或者[2^(x-1), 2^x],
 *     如果Value在Depth=2层，那么其值的范围是[0, 2^(x-2))、[2^(x-2), 2^(x-1))、[2^(x-1), 2^(x-1) + 2^(x-2))、[2^(x-1) + 2^(x-2), 2^x]
 *     以此类推...
 *  2、四叉树中，根节点的Depth=0, Id = 0，其四个子节点，按照象限旋转，第一象限的Depth=1, Id=1，第二象限的Depth=1, Id=2，第三象限的Depth=1, Id=3，第四象限的Depth=1, Id=4
 *     以此类推...
 */

public class QuadTreeTests
{
    /* MockBoundable is a minimal object implementing IBoundable */
    public class MockBoundable : IBoundable
    {
        private Random _random = new Random();
        private AABBBox _box;

        public MockBoundable(float2 min, float2 max)
        {
            var point1 = new float2(_random.NextSingle() * (max.x - min.x), _random.NextSingle() * (max.y - 1 - min.y));
            var point2 = new float2(_random.NextSingle() * (max.x - min.x), _random.NextSingle() * (max.y - 1 - min.y));
            var pass = Math.Abs(point1.x - point2.x) > 1.0f && Math.Abs(point1.y - point2.y) > 1.0f;
            while (!pass)
            {
                point1 = new float2(_random.NextSingle() * (max.x - min.x), _random.NextSingle() * (max.y - 1 - min.y));
                point2 = new float2(_random.NextSingle() * (max.x - min.x), _random.NextSingle() * (max.y - 1 - min.y));
                pass = Math.Abs(point1.x - point2.x) > 1.0f && Math.Abs(point1.y - point2.y) > 1.0f;
            }
            
            var minPoint = math.min(point1, point2);
            var maxPoint = math.max(point1, point2);
            _box = new AABBBox(minPoint, maxPoint);
        }

        public AABBBox GetBoundaryBox() => _box;
    }

    private static Func<AABBBox, int, AABBBox> _computeBoxFunc;

    public QuadTreeTests(ITestOutputHelper testOutputHelper)
    {
        Log.SetLog(new TestLogger(testOutputHelper));
        MethodInfo computeBox =
            typeof(QuadTree<MockBoundable>).GetMethod("_ComputeBox", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(computeBox);
        _computeBoxFunc = (Func<AABBBox, int, AABBBox>)computeBox.CreateDelegate(typeof(Func<AABBBox, int, AABBBox>));
    }

    /// <summary>
    /// 层级验证。如果节点包含checkBox，那么其父节点也一定包含checkBox
    /// </summary>
    private static void _ValidateHierarchy(QuadTree<MockBoundable>.TreeNode node, AABBBox checkBox)
    {
        Assert.True(node.NodeBox.Contains(checkBox));
        for (int i = 0; i < 4; i++)
        {
            var childNode = node.Children[i];
            if (childNode == null)
            {
                continue;
            }
            Assert.True(!childNode.NodeBox.Contains(checkBox));
        }
        
        var curNode = node.Parent;
        while (curNode != null)
        {
            Assert.True(curNode.NodeBox.Contains(checkBox));
            curNode = curNode.Parent;
        }
    }

    /// <summary>
    /// 兄弟节点排斥性验证。如果节点包含checkBox，那么其兄弟节点一定不包含checkBox
    /// </summary>
    private static void _ValidateSiblingExclusion(QuadTree<MockBoundable>.TreeNode node, AABBBox checkBox)
    {
        if (node.Parent == null)
        {
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            var childNode = node.Parent.Children[i];
            if (childNode == node)
            {
                continue;
            }

            Assert.True(!childNode.NodeBox.Contains(checkBox));
        }
    }

    [Fact]
    public void AddValueStoresItem()
    {
        var rootBox = new AABBBox(new float2(0, 0), new float2(100, 100));
        var tree = new QuadTree<MockBoundable>(rootBox);
        for (int i = 0; i < 10000; i++)
        {
            var item = new MockBoundable(rootBox.Min, rootBox.Max);
            var itemBox = item.GetBoundaryBox();
            // Log.Debug($"第{i}个包围盒是{itemBox}");
            tree.Add(item);
            var node = tree.Find(item);
            Assert.NotNull(node);
            _ValidateHierarchy(node, itemBox);
            _ValidateSiblingExclusion(node, itemBox);
        }
    }

    [Fact]
    public void RemoveExistingValueEliminatesItem()
    {
        var rootBox = new AABBBox(new float2(0, 0), new float2(100, 100));
        var tree = new QuadTree<MockBoundable>(rootBox);
        var item = new MockBoundable(rootBox.Min, rootBox.Max);
        tree.Add(item);
        var removed = tree.Remove(item);
        Assert.True(removed);
        Assert.Null(tree.Find(item));
    }

    [Fact]
    public void RemoveNonExistingValueDoesNothing()
    {
        var rootBox = new AABBBox(new float2(0, 0), new float2(100, 100));
        var tree = new QuadTree<MockBoundable>(rootBox);
        var item = new MockBoundable(rootBox.Min, rootBox.Max);
        Assert.False(tree.Remove(item));
    }

    [Fact]
    public void QueryWithinBoundsReturnsExpectedValues()
    {
        var rootBox = new AABBBox(new float2(0, 0), new float2(100, 100));
        var tree = new QuadTree<MockBoundable>(rootBox);
        // var items = new List<MockBoundable> { new MockBoundable(1), new MockBoundable(2) };
        // items.ForEach(x => tree.Add(x));
        // var results = tree.Query(new AABBBox(new float2(0, 0), new float2(50, 50)));
        // Assert.NotEmpty(results);
    }

    [Fact]
    public void AddExceedThresholdTriggersSplit()
    {
        // var tree = new QuadTree<MockBoundable>(new AABBBox(new float2(0, 0), new float2(50, 50)));
        // for (int i = 0; i < 10; i++)
        // {
        //     tree.Add(new MockBoundable(i));
        // }
        //
        // Assert.Null(tree.Find(new MockBoundable(999)));
    }
}