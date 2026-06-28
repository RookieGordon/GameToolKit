/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : 实例提供者接口 (引擎无关)。把"如何从一个资源原型创建/激活/失活/销毁实例"这件
 *                与引擎强相关的事抽象出来, 注入给 ResourceManager 的实例对象池使用。
 *                这样实例池逻辑保持引擎无关, 而 GameObject 的 Instantiate/Destroy 由 Engine 层实现。
 */

namespace ToolKit.Tools.Common
{
    public interface IInstanceProvider
    {
        /// <summary>
        /// 该原型是否可被实例化/池化。
        /// <para>用于把"可被应用的共享资源"(如 Sprite/Material/byte[]) 挡在实例池之外:</para>
        /// <para>由于 GameObject 与 Sprite 同为 UnityEngine.Object, 无法用标记接口隔离,</para>
        /// <para>因此改由 Provider 按角色判定 —— 只有能实例化出独立副本的原型(如 GameObject)才返回 true。</para>
        /// </summary>
        bool CanInstantiate(IAssetHandle prototype);

        /// <summary> 由资源原型 (已加载成功的句柄) 创建一个新实例 </summary>
        object Create(IAssetHandle prototype);

        /// <summary> 实例从池中取出时调用 (如 SetActive(true)) </summary>
        void OnGet(object instance);

        /// <summary> 实例归还到池时调用 (如 SetActive(false)) </summary>
        void OnReturn(object instance);

        /// <summary> 实例被销毁时调用 (超出容量或清池, 如 Object.Destroy) </summary>
        void OnDestroy(object instance);

        /// <summary>
        /// 实例是否仍存活 (未被引擎销毁)。
        /// <para>Unity 的 Object 在被 Destroy 后, C# 引用仍非 null 但逻辑上 == null;</para>
        /// <para>实例型 ResourceRef 据此判断: 已销毁的实例 Get 返回 null、回收时不再入池。</para>
        /// </summary>
        bool IsAlive(object instance);
    }
}
