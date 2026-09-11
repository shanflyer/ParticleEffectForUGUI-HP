using System.Collections.Generic;
using Coffee.UIExtensions;
using UnityEditor;
using UnityEngine;

namespace FxUIParticleTest
{
    /// <summary>
    /// 生成 30 个典型 UI 特效预制体:每个特效 3~5 个子 ParticleSystem,
    /// 复用工程内现有材质(7 个)与纹理,约 1/4 带 Trail。
    /// 菜单:FxUIParticle > 1. Generate Effect Library (30 Effects)
    /// </summary>
    public static class FxEffectLibraryGenerator
    {
        const string OutputDir = "Assets/FxUIParticleTest/Effects";
        const int EffectCount = 30;

        static readonly string[] MaterialPaths =
        {
            "Assets/Demo/Performance Demo/Materials/UIParticle_PerformanceDemo_Fire.mat",
            "Assets/Demo/Performance Demo/Materials/UIParticle_PerformanceDemo_Spread.mat",
            "Assets/Demo/CustomView/UI-Star.mat",
            "Assets/Demo/CustomView/UI-Star-Add.mat",
            "Assets/Demo/CustomView/UI-Cloud.mat",
            "Assets/Demo/CustomView/UI-StretchTrait.mat",
            "Packages/com.coffee.ui-particle/Shaders/UIAdditive.mat",
        };

        [MenuItem("FxUIParticle/1. Generate Effect Library (30 Effects)")]
        public static void Generate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var materials = new List<Material>();
            foreach (var path in MaterialPaths)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat != null) materials.Add(mat);
            }

            if (materials.Count == 0)
            {
                Debug.LogError("[FxEffectLibraryGenerator] 未找到任何可用材质,检查 MaterialPaths。");
                return;
            }

            if (!AssetDatabase.IsValidFolder(OutputDir))
            {
                if (!AssetDatabase.IsValidFolder("Assets/FxUIParticleTest"))
                    AssetDatabase.CreateFolder("Assets", "FxUIParticleTest");
                AssetDatabase.CreateFolder("Assets/FxUIParticleTest", "Effects");
            }

            var rand = new System.Random(20260831);
            var created = 0;

            for (var i = 0; i < EffectCount; i++)
            {
                var path = $"{OutputDir}/EF_{i:00}.prefab";

                var root = new GameObject($"EF_{i:00}", typeof(RectTransform));
                try
                {
                    var rt = (RectTransform)root.transform;
                    rt.sizeDelta = new Vector2(240f, 240f);

                    var hue = (float)i / EffectCount;
                    var baseColor = Color.HSVToRGB(hue, 0.75f, 1f);
                    var mat = materials[i % materials.Count];
                    var withTrail = i % 4 == 0;
                    var psCount = 3 + rand.Next(3); // 3~5

                    for (var j = 0; j < psCount; j++)
                    {
                        CreateChildParticleSystem(root.transform, j, baseColor, mat, withTrail, rand);
                    }

                    var uiParticle = root.AddComponent<UIParticle>();
                    uiParticle.scale = 1f;
                    uiParticle.RefreshParticles();

                    PrefabUtility.SaveAsPrefabAssetAndConnect(
                        root, path, InteractionMode.AutomatedAction);
                    created++;
                }
                finally { if (root != null) Object.DestroyImmediate(root); }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[FxEffectLibraryGenerator] 完成:生成/确认 {created}/{EffectCount} 个特效预制体于 {OutputDir}" +
                      $",材质数 {materials.Count},带 Trail 特效 {(EffectCount + 3) / 4} 个。");
            EditorApplication.delayCall += () =>
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Object>(OutputDir));
        }

        static void CreateChildParticleSystem(Transform parent, int index, Color baseColor,
            Material mat, bool withTrail, System.Random rand)
        {
            var go = new GameObject($"PS_{index}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(
                ((float)rand.NextDouble() - 0.5f) * 160f,
                ((float)rand.NextDouble() - 0.5f) * 160f, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true;
            main.duration = 1f + (float)rand.NextDouble() * 1.5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(20f, 150f);
            var sizeMin = 15f + (float)rand.NextDouble() * 20f;
            main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMin + 30f + (float)rand.NextDouble() * 30f);
            var shade = 0.6f + (float)rand.NextDouble() * 0.4f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(baseColor.r * shade, baseColor.g * shade, baseColor.b * shade, 1f));
            main.maxParticles = 200 + rand.Next(400);
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Local;
            main.gravityModifier = 0f;
            main.playOnAwake = true;

            var emission = ps.emission;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(20f + (float)rand.NextDouble() * 30f);
            if (index == 0 && rand.Next(5) == 0)
            {
                emission.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)(30 + rand.Next(50))) });
            }

            var shape = ps.shape;
            shape.enabled = true;
            switch (rand.Next(4))
            {
                case 0:
                    shape.shapeType = ParticleSystemShapeType.Box;
                    shape.scale = new Vector3(1f, 1f, 0.1f);
                    break;
                case 1:
                    shape.shapeType = ParticleSystemShapeType.Circle;
                    shape.radius = 0.8f;
                    break;
                case 2:
                    shape.shapeType = ParticleSystemShapeType.Sphere;
                    shape.radius = 0.8f;
                    break;
                default:
                    shape.shapeType = ParticleSystemShapeType.Cone;
                    shape.radius = 0.6f;
                    shape.angle = 25f;
                    break;
            }

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(baseColor, 0f), new GradientColorKey(baseColor, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.6f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            var shrink = rand.Next(2) == 0;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, shrink ? 1f : 0.4f, 1f, shrink ? 0.4f : 1f));

            if (rand.Next(10) < 4)
            {
                var rotation = ps.rotationOverLifetime;
                rotation.enabled = true;
                var speed = (rand.Next(2) == 0 ? 1 : -1) * (45f + (float)rand.NextDouble() * 90f);
                rotation.z = new ParticleSystem.MinMaxCurve(speed * Mathf.Deg2Rad);
            }

            var psr = ps.GetComponent<ParticleSystemRenderer>();
            psr.renderMode = ParticleSystemRenderMode.Billboard;
            psr.sharedMaterial = mat;
            psr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            psr.receiveShadows = false;

            if (withTrail)
            {
                var trails = ps.trails;
                trails.enabled = true;
                trails.ratio = 1f;
                trails.lifetime = 0.4f;
                trails.dieWithParticles = true;
                trails.inheritParticleColor = true;
                trails.minVertexDistance = 0.2f;
                psr.trailMaterial = mat;
            }
        }
    }
}
