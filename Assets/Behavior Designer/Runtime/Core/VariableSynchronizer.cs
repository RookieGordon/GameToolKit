// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.VariableSynchronizer
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;
using Debug = BehaviorDesigner.Runtime.BehaviorDebug;
using CSTask = System.Threading.Tasks.Task;

namespace BehaviorDesigner.Runtime
{
    [JsonIgnoreBase]
    public partial class VariableSynchronizer
    {
#if !UNITY_PLATFORM
        [JsonProperty]
        private UpdateIntervalType updateInterval;

        [JsonProperty]
        private float updateIntervalSeconds;

        [JsonProperty]
        private List<VariableSynchronizer.SynchronizedVariable> synchronizedVariables =
            new List<VariableSynchronizer.SynchronizedVariable>();
#endif

#if !UNITY_PLATFORM
        public bool enabled = true;
#endif

        public UpdateIntervalType UpdateInterval
        {
            get => this.updateInterval;
            set
            {
                this.updateInterval = value;
                this.UpdateIntervalChanged();
            }
        }

        public float UpdateIntervalSeconds
        {
            get => this.updateIntervalSeconds;
            set
            {
                this.updateIntervalSeconds = value;
                this.UpdateIntervalChanged();
            }
        }

        public List<VariableSynchronizer.SynchronizedVariable> SynchronizedVariables
        {
            get => this.synchronizedVariables;
            set
            {
                this.synchronizedVariables = value;
                this.enabled = true;
            }
        }

#if !UNITY_PLATFORM
        private async void UpdateIntervalChanged()
        {
            await this.UpdateIntervalChangedAsync();
        }

        private async CSTask UpdateIntervalChangedAsync()
        {
            if (this.updateInterval == UpdateIntervalType.EveryFrame)
            {
                this.enabled = true;
            }
            else if (this.updateInterval == UpdateIntervalType.SpecifySeconds)
            {
                Tick();
                await CSTask.Delay((int)this.updateIntervalSeconds);
                this.enabled = false;
            }
            else
            {
                this.enabled = false;
            }
        }
#endif

#if !UNITY_PLATFORM
        public VariableSynchronizer()
        {
            this.Awake();
        }
#endif
        
#if!UNITY_PLATFORM
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
                        // case VariableSynchronizer.SynchronizationType.Animator:
                        //     synchronizedVariable.animator =
                        //         synchronizedVariable.targetComponent as Animator;
                        //     if (
                        //         synchronizedVariable.animator
                        //         == null
                        //     )
                        //     {
                        //         str = "the component is not of type Animator";
                        //         break;
                        //     }
                        //
                        //     synchronizedVariable.targetID = Animator.StringToHash(synchronizedVariable.targetName);
                        //     System.Type propertyType = synchronizedVariable
                        //         .sharedVariable
                        //         .GetType()
                        //         .GetProperty("Value")
                        //         .PropertyType;
                        //     if (propertyType.Equals(typeof(bool)))
                        //     {
                        //         synchronizedVariable.animatorParameterType = VariableSynchronizer
                        //             .AnimatorParameterType
                        //             .Bool;
                        //         break;
                        //     }
                        //
                        //     if (propertyType.Equals(typeof(float)))
                        //     {
                        //         synchronizedVariable.animatorParameterType = VariableSynchronizer
                        //             .AnimatorParameterType
                        //             .Float;
                        //         break;
                        //     }
                        //
                        //     if (propertyType.Equals(typeof(int)))
                        //     {
                        //         synchronizedVariable.animatorParameterType = VariableSynchronizer
                        //             .AnimatorParameterType
                        //             .Integer;
                        //         break;
                        //     }
                        //
                        //     str =
                        //         "there is no animator parameter type that can synchronize with "
                        //         + (object)propertyType;
                        //     break;
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
#endif
        public void Update()
        {
#if !UNITY_PLATFORM
            if (!this.enabled)
            {
                return;
            }
#endif
            this.Tick();
        }
        
#if !UNITY_PLATFORM
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
                    // case VariableSynchronizer.SynchronizationType.Animator:
                    //     if (synchronizedVariable.setVariable)
                    //     {
                    //         switch (synchronizedVariable.animatorParameterType)
                    //         {
                    //             case VariableSynchronizer.AnimatorParameterType.Bool:
                    //                 synchronizedVariable
                    //                     .sharedVariable
                    //                     .SetValue((object)
                    //                         synchronizedVariable
                    //                             .animator
                    //                             .GetBool(synchronizedVariable.targetID));
                    //                 continue;
                    //             case VariableSynchronizer.AnimatorParameterType.Float:
                    //                 synchronizedVariable
                    //                     .sharedVariable
                    //                     .SetValue((object)
                    //                         synchronizedVariable
                    //                             .animator
                    //                             .GetFloat(synchronizedVariable.targetID));
                    //                 continue;
                    //             case VariableSynchronizer.AnimatorParameterType.Integer:
                    //                 synchronizedVariable
                    //                     .sharedVariable
                    //                     .SetValue((object)
                    //                         synchronizedVariable
                    //                             .animator
                    //                             .GetInteger(synchronizedVariable.targetID));
                    //                 continue;
                    //             default:
                    //                 continue;
                    //         }
                    //     }
                    //     else
                    //     {
                    //         switch (synchronizedVariable.animatorParameterType)
                    //         {
                    //             case VariableSynchronizer.AnimatorParameterType.Bool:
                    //                 synchronizedVariable
                    //                     .animator
                    //                     .SetBool(synchronizedVariable.targetID,
                    //                         (bool)synchronizedVariable.sharedVariable.GetValue());
                    //                 continue;
                    //             case VariableSynchronizer.AnimatorParameterType.Float:
                    //                 synchronizedVariable
                    //                     .animator
                    //                     .SetFloat(synchronizedVariable.targetID,
                    //                         (float)synchronizedVariable.sharedVariable.GetValue());
                    //                 continue;
                    //             case VariableSynchronizer.AnimatorParameterType.Integer:
                    //                 synchronizedVariable
                    //                     .animator
                    //                     .SetInteger(synchronizedVariable.targetID,
                    //                         (int)synchronizedVariable.sharedVariable.GetValue());
                    //                 continue;
                    //             default:
                    //                 continue;
                    //         }
                    //     }
                    case VariableSynchronizer.SynchronizationType.PlayMaker:
                    case VariableSynchronizer.SynchronizationType.uFrame:
                        synchronizedVariable.thirdPartyTick(synchronizedVariable);
                        break;
                }
            }
        }
