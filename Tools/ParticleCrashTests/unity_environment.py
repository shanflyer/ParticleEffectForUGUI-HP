"""Locate Unity's bundled compiler/runtime without starting the Editor."""
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def default_editor():
    override = os.environ.get("UNITY_EDITOR_PATH")
    if override:
        value = Path(override)
        return value.parent if value.name.lower() == "unity.exe" else value
    version = (ROOT / "ProjectSettings/ProjectVersion.txt").read_text().splitlines()[0].split(":", 1)[1].strip()
    candidates = [Path("D:/Unity") / version / "Editor",
                  Path("C:/Program Files/Unity/Hub/Editor") / version / "Editor"]
    return next((p for p in candidates if (p / "Unity.exe").exists()), candidates[0])


def framework_path(editor):
    base = editor / "Data/NetCoreRuntime/shared/Microsoft.NETCore.App"
    versions = sorted(base.iterdir(), key=lambda p: tuple(int(x) for x in p.name.split(".")))
    return versions[-1]


def runtime_config(framework):
    return {"runtimeOptions": {"tfm": "net" + framework.name.split(".")[0] + ".0",
            "framework": {"name": "Microsoft.NETCore.App", "version": framework.name}}}
