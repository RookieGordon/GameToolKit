using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks
{
    public partial class StackedAction
    {
        
        public override void OnAwake()
        {
            if (actions == null)
            {
                return;
            }

            for (int i = 0; i < actions.Length; ++i)
            {
                if (actions[i] == null)
                {
                    continue;
                }
                actions[i].GameObject = gameObject;
                actions[i].Transform = transform;
                actions[i].Owner = Owner;
#if UNITY_EDITOR || DLL_RELEASE || DLL_DEBUG
                actions[i].NodeData = new NodeData();
#endif
                actions[i].OnAwake();
            }
        }
        public override void OnTriggerEnter(Collider other)
        {
            if (actions == null)
            {
                return;
            }

            for (int i = 0; i < actions.Length; ++i)
            {
                if (actions[i] == null)
                {
                    continue;
                }

                actions[i].OnTriggerEnter(other);
            }
        }

        public override void OnTriggerEnter2D(Collider2D other)
        {
            if (actions == null)
            {
                return;
            }

            for (int i = 0; i < actions.Length; ++i)
            {
                if (actions[i] == null)
                {
                    continue;
                }

                actions[i].OnTriggerEnter2D(other);
            }
        }

        public override void OnTriggerExit(Collider other)
        {
            if (actions == null)
            {
                return;
            }

            for (int i = 0; i < actions.Length; ++i)
            {
                if (actions[i] == null)
                {
                    continue;
                }

                actions[i].OnTriggerExit(other);
            }
        }

        public override void OnTriggerExit2D(Collider2D other)
        {
            if (actions == null)
            {
                return;
            }

            for (int i = 0; i < actions.Length; ++i)
            {
                if (actions[i] == null)
                {
                    continue;
                }

                actions[i].OnTriggerExit2D(other);
            }
        }

        public override void OnCollisionEnter(Collision collision)
        {
            if (actions == null)
            {
                return;
            }

            for (int i = 0; i < actions.Length; ++i)
            {
                if (actions[i] == null)
                {
                    continue;
                }

                actions[i].OnCollisionEnter(collision);
            }
        }

        public override void OnCollisionEnter2D(Collision2D collision)
        {
            if (actions == null)
            {
                return;
            }

            for (int i = 0; i < actions.Length; ++i)
            {
                if (actions[i] == null)
                {
                    continue;
                }

                actions[i].OnCollisionEnter2D(collision);
            }
        }

        public override void OnCollisionExit(Collision collision)
        {
            if (actions == null)
            {
                return;
            }

            for (int i = 0; i < actions.Length; ++i)
            {
                if (actions[i] == null)
                {
                    continue;
                }

                actions[i].OnCollisionExit(collision);
            }
        }

        public override void OnCollisionExit2D(Collision2D collision)
        {
            if (actions == null)
            {
                return;
            }

            for (int i = 0; i < actions.Length; ++i)
            {
                if (actions[i] == null)
                {
                    continue;
                }

                actions[i].OnCollisionExit2D(collision);
            }
        }
        
    }
}