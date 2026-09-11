using System.Diagnostics;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace FxUIParticleTest
{
    /// <summary>
    /// 菜单:FxUIParticle > 4. Build And Deploy APK
    /// 构建 APK/00.apk -> adb install -r -> 启动主 Activity,一键完成部署。
    /// adb 路径优先取环境变量 ADB_PATH,其次 ANDROID_SDK_ROOT / ANDROID_HOME,最后回退到 PATH。
    /// </summary>
    public static class FxBuildAndDeploy
    {
        const string ApkPath = "APK/00.apk";
        const string Package = "com.Coffee.ParticleEffectForUGUI";
        static bool _running;

        static string Adb
        {
            get
            {
                var explicitPath = System.Environment.GetEnvironmentVariable("ADB_PATH");
                if (!string.IsNullOrEmpty(explicitPath) && System.IO.File.Exists(explicitPath))
                    return explicitPath;

                foreach (var variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
                {
                    var root = System.Environment.GetEnvironmentVariable(variable);
                    if (string.IsNullOrEmpty(root)) continue;
                    foreach (var name in new[] { "adb.exe", "adb" })
                    {
                        var candidate = System.IO.Path.Combine(root, "platform-tools", name);
                        if (System.IO.File.Exists(candidate)) return candidate;
                    }
                }

                // 回退到 PATH 上的 adb。
                return "adb";
            }
        }

        [MenuItem("FxUIParticle/4. Build And Deploy APK")]
        public static async void BuildAndDeploy()
        {
            if (_running || EditorApplication.isPlayingOrWillChangePlaymode) return;
            _running = true;
            try
            {
                var report = BuildPipeline.BuildPlayer(
                    EditorBuildSettings.scenes,
                    ApkPath,
                    BuildTarget.Android,
                    BuildOptions.None
                );
                if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                {
                    UnityEngine.Debug.LogError($"[FxBuildAndDeploy] 构建失败: {report.summary.result}");
                    return;
                }

                if (!await Run($"install -r -t \"{System.IO.Path.GetFullPath(ApkPath)}\"")
                    || !await Run("logcat -c")
                    || !await Run($"shell am force-stop {Package}")
                    || !await Run($"shell am start -n {Package}/com.unity3d.player.UnityPlayerActivity"))
                {
                    UnityEngine.Debug.LogError("[FxBuildAndDeploy] adb 失败或超时，已停止后续部署步骤。");
                    return;
                }
                UnityEngine.Debug.Log("[FxBuildAndDeploy] 完成:构建 + 安装 + 启动。");
            }
            catch (System.Exception e) { UnityEngine.Debug.LogException(e); }
            finally { _running = false; }
        }

        static Task<bool> Run(string args)
        {
            // Waiting for adb on the editor thread freezes its UI for up to two minutes.
            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo(Adb, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    if (!p.WaitForExit(120000))
                    {
                        if (!p.HasExited) p.Kill(); // Only this invocation's client process.
                        return false;
                    }
                    return p.ExitCode == 0;
                }
            });
        }
    }
}