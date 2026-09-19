using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEngine;

namespace FxUIParticleTest
{
    public static class Unity6Validation
    {
        // Compile real Player assemblies without building or launching a player.
        public static void CompilePlayerScripts()
        {
            try
            {
                const string output = "TempDiag/Unity6PlayerScripts";
                Directory.CreateDirectory(output);
                var result = PlayerBuildInterface.CompilePlayerScripts(new ScriptCompilationSettings
                {
                    target = BuildTarget.StandaloneWindows64,
                    group = BuildTargetGroup.Standalone,
                    options = ScriptCompilationOptions.DevelopmentBuild
                }, output);
                if (result.assemblies == null || result.assemblies.Count == 0)
                    throw new InvalidOperationException("Player script compilation returned no assemblies.");
                File.WriteAllLines(output + "/assemblies.txt", result.assemblies);
                Debug.Log("PASS Unity 6 Player script compilation: " + result.assemblies.Count + " assemblies");
                EditorApplication.Exit(0);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
    }
}
