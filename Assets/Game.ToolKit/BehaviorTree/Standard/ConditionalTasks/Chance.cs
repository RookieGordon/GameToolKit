using System;
using Bonsai.Core;

namespace Bonsai.Standard
{
    [BonsaiNode("Conditional/", "Condition")]
    public partial class Chance : ConditionalTask
    {
        private Random _random = new Random();
        public override bool Condition()
        {
            // Return true if the probability is within the range [0, chance];
            return this._random.Next(0, 1) + 1 <= chance;
        }
    }
}