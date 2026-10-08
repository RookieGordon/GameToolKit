/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 实例自动归还组件 (P5, §12.2)。绑定具体的不池化租约 (非可变 token)：
 *                实例被业务 Destroy 或主动调用 Detach 后归还租约，归还目标始终是创建时的原池代际。
 */

using System;
using ToolKit.Tools.Common.Resource;
using UnityEngine;

namespace UnityToolKit.Runtime.Resource
{
    [DisallowMultipleComponent]
    public sealed class InstanceAutoReturn : MonoBehaviour
    {
        private IDisposable? _lease;
        private bool _detached;

        private InstanceAutoReturn()
        {
        }

        /// <summary> 绑定具体租约；同一组件重复绑定前先 Detach 旧租约 </summary>
        public void Bind(IDisposable lease)
        {
            _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        }

        /// <summary> 主动归还前先解除绑定 (下次租用绑定新租约) </summary>
        public void Detach()
        {
            _detached = true;
        }

        private void OnDestroy()
        {
            if (_detached)
            {
                return;
            }
            _detached = true;
            _lease?.Dispose(); // 实例被外部 Destroy：归还具体租约，不重入归还
            _lease = null;
        }
    }
}
