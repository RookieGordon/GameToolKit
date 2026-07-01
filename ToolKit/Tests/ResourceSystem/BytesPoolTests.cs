/*
 * BytesPool 测试 (xUnit)
 */

using ToolKit.Tools.Common;

namespace ToolKit.Tests.ResourceSystem
{
    public class BytesPoolTests
    {
        // 测试点：租借缓冲区时返回长度不小于请求值的数组。
        [Fact]
        public void Rent_ReturnsBufferAtLeastRequested()
        {
            var buf = BytesPool.Rent(100);
            Assert.NotNull(buf);
            Assert.True(buf.Length >= 100);
            BytesPool.Return(buf);
        }

        // 测试点：归还 null 缓冲区应安全无异常。
        [Fact]
        public void Return_Null_DoesNotThrow()
        {
            BytesPool.Return(null);
        }

        // 测试点：缓冲区归还后再次租借仍可正常使用。
        [Fact]
        public void RentReturnRent_Usable()
        {
            var a = BytesPool.Rent(64);
            BytesPool.Return(a);
            var b = BytesPool.Rent(64);
            Assert.NotNull(b);
            Assert.True(b.Length >= 64);
            BytesPool.Return(b);
        }
    }
}
