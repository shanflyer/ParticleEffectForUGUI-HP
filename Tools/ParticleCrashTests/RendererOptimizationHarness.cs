// Selected production method bodies are compiled with managed stand-ins only.
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering { public enum IndexFormat { UInt16 } }
namespace UnityEngine
{
    public enum HideFlags { HideAndDontSave }
    public struct Vector3
    {
        public int value;
        public static Vector3 zero => default;
        public static bool operator ==(Vector3 a, Vector3 b) => a.value == b.value;
        public static bool operator !=(Vector3 a, Vector3 b) => a.value != b.value;
        public override bool Equals(object obj) => obj is Vector3 other && this == other;
        public override int GetHashCode() => value;
    }
    public struct Bounds { public Vector3 extents; }
    public struct Rect { public bool overlaps; public bool Overlaps(Rect other, bool inverse) => overlaps; }
    public static class UISystemProfilerApi { public static void AddMarker(string name, object obj) { } }
    public class CullEvent { public int calls; public void Invoke(bool value) { calls++; } }
    public class Graphic { public virtual void Cull(Rect rect, bool valid) { } }
    public class Texture { }
    public class Material
    {
        public int textureReads;
        private Texture _texture;
        public Texture mainTexture { get { textureReads++; return _texture; } set { _texture = value; } }
    }
    public class Mesh
    {
        public static int live, attempts, failAt;
        public bool destroyed;
        public string name; public HideFlags hideFlags; public IndexFormat indexFormat;
        public Mesh() { if (++attempts == failAt) throw new Exception("injected allocation failure"); live++; }
    }
    public struct CombineInstance { public Mesh mesh; }
    public class CanvasRenderer
    {
        public bool cull;
        public int materialCount, materialCalls;
        public Material last;
        public void SetMaterial(Material value, int index) { last = value; materialCalls++; }
        public void Clear() { materialCount = 0; last = null; }
    }
    public class ParticleSystem
    {
        public struct Trails { public bool enabled; }
        public Trails trails;
        public Texture sprite;
        public int spriteReads;
        public Texture GetTextureForSprite() { spriteReads++; return sprite; }
    }
    public class ParticleSystemRenderer { public Material sharedMaterial, trailMaterial; }
}
namespace Coffee.UIParticleInternal
{
    public static class Misc
    {
        public static void Destroy(Mesh mesh)
        {
            if (mesh == null) return;
            if (mesh.destroyed) throw new Exception("double destroy");
            mesh.destroyed = true; Mesh.live--;
        }
    }
}
namespace Coffee.UIExtensions
{
    internal class UIParticle { public static int earlyCull; public object[] m_AnimatableProperties = Array.Empty<object>(); }
    internal partial class UIParticleRenderer : Graphic
    {
        private bool _forceBake, _staticValid, _meshCleared, _uguiClipCulled;
        private float _nextCullProbeTime;
        private Bounds _lastBounds = new Bounds { extents = new Vector3 { value = 1 } };
        private Rect rootCanvasRect;
        private CullEvent onCullStateChanged = new CullEvent();
        private void OnCullingChanged() { }
        private CombineInstance[] _mergedCombines;
        private Material[] _mergedSubmeshMaterials, _mergedMaterials;
        private Texture[] _mergedTextures;
        private bool[] _mergedIsTrail;
        private int[] _mergedTrailIdx;
        private ParticleSystem[] _mergedSystems;
        private ParticleSystemRenderer[] _mergedPsRenderers;
        private bool _materialsDirty = true, _mergedUniform = true, _isTrail, _boundTrails;
        private Material _submittedMaterial, _boundMaterial;
        private Texture _boundTexture;
        private UIParticle _parent = new UIParticle();
        private ParticleSystem _particleSystem;
        private ParticleSystemRenderer _renderer;
        public CanvasRenderer canvasRenderer = new CanvasRenderer();
        public Material materialForRendering = new Material();
        public Texture mainTexture => _isTrail ? null : _particleSystem?.GetTextureForSprite();

