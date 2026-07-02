namespace ToolKit.Tools.Common
{
    public interface IApplicable
    {
        /// <summary>
        /// 将资源应用到目标对象上。
        /// </summary>
        /// <param name="target">目标对象</param>
        /// <param name="resource">资源</param>
        void Apply<T, R>(T target, R resource, params object[] applayArgs) where T : class where R : class;
        
        /// <summary>
        /// 丢弃当前的修改应用
        /// </summary>
        void Revert<T>(T target) where T : class;
    }
}