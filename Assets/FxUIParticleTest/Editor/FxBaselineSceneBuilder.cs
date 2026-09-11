using System.Collections.Generic;
using System.Linq;
using Coffee.UIExtensions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FxUIParticleTest
{
    /// <summary>
    /// 构建基线测试场景:1080x1920 竖屏 Canvas,5x6 特效槽位之间穿插普通 UI Image,
    /// 底部三个面板分别套 Mask / RectMask2D / CanvasGroup。
    /// 菜单:FxUIParticle > 2. Build Baseline Scene
    /// </summary>
    public static class FxBaselineSceneBuilder
    {
        const string EffectDir = "Assets/FxUIParticleTest/Effects";
        const string ScenePath = "Assets/FxUIParticleTest/Scenes/FxUIParticle_Baseline.unity";

        const float SlotW = 170f, SlotH = 150f, Gap = 6f;

        [MenuItem("FxUIParticle/2. Build Baseline Scene")]
        public static void Build()
        {
            var prefabs = LoadEffectPrefabs();
            if (prefabs.Count == 0)
            {
                Debug.LogError("[FxBaselineSceneBuilder] Effects 目录为空,请先执行菜单 FxUIParticle/1 生成特效库。");
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode
                || !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateCameraAndEventSystem();
            var canvasGo = CreateCanvas();
            var canvas = canvasGo.transform;

            CreateBackground(canvas);
            var caseLabel = CreateHeaderText(canvas, out var font);

            var gridSlots = CreateEffectGrid(canvas);
            var specialSlots = CreateSpecialPanels(canvas, font);

            var controller = canvasGo.AddComponent<FxBaselineController>();
            controller.autoRecord = false;
            controller.initialCase = 6;
            controller.effectPrefabs = prefabs.ToArray();
            controller.gridSlots = gridSlots.ToArray();
            controller.specialSlots = specialSlots.ToArray();
            controller.specialPanels = new[]
            {
                canvas.Find("Panel_Mask").gameObject,
                canvas.Find("Panel_RectMask").gameObject,
                canvas.Find("Panel_CanvasGroup").gameObject,
            };
            controller.caseLabel = caseLabel;

            System.IO.Directory.CreateDirectory("Assets/FxUIParticleTest/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            var buildScenes = EditorBuildSettings.scenes.ToList();
            if (buildScenes.All(s => s.path != ScenePath))
            {
                buildScenes.Add(new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = buildScenes.ToArray();
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[FxBaselineSceneBuilder] 场景已生成:{ScenePath}(已加入 Build Settings)," +
                      $"特效槽位 {gridSlots.Count} + 面板槽位 {specialSlots.Count},预制体 {prefabs.Count} 个。");
        }

        static List<GameObject> LoadEffectPrefabs()
        {
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { EffectDir });
            return guids
                .Select(g => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(p => p != null)
                .OrderBy(p => p.name)
                .ToList();
        }

        static void CreateCameraAndEventSystem()
        {
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.06f, 0.07f, 0.1f, 1f);
            cam.transform.position = new Vector3(0f, 0f, -10f);

            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
            esGo.AddComponent<StandaloneInputModule>();
        }

        static GameObject CreateCanvas()
        {
            var go = new GameObject("Canvas");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;

            go.AddComponent<GraphicRaycaster>();
            return go;
        }

        static void CreateBackground(Transform canvas)
        {
            var bg = CreateImage(canvas, "Background", new Color(0.09f, 0.1f, 0.14f, 1f));
            Stretch((RectTransform)bg.transform);
        }

        static Text CreateHeaderText(Transform canvas, out Font font)
        {
            font = LoadFont();
            var go = new GameObject("CaseLabel", typeof(RectTransform));
            go.transform.SetParent(canvas, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(1000f, 70f);
            rt.anchoredPosition = new Vector2(0f, -55f);

            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = 40;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = "Case C: 10 Different Effects";
            return text;
        }

        static List<RectTransform> CreateEffectGrid(Transform canvas)
        {
            var grid = new GameObject("EffectGrid", typeof(RectTransform));
            grid.transform.SetParent(canvas, false);
            var gridRt = (RectTransform)grid.transform;
            Stretch(gridRt);

            // 5 列 x 6 行,槽位 170x150,间距 6;y 范围 755 ~ -25(画布中心原点,±960)
            var slots = new List<RectTransform>(30);
            for (var r = 0; r < 6; r++)
            {
                for (var c = 0; c < 5; c++)
                {
                    var slot = new GameObject($"Slot_{slots.Count:00}", typeof(RectTransform));
                    slot.transform.SetParent(grid.transform, false);
                    var rt = (RectTransform)slot.transform;
                    rt.sizeDelta = new Vector2(SlotW, SlotH);
                    rt.anchoredPosition = new Vector2(
                        -352f + c * (SlotW + Gap),
                        755f - r * (SlotH + Gap));

                    // 槽位之间穿插一个普通 UI Image,模拟真实 UI 中粒子与图元交替的层级
                    var sep = CreateImage(grid.transform, $"Sep_{slots.Count:00}",
                        new Color(Random.value, Random.value * 0.5f, Random.value * 0.3f, 1f));
                    var sepRt = (RectTransform)sep.transform;
                    sepRt.sizeDelta = new Vector2(SlotW, 10f);
                    sepRt.anchoredPosition = new Vector2(rt.anchoredPosition.x, rt.anchoredPosition.y - SlotH / 2f - 8f);

                    slots.Add(rt);
                }
            }
            return slots;
        }

        static List<RectTransform> CreateSpecialPanels(Transform canvas, Font font)
        {
            var slots = new List<RectTransform>(9);
            CreateSpecialPanel(canvas, "Panel_Mask", -360f, font,
                p => { p.AddComponent<Mask>(); p.GetComponent<Mask>().showMaskGraphic = false; }, slots);
            CreateSpecialPanel(canvas, "Panel_RectMask", 0f, font,
                p => p.AddComponent<RectMask2D>(), slots);
            CreateSpecialPanel(canvas, "Panel_CanvasGroup", 360f, font,
                p =>
                {
                    var cg = p.AddComponent<CanvasGroup>();
                    cg.alpha = 0.6f;
                    cg.interactable = false;
                    cg.blocksRaycasts = false;
                }, slots);
            return slots;
        }

        static void CreateSpecialPanel(Transform canvas, string name, float x, Font font,
            System.Action<GameObject> addMask, List<RectTransform> slots)
        {
            var panel = CreateImage(canvas, name, new Color(0.16f, 0.18f, 0.24f, 1f));
            var rt = (RectTransform)panel.transform;
            rt.sizeDelta = new Vector2(340f, 470f);
            rt.anchoredPosition = new Vector2(x, -365f);
            addMask(panel);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(panel.transform, false);
            var labelRt = (RectTransform)labelGo.transform;
            labelRt.anchorMin = new Vector2(0.5f, 1f);
            labelRt.anchorMax = new Vector2(0.5f, 1f);
            labelRt.pivot = new Vector2(0.5f, 1f);
            labelRt.sizeDelta = new Vector2(320f, 30f);
            labelRt.anchoredPosition = new Vector2(0f, -6f);
            var label = labelGo.AddComponent<Text>();
            label.font = font;
            label.fontSize = 24;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = new Color(0.8f, 0.85f, 1f);
            label.text = name;

            for (var i = 0; i < 3; i++)
            {
                var slot = new GameObject($"{name}_Slot_{i}", typeof(RectTransform));
                slot.transform.SetParent(panel.transform, false);
                var slotRt = (RectTransform)slot.transform;
                slotRt.sizeDelta = new Vector2(300f, 130f);
                slotRt.anchoredPosition = new Vector2(0f, 164f - i * 136f);
                slots.Add(slotRt);
            }
        }

        static GameObject CreateImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            return go;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static Font LoadFont()
        {
            try
            {
                return Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            catch
            {
                return Font.CreateDynamicFontFromOSFont("Roboto", 40);
            }
        }
    }
}
