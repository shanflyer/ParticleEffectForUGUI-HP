using System;
using System.Collections.Generic;
using Coffee.UIParticleInternal;
using UnityEditor;
using UnityEngine;

namespace Coffee.UIExtensions
{
    internal static class UIParticleUpdater
    {
        private static readonly List<UIParticle> s_ActiveParticles = new List<UIParticle>();
        private static readonly List<UIParticleAttractor> s_ActiveAttractors = new List<UIParticleAttractor>();
        private static readonly HashSet<int> s_UpdatedGroupIds = new HashSet<int>();
        private static readonly HashSet<int> s_FallbackGroupIds = new HashSet<int>();
        private static readonly Dictionary<int, List<UIParticle>> s_SharingGroups = new Dictionary<int, List<UIParticle>>();
        private static int s_FrameCount = -1;
        private static bool s_MembershipChanged;
        private static readonly List<UIParticle> s_FrameParticles = new List<UIParticle>();
        private static readonly List<UIParticleAttractor> s_FrameAttractors = new List<UIParticleAttractor>();
        private static readonly HashSet<UIParticle> s_FailedPreparation = new HashSet<UIParticle>();
        private static readonly HashSet<UIParticle> s_ReportedFailures = new HashSet<UIParticle>();
        private static readonly Dictionary<UIParticle, (bool shared, int group)> s_LastBindings = new Dictionary<UIParticle, (bool, int)>();
        private static readonly HashSet<int> s_DirtyGroups = new HashSet<int>();
        private static readonly HashSet<UIParticle> s_DirtyParticles = new HashSet<UIParticle>();
        private static readonly Dictionary<int, UIParticle> s_FramePrimaries = new Dictionary<int, UIParticle>();
        private static readonly Dictionary<int, (bool output, bool hidden, bool clipped)> s_Visibility = new Dictionary<int, (bool, bool, bool)>();
        private static readonly List<UIParticle> s_Simulators = new List<UIParticle>();
        private static int s_NextRebalanceFrame;

        internal static bool s_UseGroupCache;

        public static int uiParticleCount => s_ActiveParticles.Count;

        public static void Register(UIParticle particle)
        {
            if (particle == null || s_ActiveParticles.Contains(particle)) return;
            s_ActiveParticles.Add(particle);
            MarkBindingChanged(particle);
            s_MembershipChanged = true;
        }

        public static void Unregister(UIParticle particle)
        {
            if (particle == null) return;
            MarkBindingChanged(particle);
            s_LastBindings.Remove(particle);
            s_DirtyParticles.Remove(particle);
            s_ActiveParticles.Remove(particle);
            s_ReportedFailures.Remove(particle);
            RemoveFromSharingGroup(particle);
            s_MembershipChanged = true;
        }

        internal static void MarkParticleDirty(UIParticle particle)
        {
            if (particle == null) return;
            particle.InvalidateRendererCaches();
            // Do not retain disabled/unregistered objects or dirty unrelated groups.
            if (!s_ActiveParticles.Contains(particle)) return;
            MarkBindingChanged(particle);
            if (!particle.useMeshSharing) return;
            // Explicit API only: cover even a late call after frame preparation and
            // do not depend on a possibly stale or disabled group lookup cache.
            for (var i = 0; i < s_ActiveParticles.Count; i++)
            {
                var member = s_ActiveParticles[i];
                if (member != null && member != particle && member.useMeshSharing
                    && member.groupId == particle.groupId) member.InvalidateRendererCaches();
            }
        }

        private static void MarkBindingChanged(UIParticle particle)
        {
            if (s_LastBindings.TryGetValue(particle, out var old) && old.shared) s_DirtyGroups.Add(old.group);
            if (particle.useMeshSharing) s_DirtyGroups.Add(particle.groupId);
            s_DirtyParticles.Add(particle);
        }

        private static void RemoveFromSharingGroup(UIParticle uip)
        {
            if (!uip._isInSharingMap) return;
            if (s_SharingGroups.TryGetValue(uip._sharingMapGroupId, out var list))
            {
                list.Remove(uip);
                if (list.Count == 0) s_SharingGroups.Remove(uip._sharingMapGroupId);
            }
            uip._isInSharingMap = false;
        }

        private static void SyncSharingGroup(UIParticle uip)
        {
            if (uip._isInSharingMap && uip.useMeshSharing && uip._sharingMapGroupId == uip.groupId) return;

            RemoveFromSharingGroup(uip);

            if (!uip.useMeshSharing) return;

            var gid = uip.groupId;
            if (!s_SharingGroups.TryGetValue(gid, out var list))
            {
                list = new List<UIParticle>(4);
                s_SharingGroups.Add(gid, list);
            }
            list.Add(uip);
            uip._isInSharingMap = true;
            uip._sharingMapGroupId = gid;
        }

