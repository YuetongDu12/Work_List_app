# Builds dist\WorkList-Setup.exe using the C# compiler that ships with Windows.
# Run:  powershell -ExecutionPolicy Bypass -File build.ps1
# Add -TestHooks only for sandbox testing; never ship that build.
param([switch]$TestHooks)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
New-Item -ItemType Directory -Force build, dist | Out-Null
$define = if ($TestHooks) { "/define:TESTHOOKS" } else { "/define:RELEASE" }

if (-not (Test-Path app.ico)) { & powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1 }

# 1. The app
& $csc /nologo $define /target:winexe /win32icon:app.ico /out:build\WorkList.exe `
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll WorkList.cs
if ($LASTEXITCODE -ne 0) { throw "WorkList.exe failed to build" }

# 2. The installer, with the app embedded inside it
& $csc /nologo $define /target:winexe /win32icon:app.ico /out:dist\WorkList-Setup.exe `
  /resource:build\WorkList.exe,WorkList.exe `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll Setup.cs
if ($LASTEXITCODE -ne 0) { throw "WorkList-Setup.exe failed to build" }

# The bare app is now inside the installer; remove it so the installer is the only thing to run.
Remove-Item -Recurse -Force build

Get-Item dist\WorkList-Setup.exe | Select-Object Name, @{n="KB";e={[int]($_.Length/1KB)}}
