namespace BehaviorDesigner.Runtime.Tasks
{
    public partial class ConditionalEvaluator
    {
        public override void OnAwake()
        {
            if (conditionalTask != null)
            {
                conditionalTask.Owner = Owner;
                conditionalTask.GameObject = gameObject;
                conditionalTask.Transform = transform;
                conditionalTask.OnAwake();
            }
        }
    }
}