param([string]$Version)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectXml = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'BatteryDisplay.csproj') -Raw -Encoding UTF8)
if (!$Version) { $Version = $projectXml.Project.PropertyGroup.Version }
if ($projectXml.Project.PropertyGroup.Version -ne $Version) { throw '打包版本与项目版本不一致。' }
$stage = Join-Path $projectRoot ('.dotnet\package-' + [guid]::NewGuid().ToString('N'))
$releaseDirectory = Join-Path $projectRoot 'release'
New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null
& (Join-Path $projectRoot 'build.ps1') -PublishDirectory $stage
$required = @('BatteryDisplay.exe', 'BatteryDisplay.dll', 'BatteryDisplay.deps.json', 'BatteryDisplay.runtimeconfig.json',
    'resources.pri', 'App.xbf', 'periring.ico', 'periring-64.png', 'README.md', 'SUPPORTED-DEVICES.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE',
    'Assets\periring.png', 'docs\images\devices.png', 'docs\images\device-order.png', 'docs\images\settings.png',
    'docs\images\battery-alerts.png', 'docs\images\device-detail.png', 'docs\images\updates.png', 'docs\images\diagnostics.png')
foreach ($name in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $stage $name))) { throw "发布文件缺失：$name" }
}
$stagePrefix = $stage.TrimEnd('\') + '\'
$unexpected = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object {
    $rel = $_.FullName.Substring($stagePrefix.Length)
    $rel -match '(^|[\\/])data([\\/]|$)' -or
    ($_.Extension -notin @('.dll', '.exe', '.json', '.pri', '.xbf', '.ico', '.png', '.md', '.pdb') -and $_.Name -ne 'LICENSE') -or
        $_.Name -match '^(diagnostics|settings|battery-cache|battery-history|self-test|ui-self-test|update-check)\.json$|-error\.txt$|^ui-startup\.log$|^winui-|^combined-|^icon-|^separated-'
}
if ($unexpected) { throw ('发布目录包含非发布文件：' + ($unexpected.Name -join ', ')) }
$zip = Join-Path $releaseDirectory "PeriRing-v$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$hashFile = Join-Path $releaseDirectory "PeriRing-v$Version-SHA256.txt"
[IO.File]::WriteAllText($hashFile, "$hash  $([IO.Path]::GetFileName($zip))`n", [Text.UTF8Encoding]::new($false))
Write-Host "发布包：$zip"
Write-Host "SHA256：$hash"
