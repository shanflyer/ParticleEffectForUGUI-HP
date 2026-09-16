using System.Collections.Generic;
using System.IO;
using Coffee.UIExtensions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace FxUIParticleTest
{
    public static class SpriteMaskComparisonSceneBuilder
    {
        private const string Folder = "Assets/FxUIParticleTest/SpriteMaskDemo";

        public static void BuildValidationPlayer()
        {
            if (!Application.isBatchMode) throw new System.InvalidOperationException("Batch validation only.");
            CreateScene();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Folder + "/SpriteMaskComparison.unity" },
                locationPathName = "Builds/SpriteMaskValidation/SpriteMaskValidation.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development
            });
            EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }

        [MenuItem("FxTest/SpriteMask/Create Native vs HP Scene")]
        public static void CreateScene()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(Folder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var pixels = new Color[64 * 64];
            for (var y = 0; y < 64; y++)
                for (var x = 0; x < 64; x++)
                    pixels[y * 64 + x] = new Color(1, 1, 1,
                        Mathf.Clamp01((1 - new Vector2((x - 31.5f) / 32, (y - 31.5f) / 32).magnitude) * 3));
            texture.SetPixels(pixels);
            texture.Apply();
            File.WriteAllBytes(Folder + "/Mask.png", texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(Folder + "/Mask.png");
            var importer = (TextureImporter)AssetImporter.GetAtPath(Folder + "/Mask.png");
            importer.textureType = TextureImporterType.Sprite;
            importer.spritePixelsPerUnit = 48;
            importer.alphaIsTransparency = true;
            importer.SaveAndReimport();
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(Folder + "/Mask.png");
            var native = AssetDatabase.LoadAssetAtPath<Material>(Folder + "/Native.mat");
            if (!native)
            {
                native = new Material(Shader.Find("Sprites/Default"));
                AssetDatabase.CreateAsset(native, Folder + "/Native.mat");
            }
            var hp = AssetDatabase.LoadAssetAtPath<Material>(Folder + "/HP.mat");
            if (!hp)
            {
                hp = new Material(Shader.Find("UI/Additive"));
                AssetDatabase.CreateAsset(hp, Folder + "/HP.mat");
            }
            var camera = new GameObject("Comparison Camera", typeof(Camera)).GetComponent<Camera>();
            camera.transform.position = new Vector3(0, 0, -10);
            camera.orthographic = true;
            camera.orthographicSize = 4;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.025f, .035f, .05f);
            camera.tag = "MainCamera";
            var canvas = new GameObject("World Canvas", typeof(RectTransform), typeof(Canvas)).GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(12, 8);
            var masks = new List<SpriteMask>();
            for (var row = 0; row < 2; row++)
                for (var side = 0; side < 2; side++)
                {
                    var root = new GameObject((side == 0 ? "Native " : "HP ") + (row == 0 ? "Inside" : "Outside"),
                        typeof(RectTransform), typeof(SortingGroup));
                    if (side == 1) root.transform.SetParent(canvas.transform, false);
                    root.transform.localPosition = new Vector3(side == 0 ? -2.7f : 2.7f, row == 0 ? 1.5f : -1.5f, 0);
                    for (var i = 0; i < 2; i++)
                    {
                        var mask = new GameObject("SpriteMask " + i, typeof(SpriteMask)).GetComponent<SpriteMask>();
                        mask.transform.SetParent(root.transform, false);
                        mask.transform.localPosition = new Vector3(i == 0 ? -.3f : .35f, i == 0 ? -.1f : .25f, 0);
                        mask.sprite = sprite;
                        mask.alphaCutoff = .65f;
                        masks.Add(mask);
                    }
                    var ps = new GameObject("Original particle", typeof(ParticleSystem)).GetComponent<ParticleSystem>();
                    ps.transform.SetParent(root.transform, false);
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.useAutoRandomSeed = false;
                    ps.randomSeed = 1234;
                    var main = ps.main;
                    main.startLifetime = 2;
                    main.startSize = .12f;
                    main.startSpeed = .08f;
                    main.maxParticles = 800;
                    main.startColor = new Color(.2f, .75f, 1, .9f);
                    var emission = ps.emission; emission.rateOverTime = 350;
                    var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Box; shape.scale = new Vector3(2.5f, 2, 0);
                    var renderer = ps.GetComponent<ParticleSystemRenderer>();
                    renderer.sharedMaterial = side == 0 ? native : hp;
                    renderer.maskInteraction = row == 0 ? SpriteMaskInteraction.VisibleInsideMask : SpriteMaskInteraction.VisibleOutsideMask;
                    if (side == 1)
                    {
                        var ui = root.AddComponent<UIParticle>();
                        ui.scale = 1;
                        ui.autoScalingMode = UIParticle.AutoScalingMode.None;
                        ui.RefreshParticles();
                    }
                }
            var driver = new GameObject("Mask Animation and Pool Controls", typeof(SpriteMaskComparisonDriver)).GetComponent<SpriteMaskComparisonDriver>();
            driver.masks = masks.ToArray();
            Label(canvas, "Native SpriteMask", new Vector2(-2.7f, 3.35f));
            Label(canvas, "HP Canvas SpriteMask", new Vector2(2.7f, 3.35f));
            Label(canvas, "Inside (top) / Outside (bottom)", new Vector2(0, -3.35f));
            EditorSceneManager.SaveScene(scene, Folder + "/SpriteMaskComparison.unity");
            AssetDatabase.SaveAssets();
            Debug.Log("Created " + Folder + "/SpriteMaskComparison.unity. Enter Play mode; use Mask Animation and Pool Controls.");
        }

        private static void Label(Canvas canvas, string value, Vector2 position)
        {
            var label = new GameObject(value, typeof(RectTransform), typeof(Text)).GetComponent<Text>();
            label.transform.SetParent(canvas.transform, false);
            label.rectTransform.anchoredPosition = position;
            label.rectTransform.sizeDelta = new Vector2(900, 80);
            label.transform.localScale = Vector3.one * .006f;
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 42;
            label.alignment = TextAnchor.MiddleCenter;
            label.text = value;
        }
    }
}
