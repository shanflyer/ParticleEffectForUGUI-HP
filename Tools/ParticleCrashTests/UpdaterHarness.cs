// Runs the actual UIParticleUpdater.cs with managed stand-ins. No Unity process,
// native ParticleSystem calls, meshes, graphics driver, or pressure scene is used.
using System;
using System.Collections.Generic;
using System.Reflection;
using Coffee.UIExtensions;

namespace UnityEngine
{
    public static class Debug { public static int errors; public static void LogException(Exception e, object context) { errors++; } }
    public static class Time { public static int frameCount; }
    public enum RuntimeInitializeLoadType { SubsystemRegistration, BeforeSceneLoad }
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType t) { } }
}
namespace UnityEditor
{
    public sealed class InitializeOnLoadMethodAttribute : Attribute { }
    public enum PlayModeStateChange { EnteredEditMode, EnteredPlayMode }
    public static class EditorApplication { public static Action<PlayModeStateChange> playModeStateChanged; }
}
namespace Coffee.UIParticleInternal
{
    public static class UIExtraCallbacks { public static Action onAfterCanvasRebuild; }
}
namespace Coffee.UIExtensions
{
    public static class SpriteMaskResolver { public static void BeginFrame() { } }
    public class UIParticleRenderer { public bool isActiveAndEnabled = true; }
    public class UIParticleAttractor { public bool isActiveAndEnabled = true; public void Attract() { } }
    public partial class UIParticle
    {
        public static int earlyCull;
        public float estimatedBakeCost = 1, bakePhase;
        public bool simulationOwner, groupAllAlphaHidden, groupAllClipped;
        public bool output = true, hidden, clipped;
        public int activeRendererCount => renderers.Count;
        public int mergedRendererCount => 0;
        public int releases;
        public bool SetSimulationOwner(bool value)
        {
            if (simulationOwner == value) return false;
            simulationOwner = value;
            if (!value) releases++;
            return true;
        }
        public void GetOutputVisibility(out bool hasOutput, out bool alphaHidden, out bool clipHidden)
        { hasOutput = output; alphaHidden = hidden; clipHidden = hidden || clipped; }
        public bool isActiveAndEnabled = true, useMeshSharing = true, canSimulate = true, isPrimary;
        public bool _isInSharingMap, _fastPathProcessed, hasUnmergedFallback, needsSpriteMaskIsolation, changed = true;
        public int groupId = 1, _sharingMapGroupId, prepared, updated, invalidated, allocations, cleared;
        public object canvas = new object();
        public Action onUpdate, onPrepare;
        public readonly List<UIParticleRenderer> renderers = new List<UIParticleRenderer> { new UIParticleRenderer() };
        public bool PrepareForUpdate() { prepared++; onPrepare?.Invoke(); var result = changed; changed = false; return result; }
        public void UpdateTransformScale() { prepared++; }
        public void UpdateRenderers() { updated++; onUpdate?.Invoke(); }
        public void InvalidateRendererCaches() { invalidated++; }
        public void ClearRendererMeshes() { cleared++; }
        public void RequestUnmergedFallback() { hasUnmergedFallback = true; changed = true; }
        public UIParticleRenderer GetRendererIfExists(int index)
            => 0 <= index && index < renderers.Count ? renderers[index] : null;
        public UIParticleRenderer GetRenderer(int index)
        {
            while (renderers.Count <= index) { renderers.Add(new UIParticleRenderer()); allocations++; }
            return renderers[index];
        }
    }
}
static class UpdaterHarness
{
    static int passed, failed;
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Refresh()
    {
        try { typeof(UIParticleUpdater).GetMethod("Refresh", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    static void Reset()
    {
        typeof(UIParticleUpdater).GetMethod("OnDomainReload", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        UnityEngine.Time.frameCount = 1;
        UnityEngine.Debug.errors = 0;
        UIParticle.earlyCull = 0;
    }
    static void Test(string name, Action body)
    {
        Reset();
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static void Add(params UIParticle[] particles) { foreach (var p in particles) UIParticleUpdater.Register(p); }
    static List<UIParticleRenderer> Members(int group = 1, int index = 0)
    { var result = new List<UIParticleRenderer>(); UIParticleUpdater.GetGroupedRenderers(group, index, result); return result; }
    static int Main()
    {
        Test("all replicas prepared before explicit primary sends mesh", () => {
            var primary = new UIParticle { isPrimary = true }; var replica = new UIParticle();
            primary.onUpdate = () => Assert(primary.prepared == 1 && replica.prepared == 1, "partial binding transition");
            Add(primary, replica); Refresh(); Assert(replica.prepared == 1 && primary.updated == 1, "duplicate prepare/bake");
        });
        Test("cached group complete before primary lookup", () => {
            UIParticleUpdater.s_UseGroupCache = true;
            var primary = new UIParticle { isPrimary = true }; var replica = new UIParticle();
            primary.onUpdate = () => Assert(Members().Count == 2, "partial sharing map"); Add(primary, replica); Refresh();
        });
        Test("Replica cannot consume Auto simulation slot", () => {
            var replica = new UIParticle { canSimulate = false }; var auto = new UIParticle();
            Add(replica, auto); Refresh(); Assert(auto.updated == 1 && replica.updated == 0, "group has no simulated mesh");
        });
        Test("registration is idempotent", () => {
            var p = new UIParticle(); Add(p, p); Assert(UIParticleUpdater.uiParticleCount == 1, "duplicate registration");
        });
        Test("mesh distribution cannot allocate unbound renderers", () => {
            var p = new UIParticle(); Add(p); Refresh();
            Assert(Members(1, 5).Count == 0 && p.allocations == 0, "lookup allocated a renderer");
        });
        Test("failed update clears frame group markers", () => {
            var p = new UIParticle { isPrimary = true, onUpdate = () => throw new Exception("test modifier failure") };
            Add(p); try { Refresh(); } catch (Exception) { }
            var ids = (HashSet<int>)typeof(UIParticleUpdater).GetField("s_UpdatedGroupIds", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Assert(ids.Count == 0, "exception poisoned group markers");
            p.onUpdate = null; UnityEngine.Time.frameCount++; Refresh(); Assert(p.updated == 2, "group not retried");
        });
        Test("paused primary invalidated when replica joins", () => {
            var p = new UIParticle(); Add(p); Refresh(); var oldInvalidations = p.invalidated;
            Add(new UIParticle()); UnityEngine.Time.frameCount++; Refresh();
            Assert(p.invalidated > oldInvalidations, "new replica cannot receive a cached paused mesh");
        });
        Test("cache toggle repairs moved groups", () => {
            UIParticleUpdater.s_UseGroupCache = true; var p = new UIParticle(); Add(p); Refresh();
            UIParticleUpdater.s_UseGroupCache = false; p.groupId = 7; UnityEngine.Time.frameCount++; Refresh();
            UIParticleUpdater.s_UseGroupCache = true; UnityEngine.Time.frameCount++; Refresh();
            Assert(Members(1).Count == 0 && Members(7).Count == 1, "stale group membership");
        });
        Test("same-frame callback is ignored", () => {
            var p = new UIParticle(); Add(p); Refresh(); Refresh(); Assert(p.updated == 1, "double simulation");
        });
#if FIXED
        Test("oversize fallback covers whole group and stays deferred", () => {
            var a = new UIParticle(); var b = new UIParticle(); var other = new UIParticle { groupId = 2 };
            Add(a, b, other); UIParticleUpdater.RequestUnmergedFallback(a);
            Assert(a.hasUnmergedFallback && b.hasUnmergedFallback && !other.hasUnmergedFallback, "fallback group mismatch");
            Assert(a.prepared == 0 && b.prepared == 0, "rebound inside bake");
        });
        Test("late replica inherits oversized group fallback", () => {
            var a = new UIParticle { hasUnmergedFallback = true }; var b = new UIParticle();
            Add(a, b); Refresh(); Assert(b.hasUnmergedFallback, "mixed merged/unmerged group");
        });
#endif
#if EXTENDED
        Test("explicit dirty from replica invalidates owner and consumers only", () => {
            var primary = new UIParticle { isPrimary = true }; var replica = new UIParticle { canSimulate = false };
            var other = new UIParticle { groupId = 2 }; Add(primary, replica, other); Refresh();
            var a = primary.invalidated; var b = replica.invalidated; var c = other.invalidated;
            replica.MarkParticleDirty();
            Assert(primary.invalidated > a && replica.invalidated > b && other.invalidated == c, "late dirty did not reach source or touched unrelated group");
            UnityEngine.Time.frameCount++; Refresh();
            Assert(primary.simulationOwner && !replica.simulationOwner && other.invalidated == c, "dirty changed ownership or scope");
        });
        Test("explicit dirty on standalone does not touch same-numbered shared group", () => {
            var standalone = new UIParticle { useMeshSharing = false }; var shared = new UIParticle();
            Add(standalone, shared); Refresh(); var count = shared.invalidated;
            standalone.MarkParticleDirty(); UnityEngine.Time.frameCount++; Refresh();
            Assert(shared.invalidated == count, "standalone dirtied shared group");
        });
        Test("dirty before registration does not retain object or affect active group", () => {
            var active = new UIParticle(); Add(active); Refresh(); var count = active.invalidated;
            var disabled = new UIParticle { isActiveAndEnabled = false }; disabled.MarkParticleDirty();
            var dirty = (HashSet<UIParticle>)typeof(UIParticleUpdater).GetField("s_DirtyParticles", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Assert(disabled.invalidated == 1 && !dirty.Contains(disabled), "disabled object retained");
            UnityEngine.Time.frameCount++; Refresh(); Assert(active.invalidated == count, "disabled object dirtied active group");
        });
        Test("joining a group does not invalidate unrelated groups", () => {
            var a = new UIParticle { groupId = 1 }; var b = new UIParticle { groupId = 2 };
            Add(a, b); Refresh(); var old = b.invalidated;
            Add(new UIParticle { groupId = 1 }); UnityEngine.Time.frameCount++; Refresh();
            Assert(b.invalidated == old, "unrelated group rebaked");
        });
        Test("moving groups invalidates old and new consumers only", () => {
            var moved = new UIParticle { groupId = 1 }; var oldGroup = new UIParticle { groupId = 1 };
            var newGroup = new UIParticle { groupId = 2 }; var other = new UIParticle { groupId = 3 };
            Add(moved, oldGroup, newGroup, other); Refresh();
            var a = oldGroup.invalidated; var b = newGroup.invalidated; var c = other.invalidated;
            moved.groupId = 2; UnityEngine.Time.frameCount++; Refresh();
            Assert(oldGroup.invalidated > a && newGroup.invalidated > b && other.invalidated == c, "wrong invalidation scope");
        });
        Test("standalone binding change does not invalidate shared effects", () => {
            var a = new UIParticle { useMeshSharing = false }; var b = new UIParticle();
            Add(a, b); Refresh(); var old = b.invalidated; a.changed = true;
            UnityEngine.Time.frameCount++; Refresh(); Assert(b.invalidated == old, "standalone change invalidated shared group");
        });
        Test("explicit primary takeover releases previous simulation owner", () => {
            var auto = new UIParticle(); var next = new UIParticle(); Add(auto, next); Refresh();
            Assert(auto.simulationOwner && !next.simulationOwner, "initial owner incorrect");
            next.isPrimary = true; next.changed = true; UnityEngine.Time.frameCount++; Refresh();
            Assert(!auto.simulationOwner && auto.releases == 1 && next.simulationOwner, "owner resource handoff failed");
        });
        Test("weighted phase assignment balances unequal group costs", () => {
            var a = new UIParticle { groupId = 1, estimatedBakeCost = 10 };
            var b = new UIParticle { groupId = 2, estimatedBakeCost = 8 };
            var c = new UIParticle { groupId = 3, estimatedBakeCost = 2 };
            Add(c, b, a); Refresh();
            Assert(a.bakePhase != b.bakePhase && b.bakePhase == c.bakePhase, "workload lanes are unbalanced");
        });
        Test("one visible replica prevents full group culling", () => {
            UIParticle.earlyCull = 2;
            var a = new UIParticle { hidden = true, clipped = true }; var b = new UIParticle();
            Add(a, b); Refresh(); Assert(!a.groupAllAlphaHidden && !a.groupAllClipped, "visible replica was culled");
        });
        Test("fully hidden group is culled and reappearance refills only that group", () => {
            UIParticle.earlyCull = 2;
            var a = new UIParticle { hidden = true }; var b = new UIParticle { hidden = true };
            var other = new UIParticle { groupId = 2 }; Add(a, b, other); Refresh();
            Assert(a.groupAllAlphaHidden && a.groupAllClipped, "hidden group not detected");
            var old = a.invalidated; var unrelated = other.invalidated; b.hidden = false;
            UnityEngine.Time.frameCount++; Refresh();
            Assert(!a.groupAllAlphaHidden && a.invalidated > old && other.invalidated == unrelated, "reappearance missed or invalidated unrelated group");
        });
        Test("simulation-only primary does not defeat consumer visibility", () => {
            UIParticle.earlyCull = 2;
            var a = new UIParticle { isPrimary = true, output = false }; var b = new UIParticle { hidden = true };
            Add(a, b); Refresh(); Assert(a.groupAllAlphaHidden, "non-rendering primary counted as visible");
        });
        Test("turning culling off clears visibility suppression", () => {
            UIParticle.earlyCull = 2; var a = new UIParticle { hidden = true }; Add(a); Refresh();
            var old = a.invalidated; UIParticle.earlyCull = 0; UnityEngine.Time.frameCount++; Refresh();
            Assert(!a.groupAllAlphaHidden && !a.groupAllClipped && a.invalidated > old, "culling OFF left cached suppression");
        });
        Test("failed preparation cannot send partially rebound meshes", () => {
            var a = new UIParticle { useMeshSharing = false, onPrepare = () => throw new Exception("bad binding") };
            var b = new UIParticle { useMeshSharing = false }; Add(a, b); Refresh();
            Assert(a.updated == 0 && b.updated == 1, "partially prepared effect updated or blocked others");
        });
        Test("persistent failure logs once and recovery resets suppression", () => {
            var p = new UIParticle { onUpdate = () => throw new Exception("persistent") }; Add(p);
            Refresh(); UnityEngine.Time.frameCount++; Refresh(); Assert(UnityEngine.Debug.errors == 1, "per-frame log flood");
            p.onUpdate = null; UnityEngine.Time.frameCount++; Refresh();
            p.onUpdate = () => throw new Exception("new failure"); UnityEngine.Time.frameCount++; Refresh();
            Assert(UnityEngine.Debug.errors == 2, "failure after recovery hidden");
        });
        Test("one failing effect cannot block independent effects", () => {
            var a = new UIParticle { useMeshSharing = false, onUpdate = () => throw new Exception("bad modifier") };
            var b = new UIParticle { useMeshSharing = false }; Add(a, b); Refresh();
            Assert(b.updated == 1, "exception blocked independent effect");
        });
        Test("orphan Replica clears stale mesh when provider disappears", () => {
            var a = new UIParticle(); var b = new UIParticle { canSimulate = false }; Add(a, b); Refresh();
            UIParticleUpdater.Unregister(a); UnityEngine.Time.frameCount++; Refresh();
            Assert(b.cleared == 1, "orphan replica kept old geometry");
        });
        Test("unregistering current particle cannot skip the next one", () => {
            var a = new UIParticle { useMeshSharing = false }; var b = new UIParticle { useMeshSharing = false };
            a.onUpdate = () => { a.isActiveAndEnabled = false; UIParticleUpdater.Unregister(a); };
            Add(a, b); Refresh(); Assert(b.updated == 1, "list mutation skipped next particle");
        });
        Test("new particle cannot update before its first preparation", () => {
            var a = new UIParticle { useMeshSharing = false }; var b = new UIParticle { useMeshSharing = false };
            a.onUpdate = () => Add(b); Add(a); Refresh();
            Assert(b.updated == 0 && b.prepared == 0, "new member updated mid-frame");
            UnityEngine.Time.frameCount++; Refresh(); Assert(b.updated == 1 && b.prepared == 1, "new member never scheduled");
        });
        Test("completed pass releases frame snapshot references", () => {
            Add(new UIParticle()); Refresh();
            var snapshot = (List<UIParticle>)typeof(UIParticleUpdater).GetField("s_FrameParticles", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Assert(snapshot.Count == 0, "snapshot retains scene objects");
        });
#endif
        Console.WriteLine($"RESULT {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
