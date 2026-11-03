using System;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;

namespace UnityToolKit.Runtime.ResourceSystem
{
    public class AssetBundleLoader: ILoader
    {
        public Task<IResource> LoadAsync(ResourceKey key, IExecutionContext context, IProgress<OperationProgress> progress, CancellationToken cancellation)
        {
            throw new NotImplementedException();
        }
    }
}