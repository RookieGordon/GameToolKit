using System;
using System.Text;
using Bonsai.Core;

namespace Bonsai.Standard
{
    /// <summary>
    /// Tests if the value at the given key is a certain type.
    /// </summary>
    [BonsaiNode("Conditional/", "Condition")]
    public partial class IsValueOfType : ConditionalAbort
    {
        /// <summary>
        /// The type to test against.
        /// </summary>
        public Type type;
        
        private Action<Blackboard.KeyEvent> OnBlackboardChanged;

        public override void OnStart()
        {
            OnBlackboardChanged = delegate(Blackboard.KeyEvent e)
            {
                if (e.Key == key)
                {
                    Evaluate();
                }
            };
        }

        public override bool Condition()
        {
            if (type == null || !Blackboard.Contains(key))
            {
                return false;
            }

            object value = Blackboard.Get(key);

            // Value is unset, nothing to check.
            if (value == null)
            {
                return false;
            }

            return value.GetType() == type;
        }

        public void OnAfterDeserialize()
        {
            if (string.IsNullOrEmpty(typename))
            {
                type = null;
            }

            else
            {
                type = Type.GetType(typename);
            }
        }

        public void OnBeforeSerialize()
        {
            if (type != null)
            {
                typename = type.AssemblyQualifiedName;
            }

            else
            {
                typename = "";
            }
        }

        protected override void OnObserverBegin()
        {
            Blackboard.AddObserver(OnBlackboardChanged);
        }

        protected override void OnObserverEnd()
        {
            Blackboard.RemoveObserver(OnBlackboardChanged);
        }

        public override void Description(StringBuilder builder)
        {
            base.Description(builder);
            builder.AppendLine();

            if (key == null || key.Length == 0)
            {
                builder.Append("No key is set to check");
            }
            else
            {
                builder.AppendFormat("Blackboard key {0} is {1}", key, Utility.TypeExtensions.NiceName(type));
            }
        }
    }
}