        static int passed, failed;
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        static void Test(string name, Action body)
        {
            try { body(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
            finally { Mesh.failAt = 0; }
        }
        static UIParticleRenderer Merged(int count)
        {
            var result = new UIParticleRenderer
            {
                _mergedSystems = new ParticleSystem[count], _mergedPsRenderers = new ParticleSystemRenderer[count],
                _mergedSubmeshMaterials = new Material[count], _mergedMaterials = new Material[count],
                _mergedTextures = new Texture[count], _mergedIsTrail = new bool[count], _mergedTrailIdx = new int[count]
            };
            for (var i = 0; i < count; i++)
            {
                result._mergedSystems[i] = new ParticleSystem();
                result._mergedPsRenderers[i] = new ParticleSystemRenderer { sharedMaterial = result.materialForRendering };
                result._mergedMaterials[i] = result.materialForRendering;
            }
            result._particleSystem = result._mergedSystems[0]; result._renderer = result._mergedPsRenderers[0];
            return result;
        }
        static int Main()
        {
            Test("clip reappearance invalidates paused cache and forces next bake", () => {
                UIParticle.earlyCull = 1;
                var r = Merged(1); r.Cull(new Rect { overlaps = false }, true);
                r._staticValid = true; r._meshCleared = true; r._forceBake = false; r._nextCullProbeTime = 10;
                r.Cull(new Rect { overlaps = true }, true);
                Assert(r._forceBake && !r._staticValid && !r._meshCleared && !r.canvasRenderer.cull && r._nextCullProbeTime == 0, "stale mesh on reappearance");
                r._forceBake = false; r.Cull(new Rect { overlaps = true }, true);
                Assert(!r._forceBake, "stable visibility bypasses bake schedule every frame");
            });
            Test("invalid clip recovers even when previous bounds are empty", () => {
                UIParticle.earlyCull = 2; var r = Merged(1); r._lastBounds = default;
                r.Cull(new Rect { overlaps = true }, false); r._forceBake = false;
                r.Cull(new Rect { overlaps = true }, true);
                Assert(r._forceBake && r.canvasRenderer.cull, "empty mesh did not request refill or displayed prematurely");
            });
            Test("disabling early cull restores fresh geometry and clears cached clip", () => {
                UIParticle.earlyCull = 1; var r = Merged(1); r.Cull(default, true);
                r._forceBake = false; r._staticValid = true; UIParticle.earlyCull = 0;
                r.Cull(new Rect { overlaps = true }, true);
                Assert(r._forceBake && !r._staticValid && !r._uguiClipCulled && !r.canvasRenderer.cull, "cull toggle left stale state");
            });
            Test("unchanged material is submitted once across repeated meshes", () => {
                var r = Merged(3); for (var i = 0; i < 100; i++) r.SetCanvasRendererMaterials(r.canvasRenderer);
                Assert(r.canvasRenderer.materialCalls == 1, "redundant native material submissions");
            });
            Test("clearing a canvas forces material restoration", () => {
                var r = Merged(2); r.SetCanvasRendererMaterials(r.canvasRenderer); r.ClearCanvas();
                r.SetCanvasRendererMaterials(r.canvasRenderer);
                Assert(r.canvasRenderer.materialCalls == 2 && r.canvasRenderer.materialCount == 1, "material missing after clear");
            });
            Test("changed stencil material is submitted on its own replica", () => {
                var a = Merged(2); var b = Merged(2);
                a.SetCanvasRendererMaterials(a.canvasRenderer); b.SetCanvasRendererMaterials(b.canvasRenderer);
                Assert(a.canvasRenderer.last != b.canvasRenderer.last, "replica used primary material");
                var changed = new Material(); b.materialForRendering = changed; b.SetCanvasRendererMaterials(b.canvasRenderer);
                Assert(b.canvasRenderer.last == changed && b.canvasRenderer.materialCalls == 2, "changed stencil ignored");
            });
            Test("external material count reset is repaired", () => {
                var r = Merged(2); r.SetCanvasRendererMaterials(r.canvasRenderer); r.canvasRenderer.Clear();
                r.SetCanvasRendererMaterials(r.canvasRenderer); Assert(r.canvasRenderer.materialCalls == 2, "external clear not detected");
            });
            Test("dirty material with unchanged reference is resubmitted", () => {
                var r = Merged(2); r.SetCanvasRendererMaterials(r.canvasRenderer); r._materialsDirty = true;
                r.SetCanvasRendererMaterials(r.canvasRenderer); Assert(r.canvasRenderer.materialCalls == 2, "material dirtiness ignored");
            });
            Test("replica binding does not allocate intermediate meshes", () => {
                var before = Mesh.live; var r = Merged(5);
                Assert(r._mergedCombines == null && Mesh.live == before, "eager mesh allocation");
            });
            Test("first simulation allocates once and reuses intermediates", () => {
                var before = Mesh.live; var r = Merged(5); r.EnsureMergedMeshes(); var first = r._mergedCombines[0].mesh;
                r.EnsureMergedMeshes(); Assert(Mesh.live == before + 5 && first == r._mergedCombines[0].mesh, "meshes not reused");
                r.ReleaseMergedMeshes(); Assert(Mesh.live == before, "meshes leaked");
            });
            Test("simulation ownership release permits later reacquisition", () => {
                var before = Mesh.live; var r = Merged(2); r.EnsureMergedMeshes(); r.ReleaseMergedMeshes(); r.ReleaseMergedMeshes();
                r.EnsureMergedMeshes(); Assert(Mesh.live == before + 2, "reacquisition failed"); r.ReleaseMergedMeshes();
            });
            Test("partial mesh allocation can be cleaned up", () => {
                var before = Mesh.live; var r = Merged(4); Mesh.failAt = Mesh.attempts + 2;
                try { r.EnsureMergedMeshes(); } catch (Exception) { }
                r.ReleaseMergedMeshes(); Assert(Mesh.live == before, "partial allocation leaked");
            });
            Test("cached merged binding accepts unchanged configuration", () => {
                var r = Merged(3); Assert(!r.bindingIsInvalid, "valid binding invalidated");
            });
            Test("binding reads each sprite once and shared material texture once", () => {
                var r = Merged(5); Assert(!r.bindingIsInvalid, "valid binding invalidated");
                foreach (var ps in r._mergedSystems) Assert(ps.spriteReads == 1, "repeated sprite query");
                Assert(r.materialForRendering.textureReads == 1, "repeated shared material query");
            });
            Test("shared texture edits are detected on the next validation pass", () => {
                var r = Merged(3); Assert(!r.bindingIsInvalid, "initial binding wrong");
                r.materialForRendering.mainTexture = new Texture();
                Assert(r.bindingIsInvalid, "material edit hidden by cache");
            });
            Test("sprite override change is detected even if effective texture is unchanged", () => {
                var r = Merged(2); var texture = new Texture(); r.materialForRendering.mainTexture = texture;
                for (var i = 0; i < 2; i++) { r._mergedTextures[i] = texture; r._mergedSystems[i].sprite = texture; }
                r._boundTexture = texture; Assert(!r.bindingIsInvalid, "sprite binding wrong");
                r._mergedSystems[0].sprite = null;
                Assert(r.bindingIsInvalid, "raw Canvas texture override change missed");
            });
            Test("trails reuse body material texture but still detect trail material edits", () => {
                var r = Merged(3);
                for (var i = 0; i < 3; i++) {
                    r._mergedSystems[i].trails.enabled = true; r._mergedIsTrail[i] = true;
                    r._mergedTrailIdx[i] = i; r._mergedSubmeshMaterials[i] = r.materialForRendering;
                    r._mergedPsRenderers[i].trailMaterial = r.materialForRendering;
                }
                Assert(!r.bindingIsInvalid && r.materialForRendering.textureReads == 1, "trail reread texture");
                r._mergedPsRenderers[2].trailMaterial = new Material();
                Assert(r.bindingIsInvalid, "non-first trail material edit missed");
            });
            Test("bound source identity is respected after temporary source swap", () => {
                var r = Merged(3); var texture = new Texture(); r.materialForRendering.mainTexture = texture;
                for (var i = 0; i < 3; i++) r._mergedTextures[i] = texture;
                r._particleSystem = r._mergedSystems[2]; r._particleSystem.sprite = texture; r._boundTexture = texture;
                Assert(!r.bindingIsInvalid, "bound source confused with first source");
                Assert(r._particleSystem.spriteReads == 1, "bound source queried twice");
            });
            Test("cached binding detects non-first material replacement", () => {
                var r = Merged(3); r._mergedPsRenderers[2].sharedMaterial = new Material();
                Assert(r.bindingIsInvalid, "material replacement missed");
            });
            Test("cached binding detects non-first sprite replacement", () => {
                var r = Merged(3); r._mergedSystems[2].sprite = new Texture();
                Assert(r.bindingIsInvalid, "sprite replacement missed");
            });
            Test("cached binding detects trail layout changes", () => {
                var r = Merged(3); r._mergedSystems[1].trails.enabled = true;
                Assert(r.bindingIsInvalid, "trail activation missed");
            });
            Console.WriteLine($"RESULT {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
    }
}
