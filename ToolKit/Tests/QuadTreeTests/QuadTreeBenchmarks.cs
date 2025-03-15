using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using ToolKit.DataStructure;
using Unity.Mathematics; /* Escape any special characters as needed */

public class QuadTreeBenchmarks
{
    /* MockBoundable is a minimal object implementing IBoundable */
    public class MockBoundable : IBoundable
    {
        private AABBBox _box;
        public MockBoundable(int seed)
        {
            float x = seed % 1000;
            float y = (seed * 3) % 1000;
            _box = new AABBBox(new float2(x, y), new float2(x + 1, y + 1));
        }
        public AABBBox GetBoundaryBox() => _box;
    }
    
    private QuadTree<MockBoundable> _quadTree;
    private List<MockBoundable> _items;
    
    public static void TestBenchmark()
    {
        var config = DefaultConfig.Instance
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);
        config.AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance));
        BenchmarkRunner.Run<QuadTreeBenchmarks>(config);
    }

    [GlobalSetup]
    public void Setup()
    {
        /* Create a test boundary box and a list of items to add */
        var boundary = new AABBBox(new float2(0, 0), new float2(1000, 1000));
        _quadTree = new QuadTree<MockBoundable>(boundary);
        _items = new List<MockBoundable>();
        for (int i = 0; i < 10000; i++)
        {
            _items.Add(new MockBoundable(i));
        }
    }

    [Benchmark]
    public void AddBenchmark()
    {
        foreach (var item in _items)
        {
            _quadTree.Add(item);
        }
    }

    [Benchmark]
    public void QueryBenchmark()
    {
        var queryBox = new AABBBox(new float2(250, 250), new float2(750, 750));
        _quadTree.Query(queryBox);
    }
}