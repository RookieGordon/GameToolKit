using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    [BonsaiNode("Conditional/", "Condition")]
    public class Chance : ConditionalTask
    {
        private Random _random = new Random();
        
#if UNITY_EDITOR
        [UnityEngine.Tooltip("The probability that the condition succeeds.")] [UnityEngine.Range(0f, 1f)]
#endif
        public float chance = 0.5f;
        
        public override bool Condition()
        {
            // Return true if the probability is within the range [0, chance];
            return this._random.Next(0, 1) + 1 <= chance;
        }
    }
}