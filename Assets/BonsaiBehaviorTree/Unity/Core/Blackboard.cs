namespace Bonsai.Core
{
#if UNITY_EDITOR
    public partial class Blackboard
    {
        [Newtonsoft.Json.JsonIgnore] public BlackboardProxy Proxy;
    }
#endif
}