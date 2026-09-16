"""Compile project sources and test updater logic without starting Unity/native rendering."""
import argparse
import json
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", type=Path, default=Path("D:/Unity 2022.3.49f1/Editor"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    output = root / "TempDiag/crash_fix_compile"
    output.mkdir(parents=True, exist_ok=True)
    runtime = args.editor / "Data/NetCoreRuntime"
    dotnet = runtime / "dotnet.exe"
    csc = args.editor / "Data/DotNetSdkRoslyn/csc.dll"
    framework = runtime / "shared/Microsoft.NETCore.App/6.0.21"
    bee = root / "Library/Bee/artifacts/1900b0aEDbg.dag"

    def run(command, log_name, expect_failure=False):
        result = subprocess.run([str(x) for x in command], cwd=root,
                                capture_output=True, text=True, errors="replace")
        text = result.stdout + result.stderr
        (output / log_name).write_text(text, encoding="utf-8")
        print(text.strip())
        if (result.returncode != 0) != expect_failure:
            raise RuntimeError(f"Unexpected exit code {result.returncode}: {log_name}")
        return text

    # Reuse Unity's existing references/defines, but write only diagnostic outputs.
    # csc compiles metadata; it never loads or executes the Unity engine.
    compiled = []
    for name in ("Unity.RenderPipelines.Universal.Runtime", "Coffee.UIParticle", "Assembly-CSharp", "Coffee.UIParticle.Editor", "Assembly-CSharp-Editor"):
        source_rsp = bee / (name + ".rsp")
        lines = source_rsp.read_text(encoding="utf-8-sig").splitlines()
        lines = [line for line in lines if not line.lstrip().startswith(
            ("-out:", "-refout:", "-analyzer:", "-additionalfile:"))]
        if name == "Coffee.UIParticle":
            known = {line.strip('"').replace('\\', '/') for line in lines}
            for src in (root / "Packages/src/Runtime").rglob("*.cs"):
                relative = src.relative_to(root).as_posix()
                if relative not in known:
                    lines.append('"' + relative + '"')
        for dependency in compiled:
            lines = [f'-r:"{output / (dependency + ".dll")}"'
                     if line.startswith("-r:") and any(line.replace('\\', '/').endswith('/' + dependency + suffix)
                                                       for suffix in ('.ref.dll"', '.dll"')) else line
                     for line in lines]
        lines.append(f'-out:"{output / (name + ".dll")}"')
        response = output / (name + ".rsp")
        response.write_text("\n".join(lines), encoding="utf-8")
        run([dotnet, csc, "@" + str(response)], name + "_compile.txt")
        print(f"PASS offline compilation: {name}")
        compiled.append(name)

    for variant, updater in (
        ("before", "TempDiag/before_crash_fix_20260907/Packages/src/Runtime/UIParticleUpdater.cs"),
        ("fixed", "Packages/src/Runtime/UIParticleUpdater.cs"),
    ):
        if variant == "before" and not (root / updater).exists():
            print("SKIP historical baseline (not distributed): " + updater)
            continue
        assembly = output / ("updater_" + variant + ".dll")
        defines = "UNITY_EDITOR,UNITY_2019_3_OR_NEWER" + (",FIXED" if variant == "fixed" else "")
        lines = ["-nologo", "-target:exe", "-nostdlib+", "-define:" + defines,
                 f'-out:"{assembly}"', '"Tools/ParticleCrashTests/UpdaterHarness.cs"',
                 f'"{updater}"', '"Packages/src/Runtime/UIParticleProfiler.cs"']
        lines.extend(f'-r:"{framework / (name + ".dll")}"' for name in
                     ("System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Console"))
        response = assembly.with_suffix(".rsp")
        response.write_text("\n".join(lines), encoding="utf-8")
        run([dotnet, csc, "@" + str(response)], f"updater_{variant}_compile.txt")
        assembly.with_suffix(".runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
            "tfm": "net6.0", "framework": {"name": "Microsoft.NETCore.App", "version": "6.0.21"}
        }}), encoding="utf-8")
        result = run([dotnet, assembly], f"updater_{variant}_results.txt", variant == "before")
        expected = "RESULT 2 passed, 7 failed" if variant == "before" else "RESULT 11 passed, 0 failed"
        if expected not in result:
            raise RuntimeError("Regression counts changed; inspect the detailed results")
    print("PASS: project compilation and managed regression checks; Unity was not started.")


if __name__ == "__main__":
    main()
