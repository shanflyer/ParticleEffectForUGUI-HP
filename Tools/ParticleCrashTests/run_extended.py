"""Additional managed-only regressions. Never launches Unity or a graphics context."""
import json
from pathlib import Path
import subprocess
import sys
import argparse
from unity_environment import default_editor, framework_path, runtime_config

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "TempDiag/crash_fix_compile"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--editor", type=Path, default=default_editor())
EDITOR = parser.parse_args().editor
RUNTIME = EDITOR / "Data/NetCoreRuntime"
DOTNET = RUNTIME / "dotnet.exe"
CSC = EDITOR / "Data/DotNetSdkRoslyn/csc.dll"
FRAMEWORK = framework_path(EDITOR)

def check(name, sources, defines="", expect_fail=False):
    OUT.mkdir(parents=True, exist_ok=True)
    dll = OUT / (name + ".dll")
    lines = ["-nologo", "-target:exe", "-nostdlib+", f'-out:"{dll}"']
    if defines:
        lines.append("-define:" + defines)
    for ref in ("System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Console", "System.Linq"):
        lines.append(f'-r:"{FRAMEWORK / (ref + ".dll")}"')
    lines.extend('"' + src + '"' for src in sources)
    rsp = dll.with_suffix(".rsp")
    rsp.write_text("\n".join(lines), encoding="utf-8")
    subprocess.run([str(DOTNET), str(CSC), "@" + str(rsp)], cwd=ROOT, check=True)
    dll.with_suffix(".runtimeconfig.json").write_text(json.dumps(runtime_config(FRAMEWORK)), encoding="utf-8")
    result = subprocess.run([str(DOTNET), str(dll)], cwd=ROOT, capture_output=True, text=True)
    (OUT / (name + "_results.txt")).write_text(result.stdout + result.stderr, encoding="utf-8")
    print(name + "\n" + result.stdout + result.stderr)
    if (result.returncode != 0) != expect_fail:
        raise RuntimeError(name + " unexpected result")

def compile_player():
    player_output = OUT / "player"
    player_output.mkdir(parents=True, exist_ok=True)
    for name in ("Unity.RenderPipelines.Universal.Runtime", "Coffee.UIParticle", "Assembly-CSharp"):
        responses = list((ROOT / "Library/Bee/artifacts").glob("*PDevDbg.dag/" + name + ".rsp"))
        if not responses:
            print("SKIP cached Player compilation (build a Player first): " + name)
            continue
        source = max(responses, key=lambda path: path.stat().st_mtime)
        lines = [line for line in source.read_text(encoding="utf-8-sig").splitlines()
                 if not line.startswith(("-out:", "-refout:", "-analyzer:", "-additionalfile:"))]
        lines = [line for line in lines if "Assets/FxUIParticleTest/Overdraw/" not in line.replace("\\", "/")]
        if name == "Coffee.UIParticle":
            known = {line.strip('"').replace('\\', '/') for line in lines}
            for src in (ROOT / "Packages/src/Runtime").rglob("*.cs"):
                relative = src.relative_to(ROOT).as_posix()
                if relative not in known: lines.append('"' + relative + '"')
        if name == "Assembly-CSharp":
            lines = ['-r:"TempDiag/crash_fix_compile/player/Coffee.UIParticle.dll"'
                     if line.startswith("-r:") and "Coffee.UIParticle" in line else line for line in lines]
        # Preserve assembly identity: Unity 6 uses InternalsVisibleTo between URP/Core.
        lines.append(f'-out:"{player_output / (name + ".dll")}"')
        rsp = OUT / ("player_" + name + ".rsp")
        rsp.write_text("\n".join(lines), encoding="utf-8")
        result = subprocess.run([str(DOTNET), str(CSC), "@" + str(rsp)], cwd=ROOT, capture_output=True, text=True)
        (OUT / ("player_" + name + "_compile.txt")).write_text(result.stdout + result.stderr, encoding="utf-8")
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        print("PASS offline Player compilation: " + name)

def check_renderer_optimizations():
    source = (ROOT / "Packages/src/Runtime/UIParticleRenderer.cs").read_text(encoding="utf-8-sig")
    def member(signature):
        start = source.index(signature)
        opening = source.index("{", start)
        depth = 1
        end = opening + 1
        while depth:
            depth += (source[end] == "{") - (source[end] == "}")
            end += 1
        return source[start:end]
    methods = [member(name) for name in (
        "private void EnsureMergedMeshes()", "private void ReleaseMergedMeshes()",
        "private void SetCanvasRendererMaterials(CanvasRenderer cr)", "private void ClearCanvas()",
        "internal bool bindingIsInvalid", "internal void InvalidateMeshCache()", "public override void Cull(")]
    assert "new Mesh" not in member("public void SetMerged("), "Replica binding must remain lazy"
    extracted = OUT / "RendererOptimizationMethods.cs"
    extracted.write_text("using UnityEngine; using UnityEngine.Rendering; using Coffee.UIParticleInternal;\n"
                         "namespace Coffee.UIExtensions { internal partial class UIParticleRenderer {\n"
                         + "\n".join(methods) + "\n}}", encoding="utf-8")
    check("renderer_optimizations", ["Tools/ParticleCrashTests/RendererOptimizationHarness.cs",
          extracted.relative_to(ROOT).as_posix(), "Packages/src/Runtime/UIParticleProfiler.cs"])

