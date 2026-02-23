/*
 * author       : Gordon
 * datetime     : 2026/2/23
 * description  : 顶点和骨骼烘焙动画中，使用动画事件
 */

using UnityEngine;
using UnityToolKit.Engine.Animation;

namespace Tests.GPUAnimationTest
{
    public class GPUAnimationSample2 : MonoBehaviour
    {
        public GameObject BoneAnimationObj;
        
        private Animator _animator;
        private GPUAnimationController _bAnimationController;
        
        private void Awake()
        {
            _bAnimationController = Instantiate(BoneAnimationObj).GetComponent<GPUAnimationController>();
            _bAnimationController.OnAnimEvent = OnAnimationEvent;
        }

        void Start()
        {
            _bAnimationController.SetAnimation(0);
        }
        
        void OnAnimationEvent(string eventName)
        {
            Debug.Log($"GPU Animation Event Triggered: {eventName}");
        }
    }
}
