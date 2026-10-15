/*
 * 资源系统测试串行化：默认 xunit 并行运行不同测试类，而 ResourceManager 的默认执行上下文是
 * 进程级共享实例 (ImmediateExecutionContext.Instance)。并行运行会让互不相关的用例争用同一个
 * 串行域，产生与被测逻辑无关的抖动 (观察到的表现是两个毫不相干的用例偶发失败)。
 * 这里把所有资源系统用例放进同一个集合，串行执行。
 */

using Xunit;

namespace ToolKit.Tests.ResourceSystem
{
    [CollectionDefinition("ResourceSystem", DisableParallelization = true)]
    public sealed class ResourceSystemCollection
    {
    }
}
