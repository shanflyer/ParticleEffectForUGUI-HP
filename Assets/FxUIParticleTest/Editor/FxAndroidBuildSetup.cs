using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace FxUIParticleTest
{
    /// <summary>
    /// 菜单:FxUIParticle > 3. Prepare Android Build
    /// 基线场景置顶(index 0)+ IL2CPP + ARM64,一条菜单完成安卓构建前配置。
    /// </summary>
    public static class FxAndroidBuildSetup
    {
        [MenuItem("FxUIParticle/3. Prepare Android Build (IL2CPP+ARM64+Scene)")]
        public static void Prepare()
        {
            const string scenePath = "Assets/FxUIParticleTest/Scenes/FxUIParticle_Baseline.unity";

            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.RemoveAll(s => s.path == scenePath);
            scenes.Insert(0, new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            Debug.Log($"[FxAndroidBuildSetup] 完成:场景置顶 {scenePath},IL2CPP + ARM64。" +
                      $"包名 {Application.identifier},Switch Platform 后即可 Build。");
        }
    }
}
