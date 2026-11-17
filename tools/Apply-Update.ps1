param([Parameter(Mandatory=$true)][string]$PlanPath)
$ErrorActionPreference = 'Stop'
$plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PlanPath))
$target = [IO.Path]::GetFullPath($plan.Target).TrimEnd('\')
$stage = [IO.Path]::GetFullPath($plan.Stage)
$backup = [IO.Path]::GetFullPath($plan.Backup)
if ($stage -ne (Join-Path $workspace 'stage') -or $backup -ne (Join-Path $workspace 'backup') -or $plan.Health -ne (Join-Path $workspace 'healthy')) { throw 'Invalid update workspace' }
if ((Split-Path -Parent $workspace).TrimEnd('\') -ne [IO.Path]::GetTempPath().TrimEnd('\') -or (Split-Path -Leaf $workspace) -notmatch '^PeriRing-update-[a-f0-9]{32}$') { throw 'Invalid temporary directory' }
$lease = [IO.File]::Open((Join-Path $workspace 'active.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$completed = $false
$changed = @()
$newProcess = $null
function TargetFile([string]$root, [string]$name) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $name))
    if (!$path.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $name -match '(^|[\\/])data([\\/]|$)|:') { throw 'Invalid update path' }
    # Reject directory junctions and symbolic links in the installed path.
    $parent = Split-Path -Parent $path
    while ($parent.Length -ge $root.Length) {
        if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Update target is a link' }
        $parent = Split-Path -Parent $parent
    }
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Update file is a link' }
    return $path
}
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $target 'data') | Out-Null
    $parentProcess = Get-Process -Id $plan.ParentId -ErrorAction SilentlyContinue
    if ($parentProcess -and !$parentProcess.WaitForExit(30000)) { throw 'App did not exit' }
    foreach ($name in $plan.Files) {
        $destination = TargetFile $target $name
        $source = TargetFile $stage $name
        $saved = TargetFile $backup $name
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $saved) | Out-Null
        $existed = Test-Path -LiteralPath $destination
        if ($existed) { Copy-Item -LiteralPath $destination -Destination $saved -Force }
        $changed += [pscustomobject]@{ Path=$destination; Saved=$saved; Existed=$existed }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
    $newProcess = Start-Process -FilePath (Join-Path $target 'BatteryDisplay.exe') -ArgumentList @('--update-health', ('"' + $plan.Health + '"')) -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ([DateTime]::UtcNow -lt $deadline -and !(Test-Path -LiteralPath $plan.Health) -and !$newProcess.HasExited) { Start-Sleep -Milliseconds 200 }
    if (!(Test-Path -LiteralPath $plan.Health)) { throw 'Updated app failed its startup health check' }
    @{ Success=$true; Time=[DateTimeOffset]::Now } | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $target 'data\last-update.json')
    $completed = $true
} catch {
    $failure = $_.Exception.Message
    if ($newProcess -and !$newProcess.HasExited) { Stop-Process -Id $newProcess.Id; $newProcess.WaitForExit() }
    try {
        foreach ($file in $changed) {
            if ($file.Existed) { Copy-Item -LiteralPath $file.Saved -Destination $file.Path -Force }
            elseif (Test-Path -LiteralPath $file.Path) { Remove-Item -LiteralPath $file.Path -Force }
        }
        @{ Success=$false; Time=[DateTimeOffset]::Now; Error=$failure } | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $target 'data\last-update.json')
        if ($changed.Count -gt 0) { Start-Process -FilePath (Join-Path $target 'BatteryDisplay.exe') -WindowStyle Hidden }
        $completed = $true
    } catch { $failure += '; rollback: ' + $_.Exception.Message }
    $failure | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $workspace 'update-error.txt')
    exit 1
} finally {
    if ($completed) {
        try {
            # Both targets have been resolved and verified inside our unique temporary directory.
            if (Test-Path -LiteralPath $stage) {
                $pending = New-Object 'System.Collections.Generic.Stack[string]'
                $pending.Push($stage)
                while ($pending.Count -gt 0) {
                    $item = $pending.Pop()
                    $attributes = [IO.File]::GetAttributes($item)
                    if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to clean linked staging files' }
                    if ($attributes -band [IO.FileAttributes]::Directory) {
                        foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($item)) { $pending.Push($child) }
                    }
                }
                Remove-Item -LiteralPath $stage -Recurse -Force
            }
            $download = Join-Path $workspace 'update.zip'
            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
            [IO.File]::WriteAllText((Join-Path $workspace 'complete'), [DateTimeOffset]::UtcNow.ToString('O'))
        } catch { $_.Exception.Message | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $workspace 'cleanup-error.txt') }
    }
    $lease.Dispose()
}