#endif
        
        private static Func<object> CreateGetDelegate(object instance, MethodInfo method)
        {
            ConstantExpression instance1 = Expression.Constant(instance);
            return Expression
                .Lambda<Func<object>>((Expression)Expression.Call(instance1, method))
                .Compile();
        }

        private static Action<object> CreateSetDelegate(object instance, MethodInfo method)
        {
            ConstantExpression instance1 = Expression.Constant(instance);
            ParameterExpression parameterExpression = Expression.Parameter(typeof(object), "p");
            UnaryExpression unaryExpression = Expression.Convert((Expression)parameterExpression,
                method.GetParameters()[0].ParameterType);
            return Expression
                .Lambda<Action<object>>((Expression)
                    Expression.Call((Expression)instance1, method, (Expression)unaryExpression),
                    parameterExpression)
                .Compile();
        }

        public enum SynchronizationType
        {
            BehaviorDesigner,
            Property,
            Animator,
            PlayMaker,
            uFrame,
        }

        public enum AnimatorParameterType
        {
            Bool,
            Float,
            Integer,
        }

        [Serializable]
        public partial class SynchronizedVariable
        {
            public VariableSynchronizer.SynchronizationType synchronizationType;
            
            public bool setVariable;
            
            public Behavior behavior;
            
            public string variableName;
            
            public bool global;
            
#if !UNITY_PLATFORM
            public System.Object targetComponent;
#endif
            
            public string targetName;
            
            public bool targetGlobal;
            
            public SharedVariable targetSharedVariable;
            
            public Action<object> setDelegate;
            
            public Func<object> getDelegate;
            
            public VariableSynchronizer.AnimatorParameterType animatorParameterType;
            
            public int targetID;
            
            public Action<VariableSynchronizer.SynchronizedVariable> thirdPartyTick;
            
            public Enum variableType;
            
            public object thirdPartyVariable;
            
            public SharedVariable sharedVariable;

#if !UNITY_PLATFORM
            public SynchronizedVariable(
                VariableSynchronizer.SynchronizationType synchronizationType,
                bool setVariable,
                Behavior behavior,
                string variableName,
                bool global,
                System.Object targetComponent,
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
#endif
        }
    }
}