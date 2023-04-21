namespace Bonsai.Utility
{
#if UNITY_EDITOR
    public partial class Timer
    {
        /// <summary>
        /// The wait time for the timer in seconds.
        /// </summary>
        [UnityEngine.Min(0)] public float interval = 1f;

        /// <summary>
        /// The random deviation of the interval. 
        /// <para>
        /// If not zero, a random value between [-deviation, +deviation] 
        /// is added to <see cref="interval"/> on <see cref="Start"/>
        /// </para>
        /// </summary>
        [UnityEngine.Tooltip("Adds a random range value to the interval between [-Deviation, +Deviation]")]
        [UnityEngine.Min(0)]
        public float deviation = 0f;
    }
#endif
}