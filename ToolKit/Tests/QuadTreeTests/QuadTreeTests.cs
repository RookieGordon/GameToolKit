using System.Collections.Generic;
using System.Reflection;
using Xunit;
using Unity.Mathematics;
using ToolKit.DataStructure;
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
            var center = new float2(_random.NextSingle() * (max.x - min.x), _random.NextSingle() * (max.y - min.y));
            var size = new float2((int)(_random.NextSingle() * 10), (int)(_random.NextSingle() * 10));
            _box = new AABBBox(center, size, false);
        }

        public AABBBox GetBoundaryBox() => _box;
    }

    private readonly ITestOutputHelper _testOutputHelper;
    private static Func<AABBBox, int, AABBBox> _computeBoxFunc;

    public QuadTreeTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        MethodInfo computeBox =
            typeof(QuadTree<MockBoundable>).GetMethod("_ComputeBox", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(computeBox);
        _computeBoxFunc = (Func<AABBBox, int, AABBBox>)computeBox.CreateDelegate(typeof(Func<AABBBox, int, AABBBox>));
    }

    private static int _ComputeBoxAddress(AABBBox rootBox, AABBBox box)
    {
        var address = 0;
        _DFS(rootBox, box, 0, ref address);
        return address;
    }

    private static void _DFS(AABBBox nodeBox, AABBBox queryBox, int idx,
        ref int address)
    {
        if (nodeBox.Contains(queryBox))
        {
            address = address * 10 + idx;
            for (int i = 0; i < 4; i++)
            {
                var childBox = _computeBoxFunc(nodeBox, i);
                _DFS(childBox, queryBox, i, ref address);
            }
        }
    }

    [Fact]
    public void AddValueStoresItem()
    {
        var rootBox = new AABBBox(new float2(0, 0), new float2(100, 100));
        var tree = new QuadTree<MockBoundable>(rootBox);
        for (int i = 0; i < 10; i++)
        {
            var item = new MockBoundable(rootBox.Min, rootBox.Max);
            tree.Add(item);
            var node = tree.Find(item);
            Assert.NotNull(node);
            var address = _ComputeBoxAddress(rootBox, item.GetBoundaryBox());
            if (node.Address != address)
            {
                address = _ComputeBoxAddress(rootBox, item.GetBoundaryBox());
            }

            Assert.Equal(address, node.Address);
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