        public static void Register(UIParticleAttractor attractor)
        {
            if (attractor == null) return;
            if (!s_ActiveAttractors.Contains(attractor)) s_ActiveAttractors.Add(attractor);
        }

        public static void Unregister(UIParticleAttractor attractor)
        {
            if (attractor == null) return;
            s_ActiveAttractors.Remove(attractor);
        }

#if UNITY_EDITOR
#if UNITY_2019_3_OR_NEWER
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void OnDomainReload()
        {
            s_ActiveParticles.Clear();
            s_ActiveAttractors.Clear();
            s_FrameParticles.Clear();
            s_FrameAttractors.Clear();
            s_FailedPreparation.Clear();
            s_ReportedFailures.Clear();
            s_UpdatedGroupIds.Clear();
            s_FallbackGroupIds.Clear();
            s_SharingGroups.Clear();
            s_LastBindings.Clear();
            s_DirtyGroups.Clear();
            s_DirtyParticles.Clear();
            s_FramePrimaries.Clear();
            s_Visibility.Clear();
            s_Simulators.Clear();
            s_NextRebalanceFrame = 0;
            s_UseGroupCache = false;
            s_FrameCount = -1;
            s_MembershipChanged = true;
        }
#endif

        [InitializeOnLoadMethod]
        private static void InitializeOnLoad()
        {
            UIExtraCallbacks.onAfterCanvasRebuild += Refresh;

            EditorApplication.playModeStateChanged += state =>
            {
                UIExtraCallbacks.onAfterCanvasRebuild -= Refresh;
                if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.EnteredPlayMode)
                {
                    UIExtraCallbacks.onAfterCanvasRebuild += Refresh;
                }
            };
        }
#else
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeOnLoad()
        {
            UIExtraCallbacks.onAfterCanvasRebuild += Refresh;
        }
#endif

        private static void Refresh()
        {
            // Do not allow it to be called in the same frame.
            if (s_FrameCount == Time.frameCount) return;
            s_FrameCount = Time.frameCount;
            UIParticleProfiler.BeginFrame(Time.frameCount);
            try { RefreshFrame(); }
            finally
            {
                s_UpdatedGroupIds.Clear();
                s_FrameParticles.Clear();
                s_FrameAttractors.Clear();
                s_FailedPreparation.Clear();
                s_FramePrimaries.Clear();
                s_Visibility.Clear();
                s_Simulators.Clear();
                UIParticleProfiler.EndFrame();
            }
        }

