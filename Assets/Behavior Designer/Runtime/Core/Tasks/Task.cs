// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Runtime.Tasks.Task
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System.Collections;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks
{
  public abstract class Task
  {
    protected GameObject gameObject;
    protected Transform transform;
    [SerializeField]
    private NodeData nodeData;
    [SerializeField]
    private Behavior owner;
    [SerializeField]
    private int id = -1;
    [SerializeField]
    private string friendlyName = string.Empty;
    [SerializeField]
    private bool instant = true;
    private int referenceID = -1;
    private bool disabled;

    public virtual void OnAwake()
    {
    }

    public virtual void OnStart()
    {
    }

    public virtual TaskStatus OnUpdate() => TaskStatus.Success;

    public virtual void OnLateUpdate()
    {
    }

    public virtual void OnFixedUpdate()
    {
    }

    public virtual void OnEnd()
    {
    }

    public virtual void OnPause(bool paused)
    {
    }

    public virtual void OnConditionalAbort()
    {
    }

    public virtual float GetPriority() => 0.0f;

    public virtual float GetUtility() => 0.0f;

    public virtual void OnBehaviorRestart()
    {
    }

    public virtual void OnBehaviorComplete()
    {
    }

    public virtual void OnReset()
    {
    }

    public virtual void OnDrawGizmos()
    {
    }

    public virtual string OnDrawNodeText() => string.Empty;

    protected void StartCoroutine(string methodName) => this.Owner.StartTaskCoroutine(this, methodName);

    protected Coroutine StartCoroutine(IEnumerator routine) => this.Owner.StartCoroutine(routine);

    protected Coroutine StartCoroutine(string methodName, object value) => this.Owner.StartTaskCoroutine(this, methodName, value);

    protected void StopCoroutine(string methodName) => this.Owner.StopTaskCoroutine(methodName);

    protected void StopCoroutine(IEnumerator routine) => this.Owner.StopCoroutine(routine);

    protected void StopAllCoroutines() => this.Owner.StopAllTaskCoroutines();

    public virtual void OnCollisionEnter(Collision collision)
    {
    }

    public virtual void OnCollisionExit(Collision collision)
    {
    }

    public virtual void OnTriggerEnter(Collider other)
    {
    }

    public virtual void OnTriggerExit(Collider other)
    {
    }

    public virtual void OnCollisionEnter2D(Collision2D collision)
    {
    }

    public virtual void OnCollisionExit2D(Collision2D collision)
    {
    }

    public virtual void OnTriggerEnter2D(Collider2D other)
    {
    }

    public virtual void OnTriggerExit2D(Collider2D other)
    {
    }

    public virtual void OnControllerColliderHit(ControllerColliderHit hit)
    {
    }

    public virtual void OnAnimatorIK()
    {
    }

    public GameObject GameObject
    {
      set => this.gameObject = value;
    }

    public Transform Transform
    {
      set => this.transform = value;
    }

    protected T GetComponent<T>() where T : Component => this.gameObject.GetComponent<T>();

    protected Component GetComponent(System.Type type) => this.gameObject.GetComponent(type);

    protected void TryGetComponent<T>(out T component) where T : Component => this.gameObject.TryGetComponent<T>(out component);

    protected void TryGetComponent(System.Type type, out Component component) => this.gameObject.TryGetComponent(type, out component);

    protected GameObject GetDefaultGameObject(GameObject go) => (UnityEngine.Object) go == (UnityEngine.Object) null ? this.gameObject : go;

    public NodeData NodeData
    {
      get => this.nodeData;
      set => this.nodeData = value;
    }

    public Behavior Owner
    {
      get => this.owner;
      set => this.owner = value;
    }

    public int ID
    {
      get => this.id;
      set => this.id = value;
    }

    public virtual string FriendlyName
    {
      get => this.friendlyName;
      set => this.friendlyName = value;
    }

    public bool IsInstant
    {
      get => this.instant;
      set => this.instant = value;
    }

    public int ReferenceID
    {
      get => this.referenceID;
      set => this.referenceID = value;
    }

    public bool Disabled
    {
      get => this.disabled;
      set => this.disabled = value;
    }
  }
}
