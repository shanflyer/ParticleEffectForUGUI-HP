using System.Collections.Generic;
using Coffee.UIParticleInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Coffee.UIExtensions
{
    internal partial class UIParticleRenderer
    {
        // Explicit tag survives Unity's destroyed-object null semantics until Reset.
        internal bool isBridge { get; private set; }
        internal Renderer sourceRenderer => isBridge ? _bridgeSource : _renderer;
        internal int outputIndex => _index;
        private Renderer _bridgeSource;
        private MeshFilter _bridgeFilter;
        private Mesh _boundSourceMesh, _bridgeOutput, _lineScratch;
        private bool _originalForceRenderingOff;
        private int _lineRecoveryFrames;
        private CombineInstance[] _bridgeCombine;
        private List<Vector3> _lineVertices;
        private List<int> _lineIndices;
        private LineSnapshotValidator _lineValidator;

        private BridgeGeometrySnapshot _bridgeSnapshot;
        private Matrix4x4 _bridgeMatrix;
        private bool _bridgeGeometryValid, _bridgeGamma, _bridgeWasHidden, _bridgeHadModifiers;
        private ParticleBakeClock _bridgeClock;

        internal void InvalidateBridgeCache() { _bridgeGeometryValid = false; _bridgeSubmittedTexture = null; }

        internal void InvalidateSpriteMaskGeometry() { _spriteMask?.InvalidateGeometry(); }

        internal static bool CanBridge(Renderer source)
        {
            if (!source) return false;
            if (source is MeshRenderer)
            {
                if (!source.TryGetComponent<MeshFilter>(out var filter) || !filter.sharedMesh
                    || !filter.sharedMesh.isReadable || filter.sharedMesh.subMeshCount != 1) return false;
            }
            else if (!(source is TrailRenderer) && !(source is LineRenderer)) return false;
            source.GetSharedMaterials(s_Materials);
            var supported = s_Materials.Count == 1 && s_Materials[0];
            s_Materials.Clear();
            return supported;
        }

        internal void SetBridge(UIParticle parent, Renderer source)
        {
            _parent = parent;
            if (_bridgeCombine == null) _bridgeCombine = new CombineInstance[1];
            isBridge = true;
            _bridgeSource = source;
            source.TryGetComponent(out _bridgeFilter);
            _boundSourceMesh = _bridgeFilter ? _bridgeFilter.sharedMesh : null;
            _originalForceRenderingOff = source.forceRenderingOff;
            source.forceRenderingOff = true;
            maskable = parent.maskable;
            gameObject.layer = parent.gameObject.layer;
            material = _boundMaterial = source.sharedMaterial;
            _boundTexture = mainTexture;
            _isTrail = false;
            _lastBakeFrame = -1;
            _bridgeGeometryValid = false;
            _bridgeSubmittedTexture = null;
            _bridgeClock.Reset();
            _bridgeWasHidden = false;
            enabled = true;
            RecalculateClipping();
        }

        internal void MaintainBridgeSuppression()
        {
            if (isBridge && _bridgeSource) _bridgeSource.forceRenderingOff = true;
        }

        private bool BridgeBindingIsInvalid()
        {
            return !CanBridge(_bridgeSource)
                || _bridgeSource.GetComponentInParent<UIParticle>(true) != _parent
                || _boundMaterial != _bridgeSource.sharedMaterial || _boundTexture != mainTexture
                || (_bridgeSource is MeshRenderer && (!_bridgeFilter || _bridgeFilter.sharedMesh != _boundSourceMesh));
        }

        private void ReleaseBridge()
        {
            if (_bridgeSource) _bridgeSource.forceRenderingOff = _originalForceRenderingOff;
            _bridgeSource = null;
            _bridgeFilter = null;
            _boundSourceMesh = null;
            if (_bridgeCombine != null) _bridgeCombine[0].mesh = null;
            isBridge = false;
            _bridgeGeometryValid = false;
            _bridgeSnapshot = null;
            _bridgeClock.Reset();
            _lineValidator?.Reset();
            _lineRecoveryFrames = 0;
        }

        private void DestroyBridgeMeshes()
        {
            Misc.Destroy(_bridgeOutput);
            Misc.Destroy(_lineScratch);
            _bridgeOutput = _lineScratch = null;
        }

        private static Mesh CreateBridgeMesh(string name, IndexFormat format)
        {
            UIParticleProfiler.current.meshesCreated++;
            return new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave, indexFormat = format };
        }

        private void ClearBridgeOutput()
        {
            _bridgeGeometryValid = false;
            _bridgeSubmittedTexture = null;
            if (!_meshCleared) ClearCanvas();
            _meshCleared = true;
            _lastBounds = new Bounds();
            _lineValidator?.Reset();
        }

        private void UpdateBridge(Camera camera)
        {
            if (!_parent || !canvas || !isActiveAndEnabled || !_bridgeSource
                || !_bridgeSource.enabled || !_bridgeSource.gameObject.activeInHierarchy
                || _originalForceRenderingOff || !_parent.canRender)
            { ClearBridgeOutput(); return; }
            if (_lastBakeFrame == Time.frameCount) return;
            _lastBakeFrame = Time.frameCount;
            // A bridge is local to this consumer, so group visibility is irrelevant.
            if (!canvas.isActiveAndEnabled
                || (Application.isPlaying && UIParticle.earlyCull > 0 && alphaHidden))
            {
                _bridgeWasHidden = true;
                return; // Native Line/Trail source remains enabled and continues sampling.
            }
            var resumed = _bridgeWasHidden;
            _bridgeWasHidden = false;
            var probe = false;
            if (Application.isPlaying && UIParticle.earlyCull > 0 && clipHidden)
            {
                if (Time.unscaledTime < _nextCullProbeTime) return;
                _nextCullProbeTime = Time.unscaledTime + 0.1f;
                probe = true;
            }
            if (Application.isPlaying && UIParticle.bakeFPS > 0
                && !_bridgeClock.AdvanceAtTick(0, 0,
                    (long)System.Math.Floor(Time.unscaledTimeAsDouble * UIParticle.bakeFPS),
                    !_bridgeGeometryValid || resumed || probe || _forceBake, out _, out _)) return;
            _forceBake = false;
            var scale = _parent.scale3DForCalc.GetScaled(_parent.parentScale);
            if (!scale.IsVisible()) { ClearBridgeOutput(); return; }
            var origin = _parent.transform.position;
            var matrix = transform.worldToLocalMatrix * Matrix4x4.Translate(origin)
                * Matrix4x4.Scale(scale) * Matrix4x4.Translate(-origin);
            Mesh input;
            if (_bridgeSource is MeshRenderer)
            {
                input = _bridgeFilter ? _bridgeFilter.sharedMesh : null;
                if (!input || !input.isReadable || input.subMeshCount != 1) { ClearBridgeOutput(); return; }
                matrix *= _bridgeSource.localToWorldMatrix;
            }
            else
            {
                if (!camera) { ClearBridgeOutput(); return; }
                var trail = _bridgeSource as TrailRenderer;
                var line = _bridgeSource as LineRenderer;
                if ((trail && trail.positionCount < 2) || (line && line.positionCount < 2))
                { ClearBridgeOutput(); return; }
                if (trail && Application.isPlaying)
                {
                    if (Mathf.Max(Time.deltaTime, Time.unscaledDeltaTime) > 0.1f)
                    { _lineRecoveryFrames = 3; return; }
                    if (_lineRecoveryFrames > 0) { _lineRecoveryFrames--; return; }
                }
                if (!_lineScratch) _lineScratch = CreateBridgeMesh("UIParticle Line Scratch", IndexFormat.UInt32);
                if (_lineValidator == null)
                {
                    _lineValidator = new LineSnapshotValidator();
                    _lineVertices = new List<Vector3>();
                    _lineIndices = new List<int>();
                }
                _lineScratch.Clear(false);
                var started = UIParticleProfiler.Timestamp();
                // Unity 6.4: without useTransform, world-space lines/trails are already
                // world geometry. Only a local-space LineRenderer needs its source matrix.
                if (trail) trail.BakeMesh(_lineScratch, camera, false);
                else line.BakeMesh(_lineScratch, camera, false);
                UIParticleProfiler.EndStage(2, started);
                UIParticleProfiler.current.bakeOps++;
                UIParticleProfiler.current.bakedVertices += _lineScratch.vertexCount;
                UIParticle.s_FrameBakeOps++;
                UIParticle.s_FrameBakedVerts += _lineScratch.vertexCount;
                if (_lineScratch.vertexCount > MaxUiVertices)
                { ReportVertexLimit(_lineScratch.vertexCount); ClearBridgeOutput(); return; }
                _lineScratch.GetVertices(_lineVertices);
                _lineIndices.Clear();
                if (_lineScratch.subMeshCount > 0) _lineScratch.GetIndices(_lineIndices, 0);
                var result = _lineValidator.Evaluate(_lineVertices, _lineIndices);
                if (result == LineSnapshotValidator.Result.Empty) { ClearBridgeOutput(); return; }
                if (result != LineSnapshotValidator.Result.Valid)
                {
                    if (_lineValidator.expired)
                    {
                        ClearCanvas(); _meshCleared = true; _lastBounds = new Bounds();
                        // Keep rejection history: next finite snapshot may rebase.
                    }
                    return;
                }
                input = _lineScratch;
                if (line && !line.useWorldSpace) matrix *= _bridgeSource.localToWorldMatrix;
            }
            if (input.vertexCount == 0) { ClearBridgeOutput(); return; }
            if (input.vertexCount > MaxUiVertices)
            { ReportVertexLimit(input.vertexCount); ClearBridgeOutput(); return; }
            if (!_bridgeOutput) _bridgeOutput = CreateBridgeMesh("UIParticle Bridge Output", IndexFormat.UInt16);
            if (_bridgeSnapshot == null) _bridgeSnapshot = new BridgeGeometrySnapshot();
            var compareStarted = UIParticleProfiler.Timestamp();
            var contentChanged = _bridgeSnapshot.Capture(input);
            UIParticleProfiler.current.bridgeCompareMs += UIParticleProfiler.Milliseconds(compareStarted);
            var gamma = UIParticleProjectSettings.autoColorCorrection && canvas.ShouldGammaToLinearInMesh();
            var modifiers = InternalListPool<Component>.Rent();
            bool hasModifiers;
            try { GetComponents(typeof(IMeshModifier), modifiers); hasModifiers = modifiers.Count > 0; }
            finally { InternalListPool<Component>.Return(ref modifiers); }
            if (_bridgeGeometryValid && !contentChanged && _bridgeMatrix.Equals(matrix)
                && _bridgeGamma == gamma && !hasModifiers && !_bridgeHadModifiers)
            {
                UpdateBridgeMaterial();
                UIParticleProfiler.current.bridgeCacheHits++;
                return;
            }
            var combineStarted = UIParticleProfiler.Timestamp();
            _bridgeOutput.Clear(false);
            _bridgeCombine[0].mesh = input;
            _bridgeCombine[0].transform = matrix;
            try { _bridgeOutput.CombineMeshes(_bridgeCombine, true, true); }
            finally { _bridgeCombine[0].mesh = null; }
            if (gamma)
                _bridgeOutput.LinearToGamma();
            var components = InternalListPool<Component>.Rent();
            try
            {
                GetComponents(typeof(IMeshModifier), components);
                if (components.Count > 0)
                {
                    _bridgeOutput.CopyTo(s_VertexHelper);
                    for (var i = 0; i < components.Count; i++) ((IMeshModifier)components[i]).ModifyMesh(s_VertexHelper);
                    if (s_VertexHelper.currentVertCount > MaxUiVertices)
                    { ReportVertexLimit(s_VertexHelper.currentVertCount); ClearBridgeOutput(); return; }
                    s_VertexHelper.FillMesh(_bridgeOutput);
                }
            }
            finally
            {
                InternalListPool<Component>.Return(ref components);
                UIParticleProfiler.EndStage(3, combineStarted);
            }
            if (!_parent || !_bridgeSource || !isActiveAndEnabled) return;
            _bridgeOutput.RecalculateBounds();
            var bounds = _bridgeOutput.bounds;
            var center = bounds.center; center.z = 0; bounds.center = center;
            var size = bounds.size; size.z = 0; bounds.size = size;
            _bridgeOutput.bounds = _lastBounds = bounds;
            UpdateBridgeMaterial();
            var submitStarted = UIParticleProfiler.Timestamp();
            canvasRenderer.SetMesh(_bridgeOutput);
            _bridgeGeometryValid = true;
            _bridgeMatrix = matrix;
            _bridgeGamma = gamma;
            _bridgeHadModifiers = hasModifiers;
            _meshCleared = false;
            UIParticleProfiler.current.setMeshOps++;
            UIParticleProfiler.EndStage(4, submitStarted);
        }
        private void UpdateBridgeMaterial()
        {
            // Material animation is independent from geometry reuse, including MPB removal.
            if (_parent.m_AnimatableProperties.Length > 0 && materialForRendering)
                materialForRendering.CopyPropertiesFromMaterial(base.GetModifiedMaterial(material));
            UpdateMaterialProperties();
            var texture = materialForRendering ? materialForRendering.mainTexture : mainTexture;
            SetCanvasRendererMaterials(canvasRenderer);
            if (_bridgeSubmittedTexture != texture)
            {
                canvasRenderer.SetTexture(texture);
                _bridgeSubmittedTexture = texture;
            }
        }
        private Texture _bridgeSubmittedTexture;
    }
}
