// Exercise actual utility sources using pure managed representations of Unity data.
using System;
using System.Collections.Generic;
using Coffee.UIParticleInternal;
using UnityEngine;

namespace UnityEngine
{
    public class Object
    {
        public int hash;
        public override int GetHashCode() => hash == 0 ? base.GetHashCode() : hash;
        public static implicit operator bool(Object obj) => !ReferenceEquals(obj, null);
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        public void Scale(Vector3 b) { x *= b.x; y *= b.y; z *= b.z; }
        public void Set(float a, float b, float c) { x = a; y = b; z = c; }
    }
    public class Transform : Object { public Vector3 position; public Vector3 InverseTransformPoint(Vector3 p) => p; }
    public static class Mathf
    {
        public static bool Approximately(float a, float b) => Math.Abs(a - b) < 0.000001f;
        public static float Abs(float x) => Math.Abs(x);
        public static float Sign(float x) => x >= 0 ? 1 : -1;
        public static int NextPowerOfTwo(int n) { int i = 1; while (i < n) i *= 2; return i; }
    }
    public enum ParticleSystemRenderMode { Billboard, Mesh, None }
    public enum ParticleSystemSimulationSpace { Local, World, Custom }
    public enum ParticleSystemAnimationMode { Grid, Sprites }
    public class Mesh : Object { }
    public class Texture2D : Object { }
    public class Sprite : Object { public Texture2D texture; }
    public class Material : Object { public int renderQueue; }
    public static class SortingLayer { public static int GetLayerValueFromID(int id) => id; }
    public class ParticleSystemRenderer : Object
    {
        public ParticleSystemRenderMode renderMode; public Mesh mesh;
        public Material sharedMaterial, trailMaterial; public int sortingLayerID, sortingOrder; public float sortingFudge;
    }
    public class ParticleSystem : Object
    {
        public struct Particle { }
        public class MainModule { public ParticleSystemSimulationSpace simulationSpace; public Transform customSimulationSpace; }
        public struct ShapeModule
        {
            readonly ParticleSystem owner;
            public ShapeModule(ParticleSystem p) { owner = p; }
            public bool enabled => true;
            public bool alignToDirection => true;
            public Vector3 scale { get => owner.shapeScale; set => owner.shapeScale = value; }
        }
        public class TextureSheetAnimationModule
        {
            public bool enabled; public ParticleSystemAnimationMode mode; public int spriteCount;
            public Sprite GetSprite(int i) => null;
        }
        public class SubEmittersModule
        {
            public bool enabled; public int subEmittersCount;
            public ParticleSystem GetSubEmitterSystem(int i) => null;
        }
        public Transform transform = new Transform(); public ParticleSystemRenderer renderer = new ParticleSystemRenderer();
        public MainModule main = new MainModule(); public Vector3 shapeScale;
        public ShapeModule shape => new ShapeModule(this);
        public TextureSheetAnimationModule textureSheetAnimation = new TextureSheetAnimationModule();
        public SubEmittersModule subEmitters = new SubEmittersModule();
        public T GetComponent<T>() where T : class => renderer as T;
    }
}
namespace Coffee.UIParticleInternal
{
    static class SpriteExtension { public static Texture2D GetActualTexture(this Sprite s) => s.texture; }
}
static class UtilityHarness
{
    static int passed, failed;
    static void Assert(bool x, string m) { if (!x) throw new Exception(m); }
    static void Test(string name, Action body)
    {
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static int Main()
    {
        Test("zero shape axes are written back to the particle system", () => {
            var ps = new ParticleSystem(); ps.ValidateShape();
            Assert(ps.shapeScale.x > 0 && ps.shapeScale.y > 0 && ps.shapeScale.z > 0, "shape still has zero axes");
        });
        Test("shape validation preserves nonzero axis signs and values", () => {
            var ps = new ParticleSystem { shapeScale = new Vector3(-2, 0, 3) }; ps.ValidateShape();
            Assert(ps.shapeScale.x == -2 && ps.shapeScale.z == 3 && ps.shapeScale.y > 0, "changed nonzero axes");
        });
        Test("missing particle renderer cannot enter native baking", () => {
            Assert(!ParticleSystemExtensions.CanBakeMesh(null), "accepted null renderer");
            Assert(!new ParticleSystemRenderer { renderMode = ParticleSystemRenderMode.Mesh }.CanBakeMesh(), "accepted missing mesh");
        });
        Test("sorting tolerates removed systems and missing renderers", () => {
            var list = new List<ParticleSystem> { new ParticleSystem(), null, new ParticleSystem { renderer = null } };
            list.SortForRendering(new Transform(), false); Assert(list[0] == null, "null ordering inconsistent");
        });
        Test("equal render keys preserve original order", () => {
            var material = new Material(); var list = new List<ParticleSystem>();
            for (int i = 0; i < 40; i++) { var ps = new ParticleSystem(); ps.renderer.sharedMaterial = material; list.Add(ps); }
            var before = list.ToArray(); list.SortForRendering(new Transform(), false);
            for (int i = 0; i < before.Length; i++) Assert(list[i] == before[i], "unstable ties");
        });
        Test("material hash comparison cannot overflow", () => {
            var low = new ParticleSystem(); low.renderer.sharedMaterial = new Material { hash = int.MinValue };
            var high = new ParticleSystem(); high.renderer.sharedMaterial = new Material { hash = int.MaxValue };
            var list = new List<ParticleSystem> { high, low }; list.SortForRendering(new Transform(), true);
            Assert(list[0] == low, "integer subtraction overflowed");
        });
        Test("non-finite scale is never visible to native baking", () => {
            Assert(!new Vector3(float.PositiveInfinity, 1, 1).IsVisible(), "infinite scale accepted");
            Assert(!new Vector3(float.NaN, 1, 1).IsVisible(), "NaN accepted");
            Assert(new Vector3(-1, 1, 1).IsVisible(), "negative scale rejected");
        });
        Console.WriteLine($"RESULT {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
    }
}
