using UnityEngine;

namespace FxUIParticleTest
{
    /// <summary>Moves native/HP masks identically; Inspector switches also exercise object pooling.</summary>
    public sealed class SpriteMaskComparisonDriver : MonoBehaviour
    {
        public SpriteMask[] masks;
        public bool animateMasks = true;
        public bool poolMasks;
        public bool lowAlphaCutoff;
        private Vector3[] _positions;

        private void Start()
        {
            _positions = new Vector3[masks.Length];
            for (var i = 0; i < masks.Length; i++) _positions[i] = masks[i].transform.localPosition;
        }

        private void Update()
        {
            if (_positions == null) return;
            for (var i = 0; i < masks.Length; i++)
            {
                var mask = masks[i];
                if (!mask) continue;
                mask.gameObject.SetActive(!poolMasks || Mathf.Sin(Time.time * 2) > -.3f);
                mask.alphaCutoff = lowAlphaCutoff ? .15f : .65f;
                mask.transform.localPosition = _positions[i] + (animateMasks
                    ? new Vector3(Mathf.Sin(Time.time) * .35f, Mathf.Cos(Time.time * .8f) * .15f, 0) : Vector3.zero);
                mask.transform.localRotation = animateMasks ? Quaternion.Euler(0, 0, Time.time * 25) : Quaternion.identity;
            }
        }
    }
}
