@echo off
setlocal EnableDelayedExpansion

if "%~1"=="" (
  echo Usage: Tools\ProfilerAI\extract_raw_frame.cmd EXPORT_DIR FRAME_INDEX
  echo Example: Tools\ProfilerAI\extract_raw_frame.cmd ProfilerAIExports\20260528_150634 563
  exit /b 2
)

if "%~2"=="" (
  echo Usage: Tools\ProfilerAI\extract_raw_frame.cmd EXPORT_DIR FRAME_INDEX
  echo Example: Tools\ProfilerAI\extract_raw_frame.cmd ProfilerAIExports\20260528_150634 563
  exit /b 2
)

where python >nul 2>nul
if %ERRORLEVEL%==0 (
  python "%~dp0extract_raw_frame.py" --export-dir "%~1" --frame "%~2"
  exit /b !ERRORLEVEL!
)

where py >nul 2>nul
if %ERRORLEVEL%==0 (
  py -3 "%~dp0extract_raw_frame.py" --export-dir "%~1" --frame "%~2"
  exit /b !ERRORLEVEL!
)

echo Python was not found on PATH.
exit /b 9009
