using UnityEngine;

namespace Bonsai.Standard
{
    public partial class Chance
    {
        [Tooltip("The probability that the condition succeeds.")] [Range(0f, 1f)]
        public float chance = 0.5f;
    }
}