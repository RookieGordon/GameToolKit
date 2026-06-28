/*
 * BytesPool 测试
 */

using NUnit.Framework;
using ToolKit.Tools.Common;

namespace Tests.ResourceSystemTest
{
    [TestFixture]
    public class BytesPoolTests
    {
        [Test]
        public void Rent_ReturnsBufferAtLeastRequested()
        {
            var buf = BytesPool.Rent(100);
            Assert.IsNotNull(buf);
            Assert.GreaterOrEqual(buf.Length, 100);
            BytesPool.Return(buf);
        }

        [Test]
        public void Return_Null_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => BytesPool.Return(null));
        }

        [Test]
        public void RentReturnRent_Usable()
        {
            var a = BytesPool.Rent(64);
            BytesPool.Return(a);
            var b = BytesPool.Rent(64);
            Assert.IsNotNull(b);
            Assert.GreaterOrEqual(b.Length, 64);
            BytesPool.Return(b);
        }
    }
}
