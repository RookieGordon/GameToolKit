using ToolKit.Tools.Common;
namespace ToolKit.Tests.ResourceSystem
{
    public class DebugTests
    {
        // 测试点：最小 LoadRef 路径能完成并返回有效凭证。
        [Fact]
        public async Task Debug_OneLoad()
        {
            var mgr = new ResourceManager();
            mgr.RegisterLoader(new FakeLoader());
            var r = await mgr.LoadRefAsync("a");   // ← 这一行会不会卡?
            Assert.True(r.IsValid);
        }
        
        // 测试点：同步 FakeLoader 下实例化任务应立即完成。
        [Fact]   // 同步, 不 await, 不可能卡
        public void Diag_TaskCompletesSynchronously()
        {
            var mgr = new ResourceManager();
            mgr.RegisterLoader(new FakeLoader { Factory = a => new FakePrefab { Name = a } });
            mgr.RegisterInstancer(new FakeInstanceProvider());

            var t = mgr.InstantiateRefAsync("p");
            Assert.True(t.IsCompleted, $"status={t.Status}");
        }
        
        // 测试点：完整同步实例化、取对象、释放和管理器销毁路径。
        [Fact]   // 同步, 无 await, 无 IDisposable fixture
        public void Diag_FullInstantiate_Sync()
        {
            var mgr = new ResourceManager();
            mgr.RegisterLoader(new FakeLoader { Factory = a => new FakePrefab { Name = a } });
            mgr.RegisterInstancer(new FakeInstanceProvider());

            var t = mgr.InstantiateRefAsync("p");
            Assert.True(t.IsCompleted);
            var r = t.Result;                       // 已完成, 不阻塞
            var inst = r.Get<FakeInstance>();
            Assert.NotNull(inst);
            Assert.True(inst!.Active);
            Assert.Equal(1, mgr.GetLiveInstanceCount("p"));
            r.Dispose();
            Assert.Equal(0, mgr.GetLiveInstanceCount("p"));
            mgr.Dispose();                          // 复现 fixture teardown
        }
    }
}
