# Builds bin\OrclWAMP.exe - a single portable exe for .NET Framework 4.8 (built into Windows 10/11).
# Needs the Roslyn C# compiler, which comes with Visual Studio or the VS Build Tools (any edition).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

function Find-Csc {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
        if ($found) { return $found }
    }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        $hit = Get-ChildItem (Join-Path $base 'Microsoft Visual Studio') -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\Roslyn\\csc\.exe$' } | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw 'Roslyn csc.exe not found. Install Visual Studio or "Build Tools for Visual Studio" (C# compiler).'
}

$csc = Find-Csc
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll' |
    ForEach-Object { '/reference:' + (Join-Path $fw $_) }
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

Write-Host "Compiler: $csc"
& $csc /nologo /noconfig /target:winexe /platform:anycpu /optimize+ /debug- /langversion:latest /warnaserror- /nowarn:1701,1702,0649 `
    "/out:$(Join-Path $out 'OrclWAMP.exe')" `
    "/win32icon:$(Join-Path $root 'assets\OrclWAMP.ico')" `
    "/win32manifest:$(Join-Path $root 'src\app.manifest')" `
    "/resource:$(Join-Path $root 'assets\logo.png'),OrclWAMP.logo.png" `
    "/reference:$(Join-Path $fw 'mscorlib.dll')" /nostdlib+ `
    @refs @sources
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Write-Host "Built $(Join-Path $out 'OrclWAMP.exe')" -ForegroundColor Green
