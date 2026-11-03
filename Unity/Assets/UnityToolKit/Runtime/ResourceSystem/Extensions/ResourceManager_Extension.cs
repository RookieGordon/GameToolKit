using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityToolKit.Runtime.Utility;

namespace UnityToolKit.Runtime.ResourceSystem
{
    /// <summary> 一项预加载：把 Address 对应的预制体预先实例化 Count 个，隐藏后留在实例池里 </summary>
    public class PreloadConfig
    {
        public ResourceSource Source { get; set; }
        public string Address { get; set; }
        public int Count { get; set; } = 1;
        /// <summary> 这一项自己的进度 (0..1，按已就绪实例数计)；可空 </summary>
        public IProgress<OperationProgress> Progress { get; set; }
        /// <summary> 这一项自己的取消；只取消这一项，不影响同批其他项 </summary>
        public CancellationToken Token { get; set; } = default;
    }

    /// <summary> 一批预加载的结果；尝试数不等于成功数 </summary>
    public sealed class PreloadResult
    {
        /// <summary> 计划实例总数 (各项 Count 之和) </summary>
        public int Total { get; internal set; }
        /// <summary> 已创建并留在池里的实例数 </summary>
        public int Completed { get; internal set; }
        /// <summary> 被自己的 Token 取消的项 (整批取消时方法直接抛 OperationCanceledException，不走这里) </summary>
        public List<PreloadConfig> Canceled { get; } = new List<PreloadConfig>();
        /// <summary> 失败的项及原因；一项失败不影响其他项 </summary>
        public List<KeyValuePair<PreloadConfig, Exception>> Failed { get; } = new List<KeyValuePair<PreloadConfig, Exception>>();

        public bool IsFullyCompleted => Completed == Total && Failed.Count == 0 && Canceled.Count == 0;
    }

    public partial class ResourceManager
    {
        
        #region 预加载API
        
        /// <summary>
        /// 批量预实例化 GameObject：每项租出 Count 个实例后立即全部归还，实例被工厂隐藏并留在池里，之后 RentAsync 直接复用。
        /// 进度按"已就绪实例数 / 总实例数"汇总到 progress，各项自己的 Progress 另按本项计。
        /// token 取消整批 (抛 OperationCanceledException)；某项的 Token 只取消该项，该项记入结果的 Canceled。
        /// 创建在主线程进行，每帧最多创建 instancesPerFrame 个以免卡顿；0 表示不分帧。
        /// 注意：Count 超过池策略的 MaxIdlePerPrototype 时，多出的实例归还即被销毁。
        /// </summary>
        public async Task<PreloadResult> PreloadGameObjectAsync(List<PreloadConfig> preloads,
            IProgress<OperationProgress> progress = null, CancellationToken token = default, int instancesPerFrame = 10)
        {
            CheckInitialized();
            if (preloads == null) throw new ArgumentNullException(nameof(preloads));

            var result = new PreloadResult();
            foreach (var config in preloads) result.Total += Math.Max(0, config.Count);
            var createdThisFrame = 0;

            foreach (var config in preloads)
            {
                token.ThrowIfCancellationRequested();
                if (config == null || config.Count <= 0) continue;

                // 必须先把 Count 个租约都拿在手里再归还：边租边还只会反复复用同一个实例
                var leases = new List<InstanceLease<GameObject>>(config.Count);
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, config.Token))
                {
                    try
                    {
                        for (var i = 0; i < config.Count; i++)
                        {
                            leases.Add(await _resSystem.RentAsync<GameObject>(config.Source, config.Address, token: linked.Token));
                            result.Completed++;
                            Report(config, i + 1, result, progress);

                            if (instancesPerFrame > 0 && ++createdThisFrame >= instancesPerFrame)
                            {
                                createdThisFrame = 0;
                                await Task.Yield();                          // 回到主线程的下一次调度，分帧
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        result.Canceled.Add(config);                         // 只有这一项被取消
                    }
                    catch (Exception error) when (!(error is OperationCanceledException))
                    {
                        result.Failed.Add(new KeyValuePair<PreloadConfig, Exception>(config, error));
                    }
                    finally
                    {
                        foreach (var lease in leases) lease.Dispose();       // 归还即隐藏，留在池里
                    }
                }
            }
            return result;
        }

        /// <summary> 协程形式的批量预实例化 </summary>
        public IEnumerator PreloadGameObject(List<PreloadConfig> preloads,
            IProgress<OperationProgress> progress = null,
            Action<PreloadResult> onCompleted = null,
            Action<Exception> onError = null,
            Action onCancelled = null,
            CancellationToken token = default,
            int instancesPerFrame = 10)
        {
            var task = PreloadGameObjectAsync(preloads, progress, token, instancesPerFrame);
            return task.ToCoroutine(onCompleted, error =>
            {
                if (error is OperationCanceledException) onCancelled?.Invoke();
                else onError?.Invoke(error);
            });
        }

        private static void Report(PreloadConfig config, int readyInConfig, PreloadResult result, IProgress<OperationProgress> overall)
        {
            config.Progress?.Report(new OperationProgress((double)readyInConfig / config.Count, config.Address));
            overall?.Report(new OperationProgress(result.Total == 0 ? 1 : (double)result.Completed / result.Total,
                config.Address + " " + readyInConfig + "/" + config.Count));
        }
        
        #endregion

        #region 异步加载GameObject
        
        public async Task<InstanceLease<GameObject>> RentGameObjectAsync<GameObject>(ResourceSource source, string address, 
            CancellationToken token = default, object parameters = null)
        {
            return await RentAsync<GameObject>(source, address, token, parameters);
        }
        

        #endregion
        
        #region 协程加载GameObject

        public IEnumerator RentGameObjectAsync<GameObject>(ResourceSource source, string address,
            Action<InstanceLease<GameObject>> onCompleted = null,
            Action<Exception> onError = null,
            Action onCancelled = null,
            CancellationToken token = default)
        {
            return RentAsync<GameObject>(source, address, onCompleted, onError, onCancelled, token);
        }
        

        #endregion
    }
}
