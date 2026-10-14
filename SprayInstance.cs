using System.Collections.Generic;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class SprayInstance : MonoBehaviour
    {
        private static readonly List<SprayInstance> ActiveInstances = new List<SprayInstance>();
        private static int _registryVersion;
        private bool _isRegistered;

        public string SourceFilePath { get; set; }
        public string SprayId { get; set; }
        public ulong OwnerClientId { get; set; }
        public byte[] ImageBytes { get; set; }
        public SpriteRenderer Renderer { get; set; }

        internal static int CopyActiveInstancesTo(List<SprayInstance> destination)
        {
            destination.Clear();

            for (int i = ActiveInstances.Count - 1; i >= 0; i--)
            {
                SprayInstance instance = ActiveInstances[i];
                if (instance == null)
                {
                    ActiveInstances.RemoveAt(i);
                    _registryVersion++;
                    continue;
                }

                destination.Add(instance);
            }

            destination.Reverse();
            return _registryVersion;
        }

        internal static int RegistryVersion
        {
            get { return _registryVersion; }
        }

        private void OnEnable()
        {
            if (_isRegistered)
                return;

            ActiveInstances.Add(this);
            _isRegistered = true;
            _registryVersion++;
        }

        private void OnDisable()
        {
            Unregister();
        }

        private void OnDestroy()
        {
            Unregister();
        }

        private void Unregister()
        {
            if (!_isRegistered)
                return;

            ActiveInstances.Remove(this);
            _isRegistered = false;
            _registryVersion++;
        }
    }
}
