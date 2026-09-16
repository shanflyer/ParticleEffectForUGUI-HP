using System;
using System.Collections.Generic;
using Coffee.UIParticleInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Coffee.UIExtensions
{
    /// <summary>Stencil contract for shaders used by the Canvas SpriteMask bridge.</summary>
    public static class UIParticleSpriteMask
    {
        private static readonly HashSet<Shader> s_Shaders = new HashSet<Shader>();

        /// <summary>
        /// Opt in an existing shader after verifying that its rendered pass uses Ref [_Stencil],
        /// Comp [_StencilComp], Pass [_StencilOp], ReadMask [_StencilReadMask],
        /// WriteMask [_StencilWriteMask], and Keep for Fail/ZFail. Properties alone are not enough.
        /// Call at startup in players as well as in the Editor. No source material is modified.
        /// </summary>
        public static void RegisterStencilShader(Shader shader)
        {
            if (!shader) throw new ArgumentNullException(nameof(shader));
            s_Shaders.Add(shader);
        }

        internal static bool Supports(Material material)
        {
            if (!material || !material.shader) return false;
            var shader = material.shader;
            // These shipped shaders have been audited. Custom shaders require an explicit contract.
            if (shader.name != "UI/Additive" && shader.name != "UI/Default"
                && !s_Shaders.Contains(shader)) return false;
            return material.HasProperty("_Stencil") && material.HasProperty("_StencilComp")
                && material.HasProperty("_StencilOp") && material.HasProperty("_StencilReadMask")
                && material.HasProperty("_StencilWriteMask");
        }
    }

    internal static class SpriteMaskResolver
    {
        private static SpriteMask[] s_Masks;

        // One scene query per HP update, lazy: projects without masked particles do not pay for it.
        internal static void BeginFrame() { s_Masks = null; }

        internal static SortingGroup Scope(Transform transform)
        {
            for (var t = transform; t; t = t.parent)
                if (t.TryGetComponent<SortingGroup>(out var group) && group.isActiveAndEnabled)
                    return group;
            return null;
        }

        internal static int Compare(int layerA, int orderA, int layerB, int orderB)
        {
            var layer = SortingLayer.GetLayerValueFromID(layerA).CompareTo(SortingLayer.GetLayerValueFromID(layerB));
            return layer != 0 ? layer : orderA.CompareTo(orderB);
        }

        internal static bool Affects(SpriteMask mask, ParticleSystemRenderer renderer)
        {
            if (!mask || !mask.enabled || !mask.gameObject.activeInHierarchy || !mask.sprite || !renderer) return false;
            if (SpriteMaskNativeRendering.WasForceRenderingOff(mask)) return false;
            var maskScope = Scope(mask.transform);
            var scope = Scope(renderer.transform);
            var layer = renderer.sortingLayerID;
            var order = renderer.sortingOrder;
            // Ancestor/global masks can affect a nested group as one sorted object. A mask
            // local to a sibling/descendant group cannot escape that group. sortAtRoot skips
            // the enclosing group, matching Unity's nested SortingGroup sorting boundary.
            while (scope != maskScope)
            {
                if (!scope) return false;
                layer = scope.sortingLayerID;
                order = scope.sortingOrder;
                scope = scope.sortAtRoot ? null : Scope(scope.transform.parent);
            }
            return !mask.isCustomRangeActive
                || (Compare(layer, order, mask.backSortingLayerID, mask.backSortingOrder) > 0
                    && Compare(layer, order, mask.frontSortingLayerID, mask.frontSortingOrder) <= 0);
        }

        internal static void Resolve(ParticleSystemRenderer renderer, List<SpriteMask> results)
        {
            results.Clear();
            if (!renderer || renderer.maskInteraction == SpriteMaskInteraction.None) return;
            if (s_Masks == null) s_Masks = UnityEngine.Object.FindObjectsOfType<SpriteMask>();
            foreach (var mask in s_Masks)
                if (Affects(mask, renderer)) results.Add(mask);
            results.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        }
    }

    /// <summary>Per-output state: never shared with the simulation owner's stencil materials.</summary>
    internal sealed class SpriteMaskDrawScope : IDisposable
    {
        internal const int Bit = 128;
        private readonly UIParticleRenderer _owner;
        private readonly List<SpriteMask> _masks = new List<SpriteMask>();
        private readonly List<UIParticleSpriteMaskGraphic> _writers = new List<UIParticleSpriteMaskGraphic>();
        private UIParticleSpriteMaskGraphic _before, _after;
        private Material _material;
        private string _error;
        private int _reference, _readMask;
        private bool _active;
        internal bool blocked { get; private set; }

        internal SpriteMaskDrawScope(UIParticleRenderer owner) { _owner = owner; }

        internal void Prepare(ParticleSystemRenderer source, UIParticle parent, Matrix4x4 worldToOutput)
        {
            var previousBlocked = blocked;
            var previousActive = _active;
            var oldReference = _reference;
            var oldReadMask = _readMask;
            blocked = false;
            _active = false;
            string error = null;
            SpriteMaskResolver.Resolve(source, _masks);
            var interaction = source ? source.maskInteraction : SpriteMaskInteraction.None;
            if (interaction != SpriteMaskInteraction.None)
            {
                if (_masks.Count == 0)
                    blocked = interaction == SpriteMaskInteraction.VisibleInsideMask;
                else
                {
                    var depth = MaskUtilities.GetStencilDepth(_owner.transform,
                        MaskUtilities.FindRootSortOverrideCanvas(_owner.transform));
                    if (depth >= 8)
                        error = "SpriteMask needs one free stencil bit; eight parent UGUI Masks already use all bits.";
                    else if (!UIParticleSpriteMask.Supports(_owner.material))
                        error = "Shader '" + (_owner.material ? _owner.material.shader.name : "<null>")
                            + "' has no verified UI stencil contract. Keep its shading; add only stencil state if missing,"
                            + " then call UIParticleSpriteMask.RegisterStencilShader after auditing its pass.";
                    else if (!UIParticleSpriteMaskGraphic.shader)
                        error = "Missing Resources/UIParticleSpriteMask shader.";
                    else
                    {
                        // All masks write the SAME bit: their overlap is a union, not parity/intersection.
                        var parentBits = _owner.maskable ? (1 << depth) - 1 : 0;
                        _reference = parentBits | (interaction == SpriteMaskInteraction.VisibleInsideMask ? Bit : 0);
                        _readMask = parentBits | Bit;
                        _active = parent.canRender && source.gameObject.activeInHierarchy;
                        if (_active)
                        {
                            EnsureNodes(parent);
                            SetNodesActive(true);
                            _before.Configure(null, Matrix4x4.identity, 0, 0, Bit, true);
                            for (var i = 0; i < _masks.Count; i++)
                                _writers[i].Configure(_masks[i], worldToOutput * _masks[i].transform.localToWorldMatrix,
                                    parentBits | Bit, parentBits, Bit, false);
                            _after.Configure(null, Matrix4x4.identity, 0, 0, Bit, true);
                            ArrangeNodes();
                        }
                    }
                }
            }
            if (error != null)
            {
                blocked = true;
                if (_error != error) Debug.LogError("[UIParticle SpriteMask] " + error + " Output suppressed.", parent);
            }
            _error = error;
            SetNodesActive(_active);
            if (previousBlocked != blocked || previousActive != _active || oldReference != _reference || oldReadMask != _readMask)
                _owner.SetMaterialDirty();
        }

        private void EnsureNodes(UIParticle parent)
        {
            if (!_before) _before = UIParticleSpriteMaskGraphic.Create(parent, "Initialize");
            if (!_after) _after = UIParticleSpriteMaskGraphic.Create(parent, "Clear");
            while (_writers.Count < _masks.Count)
                _writers.Add(UIParticleSpriteMaskGraphic.Create(parent, "Write"));
        }

        private void ArrangeNodes()
        {
            // Each interval is contiguous in Canvas hierarchy order, including trails and replicas.
            var ownerIndex = _owner.transform.GetSiblingIndex();
            var ordered = _before.transform.GetSiblingIndex() == ownerIndex - _masks.Count - 1
                && _after.transform.GetSiblingIndex() == ownerIndex + 1;
            for (var i = 0; ordered && i < _masks.Count; i++)
                ordered &= _writers[i].transform.GetSiblingIndex() == ownerIndex - _masks.Count + i;
            if (ordered) return;
            PlaceBefore(_before.transform, _owner.transform);
            for (var i = 0; i < _masks.Count; i++) PlaceBefore(_writers[i].transform, _owner.transform);
            var target = _owner.transform.GetSiblingIndex();
            if (_after.transform.GetSiblingIndex() != target + 1)
            {
                var index = _after.transform.GetSiblingIndex() < target ? target : target + 1;
                _after.transform.SetSiblingIndex(index);
            }
        }

        private static void PlaceBefore(Transform node, Transform target)
        {
            var index = target.GetSiblingIndex();
            if (node.GetSiblingIndex() < index) index--;
            if (node.GetSiblingIndex() != index) node.SetSiblingIndex(index);
        }

        private void SetNodesActive(bool active)
        {
            if (_before) _before.gameObject.SetActive(active);
            if (_after) _after.gameObject.SetActive(active);
            for (var i = 0; i < _writers.Count; i++)
                if (_writers[i]) _writers[i].gameObject.SetActive(active && i < _masks.Count);
        }

        internal Material Modify(Material source)
        {
            if (!_active || !source) return source;
            if (!_material || _material.shader != source.shader)
            {
                Misc.Destroy(_material);
                _material = new Material(source) { hideFlags = HideFlags.HideAndDontSave };
            }
            _material.CopyPropertiesFromMaterial(source);
            _material.SetInt("_Stencil", _reference);
            _material.SetInt("_StencilComp", (int)CompareFunction.Equal);
            _material.SetInt("_StencilOp", (int)StencilOp.Keep);
            _material.SetInt("_StencilReadMask", _readMask);
            _material.SetInt("_StencilWriteMask", 0);
            return _material;
        }

        public void Dispose()
        {
            ReleaseNode(_before);
            ReleaseNode(_after);
            foreach (var node in _writers) ReleaseNode(node);
            _writers.Clear();
            Misc.Destroy(_material);
            _material = null;
            _active = blocked = false;
        }

        private static void ReleaseNode(UIParticleSpriteMaskGraphic node)
        {
            if (!node) return;
            node.canvasRenderer.Clear();
            node.enabled = false;
            // Reset/OnDisable can run while Unity is deactivating the entire parent. Destroying
            // its siblings immediately is illegal then; clear now, dispose after the callback.
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (node) UnityEngine.Object.DestroyImmediate(node.gameObject);
                };
                return;
            }
#endif
            UnityEngine.Object.Destroy(node.gameObject);
        }
    }
}