def check_recording_controls():
    source = (ROOT / "Assets/FxUIParticleTest/Runtime/FxBaselineController.cs").read_text(encoding="utf-8-sig")
    methods = []
    for signature in ("public void StartRecording()", "public void StopRecording()",
                      "void RestartRecordingForStateChange()", "void UpdateRecordingControls()"):
        start = source.index(signature)
        end = source.index("{", start) + 1
        depth = 1
        while depth:
            depth += (source[end] == "{") - (source[end] == "}")
            end += 1
        methods.append(source[start:end])
    extracted = OUT / "RecordingControlMethods.cs"
    extracted.write_text("namespace FxUIParticleTest { partial class FxBaselineController {\n"
                         + "\n".join(methods) + "\n}}", encoding="utf-8")
    check("recording_controls", ["Tools/ParticleCrashTests/RecordingControlsHarness.cs", extracted.relative_to(ROOT).as_posix()])
    scene = (ROOT / "Assets/FxUIParticleTest/Scenes/FxUIParticle_Baseline.unity").read_text(encoding="utf-8-sig")
    assert "  autoRecord: 0" in scene and "  initialCase: 6" in scene and "  stressCopiesPerSlot: 4" in scene
    assert '"Start Rec", StartRecording)' in source and '"Stop / Save", StopRecording)' in source

def check_particle_quantity():
    source = (ROOT / "Assets/FxUIParticleTest/Runtime/FxBaselineController.cs").read_text(encoding="utf-8-sig")
    start = source.index("internal sealed class ParticleQuantitySetting")
    end = source.index("{", start) + 1
    depth = 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    extracted = OUT / "ParticleQuantitySetting.cs"
    update_start = source.index("internal sealed class ParticleUpdateSetting")
    update_end = source.index("{", update_start) + 1
    depth = 1
    while depth:
        depth += (source[update_end] == "{") - (source[update_end] == "}")
        update_end += 1
    extracted.write_text("using System.Collections.Generic; using UnityEngine; using Coffee.UIExtensions; namespace FxUIParticleTest {\n"
                         + source[update_start:update_end] + "\n" + source[start:end] + "\n}", encoding="utf-8")
    check("particle_quantity", ["Tools/ParticleCrashTests/ParticleQuantityHarness.cs", extracted.relative_to(ROOT).as_posix()])
    assert '" ParticlePercent="' in source and "ApplyParticleSystemSelection()" in source
    assert "setting.Apply(_enabledParticleSystems, _paused)" in source
    assert "[SerializeField, Range(0, 20)] int particleQuantityLevel = 20" in source
    assert "const int ParticleQuantityStepPercent = 5" in source
    assert "() => SetParticleQuantityLevel(ResetParticleQuantityLevel)" in source
    assert "var target = total * particleQuantityLevel / 20" in source

if __name__ == "__main__":
    subprocess.run([sys.executable, str(ROOT / "Tools/ParticleCrashTests/run_offline.py"), "--editor", str(EDITOR)], cwd=ROOT, check=True)
    check("logic_extended", ["Tools/ParticleCrashTests/LogicHarness.cs", "Packages/src/Runtime/ParticleBakeClock.cs",
                            "Packages/src/Runtime/Internal/Utilities/FastAction.cs", "Packages/src/Runtime/Internal/Utilities/ObjectPool.cs",
                            "Assets/FxUIParticleTest/Runtime/FxProfilerRecorder.cs", "Packages/src/Runtime/UIParticleProfiler.cs"])
    particle_source = (ROOT / "Packages/src/Runtime/UIParticle.cs").read_text(encoding="utf-8-sig")
    public_api = particle_source.split("        public void MarkParticleDirty()", 1)[1].split("\n        }", 1)[0]
    api_file = OUT / "ParticleDirtyAPI.cs"
    api_file.write_text("namespace Coffee.UIExtensions { public partial class UIParticle { public void MarkParticleDirty()"
                        + public_api + "\n} } }", encoding="utf-8")
    check("updater_extended", ["Tools/ParticleCrashTests/UpdaterHarness.cs", "Packages/src/Runtime/UIParticleUpdater.cs", "Packages/src/Runtime/UIParticleProfiler.cs", api_file.relative_to(ROOT).as_posix()],
          "UNITY_EDITOR,UNITY_2019_3_OR_NEWER,FIXED,EXTENDED")
    for variant, prefix in (("before", "TempDiag/before_extended_audit_20260907/"), ("fixed", "")):
        if variant == "before" and not (ROOT / prefix).exists():
            print("SKIP historical utility baseline (not distributed)")
            continue
        check("utilities_" + variant, ["Tools/ParticleCrashTests/UtilityHarness.cs",
              prefix + "Packages/src/Runtime/Utilities/ParticleSystemExtensions.cs",
              prefix + "Packages/src/Runtime/Internal/Extensions/Vector3Extensions.cs"], expect_fail=variant == "before")
    check_renderer_optimizations()
    check_recording_controls()
    check_particle_quantity()
    compile_player()
