using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BehaviorDesigner.Runtime
{
    [AddComponentMenu("Behavior Designer/Variable Synchronizer")]
    public partial class VariableSynchronizer : MonoBehaviour
    {
        [SerializeField] private UpdateIntervalType updateInterval;

        [SerializeField] private float updateIntervalSeconds;

        [SerializeField] private List<VariableSynchronizer.SynchronizedVariable> synchronizedVariables =
            new List<VariableSynchronizer.SynchronizedVariable>();

        private WaitForSeconds updateWait;


        public void Awake()
        {
            for (int index = this.synchronizedVariables.Count - 1; index > -1; --index)
            {
                VariableSynchronizer.SynchronizedVariable synchronizedVariable =
                    this.synchronizedVariables[index];
                synchronizedVariable.sharedVariable = !synchronizedVariable.global
                    ? synchronizedVariable.behavior.GetVariable(synchronizedVariable.variableName)
                    : GlobalVariables.Instance.GetVariable(synchronizedVariable.variableName);
                string str = string.Empty;
                if (synchronizedVariable.sharedVariable == null)
                {
                    str = "the SharedVariable can't be found";
                }
                else
                {
                    switch (synchronizedVariable.synchronizationType)
                    {
                        case VariableSynchronizer.SynchronizationType.BehaviorDesigner:
                            Behavior targetComponent =
                                synchronizedVariable.targetComponent as Behavior;
                            if (targetComponent == null)
                            {
                                str = "the target component is not of type Behavior Tree";
                                break;
                            }

                            synchronizedVariable.targetSharedVariable =
                                !synchronizedVariable.targetGlobal
                                    ? targetComponent.GetVariable(synchronizedVariable.targetName)
                                    : GlobalVariables
                                        .Instance
                                        .GetVariable(synchronizedVariable.targetName);
                            if (synchronizedVariable.targetSharedVariable == null)
                            {
                                str = "the target SharedVariable cannot be found";
                                break;
                            }

                            break;
                        case VariableSynchronizer.SynchronizationType.Property:
                            PropertyInfo property = ((object)synchronizedVariable.targetComponent)
                                .GetType()
                                .GetProperty(synchronizedVariable.targetName);
                            if (property == (PropertyInfo)null)
                            {
                                str =
                                    "the property "
                                    + synchronizedVariable.targetName
                                    + " doesn't exist";
                                break;
                            }

                            if (synchronizedVariable.setVariable)
                            {
                                MethodInfo getMethod = property.GetGetMethod();
                                if (getMethod == (MethodInfo)null)
                                {
                                    str = "the property has no get method";
                                    break;
                                }

                                synchronizedVariable.getDelegate =
                                    VariableSynchronizer.CreateGetDelegate((object)synchronizedVariable.targetComponent,
                                        getMethod);
                                break;
                            }

                            MethodInfo setMethod = property.GetSetMethod();
                            if (setMethod == (MethodInfo)null)
                            {
                                str = "the property has no set method";
                                break;
                            }

                            synchronizedVariable.setDelegate =
                                VariableSynchronizer.CreateSetDelegate((object)synchronizedVariable.targetComponent,
                                    setMethod);
                            break;
                        case VariableSynchronizer.SynchronizationType.Animator:
                            synchronizedVariable.animator =
                                synchronizedVariable.targetComponent as Animator;
                            if (
                                synchronizedVariable.animator
                                == null
                            )
                            {
                                str = "the component is not of type Animator";
                                break;
                            }

                            synchronizedVariable.targetID = Animator.StringToHash(synchronizedVariable.targetName);
                            System.Type propertyType = synchronizedVariable
                                .sharedVariable
                                .GetType()
                                .GetProperty("Value")
                                .PropertyType;
                            if (propertyType.Equals(typeof(bool)))
                            {
                                synchronizedVariable.animatorParameterType = VariableSynchronizer
                                    .AnimatorParameterType
                                    .Bool;
                                break;
                            }

                            if (propertyType.Equals(typeof(float)))
                            {
                                synchronizedVariable.animatorParameterType = VariableSynchronizer
                                    .AnimatorParameterType
                                    .Float;
                                break;
                            }

                            if (propertyType.Equals(typeof(int)))
                            {
                                synchronizedVariable.animatorParameterType = VariableSynchronizer
                                    .AnimatorParameterType
                                    .Integer;
                                break;
                            }

                            str =
                                "there is no animator parameter type that can synchronize with "
                                + (object)propertyType;
                            break;
                        case VariableSynchronizer.SynchronizationType.PlayMaker:
                            System.Type typeWithinAssembly1 = TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.VariableSynchronizer_PlayMaker");
                            if (typeWithinAssembly1 != (System.Type)null)
                            {
                                MethodInfo method1 = typeWithinAssembly1.GetMethod("Start");
                                if (method1 != (MethodInfo)null)
                                {
                                    switch (
                                        (int)
                                        method1.Invoke((object)null,
                                            new object[1] { (object)synchronizedVariable })
                                    )
                                    {
                                        case 1:
                                            str = "the PlayMaker NamedVariable cannot be found";
                                            break;
                                        case 2:
                                            str =
                                                "the Behavior Designer SharedVariable is not the same type as the PlayMaker NamedVariable";
                                            break;
                                        default:
                                            MethodInfo method2 = typeWithinAssembly1.GetMethod("Tick");
                                            if (method2 != (MethodInfo)null)
                                            {
                                                synchronizedVariable.thirdPartyTick =
                                                    (Action<VariableSynchronizer.SynchronizedVariable>)
                                                    Delegate.CreateDelegate(typeof(Action<VariableSynchronizer.SynchronizedVariable>),
                                                        method2);
                                                break;
                                            }

                                            break;
                                    }
                                }
                                else
                                {
                                    break;
                                }
                            }
                            else
                            {
                                str = "has the PlayMaker classes been imported?";
                                break;
                            }

                            break;
                        case VariableSynchronizer.SynchronizationType.uFrame:
                            System.Type typeWithinAssembly2 = TaskUtility.GetTypeWithinAssembly("BehaviorDesigner.Runtime.VariableSynchronizer_uFrame");
                            if (typeWithinAssembly2 != (System.Type)null)
                            {
                                MethodInfo method3 = typeWithinAssembly2.GetMethod("Start");
                                if (method3 != (MethodInfo)null)
                                {
                                    switch (
                                        (int)
                                        method3.Invoke((object)null,
                                            new object[1] { (object)synchronizedVariable })
                                    )
                                    {
                                        case 1:
                                            str = "the uFrame property cannot be found";
                                            break;
                                        case 2:
                                            str =
                                                "the Behavior Designer SharedVariable is not the same type as the uFrame property";
                                            break;
                                        default:
                                            MethodInfo method4 = typeWithinAssembly2.GetMethod("Tick");
                                            if (method4 != (MethodInfo)null)
                                            {
                                                synchronizedVariable.thirdPartyTick =
                                                    (Action<VariableSynchronizer.SynchronizedVariable>)
                                                    Delegate.CreateDelegate(typeof(Action<VariableSynchronizer.SynchronizedVariable>),
                                                        method4);
                                                break;
                                            }

                                            break;
                                    }
                                }
                                else
                                {
                                    break;
                                }
                            }
                            else
                            {
                                str = "has the uFrame classes been imported?";
                                break;
                            }

                            break;
                    }
                }

                if (!string.IsNullOrEmpty(str))
                {
                    Debug.LogError($"Unable to synchronize {(object)synchronizedVariable.sharedVariable.Name}: {(object)str}");
                    this.synchronizedVariables.RemoveAt(index);
                }
            }

            if (this.synchronizedVariables.Count == 0)
            {
                this.enabled = false;
            }
            else
            {
                this.UpdateIntervalChanged();
            }
        }

        public void Tick()
        {
            for (int index = 0; index < this.synchronizedVariables.Count; ++index)
            {
                VariableSynchronizer.SynchronizedVariable synchronizedVariable =
                    this.synchronizedVariables[index];
                switch (synchronizedVariable.synchronizationType)
                {
                    case VariableSynchronizer.SynchronizationType.BehaviorDesigner:
                        if (synchronizedVariable.setVariable)
                        {
                            synchronizedVariable
                                .sharedVariable
                                .SetValue(synchronizedVariable.targetSharedVariable.GetValue());
                            break;
                        }

                        synchronizedVariable
                            .targetSharedVariable
                            .SetValue(synchronizedVariable.sharedVariable.GetValue());
                        break;
                    case VariableSynchronizer.SynchronizationType.Property:
                        if (synchronizedVariable.setVariable)
                        {
                            synchronizedVariable
                                .sharedVariable
                                .SetValue(synchronizedVariable.getDelegate());
                            break;
                        }

                        synchronizedVariable.setDelegate(synchronizedVariable.sharedVariable.GetValue());
                        break;
                    case VariableSynchronizer.SynchronizationType.Animator:
                        if (synchronizedVariable.setVariable)
                        {
                            switch (synchronizedVariable.animatorParameterType)
                            {
                                case VariableSynchronizer.AnimatorParameterType.Bool:
                                    synchronizedVariable
                                        .sharedVariable
                                        .SetValue((object)
                                            synchronizedVariable
                                                .animator
                                                .GetBool(synchronizedVariable.targetID));
                                    continue;
                                case VariableSynchronizer.AnimatorParameterType.Float:
                                    synchronizedVariable
                                        .sharedVariable
                                        .SetValue((object)
                                            synchronizedVariable
                                                .animator
                                                .GetFloat(synchronizedVariable.targetID));
                                    continue;
                                case VariableSynchronizer.AnimatorParameterType.Integer:
                                    synchronizedVariable
                                        .sharedVariable
                                        .SetValue((object)
                                            synchronizedVariable
                                                .animator
                                                .GetInteger(synchronizedVariable.targetID));
                                    continue;
                                default:
                                    continue;
                            }
                        }
                        else
                        {
                            switch (synchronizedVariable.animatorParameterType)
                            {
                                case VariableSynchronizer.AnimatorParameterType.Bool:
                                    synchronizedVariable
                                        .animator
                                        .SetBool(synchronizedVariable.targetID,
                                            (bool)synchronizedVariable.sharedVariable.GetValue());
                                    continue;
                                case VariableSynchronizer.AnimatorParameterType.Float:
                                    synchronizedVariable
                                        .animator
                                        .SetFloat(synchronizedVariable.targetID,
                                            (float)synchronizedVariable.sharedVariable.GetValue());
                                    continue;
                                case VariableSynchronizer.AnimatorParameterType.Integer:
                                    synchronizedVariable
                                        .animator
                                        .SetInteger(synchronizedVariable.targetID,
                                            (int)synchronizedVariable.sharedVariable.GetValue());
                                    continue;
                                default:
                                    continue;
                            }
                        }
                    case VariableSynchronizer.SynchronizationType.PlayMaker:
                    case VariableSynchronizer.SynchronizationType.uFrame:
                        synchronizedVariable.thirdPartyTick(synchronizedVariable);
                        break;
                }
            }
        }
        
        private void UpdateIntervalChanged()
        {
            this.StopCoroutine("CoroutineUpdate");
            if (this.updateInterval == UpdateIntervalType.EveryFrame)
            {
                this.enabled = true;
            }
            else if (this.updateInterval == UpdateIntervalType.SpecifySeconds)
            {
                if (Application.isPlaying)
                {
                    this.updateWait = new WaitForSeconds(this.updateIntervalSeconds);
                    this.StartCoroutine("CoroutineUpdate");
                }

                this.enabled = false;
            }
            else
            {
                this.enabled = false;
            }
        }

        [DebuggerHidden]
        private IEnumerator CoroutineUpdate()
        {
            Tick();
            yield return updateWait;
        }

        public partial class SynchronizedVariable
        {
            public Component targetComponent;
            
            public Animator animator;

            public SynchronizedVariable(
                VariableSynchronizer.SynchronizationType synchronizationType,
                bool setVariable,
                Behavior behavior,
                string variableName,
                bool global,
                Component targetComponent,
                string targetName,
                bool targetGlobal
            )
            {
                this.synchronizationType = synchronizationType;
                this.setVariable = setVariable;
                this.behavior = behavior;
                this.variableName = variableName;
                this.global = global;
                this.targetComponent = targetComponent;
                this.targetName = targetName;
                this.targetGlobal = targetGlobal;
            }
        }
    
    } 
}