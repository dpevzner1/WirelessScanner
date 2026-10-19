@echo off
rem Builds a complete WirelessScanner release (self-contained app + installer) via tools\build_release.py.
rem Output: artifacts\release (build products) and "GitHub Export\EXE INSTALLER" (GitHub-ready installer folder).
rem It never writes to .\publish (local run folder) and never commits or pushes. See docs\TECHNICAL_REFERENCE.md.
python "%~dp0tools\build_release.py" %*
if %ERRORLEVEL% neq 0 (
    echo Release build FAILED.
    exit /b %ERRORLEVEL%
)
echo Release build succeeded.
