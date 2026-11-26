param([string]$PublishDirectory = (Join-Path $PSScriptRoot 'dist-winui'))
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'
$buildTemp = Join-Path $PSScriptRoot '.dotnet\build-temp'
New-Item -ItemType Directory -Force -Path $buildTemp | Out-Null
$env:TEMP = $buildTemp
$env:TMP = $buildTemp
Push-Location $PSScriptRoot
try {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $installation = & $vswhere -latest -property installationPath
    $msbuild = Join-Path $installation 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    if (!(Test-Path -LiteralPath $msbuild)) { throw '需要 Visual Studio 的 Windows 应用开发组件以编译 WinUI 3。' }
    & $msbuild BatteryDisplay.csproj /restore /t:Publish /p:Configuration=Release /p:Platform=x64 /p:SelfContained=false /p:NuGetAudit=false /p:RestoreIgnoreFailedSources=true "/p:PublishDir=$([IO.Path]::GetFullPath($PublishDirectory))\" /nologo /verbosity:minimal
    if ($LASTEXITCODE -ne 0) { throw '编译失败' }
    Write-Host "已生成：$(Join-Path $PublishDirectory 'BatteryDisplay.exe')"
} finally { Pop-Location }
