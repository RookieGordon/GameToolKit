using System.Collections.Generic;
using UnityEngine;

namespace Bonsai.Standard
{
    public partial class Guard
    {
        [Tooltip(
            @"If true, then the guard will stay running until the child
      can be used (active guard count < max active guards), else
      the guard will immediately return.")]
        public bool waitUntilChildAvailable = false;

        [Tooltip("When the guard does not wait, should we return success of failure when skipping it?")]
        public bool returnSuccessOnSkip = false;

        [HideInInspector]
        public List<Guard> linkedGuards = new List<Guard>();
    }
}