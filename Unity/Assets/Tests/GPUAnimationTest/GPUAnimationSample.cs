using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor.Animations;
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
        
        public Button PlayAniBtn;
        public InputField AniNameInput;

        public GameObject SysAniationObj;
        public GameObject GPUAnimationObj;
        
        private Animator _animator;
        private GPUAnimationController _vtAnimationController;
        private GPUAnimationController _btAnimationController;

        private void Awake()
        {
            _animator = Instantiate(SysAniationObj).GetComponent<Animator>();
            _vtAnimationController = Instantiate(GPUAnimationObj).GetComponent<GPUAnimationController>();
        }

        private void Update()
        {
            _vtAnimationController.Tick(Time.deltaTime);

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
            _vtAnimationController.SetAnimation((int)motion);
        }
    }
}



