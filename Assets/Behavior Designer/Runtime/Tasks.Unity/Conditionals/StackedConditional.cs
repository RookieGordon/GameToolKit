using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks
{
    public partial class StackedConditional
    {
        public override void OnAwake()
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].GameObject = gameObject;
                conditionals[i].Transform = transform;
                conditionals[i].Owner = Owner;
#if UNITY_EDITOR || DLL_RELEASE || DLL_DEBUG
                conditionals[i].NodeData = new NodeData();
#endif
                conditionals[i].OnAwake();
            }
        }
        public override void OnTriggerEnter(Collider other)
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnTriggerEnter(other);
            }
        }

        public override void OnTriggerEnter2D(Collider2D other)
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnTriggerEnter2D(other);
            }
        }

        public override void OnTriggerExit(Collider other)
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnTriggerExit(other);
            }
        }

        public override void OnTriggerExit2D(Collider2D other)
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnTriggerExit2D(other);
            }
        }

        public override void OnCollisionEnter(Collision collision)
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnCollisionEnter(collision);
            }
        }

        public override void OnCollisionEnter2D(Collision2D collision)
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnCollisionEnter2D(collision);
            }
        }

        public override void OnCollisionExit(Collision collision)
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnCollisionExit(collision);
            }
        }

        public override void OnCollisionExit2D(Collision2D collision)
        {
            if (conditionals == null) {
                return;
            }

            for (int i = 0; i < conditionals.Length; ++i) {
                if (conditionals[i] == null) {
                    continue;
                }
                conditionals[i].OnCollisionExit2D(collision);
            }
        }
    }
}