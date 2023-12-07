// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.GlobalVariables
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System.Collections.Generic;
using UnityEngine;
using Debug = BehaviorDesigner.Runtime.BehaviorDebug;
using SerializeField = Newtonsoft.Json.JsonPropertyAttribute;

namespace BehaviorDesigner.Runtime
{
    [JsonIgnoreBase]
    public partial class GlobalVariables : IVariableSource
    {
        private static GlobalVariables instance;

#if !UNITY_PLATFORM
        [SerializeField]
        private List<SharedVariable> mVariables;

        [SerializeField]
        private VariableSerializationData mVariableData;

        [SerializeField]
        private string mVersion;
#endif

        private Dictionary<string, int> mSharedVariableIndex;

        public static GlobalVariables Instance
        {
            get
            {
                if (GlobalVariables.instance == null)
                {
                    // TODO 这种全局设置要怎么搞
                    // GlobalVariables.instance =
                    //     Resources.Load("BehaviorDesignerGlobalVariables", typeof(GlobalVariables))
                    //     as GlobalVariables;
                    if (GlobalVariables.instance != null)
                    {
                        GlobalVariables.instance.CheckForSerialization(false);
                    }
                }

                return GlobalVariables.instance;
            }
        }

        public List<SharedVariable> Variables
        {
            get => this.mVariables;
            set
            {
                this.mVariables = value;
                this.UpdateVariablesIndex();
            }
        }

        public VariableSerializationData VariableData
        {
            get => this.mVariableData;
            set => this.mVariableData = value;
        }

        public string Version
        {
            get => this.mVersion;
            set => this.mVersion = value;
        }

        public void CheckForSerialization(bool force)
        {
            if (
                !force
                && this.mVariables != null
                && (this.mVariables.Count <= 0 || this.mVariables[0] != null)
            )
            {
                return;
            }

            if (
                this.VariableData != null
                && !string.IsNullOrEmpty(this.VariableData.JSONSerialization)
            )
            {
                JSONDeserialization.Load(this.VariableData.JSONSerialization, this, this.mVersion);
            }
            else
            {
                BinaryDeserialization.Load(this, this.mVersion);
            }
        }

        public SharedVariable GetVariable(string name)
        {
            if (name == null)
            {
                return (SharedVariable)null;
            }

            this.CheckForSerialization(false);
            if (this.mVariables != null)
            {
                if (
                    this.mSharedVariableIndex == null
                    || this.mSharedVariableIndex.Count != this.mVariables.Count
                )
                {
                    this.UpdateVariablesIndex();
                }

                int index;
                if (this.mSharedVariableIndex.TryGetValue(name, out index))
                {
                    return this.mVariables[index];
                }
            }

            return (SharedVariable)null;
        }

        public List<SharedVariable> GetAllVariables()
        {
            this.CheckForSerialization(false);
            return this.mVariables;
        }

        public void SetVariable(string name, SharedVariable sharedVariable)
        {
            this.CheckForSerialization(false);
            if (this.mVariables == null)
            {
                this.mVariables = new List<SharedVariable>();
            }
            else if (this.mSharedVariableIndex == null)
            {
                this.UpdateVariablesIndex();
            }

            sharedVariable.Name = name;
            int index;
            if (
                this.mSharedVariableIndex != null
                && this.mSharedVariableIndex.TryGetValue(name, out index)
            )
            {
                SharedVariable mVariable = this.mVariables[index];
                if (
                    !mVariable.GetType().Equals(typeof(SharedVariable))
                    && !mVariable.GetType().Equals(sharedVariable.GetType())
                )
                {
                    Debug.LogError($"Error: Unable to set SharedVariable {(object)name} - the variable type {(object)mVariable.GetType()} does not match the existing type {(object)sharedVariable.GetType()}");
                }
                else
                {
                    mVariable.SetValue(sharedVariable.GetValue());
                }
            }
            else
            {
                this.mVariables.Add(sharedVariable);
                this.UpdateVariablesIndex();
            }
        }

        public void SetVariableValue(string name, object value)
        {
            this.GetVariable(name)?.SetValue(value);
        }

        public void UpdateVariableName(SharedVariable sharedVariable, string name)
        {
            this.CheckForSerialization(false);
            sharedVariable.Name = name;
            this.UpdateVariablesIndex();
        }

        public void SetAllVariables(List<SharedVariable> variables) => this.mVariables = variables;

        private void UpdateVariablesIndex()
        {
            if (this.mVariables == null)
            {
                if (this.mSharedVariableIndex == null)
                {
                    return;
                }

                this.mSharedVariableIndex = (Dictionary<string, int>)null;
            }
            else
            {
                if (this.mSharedVariableIndex == null)
                {
                    this.mSharedVariableIndex = new Dictionary<string, int>(this.mVariables.Count);
                }
                else
                {
                    this.mSharedVariableIndex.Clear();
                }

                for (int index = 0; index < this.mVariables.Count; ++index)
                {
                    if (this.mVariables[index] != null)
                    {
                        this.mSharedVariableIndex.Add(this.mVariables[index].Name, index);
                    }
                }
            }
        }
    }
}