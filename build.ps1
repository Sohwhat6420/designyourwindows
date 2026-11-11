# build.ps1: compile bin\designyourwindows.exe with the C# compiler that ships inside Windows (.NET Framework 4.x).
# Nothing to install: no SDK, no NuGet, no Visual Studio.
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found: .NET Framework 4.x is part of Windows 10 and 11; turn it on in Windows Features' }

$root = $PSScriptRoot
$out = Join-Path $root 'bin\designyourwindows.exe'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$manifest = Join-Path $root 'src\app.manifest'
$source = Join-Path $root 'src\DesignYourWindows.cs'
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$out" "/win32manifest:$manifest" `
    /r:System.Drawing.dll /r:System.Web.Extensions.dll $source
if ($LASTEXITCODE) { exit $LASTEXITCODE }
'built {0} ({1:N0} KB)' -f $out, ((Get-Item $out).Length / 1KB)