        private static void RefreshFrame()
        {
            SpriteMaskResolver.BeginFrame();
            var prepareStarted = UIParticleProfiler.Timestamp();

            // User callbacks may unregister or register components during this pass.
            // A snapshot prevents skips and unbounded same-frame processing of additions.
            s_FrameParticles.Clear();
            s_FrameParticles.AddRange(s_ActiveParticles);
            s_FrameAttractors.Clear();
            s_FrameAttractors.AddRange(s_ActiveAttractors);
            s_FailedPreparation.Clear();

            // Complete every binding/group transition before any mesh is sent to replicas.
            var rebalance = s_MembershipChanged;
            s_MembershipChanged = false;
            s_FallbackGroupIds.Clear();
            for (var i = 0; i < s_FrameParticles.Count; i++)
            {
                var uip = s_FrameParticles[i];
                if (uip == null) continue;
                // Determine every group's layout before preparing any member. A masked replica
                // must not receive a merged mesh from an unmasked simulation owner.
                var masked = uip.needsSpriteMaskIsolation;
                if (masked) uip.RequestUnmergedFallback();
                if (uip.useMeshSharing && (uip.hasUnmergedFallback || masked))
                    s_FallbackGroupIds.Add(uip.groupId);
            }
            for (var i = 0; i < s_FrameParticles.Count; i++)
            {
                var uip = s_FrameParticles[i];
                if (uip == null || !uip.isActiveAndEnabled) continue;
                if (uip.useMeshSharing && s_FallbackGroupIds.Contains(uip.groupId))
                    uip.RequestUnmergedFallback();
                try
                {
                    if (uip.canvas != null)
                    {
                        var changed = uip.PrepareForUpdate();
                        if (!s_LastBindings.TryGetValue(uip, out var old)
                            || old.shared != uip.useMeshSharing || old.group != uip.groupId) changed = true;
                        if (changed) { MarkBindingChanged(uip); rebalance = true; }
                        s_LastBindings[uip] = (uip.useMeshSharing, uip.groupId);
                    }
                    if (s_UseGroupCache) SyncSharingGroup(uip);
                }
                catch (Exception e)
                {
                    s_FailedPreparation.Add(uip);
                    MarkBindingChanged(uip);
                    ReportFailure(uip, e);
                }
            }

            PrepareOwnershipAndVisibility(ref rebalance);
            for (var i = 0; i < s_FrameParticles.Count; i++)
            {
                var uip = s_FrameParticles[i];
                if (uip != null && (s_DirtyParticles.Contains(uip)
                    || (uip.useMeshSharing && s_DirtyGroups.Contains(uip.groupId)))) uip.InvalidateRendererCaches();
            }
            s_DirtyGroups.Clear();
            s_DirtyParticles.Clear();
            UIParticleProfiler.current.prepareMs = UIParticleProfiler.Milliseconds(prepareStarted);

            s_UpdatedGroupIds.Clear();
            try
            {
                // Explicit primaries take precedence over Auto members.
                for (var i = 0; i < s_FrameParticles.Count; i++)
                {
                    var uip = s_FrameParticles[i];
                    if (uip == null || !uip.isActiveAndEnabled || uip.canvas == null
                        || s_FailedPreparation.Contains(uip)
                        || !uip.isPrimary || !s_UpdatedGroupIds.Add(uip.groupId)) continue;
                    UpdateSafely(uip);
                }

                for (var i = 0; i < s_FrameParticles.Count; i++)
                {
                    var uip = s_FrameParticles[i];
                    if (uip == null || !uip.isActiveAndEnabled || uip.canvas == null
                        || s_FailedPreparation.Contains(uip)) continue;
                    if (uip.useMeshSharing && !uip.canSimulate && GetPrimary(uip.groupId) == null)
                    {
                        uip.ClearRendererMeshes();
                        continue;
                    }
                    if (!uip.useMeshSharing || (uip.canSimulate && s_UpdatedGroupIds.Add(uip.groupId)))
                    {
                        UpdateSafely(uip);
                    }
                }
            }
            finally
            {
                // A failed native call/mesh modifier must not leave a group skipped forever.
                s_UpdatedGroupIds.Clear();
                s_FrameParticles.Clear();
                s_FailedPreparation.Clear();
                s_FramePrimaries.Clear();
                s_Visibility.Clear();
                s_Simulators.Clear();
            }

            // Attract
            for (var i = 0; i < s_FrameAttractors.Count; i++)
            {
                var attractor = s_FrameAttractors[i];
                if (attractor != null && attractor.isActiveAndEnabled) attractor.Attract();
            }
            s_FrameAttractors.Clear();
        }

        private static void UpdateSafely(UIParticle particle)
        {
            try
            {
                particle.UpdateRenderers();
                s_ReportedFailures.Remove(particle);
            }
            catch (Exception e) { ReportFailure(particle, e); }
        }

        private static void PrepareOwnershipAndVisibility(ref bool rebalance)
        {
            s_FramePrimaries.Clear();
            s_Visibility.Clear();
            s_Simulators.Clear();
            for (var i = 0; i < s_FrameParticles.Count; i++)
            {
                var p = s_FrameParticles[i];
                if (p == null || !p.isActiveAndEnabled || p.canvas == null || s_FailedPreparation.Contains(p)) continue;
                if (!p.useMeshSharing) { s_Simulators.Add(p); continue; }
                if (p.canSimulate && (!s_FramePrimaries.TryGetValue(p.groupId, out var primary) || (!primary.isPrimary && p.isPrimary)))
                    s_FramePrimaries[p.groupId] = p;
                if (UIParticle.earlyCull <= 0) continue;
                p.GetOutputVisibility(out var output, out var hidden, out var clipped);
                if (!output) continue;
                if (s_Visibility.TryGetValue(p.groupId, out var state))
                    s_Visibility[p.groupId] = (true, state.hidden && hidden, state.clipped && clipped);
                else s_Visibility[p.groupId] = (true, hidden, clipped);
            }
            foreach (var pair in s_FramePrimaries) s_Simulators.Add(pair.Value);
            for (var i = 0; i < s_FrameParticles.Count; i++)
            {
                var p = s_FrameParticles[i];
                if (p == null || !p.isActiveAndEnabled || s_FailedPreparation.Contains(p)) continue;
                var owner = p.canvas != null && (!p.useMeshSharing
                    || (s_FramePrimaries.TryGetValue(p.groupId, out var primary) && primary == p));
                rebalance |= p.SetSimulationOwner(owner);
                UIParticleProfiler.current.activeRenderers += p.activeRendererCount;
                UIParticleProfiler.current.mergedRenderers += p.mergedRendererCount;
                if (p.hasUnmergedFallback) UIParticleProfiler.current.fallbackEffects++;
                var hidden = false;
                var clipped = false;
                if (p.useMeshSharing && s_Visibility.TryGetValue(p.groupId, out var state))
                { hidden = state.hidden; clipped = state.clipped; }
                if ((p.groupAllAlphaHidden && !hidden) || (p.groupAllClipped && !clipped))
                    MarkBindingChanged(p); // Refill paused/cached consumers when the group reappears.
                p.groupAllAlphaHidden = hidden;
                p.groupAllClipped = clipped;
            }
            if (Time.frameCount >= s_NextRebalanceFrame)
            {
                rebalance = true;
                s_NextRebalanceFrame = Time.frameCount + 120;
            }
            if (!rebalance) return;
            // Largest workloads first. Only primary renderers consume simulation/bake time.
            s_Simulators.Sort((a, b) => b.estimatedBakeCost.CompareTo(a.estimatedBakeCost));
            float first = 0, second = 0;
            for (var i = 0; i < s_Simulators.Count; i++)
            {
                var p = s_Simulators[i];
                var weight = Math.Max(1, p.estimatedBakeCost);
                if (first <= second) { p.bakePhase = 0.25f; first += weight; }
                else { p.bakePhase = 0.75f; second += weight; }
            }
        }

