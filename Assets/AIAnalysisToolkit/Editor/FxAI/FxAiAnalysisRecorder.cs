using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class FxAiAnalysisRecorder
{
    private const int FrameSize = 1024;
    private const int ContactThumbSize = 256;
    private const float CameraFillRatio = 0.70f;
    private const float BoundsPadding = 1.18f;
    private const float MaxCaptureDuration = 5.0f;
    private const float CaptureFieldOfView = 35f;
    private static readonly Color32 CaptureBackgroundColor32 = new Color32(117, 117, 117, 255);
    private static readonly Color CaptureBackgroundColor = new Color(117f / 255f, 117f / 255f, 117f / 255f, 1f);
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    private enum CaptureMode
    {
        Full,
        Solo,
        Without
    }

    private sealed class RendererState
    {
        public Renderer renderer;
        public bool enabled;
    }

    private sealed class RendererItem
    {
        public int index;
        public string path;
        public ParticleSystem particleSystem;
        public ParticleSystemRenderer renderer;
        public bool originallyEnabled;
        public bool activeInHierarchy;
    }

    private sealed class CaptureView
    {
        public string name;
        public Vector3 lookDirection;
        public Vector3 up;

        public CaptureView(string name, Vector3 lookDirection, Vector3 up)
        {
            this.name = name;
            this.lookDirection = lookDirection.normalized;
            this.up = up.normalized;
        }
    }

    private static readonly CaptureView MainView = new CaptureView("angle", new Vector3(0.45f, -0.25f, 1f), Vector3.up);
    private static readonly CaptureView[] ExtraViews =
    {
        new CaptureView("front", Vector3.forward, Vector3.up),
        new CaptureView("side", Vector3.right, Vector3.up),
        new CaptureView("top", Vector3.down, Vector3.forward)
    };

    [Serializable]
    private sealed class Manifest
    {
        public string prefabName;
        public string prefabPath;
        public string generatedAt;
        public string outputDirectory;
        public float captureDuration;
        public int frameSize;
        public int contactThumbSize;
        public float cameraFillRatio;
        public List<float> sampleTimes;
        public List<ParticleEntry> particles = new List<ParticleEntry>();
        public List<ParticleEntry> skippedParticles = new List<ParticleEntry>();
    }

    [Serializable]
    private sealed class ParticleEntry
    {
        public int index;
        public string name;
        public string hierarchyPath;
        public string soloContactSheet;
        public string withoutContactSheet;
        public string settingsJson;
    }

    [Serializable]
    private sealed class ParticleSettings
    {
        public int index;
        public string name;
        public string hierarchyPath;
        public string prefabPath;
        public string generatedAt;
        public MainModuleInfo main;
        public EmissionModuleInfo emission;
        public ShapeModuleInfo shape;
        public ModuleState velocityOverLifetime;
        public ModuleState limitVelocityOverLifetime;
        public ModuleState forceOverLifetime;
        public ModuleState colorOverLifetime;
        public ModuleState sizeOverLifetime;
        public ModuleState rotationOverLifetime;
        public ModuleState noise;
        public ModuleState collision;
        public ModuleState trigger;
        public TrailModuleInfo trails;
        public RendererInfo renderer;
        public TransformInfo transform;
        public RuntimeInfo runtime;
    }

    [Serializable]
    private sealed class MainModuleInfo
    {
        public float duration;
        public bool loop;
        public string startDelay;
        public string startLifetime;
        public string startSpeed;
        public string startSize;
        public string startRotation;
        public string startColor;
        public string gravityModifier;
        public int maxParticles;
        public string simulationSpace;
        public string scalingMode;
        public bool playOnAwake;
        public bool prewarm;
    }

    [Serializable]
    private sealed class EmissionModuleInfo
    {
        public bool enabled;
        public string rateOverTime;
        public string rateOverDistance;
        public int burstCount;
        public List<BurstInfo> bursts = new List<BurstInfo>();
    }

    [Serializable]
    private sealed class BurstInfo
    {
        public float time;
        public string count;
        public int cycleCount;
        public float repeatInterval;
        public float probability;
    }

    [Serializable]
    private sealed class ShapeModuleInfo
    {
        public bool enabled;
        public string shapeType;
        public float angle;
        public float radius;
        public Vector3 scale;
        public string arc;
    }

    [Serializable]
    private sealed class ModuleState
    {
        public bool enabled;
    }

    [Serializable]
    private sealed class TrailModuleInfo
    {
        public bool enabled;
        public string mode;
        public string lifetime;
        public string widthOverTrail;
        public string colorOverTrail;
        public float ratio;
        public bool dieWithParticles;
        public bool sizeAffectsWidth;
        public bool inheritParticleColor;
    }

    [Serializable]
    private sealed class RendererInfo
    {
        public bool enabled;
        public string renderMode;
        public string alignment;
        public string sortMode;
        public string sortingLayerName;
        public int sortingOrder;
        public string materialName;
        public string materialPath;
        public string shaderName;
        public string mainTextureName;
        public string mainTexturePath;
        public int mainTextureWidth;
        public int mainTextureHeight;
        public string trailMaterialName;
        public string trailMaterialPath;
        public string meshName;
        public string bounds;
    }

    [Serializable]
    private sealed class TransformInfo
    {
        public Vector3 localPosition;
        public Vector3 localEulerAngles;
        public Vector3 localScale;
    }

    [Serializable]
    private sealed class RuntimeInfo
    {
        public int peakParticleCount;
        public float averageParticleCount;
        public bool hasTrail;
        public bool hasNoise;
        public bool hasCollision;
        public bool hasLights;
        public int materialInstanceCount;
    }

    [Serializable]
    private sealed class CaptureStats
    {
        public string mode;
        public string target;
        public string contactSheet;
        public CameraInfo camera;
        public List<FrameStats> frames = new List<FrameStats>();
    }

    [Serializable]
    private sealed class CameraInfo
    {
        public string viewName;
        public string projection;
        public Vector3 position;
        public Vector3 eulerAngles;
        public float fieldOfView;
        public float orthographicSize;
        public float nearClipPlane;
        public float farClipPlane;
        public string fittedBounds;
    }

    [Serializable]
    private sealed class FrameStats
    {
        public float time;
        public string file;
    }

    [MenuItem("Assets/AI特效分析/录制特效分析包", false, 2000)]
    private static void RecordSelectedAsset()
    {
        GameObject prefab = Selection.activeObject as GameObject;
        RecordPrefab(prefab);
    }

    [MenuItem("Assets/AI特效分析/录制特效分析包", true)]
    private static bool ValidateRecordSelectedAsset()
    {
        GameObject prefab = Selection.activeObject as GameObject;
        return IsValidFxPrefab(prefab);
    }

    [MenuItem("GameObject/AI特效分析/录制选中特效分析包", false, 30)]
    private static void RecordSelectedGameObject()
    {
        GameObject selected = Selection.activeGameObject;
        if (selected == null)
        {
            EditorUtility.DisplayDialog("AI特效分析", "请先选择一个特效对象。", "OK");
            return;
        }

        GameObject prefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(selected) as GameObject;
        if (prefab == null)
        {
            prefab = selected;
        }

        RecordPrefab(prefab);
    }

    [MenuItem("GameObject/AI特效分析/录制选中特效分析包", true)]
    private static bool ValidateRecordSelectedGameObject()
    {
        GameObject selected = Selection.activeGameObject;
        if (selected == null)
        {
            return false;
        }

        return selected.GetComponentInChildren<ParticleSystem>(true) != null;
    }

    private static bool IsValidFxPrefab(GameObject prefab)
    {
        if (prefab == null)
        {
            return false;
        }

        string path = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return prefab.GetComponentInChildren<ParticleSystem>(true) != null;
    }

    private static void RecordPrefab(GameObject prefab)
    {
        if (prefab == null)
        {
            EditorUtility.DisplayDialog("AI特效分析", "请选择包含 ParticleSystem 的特效 prefab。", "OK");
            return;
        }

        string prefabPath = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(prefabPath))
        {
            EditorUtility.DisplayDialog("AI特效分析", "当前对象不是项目资源，无法稳定生成分析包。", "OK");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("[FxAiAnalysisRecorder] 当前场景保存确认被取消，录制已中止。");
            return;
        }

        UnityEngine.Object oldSelection = Selection.activeObject;
        Scene oldScene = SceneManager.GetActiveScene();
        string oldScenePath = oldScene.path;
        bool shouldRestoreOldScene = oldScene.IsValid();
        Scene captureScene = default(Scene);
        GameObject root = null;
        Camera camera = null;
        string outputDir = null;
        List<RendererState> rendererStates = null;

        try
        {
            captureScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            captureScene.name = "FxAiAnalysisCapture";

            root = PrefabUtility.InstantiatePrefab(prefab, captureScene) as GameObject;
            if (root == null)
            {
                root = UnityEngine.Object.Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(root, captureScene);
            }

            root.name = prefab.name;
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;
            Selection.activeObject = root;

            camera = CreatePreviewCamera(captureScene);
            FrameSceneView(root);
            rendererStates = CaptureRendererStates(root);
            List<RendererItem> particleItems = CollectParticleItems(root);
            List<float> sampleTimes = BuildSampleTimes(root, out float captureDuration);

            outputDir = CreateOutputDirectory(prefab.name);
            Manifest manifest = new Manifest
            {
                prefabName = prefab.name,
                prefabPath = prefabPath,
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                outputDirectory = outputDir,
                captureDuration = captureDuration,
                frameSize = FrameSize,
                contactThumbSize = ContactThumbSize,
                cameraFillRatio = CameraFillRatio,
                sampleTimes = sampleTimes
            };

            string fullDir = Path.Combine(outputDir, "full");
            Directory.CreateDirectory(fullDir);
            CaptureState(root, camera, rendererStates, null, CaptureMode.Full, sampleTimes, fullDir, "full");

            for (int i = 0; i < particleItems.Count; i++)
            {
                RendererItem item = particleItems[i];
                if (!item.originallyEnabled || !item.activeInHierarchy)
                {
                    manifest.skippedParticles.Add(ToManifestEntry(item, outputDir, null));
                    continue;
                }

                if (EditorUtility.DisplayCancelableProgressBar(
                        "AI特效分析录制",
                        string.Format("{0}/{1} {2}", i + 1, particleItems.Count, item.path),
                        particleItems.Count <= 0 ? 1f : (float)i / particleItems.Count))
                {
                    throw new OperationCanceledException("用户取消录制。");
                }

                string particleFolder = Path.Combine(outputDir, "particles", string.Format("{0:00}_{1}", item.index, SanitizePathPart(item.renderer.gameObject.name)));
                Directory.CreateDirectory(particleFolder);

                string soloDir = Path.Combine(particleFolder, "solo");
                string withoutDir = Path.Combine(particleFolder, "without_this");
                Directory.CreateDirectory(soloDir);
                Directory.CreateDirectory(withoutDir);

                CaptureState(root, camera, rendererStates, item.renderer, CaptureMode.Solo, sampleTimes, soloDir, "solo");
                CaptureState(root, camera, rendererStates, item.renderer, CaptureMode.Without, sampleTimes, withoutDir, "without_this");

                ParticleSettings settings = BuildParticleSettings(prefabPath, item, sampleTimes);
                string settingsPath = Path.Combine(particleFolder, "settings.json");
                File.WriteAllText(settingsPath, JsonUtility.ToJson(settings, true), Utf8NoBom);

                manifest.particles.Add(ToManifestEntry(item, outputDir, particleFolder));
            }

            WriteGuide(outputDir);
            File.WriteAllText(Path.Combine(outputDir, "manifest.json"), JsonUtility.ToJson(manifest, true), Utf8NoBom);
            EditorUtility.RevealInFinder(outputDir);
            Debug.Log("[FxAiAnalysisRecorder] Exported: " + outputDir);
            ProfilerAIClaudeRunner.StartFxAnalysis(outputDir);
        }
        catch (OperationCanceledException ex)
        {
            Debug.LogWarning("[FxAiAnalysisRecorder] " + ex.Message);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("AI特效分析录制失败", ex.Message, "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            if (rendererStates != null)
            {
                RestoreRendererStates(rendererStates);
            }

            Selection.activeObject = oldSelection;
            if (camera != null)
            {
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }

            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            if (shouldRestoreOldScene)
            {
                if (!string.IsNullOrEmpty(oldScenePath) && File.Exists(oldScenePath))
                {
                    EditorSceneManager.OpenScene(oldScenePath, OpenSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                }
            }
        }
    }

    private static Camera CreatePreviewCamera(Scene scene)
    {
        GameObject cameraObject = new GameObject("Fx AI Preview Camera");
        SceneManager.MoveGameObjectToScene(cameraObject, scene);
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.orthographic = false;
        camera.fieldOfView = CaptureFieldOfView;
        camera.aspect = 1f;
        camera.cullingMask = ~0;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = CaptureBackgroundColor;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 1000f;
        return camera;
    }

    private static void FrameSceneView(GameObject root)
    {
        if (SceneView.lastActiveSceneView == null || root == null)
        {
            return;
        }

        Bounds bounds = CalculateTransformBounds(root);
        SceneView.lastActiveSceneView.Frame(bounds, false);
        SceneView.lastActiveSceneView.Repaint();
    }

    private static List<RendererState> CaptureRendererStates(GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        List<RendererState> states = new List<RendererState>(renderers.Length);
        for (int i = 0; i < renderers.Length; i++)
        {
            states.Add(new RendererState { renderer = renderers[i], enabled = renderers[i].enabled });
        }

        return states;
    }

    private static List<RendererItem> CollectParticleItems(GameObject root)
    {
        ParticleSystemRenderer[] renderers = root.GetComponentsInChildren<ParticleSystemRenderer>(true);
        List<RendererItem> items = new List<RendererItem>(renderers.Length);
        Array.Sort(renderers, (a, b) => string.Compare(GetHierarchyPath(root.transform, a.transform), GetHierarchyPath(root.transform, b.transform), StringComparison.Ordinal));

        for (int i = 0; i < renderers.Length; i++)
        {
            ParticleSystem ps = renderers[i].GetComponent<ParticleSystem>();
            items.Add(new RendererItem
            {
                index = i,
                path = GetHierarchyPath(root.transform, renderers[i].transform),
                particleSystem = ps,
                renderer = renderers[i],
                originallyEnabled = renderers[i].enabled,
                activeInHierarchy = renderers[i].gameObject.activeInHierarchy
            });
        }

        return items;
    }

    private static List<float> BuildSampleTimes(GameObject root, out float captureDuration)
    {
        ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
        float duration = 0f;
        for (int i = 0; i < systems.Length; i++)
        {
            ParticleSystem.MainModule main = systems[i].main;
            float delay = EvaluateMax(main.startDelay);
            float lifetime = EvaluateMax(main.startLifetime);
            float candidate = delay + main.duration + lifetime;
            if (main.loop)
            {
                candidate = Mathf.Max(candidate, delay + main.duration);
            }

            duration = Mathf.Max(duration, candidate);
        }

        captureDuration = Mathf.Clamp(duration <= 0.01f ? 1.5f : duration, 0.5f, MaxCaptureDuration);

        float[] preferred = { 0f, 0.05f, 0.1f, 0.2f, 0.35f, 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f };
        List<float> times = new List<float>();
        for (int i = 0; i < preferred.Length; i++)
        {
            if (preferred[i] <= captureDuration + 0.001f)
            {
                times.Add(preferred[i]);
            }
        }

        if (times.Count == 0 || Mathf.Abs(times[times.Count - 1] - captureDuration) > 0.05f)
        {
            times.Add(captureDuration);
        }

        return times;
    }

    private static CaptureStats CaptureState(
        GameObject root,
        Camera camera,
        List<RendererState> rendererStates,
        ParticleSystemRenderer target,
        CaptureMode mode,
        List<float> sampleTimes,
        string outputDir,
        string prefix)
    {
        SetRendererMode(rendererStates, target, mode);
        Bounds bounds = CalculateAnimatedBounds(root, rendererStates, sampleTimes);
        FitCameraToBounds(camera, bounds, MainView);

        CaptureStats stats = new CaptureStats
        {
            mode = mode.ToString(),
            target = target == null ? "all" : GetHierarchyPath(root.transform, target.transform),
            camera = BuildCameraInfo(camera, bounds, MainView)
        };

        CaptureFrames(root, camera, sampleTimes, outputDir, prefix, stats.frames);
        stats.contactSheet = prefix + "_contact_sheet.png";

        if (mode == CaptureMode.Full)
        {
            string viewsRoot = Path.Combine(outputDir, "views");
            for (int i = 0; i < ExtraViews.Length; i++)
            {
                CaptureView view = ExtraViews[i];
                string viewDir = Path.Combine(viewsRoot, view.name);
                Directory.CreateDirectory(viewDir);
                FitCameraToBounds(camera, bounds, view);
                CaptureFrames(root, camera, sampleTimes, viewDir, prefix + "_" + view.name, null);
            }
        }

        FitCameraToBounds(camera, bounds, MainView);
        File.WriteAllText(Path.Combine(outputDir, "capture_stats.json"), JsonUtility.ToJson(stats, true), Utf8NoBom);
        RestoreRendererStates(rendererStates);
        return stats;
    }

    private static void CaptureFrames(
        GameObject root,
        Camera camera,
        List<float> sampleTimes,
        string outputDir,
        string prefix,
        List<FrameStats> frameStats)
    {
        List<Texture2D> thumbnails = new List<Texture2D>();
        for (int i = 0; i < sampleTimes.Count; i++)
        {
            float time = sampleTimes[i];
            Simulate(root, time);
            string fileName = string.Format(CultureInfo.InvariantCulture, "{0}_{1:00}_{2:0.00}s.png", prefix, i, time);
            string path = Path.Combine(outputDir, fileName);
            Texture2D image = RenderCamera(camera);
            File.WriteAllBytes(path, image.EncodeToPNG());

            if (frameStats != null)
            {
                frameStats.Add(new FrameStats
                {
                    time = time,
                    file = fileName
                });
            }

            thumbnails.Add(ResizeTexture(image, ContactThumbSize, ContactThumbSize));
            UnityEngine.Object.DestroyImmediate(image);
        }

        string contactSheetFile = prefix + "_contact_sheet.png";
        Texture2D contactSheet = BuildContactSheet(thumbnails, ContactThumbSize);
        File.WriteAllBytes(Path.Combine(outputDir, contactSheetFile), contactSheet.EncodeToPNG());

        for (int i = 0; i < thumbnails.Count; i++)
        {
            UnityEngine.Object.DestroyImmediate(thumbnails[i]);
        }

        UnityEngine.Object.DestroyImmediate(contactSheet);
    }

    private static void SetRendererMode(List<RendererState> states, ParticleSystemRenderer target, CaptureMode mode)
    {
        for (int i = 0; i < states.Count; i++)
        {
            Renderer renderer = states[i].renderer;
            if (renderer == null)
            {
                continue;
            }

            if (mode == CaptureMode.Full)
            {
                renderer.enabled = states[i].enabled;
            }
            else if (mode == CaptureMode.Solo)
            {
                renderer.enabled = renderer == target;
            }
            else
            {
                renderer.enabled = states[i].enabled && renderer != target;
            }
        }
    }

    private static void RestoreRendererStates(List<RendererState> states)
    {
        for (int i = 0; i < states.Count; i++)
        {
            if (states[i].renderer != null)
            {
                states[i].renderer.enabled = states[i].enabled;
            }
        }
    }

    private static Bounds CalculateAnimatedBounds(GameObject root, List<RendererState> states, List<float> sampleTimes)
    {
        bool hasBounds = false;
        Bounds result = new Bounds(root.transform.position, Vector3.one);

        for (int i = 0; i < sampleTimes.Count; i++)
        {
            Simulate(root, sampleTimes[i]);
            for (int j = 0; j < states.Count; j++)
            {
                Renderer renderer = states[j].renderer;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Bounds b = renderer.bounds;
                if (!IsUsableBounds(b))
                {
                    continue;
                }

                if (!hasBounds)
                {
                    result = b;
                    hasBounds = true;
                }
                else
                {
                    result.Encapsulate(b);
                }
            }
        }

        if (!hasBounds)
        {
            result = CalculateTransformBounds(root);
        }

        Vector3 size = result.size * BoundsPadding;
        size.x = Mathf.Max(size.x, 0.5f);
        size.y = Mathf.Max(size.y, 0.5f);
        size.z = Mathf.Max(size.z, 0.5f);
        result.size = size;
        return result;
    }

    private static Bounds CalculateTransformBounds(GameObject root)
    {
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        Bounds bounds = new Bounds(root.transform.position, Vector3.one);
        for (int i = 0; i < transforms.Length; i++)
        {
            bounds.Encapsulate(transforms[i].position);
        }

        if (bounds.size.sqrMagnitude < 0.01f)
        {
            bounds.size = Vector3.one;
        }

        return bounds;
    }

    private static bool IsUsableBounds(Bounds bounds)
    {
        Vector3 size = bounds.size;
        if (float.IsNaN(size.x) || float.IsInfinity(size.x))
        {
            return false;
        }

        return size.sqrMagnitude > 0.0001f && size.x < 100000f && size.y < 100000f && size.z < 100000f;
    }

    private static void FitCameraToBounds(Camera camera, Bounds bounds, CaptureView view)
    {
        camera.orthographic = false;
        camera.fieldOfView = CaptureFieldOfView;
        camera.aspect = 1f;

        Vector3 forward = view.lookDirection;
        Vector3 right = Vector3.Cross(view.up, forward).normalized;
        if (right.sqrMagnitude < 0.0001f)
        {
            right = Vector3.right;
        }

        Vector3 up = Vector3.Cross(forward, right).normalized;
        Vector3[] corners = GetBoundsCorners(bounds);
        float minX = float.MaxValue;
        float maxX = float.MinValue;
        float minY = float.MaxValue;
        float maxY = float.MinValue;
        float minZ = float.MaxValue;
        float maxZ = float.MinValue;

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 delta = corners[i] - bounds.center;
            float x = Vector3.Dot(delta, right);
            float y = Vector3.Dot(delta, up);
            float z = Vector3.Dot(delta, forward);
            minX = Mathf.Min(minX, x);
            maxX = Mathf.Max(maxX, x);
            minY = Mathf.Min(minY, y);
            maxY = Mathf.Max(maxY, y);
            minZ = Mathf.Min(minZ, z);
            maxZ = Mathf.Max(maxZ, z);
        }

        Vector3 projectedCenter = bounds.center +
                                  right * ((minX + maxX) * 0.5f) +
                                  up * ((minY + maxY) * 0.5f) +
                                  forward * ((minZ + maxZ) * 0.5f);
        float projectedWidth = Mathf.Max(0.5f, maxX - minX);
        float projectedHeight = Mathf.Max(0.5f, maxY - minY);
        float projectedSize = Mathf.Max(projectedWidth, projectedHeight);
        float depth = Mathf.Max(0.5f, maxZ - minZ);
        float halfFov = camera.fieldOfView * Mathf.Deg2Rad * 0.5f;
        float distance = Mathf.Max(2f, (projectedSize * 0.5f) / Mathf.Tan(halfFov)) / CameraFillRatio;

        camera.transform.position = projectedCenter - forward * distance;
        camera.transform.rotation = Quaternion.LookRotation(forward, up);
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = distance + depth * 3f + 20f;
    }

    private static Vector3[] GetBoundsCorners(Bounds bounds)
    {
        Vector3 c = bounds.center;
        Vector3 e = bounds.extents;
        return new[]
        {
            c + new Vector3(-e.x, -e.y, -e.z),
            c + new Vector3(-e.x, -e.y,  e.z),
            c + new Vector3(-e.x,  e.y, -e.z),
            c + new Vector3(-e.x,  e.y,  e.z),
            c + new Vector3( e.x, -e.y, -e.z),
            c + new Vector3( e.x, -e.y,  e.z),
            c + new Vector3( e.x,  e.y, -e.z),
            c + new Vector3( e.x,  e.y,  e.z)
        };
    }

    private static CameraInfo BuildCameraInfo(Camera camera, Bounds bounds, CaptureView view)
    {
        return new CameraInfo
        {
            viewName = view.name,
            projection = "Perspective",
            position = camera.transform.position,
            eulerAngles = camera.transform.eulerAngles,
            fieldOfView = camera.fieldOfView,
            orthographicSize = camera.orthographicSize,
            nearClipPlane = camera.nearClipPlane,
            farClipPlane = camera.farClipPlane,
            fittedBounds = BoundsToString(bounds)
        };
    }

    private static void Simulate(GameObject root, float time)
    {
        ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < systems.Length; i++)
        {
            systems[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            systems[i].Clear(true);
        }

        for (int i = 0; i < systems.Length; i++)
        {
            systems[i].Simulate(time, true, true, false);
        }
    }

    private static Texture2D RenderCamera(Camera camera)
    {
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = camera.targetTexture;
        RenderTexture rt = RenderTexture.GetTemporary(FrameSize, FrameSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            camera.targetTexture = rt;
            camera.Render();
            RenderTexture.active = rt;
            Texture2D image = new Texture2D(FrameSize, FrameSize, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, FrameSize, FrameSize), 0, 0);
            image.Apply(false, false);
            ForceOpaque(image);
            return image;
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    private static void ForceOpaque(Texture2D image)
    {
        Color32[] pixels = image.GetPixels32();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i].a = 255;
        }

        image.SetPixels32(pixels);
        image.Apply(false, false);
    }

    private static Texture2D ResizeTexture(Texture2D source, int width, int height)
    {
        Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false);
        Color32[] src = source.GetPixels32();
        Color32[] dst = new Color32[width * height];
        int sourceWidth = source.width;
        int sourceHeight = source.height;

        for (int y = 0; y < height; y++)
        {
            int sy = Mathf.Clamp(Mathf.RoundToInt((float)y / Mathf.Max(1, height - 1) * (sourceHeight - 1)), 0, sourceHeight - 1);
            for (int x = 0; x < width; x++)
            {
                int sx = Mathf.Clamp(Mathf.RoundToInt((float)x / Mathf.Max(1, width - 1) * (sourceWidth - 1)), 0, sourceWidth - 1);
                dst[y * width + x] = src[sy * sourceWidth + sx];
            }
        }

        result.SetPixels32(dst);
        result.Apply(false, false);
        return result;
    }

    private static Texture2D BuildContactSheet(List<Texture2D> frames, int thumbSize)
    {
        int count = Mathf.Max(1, frames.Count);
        int columns = Mathf.Min(5, count);
        int rows = Mathf.CeilToInt((float)count / columns);
        Texture2D sheet = new Texture2D(columns * thumbSize, rows * thumbSize, TextureFormat.RGBA32, false);
        Color32[] clear = new Color32[sheet.width * sheet.height];
        for (int i = 0; i < clear.Length; i++)
        {
            clear[i] = new Color32(5, 5, 5, 255);
        }

        sheet.SetPixels32(clear);
        for (int i = 0; i < frames.Count; i++)
        {
            int col = i % columns;
            int row = rows - 1 - (i / columns);
            sheet.SetPixels(col * thumbSize, row * thumbSize, thumbSize, thumbSize, frames[i].GetPixels());
        }

        sheet.Apply(false, false);
        return sheet;
    }

    private static ParticleSettings BuildParticleSettings(string prefabPath, RendererItem item, List<float> sampleTimes)
    {
        ParticleSystem ps = item.particleSystem;
        ParticleSystemRenderer renderer = item.renderer;
        ParticleSystem.MainModule main = ps.main;
        ParticleSystem.EmissionModule emission = ps.emission;
        ParticleSystem.ShapeModule shape = ps.shape;
        ParticleSystem.TrailModule trails = ps.trails;

        return new ParticleSettings
        {
            index = item.index,
            name = renderer.gameObject.name,
            hierarchyPath = item.path,
            prefabPath = prefabPath,
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            main = new MainModuleInfo
            {
                duration = main.duration,
                loop = main.loop,
                startDelay = CurveToString(main.startDelay),
                startLifetime = CurveToString(main.startLifetime),
                startSpeed = CurveToString(main.startSpeed),
                startSize = CurveToString(main.startSize),
                startRotation = CurveToString(main.startRotation),
                startColor = ColorCurveToString(main.startColor),
                gravityModifier = CurveToString(main.gravityModifier),
                maxParticles = main.maxParticles,
                simulationSpace = main.simulationSpace.ToString(),
                scalingMode = main.scalingMode.ToString(),
                playOnAwake = main.playOnAwake,
                prewarm = main.prewarm
            },
            emission = BuildEmissionInfo(emission),
            shape = new ShapeModuleInfo
            {
                enabled = shape.enabled,
                shapeType = shape.shapeType.ToString(),
                angle = shape.angle,
                radius = shape.radius,
                scale = shape.scale,
                arc = shape.arc.ToString("F4", CultureInfo.InvariantCulture)
            },
            velocityOverLifetime = new ModuleState { enabled = ps.velocityOverLifetime.enabled },
            limitVelocityOverLifetime = new ModuleState { enabled = ps.limitVelocityOverLifetime.enabled },
            forceOverLifetime = new ModuleState { enabled = ps.forceOverLifetime.enabled },
            colorOverLifetime = new ModuleState { enabled = ps.colorOverLifetime.enabled },
            sizeOverLifetime = new ModuleState { enabled = ps.sizeOverLifetime.enabled },
            rotationOverLifetime = new ModuleState { enabled = ps.rotationOverLifetime.enabled },
            noise = new ModuleState { enabled = ps.noise.enabled },
            collision = new ModuleState { enabled = ps.collision.enabled },
            trigger = new ModuleState { enabled = ps.trigger.enabled },
            trails = new TrailModuleInfo
            {
                enabled = trails.enabled,
                mode = trails.mode.ToString(),
                lifetime = CurveToString(trails.lifetime),
                widthOverTrail = CurveToString(trails.widthOverTrail),
                colorOverTrail = ColorCurveToString(trails.colorOverTrail),
                ratio = trails.ratio,
                dieWithParticles = trails.dieWithParticles,
                sizeAffectsWidth = trails.sizeAffectsWidth,
                inheritParticleColor = trails.inheritParticleColor
            },
            renderer = BuildRendererInfo(renderer),
            transform = new TransformInfo
            {
                localPosition = renderer.transform.localPosition,
                localEulerAngles = renderer.transform.localEulerAngles,
                localScale = renderer.transform.localScale
            },
            runtime = BuildRuntimeInfo(ps, renderer, sampleTimes)
        };
    }

    private static EmissionModuleInfo BuildEmissionInfo(ParticleSystem.EmissionModule emission)
    {
        EmissionModuleInfo info = new EmissionModuleInfo
        {
            enabled = emission.enabled,
            rateOverTime = CurveToString(emission.rateOverTime),
            rateOverDistance = CurveToString(emission.rateOverDistance),
            burstCount = emission.burstCount
        };

        if (emission.burstCount > 0)
        {
            ParticleSystem.Burst[] bursts = new ParticleSystem.Burst[emission.burstCount];
            emission.GetBursts(bursts);
            for (int i = 0; i < bursts.Length; i++)
            {
                info.bursts.Add(new BurstInfo
                {
                    time = bursts[i].time,
                    count = CurveToString(bursts[i].count),
                    cycleCount = bursts[i].cycleCount,
                    repeatInterval = bursts[i].repeatInterval,
                    probability = bursts[i].probability
                });
            }
        }

        return info;
    }

    private static RendererInfo BuildRendererInfo(ParticleSystemRenderer renderer)
    {
        Material material = renderer.sharedMaterial;
        Texture texture = GetMainTexture(material);
        Material trailMaterial = renderer.trailMaterial;
        Mesh mesh = null;
        try
        {
            mesh = renderer.mesh;
        }
        catch
        {
            mesh = null;
        }

        return new RendererInfo
        {
            enabled = renderer.enabled,
            renderMode = renderer.renderMode.ToString(),
            alignment = renderer.alignment.ToString(),
            sortMode = renderer.sortMode.ToString(),
            sortingLayerName = renderer.sortingLayerName,
            sortingOrder = renderer.sortingOrder,
            materialName = material == null ? null : material.name,
            materialPath = material == null ? null : AssetDatabase.GetAssetPath(material),
            shaderName = material == null || material.shader == null ? null : material.shader.name,
            mainTextureName = texture == null ? null : texture.name,
            mainTexturePath = texture == null ? null : AssetDatabase.GetAssetPath(texture),
            mainTextureWidth = texture == null ? 0 : texture.width,
            mainTextureHeight = texture == null ? 0 : texture.height,
            trailMaterialName = trailMaterial == null ? null : trailMaterial.name,
            trailMaterialPath = trailMaterial == null ? null : AssetDatabase.GetAssetPath(trailMaterial),
            meshName = mesh == null ? null : mesh.name,
            bounds = BoundsToString(renderer.bounds)
        };
    }

    private static RuntimeInfo BuildRuntimeInfo(ParticleSystem ps, ParticleSystemRenderer renderer, List<float> sampleTimes)
    {
        int peak = 0;
        int sum = 0;
        for (int i = 0; i < sampleTimes.Count; i++)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Clear(true);
            ps.Simulate(sampleTimes[i], false, true, false);
            int count = ps.particleCount;
            peak = Mathf.Max(peak, count);
            sum += count;
        }

        ParticleSystem.LightsModule lights = ps.lights;
        return new RuntimeInfo
        {
            peakParticleCount = peak,
            averageParticleCount = sampleTimes.Count == 0 ? 0f : (float)sum / sampleTimes.Count,
            hasTrail = ps.trails.enabled,
            hasNoise = ps.noise.enabled,
            hasCollision = ps.collision.enabled,
            hasLights = lights.enabled,
            materialInstanceCount = renderer.sharedMaterials == null ? 0 : renderer.sharedMaterials.Length
        };
    }

    private static Texture GetMainTexture(Material material)
    {
        if (material == null || material.shader == null)
        {
            return null;
        }

        Texture firstTexture = null;
        int propertyCount = ShaderUtil.GetPropertyCount(material.shader);
        for (int i = 0; i < propertyCount; i++)
        {
            if (ShaderUtil.GetPropertyType(material.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
            {
                continue;
            }

            string propertyName = ShaderUtil.GetPropertyName(material.shader, i);
            Texture texture = material.GetTexture(propertyName);
            if (texture == null)
            {
                continue;
            }

            if (propertyName == "_MainTex" || propertyName == "_BaseMap")
            {
                return texture;
            }

            if (firstTexture == null)
            {
                firstTexture = texture;
            }
        }

        return firstTexture;
    }

    private static ParticleEntry ToManifestEntry(RendererItem item, string outputDir, string particleFolder)
    {
        return new ParticleEntry
        {
            index = item.index,
            name = item.renderer == null ? null : item.renderer.gameObject.name,
            hierarchyPath = item.path,
            soloContactSheet = particleFolder == null ? null : MakeRelativePath(outputDir, Path.Combine(particleFolder, "solo", "solo_contact_sheet.png")),
            withoutContactSheet = particleFolder == null ? null : MakeRelativePath(outputDir, Path.Combine(particleFolder, "without_this", "without_this_contact_sheet.png")),
            settingsJson = particleFolder == null ? null : MakeRelativePath(outputDir, Path.Combine(particleFolder, "settings.json"))
        };
    }

    private static string CreateOutputDirectory(string prefabName)
    {
        string root = Path.GetFullPath("FxAIAnalysisExports");
        string folderName = SanitizePathPart(prefabName) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string outputDir = Path.Combine(root, folderName);
        Directory.CreateDirectory(outputDir);
        return outputDir;
    }

    private static void WriteGuide(string outputDir)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("# FX AI Analysis Guide");
        sb.AppendLine();
        sb.AppendLine("Read `manifest.json` first.");
        sb.AppendLine("Use `full/full_contact_sheet.png` to understand the complete effect timing. Main frames are captured with a tilted perspective camera on a neutral gray background.");
        sb.AppendLine("Additional full-effect view folders are available under `full/views/front`, `full/views/side`, and `full/views/top`.");
        sb.AppendLine("For each entry under `particles/`, compare:");
        sb.AppendLine("- `solo/solo_contact_sheet.png`: what this renderer contributes by itself.");
        sb.AppendLine("- `without_this/without_this_contact_sheet.png`: what the complete effect loses when this renderer is hidden.");
        sb.AppendLine("- `settings.json`: the real ParticleSystem and ParticleSystemRenderer settings.");
        sb.AppendLine();
        sb.AppendLine("For each particle, classify visual role, visual importance, cost risk, and a concrete optimization suggestion.");
        sb.AppendLine("Do not suggest deleting or lowering values only from settings; use the solo/without visual evidence first.");
        File.WriteAllText(Path.Combine(outputDir, "AI_ANALYSIS_GUIDE.md"), sb.ToString(), Utf8NoBom);
    }

    private static string GetHierarchyPath(Transform root, Transform transform)
    {
        if (transform == root)
        {
            return transform.name;
        }

        Stack<string> names = new Stack<string>();
        Transform current = transform;
        while (current != null)
        {
            names.Push(current.name);
            if (current == root)
            {
                break;
            }

            current = current.parent;
        }

        return string.Join("/", names.ToArray());
    }

    private static string SanitizePathPart(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unnamed";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool bad = false;
            for (int j = 0; j < invalid.Length; j++)
            {
                if (c == invalid[j])
                {
                    bad = true;
                    break;
                }
            }

            sb.Append(bad ? '_' : c);
        }

        return sb.ToString();
    }

    private static string MakeRelativePath(string fromDirectory, string fullPath)
    {
        Uri from = new Uri(AppendDirectorySeparatorChar(fromDirectory));
        Uri to = new Uri(fullPath);
        return Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString()).Replace('/', Path.DirectorySeparatorChar);
    }

    private static string AppendDirectorySeparatorChar(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            return path + Path.DirectorySeparatorChar;
        }

        return path;
    }

    private static string BoundsToString(Bounds bounds)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "center=({0:F3},{1:F3},{2:F3}), size=({3:F3},{4:F3},{5:F3})",
            bounds.center.x,
            bounds.center.y,
            bounds.center.z,
            bounds.size.x,
            bounds.size.y,
            bounds.size.z);
    }

    private static string CurveToString(ParticleSystem.MinMaxCurve curve)
    {
        switch (curve.mode)
        {
            case ParticleSystemCurveMode.Constant:
                return curve.constant.ToString("F4", CultureInfo.InvariantCulture);
            case ParticleSystemCurveMode.TwoConstants:
                return string.Format(CultureInfo.InvariantCulture, "{0:F4}-{1:F4}", curve.constantMin, curve.constantMax);
            case ParticleSystemCurveMode.Curve:
                return string.Format(CultureInfo.InvariantCulture, "curve multiplier={0:F4}", curve.curveMultiplier);
            case ParticleSystemCurveMode.TwoCurves:
                return string.Format(CultureInfo.InvariantCulture, "twoCurves multiplier={0:F4}", curve.curveMultiplier);
            default:
                return curve.mode.ToString();
        }
    }

    private static string ColorCurveToString(ParticleSystem.MinMaxGradient gradient)
    {
        switch (gradient.mode)
        {
            case ParticleSystemGradientMode.Color:
                return ColorUtility.ToHtmlStringRGBA(gradient.color);
            case ParticleSystemGradientMode.TwoColors:
                return ColorUtility.ToHtmlStringRGBA(gradient.colorMin) + "-" + ColorUtility.ToHtmlStringRGBA(gradient.colorMax);
            case ParticleSystemGradientMode.Gradient:
                return "gradient";
            case ParticleSystemGradientMode.TwoGradients:
                return "twoGradients";
            case ParticleSystemGradientMode.RandomColor:
                return "randomColor";
            default:
                return gradient.mode.ToString();
        }
    }

    private static float EvaluateMax(ParticleSystem.MinMaxCurve curve)
    {
        switch (curve.mode)
        {
            case ParticleSystemCurveMode.Constant:
                return curve.constant;
            case ParticleSystemCurveMode.TwoConstants:
                return Mathf.Max(curve.constantMin, curve.constantMax);
            case ParticleSystemCurveMode.Curve:
                return EvaluateCurveMax(curve.curve) * curve.curveMultiplier;
            case ParticleSystemCurveMode.TwoCurves:
                return Mathf.Max(EvaluateCurveMax(curve.curveMin), EvaluateCurveMax(curve.curveMax)) * curve.curveMultiplier;
            default:
                return 0f;
        }
    }

    private static float EvaluateCurveMax(AnimationCurve curve)
    {
        if (curve == null || curve.length == 0)
        {
            return 0f;
        }

        float max = float.MinValue;
        for (int i = 0; i <= 16; i++)
        {
            float t = i / 16f;
            max = Mathf.Max(max, curve.Evaluate(t));
        }

        return max;
    }
}
