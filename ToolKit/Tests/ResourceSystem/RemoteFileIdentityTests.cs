using System;
using System.IO;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    public sealed class RemoteFileIdentityTests
    {
        [Fact]
        public async Task ResolveAsync_DistinctIdentitiesWithSameDisplayText_HaveDifferentLocalKeys()
        {
            var firstIdentity = new FileIdentity("a/b", "c", "r", "");
            var secondIdentity = new FileIdentity("a", "b/c", "r", "");
            Assert.NotEqual(firstIdentity, secondIdentity);
            Assert.Equal(firstIdentity.ToString(), secondIdentity.ToString());

            var cache = new FileCache(new FileCacheOptions
            {
                Directory = Path.Combine(Path.GetTempPath(), "gt-identity-" + Guid.NewGuid().ToString("N")),
            });
            try
            {
                const string firstUrl = "https://example.test/first";
                const string secondUrl = "https://example.test/second";
                var loader = new RemoteFileLoader(cache,
                    (_, _, _) => throw new InvalidOperationException("Resolve must not download files"),
                    DecoderRegistry.CreateDefault(),
                    url => new RemoteFileRequest(
                        url == firstUrl ? firstIdentity : secondIdentity,
                        FileValidity.Immutable, new Uri(url)));

                var first = await loader.ResolveAsync(
                    new ResourceRequest("remote", firstUrl, typeof(byte[]), null), default);
                var second = await loader.ResolveAsync(
                    new ResourceRequest("remote", secondUrl, typeof(byte[]), null), default);

                Assert.NotEqual(first.LocalKey, second.LocalKey);
            }
            finally
            {
                await cache.ShutdownAsync();
            }
        }
    }
}
