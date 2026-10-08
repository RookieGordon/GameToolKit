/*
 * author       : Gordon
 * datetime     : 2026/10/8
 * description  : 实例工厂契约 (P0, §3.5)。将租用请求转为原型请求，从原型创建实例并负责
 *                租用/归还重置、存活检查与销毁确认。OnRent/OnReturn 在执行上下文同步完成，
 *                禁止阻塞。创建或重置失败的实例必须销毁；销毁失败保留原型和隔离记录。
 *                IsAlive/CanCreate/GetInstanceKey 必须无副作用且不阻塞。
 */

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ToolKit.Tools.Common.Resource
{
    public interface IInstanceFactory
    {
        /// <summary> 将租用请求转成原型请求 (如 GameObject 实例 → GameObject 预制体) </summary>
        ResourceRequest GetPrototypeRequest(ResourceRequest instanceRequest);

        /// <summary> 实例表示标识：按结果类型和影响实例内容的参数提供稳定值；普通工厂用固定值 </summary>
        string GetInstanceKey(ResourceRequest instanceRequest);

        /// <summary> 该原型能否实例化出目标类型的实例 (角色护栏) </summary>
        bool CanCreate(object prototype, Type instanceType);

        /// <summary> 创建实例；creationRequest 为桶保存的不可变请求快照，不读隐式全局状态 </summary>
        Task<object> CreateAsync(object prototype, ResourceRequest creationRequest, CancellationToken operationToken);

        /// <summary> 实例是否仍存活 (未被引擎销毁)；无副作用 </summary>
        bool IsAlive(object instance);

        /// <summary> 租用重置 (在执行上下文同步完成) </summary>
        void OnRent(object instance);

        /// <summary> 归还重置 (在执行上下文同步完成) </summary>
        void OnReturn(object instance);

        /// <summary> 销毁并确认完成；Unity Destroy 延后完成时等待确认 </summary>
        Task DestroyAsync(object instance);
    }
}
