// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.SharedVariable`1
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Reflection;
using UnityEngine;

namespace BehaviorDesigner.Runtime
{
  public abstract class SharedVariable<T> : SharedVariable
  {
    private Func<T> mGetter;
    private Action<T> mSetter;
    [SerializeField]
    protected T mValue;

    public override void InitializePropertyMapping(BehaviorSource behaviorSource)
    {
      if (!BehaviorManager.IsPlaying || string.IsNullOrEmpty(this.PropertyMapping))
        return;
      string[] strArray = this.PropertyMapping.Split('/');
      GameObject gameObject = (GameObject) null;
      try
      {
        gameObject = object.Equals((object) this.PropertyMappingOwner, (object) null) ? (behaviorSource.Owner.GetObject() as Behavior).gameObject : this.PropertyMappingOwner;
      }
      catch (Exception ex)
      {
        Behavior behavior = behaviorSource.Owner.GetObject() as Behavior;
        if ((UnityEngine.Object) behavior != (UnityEngine.Object) null)
        {
          if (behavior.AsynchronousLoad)
          {
            Debug.LogError((object) "Error: Unable to retrieve GameObject. Properties cannot be mapped while using asynchronous load.");
            return;
          }
        }
      }
      if ((UnityEngine.Object) gameObject == (UnityEngine.Object) null)
      {
        Debug.LogError((object) ("Error: Unable to find GameObject on " + behaviorSource.behaviorName + " for property mapping with variable " + this.Name));
      }
      else
      {
        Component component = gameObject.GetComponent(TaskUtility.GetTypeWithinAssembly(strArray[0]));
        if ((UnityEngine.Object) component == (UnityEngine.Object) null)
        {
          Debug.LogError((object) ("Error: Unable to find component on " + behaviorSource.behaviorName + " for property mapping with variable " + this.Name));
        }
        else
        {
          PropertyInfo property = ((object) component).GetType().GetProperty(strArray[1]);
          if (!(property != (PropertyInfo) null))
            return;
          MethodInfo getMethod = property.GetGetMethod();
          if (getMethod != (MethodInfo) null)
            this.mGetter = (Func<T>) Delegate.CreateDelegate(typeof (Func<T>), (object) component, getMethod);
          MethodInfo setMethod = property.GetSetMethod();
          if (!(setMethod != (MethodInfo) null))
            return;
          this.mSetter = (Action<T>) Delegate.CreateDelegate(typeof (Action<T>), (object) component, setMethod);
        }
      }
    }

    public T Value
    {
      get => this.mGetter != null ? this.mGetter() : this.mValue;
      set
      {
        if (this.mSetter != null)
          this.mSetter(value);
        else
          this.mValue = value;
      }
    }

    public override object GetValue() => (object) this.Value;

    public override void SetValue(object value)
    {
      if (this.mSetter != null)
        this.mSetter((T) value);
      else if (value is IConvertible)
        this.mValue = (T) Convert.ChangeType(value, typeof (T));
      else
        this.mValue = (T) value;
    }

    public override string ToString() => (object) this.Value == null ? "(null)" : this.Value.ToString();
  }
}
