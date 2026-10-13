@echo off
setlocal
pushd "%~dp0"
if not exist "SolarisLauncher.exe" (
  echo Place this preview command next to the experimental SolarisLauncher.exe.
  popd
  exit /b 1
)
rem Existing portable mode skips permanent installation and EXE auto-updates.
"SolarisLauncher.exe" --dev-test
set "solaris_preview_exit=%errorlevel%"
popd
exit /b %solaris_preview_exit%