        private static void ReportFailure(UIParticle particle, Exception exception)
        {
            // Keep independent groups running and avoid a per-frame exception log flood.
            // A later successful update permits a future, distinct failure to be logged.
            if (particle != null && particle.isActiveAndEnabled && s_ReportedFailures.Add(particle))
                Debug.LogException(exception, particle);
        }

        public static void GetGroupedRenderers(int groupId, int index, List<UIParticleRenderer> results)
        {
            results.Clear();

            if (s_UseGroupCache)
            {
                if (!s_SharingGroups.TryGetValue(groupId, out var members)) return;

                for (var i = members.Count - 1; i >= 0; i--)
                {
                    var uip = members[i];
                    if (uip == null || !uip.isActiveAndEnabled || !uip.useMeshSharing || uip.groupId != groupId)
                    {
                        members.RemoveAt(i);
                        if (uip != null) uip._isInSharingMap = false;
                        continue;
                    }
                    if (s_FailedPreparation.Contains(uip) || uip.canvas == null) continue;
                    var renderer = uip.GetRendererIfExists(index);
                    if (renderer != null && renderer.isActiveAndEnabled) results.Add(renderer);
                }

                if (members.Count == 0) s_SharingGroups.Remove(groupId);
            }
            else
            {
                for (var i = 0; i < s_ActiveParticles.Count; i++)
                {
                    var uip = s_ActiveParticles[i];
                    if (uip != null && uip.isActiveAndEnabled && uip.canvas != null
                        && !s_FailedPreparation.Contains(uip) && uip.useMeshSharing && uip.groupId == groupId)
                    {
                        var renderer = uip.GetRendererIfExists(index);
                        if (renderer != null && renderer.isActiveAndEnabled) results.Add(renderer);
                    }
                }
            }
        }

        internal static void RequestUnmergedFallback(UIParticle source)
        {
            source.RequestUnmergedFallback();
            if (!source.useMeshSharing) return;
            // Keep renderer indices consistent for the whole group. Rebind next frame,
            // never destroy/recreate native meshes while UpdateMesh is using them.
            for (var i = 0; i < s_ActiveParticles.Count; i++)
            {
                var uip = s_ActiveParticles[i];
                if (uip != null && uip.useMeshSharing && uip.groupId == source.groupId)
                    uip.RequestUnmergedFallback();
            }
        }

        internal static UIParticle GetPrimary(int groupId)
        {
            if (s_UseGroupCache)
            {
                if (!s_SharingGroups.TryGetValue(groupId, out var members)) return null;

                UIParticle primary = null;
                for (var i = 0; i < members.Count; i++)
                {
                    var uip = members[i];
                    if (uip == null || !uip.isActiveAndEnabled || uip.canvas == null
                        || s_FailedPreparation.Contains(uip) || !uip.useMeshSharing || uip.groupId != groupId) continue;
                    if (uip.isPrimary) return uip;
                    if (primary == null && uip.canSimulate) primary = uip;
                }

                return primary;
            }

            UIParticle fallback = null;
            for (var i = 0; i < s_ActiveParticles.Count; i++)
            {
                var uip = s_ActiveParticles[i];
                if (uip == null || !uip.isActiveAndEnabled || uip.canvas == null
                    || s_FailedPreparation.Contains(uip) || !uip.useMeshSharing || uip.groupId != groupId) continue;
                if (uip.isPrimary) return uip;
                if (fallback == null && uip.canSimulate) fallback = uip;
            }

            return fallback;
        }
    }
}
