[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDir,
    [Parameter(Mandatory)][string]$OwmlDir,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'),
    [string]$CscPath
)

$ErrorActionPreference = 'Stop'
$managed = Join-Path (Resolve-Path -LiteralPath $GameDir).Path 'OuterWilds_Data\Managed'
$owml = (Resolve-Path -LiteralPath $OwmlDir).Path
$dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1

if (!$CscPath -and $dotnet) {
    $sdks = @(& $dotnet.Source --list-sdks | Where-Object { $_ -match '^\S+ \[.+\]$' })
    if ($sdks.Count -gt 0 -and $sdks[-1] -match '^(\S+) \[(.+)\]$') {
        $candidate = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $CscPath = $candidate }
    }
}
if (!$CscPath -and ${env:ProgramFiles(x86)}) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $CscPath = & $vswhere -latest -products '*' -find 'MSBuild\Current\Bin\Roslyn\csc.exe' | Select-Object -First 1
    }
}
if (!$CscPath) { throw 'Install a .NET SDK or Visual Studio Build Tools, or pass -CscPath to Roslyn.' }
$compiler = (Resolve-Path -LiteralPath $CscPath).Path
$useDotnet = [IO.Path]::GetExtension($compiler) -eq '.dll'
if ($useDotnet -and !$dotnet) { throw 'dotnet is required to run csc.dll.' }

$references = foreach ($name in @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'netstandard.dll', 'Assembly-CSharp.dll',
    'UnityEngine.CoreModule.dll', 'UnityEngine.PhysicsModule.dll', 'UnityEngine.InputLegacyModule.dll',
    'UnityEngine.IMGUIModule.dll', 'Unity.InputSystem.dll', 'Newtonsoft.Json.dll'
)) {
    (Resolve-Path -LiteralPath (Join-Path $managed $name)).Path
}
foreach ($name in 'OWML.ModHelper.dll', 'OWML.Common.dll', '0Harmony.dll') {
    $references += (Resolve-Path -LiteralPath (Join-Path $owml $name)).Path
}

$manifestPath = Join-Path $PSScriptRoot 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$output = New-Item -ItemType Directory -Path $OutputDirectory -Force
$archive = Join-Path $output.FullName ('SunStation.Control-' + $manifest.version + '.zip')
$pending = "$archive.$([guid]::NewGuid().ToString('N')).tmp"
$staging = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('sun-station-' + [guid]::NewGuid().ToString('N')))
$dll = Join-Path $staging.FullName 'SunStation.Control.dll'

try {
    $compilerArgs = @('/nologo', '/noconfig', '/target:library', '/nostdlib+', '/optimize+', '/deterministic+',
        "/pathmap:$PSScriptRoot=.", "/out:$dll")
    $compilerArgs += $references | ForEach-Object { "/reference:$_" }
    $compilerArgs += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File |
        Sort-Object Name | ForEach-Object FullName
    if ($useDotnet) { & $dotnet.Source $compiler @compilerArgs } else { & $compiler @compilerArgs }
    if ($LASTEXITCODE -ne 0) { throw "C# compiler exited with code $LASTEXITCODE." }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $files = [ordered]@{
        'manifest.json' = $manifestPath
        'SunStation.Control.dll' = $dll
        'data/player-route.jsonl' = Join-Path $PSScriptRoot 'data\player-route.jsonl'
        'data/snow-summit.json' = Join-Path $PSScriptRoot 'data\snow-summit.json'
    }
    $zip = [IO.Compression.ZipFile]::Open($pending, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $files.GetEnumerator()) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $entry.Value, "SunStation.Control/$($entry.Key)", [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $zip.Dispose() }
    Move-Item -LiteralPath $pending -Destination $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding Ascii
    Get-Item -LiteralPath $archive
}
finally {
    if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending }
    if (Test-Path -LiteralPath $dll) { Remove-Item -LiteralPath $dll }
    Remove-Item -LiteralPath $staging.FullName
}
