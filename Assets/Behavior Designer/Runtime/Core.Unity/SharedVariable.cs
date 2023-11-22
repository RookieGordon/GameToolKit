using UnityEngine;

namespace BehaviorDesigner.Runtime
{
    public abstract partial class SharedVariable
    {
        [SerializeField]
        private bool mIsShared;

        [SerializeField]
        private bool mIsGlobal;

        [SerializeField]
        private bool mIsDynamic;

        [SerializeField]
        private string mName;

        [SerializeField]
        private string mToolTip;

        [SerializeField]
        private string mPropertyMapping;

        [SerializeField]
        private GameObject mPropertyMappingOwner;

        public GameObject PropertyMappingOwner
        {
            get => this.mPropertyMappingOwner;
            set => this.mPropertyMappingOwner = value;
        }
    }
}
