using System.Text;
using Bonsai.Core;

namespace Bonsai.Standard
{
    [BonsaiNode("Tasks/", "Timer")]
    public partial class Wait : Task
    {
        public override void OnEnter()
        {
            timer.Start();
        }

        public override NodeStatus Run()
        {
            timer.Update(UnityEngine.Time.deltaTime);
            return timer.IsDone ? NodeStatus.Success : NodeStatus.Running;
        }

        public override void Description(StringBuilder builder)
        {
            builder.AppendFormat("Wait for {0:0.00}s", timer.interval);
        }
    }
}