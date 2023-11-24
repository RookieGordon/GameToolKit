using System.Collections;
using UnityEngine;

namespace BehaviorDesigner.Runtime.Tasks
{
    public abstract partial class Task
    {
        protected GameObject gameObject;
        
        protected Transform transform;
        
        [SerializeField] private NodeData nodeData;
        
        [SerializeField] private Behavior owner;
        
        [SerializeField] private int id = -1;
        
        [SerializeField] private string friendlyName = string.Empty;
        
        [SerializeField] private bool instant = true;
        
        public GameObject GameObject
        {
            set => this.gameObject = value;
        }

        public Transform Transform
        {
            set => this.transform = value;
        }
        
        protected void StartCoroutine(string methodName) => this.Owner.StartTaskCoroutine(this, methodName);

        protected Coroutine StartCoroutine(IEnumerator routine) => this.Owner.StartCoroutine(routine);

        protected Coroutine StartCoroutine(string methodName, object value) =>
            this.Owner.StartTaskCoroutine(this, methodName, value);

        protected void StopCoroutine(string methodName) => this.Owner.StopTaskCoroutine(methodName);

        protected void StopCoroutine(IEnumerator routine) => this.Owner.StopCoroutine(routine);

        protected void StopAllCoroutines()
        {
            this.Owner.StopAllTaskCoroutines();
        }
        
        public virtual void OnAnimatorIK()
        {
        }
        
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
        
        protected T GetComponent<T>() where T : Component
        {
            return this.gameObject.GetComponent<T>();
        }

        protected Component GetComponent(System.Type type)
        {
            return this.gameObject.GetComponent(type);
        }

        protected void TryGetComponent<T>(out T component) where T : Component
        {
            this.gameObject.TryGetComponent<T>(out component);
        }

        protected void TryGetComponent(System.Type type, out Component component)
        {
            this.gameObject.TryGetComponent(type, out component);
        }

        protected GameObject GetDefaultGameObject(GameObject go)
        {
            return (UnityEngine.Object)go == (UnityEngine.Object)null ? this.gameObject : go;
        }
    }
}