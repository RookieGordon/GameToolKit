/*
 * author       : Gordon
 * datetime     : 2026/2/23
 * description  : 顶点和骨骼烘焙动画使用案例
 */

using UnityEngine;
using UnityEngine.UI;
using UnityToolKit.Engine.Animation;

namespace Tests.GPUAnimationTest
{
    public class GPUAnimationSample : MonoBehaviour
    {
        private enum EMotionAnimation
        {
            Idle,
            Forward,
            Left,
            Backward,
            Right
        }

        public GameObject SysAniationObj;
        public GameObject BoneAnimationObj;
        public GameObject VertexAnimationObj;
        
        private Animator _animator;
        private GPUAnimationController _vAnimationController;
        private GPUAnimationController _bAnimationController;

        private void Awake()
        {
            _animator = Instantiate(SysAniationObj).GetComponent<Animator>();
            _vAnimationController = Instantiate(VertexAnimationObj).GetComponent<GPUAnimationController>();
            _vAnimationController.transform.position += (Vector3.right);
            _bAnimationController = Instantiate(BoneAnimationObj).GetComponent<GPUAnimationController>();
            _bAnimationController.transform.position += (Vector3.right * 2);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.W))
            {
                PlayAnimation(EMotionAnimation.Forward);
            }
            if (Input.GetKeyDown(KeyCode.A))
            {
                PlayAnimation(EMotionAnimation.Left);
            }
            if (Input.GetKeyDown(KeyCode.S))
            {
                PlayAnimation(EMotionAnimation.Backward);
            }
            if (Input.GetKeyDown(KeyCode.D))
            {
                PlayAnimation(EMotionAnimation.Right);
            }
        }

        void PlayAnimation(EMotionAnimation motion)
        {
            _animator.SetTrigger(motion.ToString());
            _vAnimationController.SetAnimation((int)motion);
            _bAnimationController.SetAnimation((int)motion);
        }
    }
}



