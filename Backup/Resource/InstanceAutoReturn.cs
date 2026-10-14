/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 实例自动归还组件 (P5, §12.2)。绑定具体的不池化租约 (非可变 token)：
 *                实例被业务 Destroy 或主动调用 Detach 后归还租约，归还目标始终是创建时的原池代际。
 */

using System;
using ToolKit.Tools.Common;
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

        /// <summary> 绑定具体租约；已存在未解除绑定时报错，重新绑定会重置 detached 状态 (R20) </summary>
        public void Bind(IDisposable lease)
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            if (_lease != null)
            {
                throw new InvalidOperationException(
                    "已存在未解除的自动归还绑定，请先 Detach 或归还原租约后再绑定新租约");
            }
            _detached = false; // 复用路径：上一次 Detach 不能让新租约失去自动归还
            _lease = lease;
        }

        /// <summary> 主动归还前先解除绑定 (归还未由本组件负责)；只解绑，不代替归还 </summary>
        public void Detach()
        {
            _detached = true;
            _lease = null;
        }

        private void OnDestroy()
        {
            if (_detached || _lease == null)
            {
                return;
            }
            var lease = _lease;
            _lease = null;
            _detached = true;
            lease.Dispose(); // 实例被外部 Destroy：归还具体租约，不重入归还
        }
    }
}
