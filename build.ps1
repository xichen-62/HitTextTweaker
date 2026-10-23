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
    (Join-Path $managed 'netstandard.dll'),
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

Add-Type -TypeDefinition $src -ReferencedAssemblies $refs -OutputAssembly $dll -OutputType Library
Write-Host ("compiled: " + $dll + " size=" + (Get-Item $dll).Length)
