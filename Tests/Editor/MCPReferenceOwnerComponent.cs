using System;
using UnityEngine;

namespace UnityMCP.Editor.Tests
{
    [Serializable]
    public sealed class MCPManagedComponentReference
    {
        [SerializeField]
        private Component component;

        public Component Component
        {
            get => component;
            set => component = value;
        }
    }

    public sealed class MCPReferenceOwnerComponent : MonoBehaviour
    {
        [SerializeField]
        private Component directReference;

        [SerializeReference]
        private MCPManagedComponentReference managedReference = new MCPManagedComponentReference();

        public Component DirectReference => directReference;
        public Component ManagedReference => managedReference.Component;

        public void SetReferences(Component component)
        {
            directReference = component;
            managedReference.Component = component;
        }
    }
}
