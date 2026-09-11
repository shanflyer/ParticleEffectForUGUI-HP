#!/usr/bin/env python3
import argparse
import os
import re
import shutil
import subprocess
import sys
from datetime import datetime
from pathlib import Path


def project_root() -> Path:
    cursor = Path(__file__).resolve().parent
    for candidate in [cursor] + list(cursor.parents):
        if (candidate / "ProjectSettings" / "ProjectVersion.txt").exists():
            return candidate
    return Path.cwd()


def resolve_project_path(path_text: str) -> Path:
    path = Path(path_text)
    if path.is_absolute():
        return path
    return project_root() / path


def read_unity_version() -> str:
    version_file = project_root() / "ProjectSettings" / "ProjectVersion.txt"
    if not version_file.exists():
        return ""
    text = version_file.read_text(encoding="utf-8", errors="ignore")
    match = re.search(r"m_EditorVersion:\s*(\S+)", text)
    return match.group(1) if match else ""


def find_unity_exe(explicit: str) -> Path:
    candidates = []
    if explicit:
        candidates.append(Path(explicit))

    env_unity = os.environ.get("UNITY_EXE")
    if env_unity:
        candidates.append(Path(env_unity))

    version = read_unity_version()
    if version:
        candidates.extend(
            [
                Path("C:/Program Files/Unity/Hub/Editor") / (version + "-x86_64") / "Editor" / "Unity.exe",
                Path("C:/Program Files/Unity/Hub/Editor") / version / "Editor" / "Unity.exe",
            ]
        )

    for candidate in candidates:
        if candidate.exists():
            return candidate

    raise FileNotFoundError(
        "Unity.exe not found. Pass --unity or set UNITY_EXE. Project Unity version: " + (version or "unknown")
    )


def default_output_dir(profile: Path, first_frame: int, last_frame: int) -> Path:
    if profile.name.lower() == "profile.data":
        base = profile.parent / "raw_frames"
    else:
        base = project_root() / "ProfilerAIExports" / "raw_frames"

    name = f"frame_{first_frame}" if first_frame == last_frame else f"frames_{first_frame}_{last_frame}"
    return base / name


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Export full raw TSV data for selected frames from a Unity Profiler .data capture."
    )
    parser.add_argument("--profile", required=True, help="Profiler .data path. Project-relative paths are allowed.")
    parser.add_argument("--frame", type=int, help="Single frame index to export.")
    parser.add_argument("--first-frame", type=int, help="First frame index to export.")
    parser.add_argument("--last-frame", type=int, help="Last frame index to export. Defaults to --frame or --first-frame.")
    parser.add_argument("--out", help="Output folder. Defaults to <export>/raw_frames/frame_N.")
    parser.add_argument("--unity", help="Unity.exe path. Defaults to UNITY_EXE or ProjectVersion-based Hub path.")
    args = parser.parse_args()

    if args.frame is None and args.first_frame is None:
        parser.error("Pass --frame or --first-frame.")

    first_frame = args.frame if args.frame is not None else args.first_frame
    last_frame = args.last_frame if args.last_frame is not None else first_frame
    if first_frame is None or last_frame is None or first_frame < 0 or last_frame < first_frame:
        parser.error("Invalid frame range.")

    profile = resolve_project_path(args.profile).resolve()
    if not profile.exists():
        raise FileNotFoundError(profile)

    out_dir = resolve_project_path(args.out).resolve() if args.out else default_output_dir(profile, first_frame, last_frame)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    staging_dir = out_dir.with_name(out_dir.name + f".partial_{stamp}")
    staging_dir.mkdir(parents=True, exist_ok=False)

    unity = find_unity_exe(args.unity)
    cmd = [
        str(unity),
        "-batchmode",
        "-quit",
        "-projectPath",
        str(project_root()),
        "-executeMethod",
        "ProfilerDataExporter.Export",
        "-profilerInput",
        str(profile),
        "-profilerOutput",
        str(staging_dir),
        "-profilerRawSamples",
        "true",
        "-profilerFirstFrame",
        str(first_frame),
        "-profilerLastFrame",
        str(last_frame),
    ]

    print("Running Unity raw frame export...")
    print("Profile:", profile)
    print("Output:", out_dir)
    print("Staging:", staging_dir)
    result = subprocess.run(cmd, cwd=str(project_root()))
    if result.returncode != 0:
        print("Unity export failed with exit code", result.returncode, file=sys.stderr)
        print("Existing output was left untouched:", out_dir, file=sys.stderr)
        print("Partial output remains for inspection:", staging_dir, file=sys.stderr)
        return result.returncode

    backup_dir = None
    if out_dir.exists():
        backup_dir = out_dir.with_name(out_dir.name + f".previous_{stamp}")
        out_dir.rename(backup_dir)

    try:
        staging_dir.rename(out_dir)
    except Exception:
        if backup_dir is not None and backup_dir.exists() and not out_dir.exists():
            backup_dir.rename(out_dir)
        raise

    if backup_dir is not None and backup_dir.exists():
        shutil.rmtree(backup_dir)

    print("Raw frame export finished:", out_dir)
    print("Read ai_profiler_samples.tsv and ai_profiler_threads.tsv in that folder.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
