param(
    [string]$GameDir = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'

# 找游戏目录：参数 > 环境变量 ADOFAI_DIR > 常见 Steam 路径
if (-not $GameDir) { $GameDir = $env:ADOFAI_DIR }
if (-not $GameDir) {
    $candidates = @(
        'D:\steam\steamapps\common\A Dance of Fire and Ice',
        'C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice',
        'C:\Program Files\Steam\steamapps\common\A Dance of Fire and Ice',
        'D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice'
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'A Dance of Fire and Ice_Data\Managed')) { $GameDir = $c; break }
    }
}
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'A Dance of Fire and Ice_Data\Managed'))) {
    throw "找不到游戏目录，请用 -GameDir 指定（例如 .\build.ps1 -GameDir 'D:\steam\steamapps\common\A Dance of Fire and Ice'）"
}

$managed = Join-Path $GameDir 'A Dance of Fire and Ice_Data\Managed'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'out' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$src = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'src\Main.cs')
$dll = Join-Path $OutDir 'HitTextTweaker.dll'
if (Test-Path $dll) { Remove-Item $dll -Force }

$refs = @(
    (Join-Path $managed 'mscorlib.dll'),
    (Join-Path $managed 'netstandard.dll'),
    (Join-Path $managed 'System.dll'),
    (Join-Path $managed 'System.Core.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'Unity.TextMeshPro.dll'),
    (Join-Path $managed 'UnityEngine.UI.dll'),
    (Join-Path $managed 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $managed 'UnityEngine.TextCoreFontEngineModule.dll'),
    (Join-Path $managed 'UnityModManager\UnityModManager.dll'),
    (Join-Path $managed 'UnityModManager\0Harmony.dll')
)
foreach ($r in $refs) {
    if (-not (Test-Path $r)) { throw "缺少程序集: $r" }
}

# 编译用 .NET SDK 自带的 Roslyn csc。
# 不用 Add-Type：Unity 那一套程序集会让它先报 CS0012（缺 mscorlib），补上 mscorlib 又报 CS8021
# （No value for RuntimeMetadataVersion），2026-11-08 在 r150 上实测两条路都走不通。
$csc = $null
$sdkRoot = Join-Path $env:ProgramFiles 'dotnet\sdk'
if (Test-Path $sdkRoot) {
    $csc = Get-ChildItem $sdkRoot -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+' } |
        Sort-Object { [version]($_.Name -replace '^([0-9.]+).*$','$1') } -Descending |
        ForEach-Object { Join-Path $_.FullName 'Roslyn\bincore\csc.dll' } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}

if ($csc) {
    $cscArgs = @('-nologo', '-target:library', '-nostdlib+', '-langversion:latest', '-nowarn:1701,1702', "-out:$dll")
    foreach ($r in $refs) { $cscArgs += "-r:$r" }
    $cscArgs += (Join-Path $PSScriptRoot 'src\Main.cs')
    & dotnet exec $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc 编译失败（exit $LASTEXITCODE）" }
}
else {
    throw '找不到 .NET SDK 里的 Roslyn csc，请先安装 .NET SDK（dotnet --list-sdks 有输出即可）'
}
Write-Host ("compiled: " + $dll + " size=" + (Get-Item $dll).Length)
