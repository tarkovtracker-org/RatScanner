function Get-ProcessTreeSnapshot {
    param(
        [int]$RootProcessId,
        [scriptblock]$Clock = { [System.Diagnostics.Stopwatch]::GetTimestamp() }
    )

    $ids = Get-ProcessTreeIds -RootProcessId $RootProcessId
    $processes = foreach ($id in $ids) {
        Get-Process -Id $id -ErrorAction SilentlyContinue
    }

    $cpuMilliseconds = 0.0
    $privateBytes = [long]0
    $workingSetBytes = [long]0
    $processCount = 0

    foreach ($process in $processes) {
        if ($null -eq $process) { continue }
        $processCount++
        # A process in the tree can exit between enumeration and property access.
        try { $cpuMilliseconds += [double]$process.TotalProcessorTime.TotalMilliseconds } catch { Write-Verbose "Process $($process.Id) exited before CPU time could be read." }
        try { $privateBytes += [long]$process.PrivateMemorySize64 } catch { Write-Verbose "Process $($process.Id) exited before private memory could be read." }
        try { $workingSetBytes += [long]$process.WorkingSet64 } catch { Write-Verbose "Process $($process.Id) exited before working set could be read." }
    }

    # Timestamp the CPU reads before a potentially slow GPU CIM query.
    $cpuTimestamp = & $Clock
    return [pscustomobject]@{
        CpuTimestamp = $cpuTimestamp
        ProcessCount = $processCount
        GpuRunningTime = Get-GpuRunningTime -ProcessIds $ids
        CpuMilliseconds = $cpuMilliseconds
        PrivateBytes = $privateBytes
        WorkingSetBytes = $workingSetBytes
    }
}

function Get-CpuSample {
    param($Previous, $Current)

    $elapsedMilliseconds = ($Current.CpuTimestamp - $Previous.CpuTimestamp) / [double][System.Diagnostics.Stopwatch]::Frequency * 1000
    $rate = if ($elapsedMilliseconds -gt 0) {
        ($Current.CpuMilliseconds - $Previous.CpuMilliseconds) / $elapsedMilliseconds * 1000
    } else { 0 }
    return [pscustomobject]@{ ElapsedMilliseconds = $elapsedMilliseconds; MillisecondsPerSecond = $rate }
}

function Get-GpuRunningTime {
    param([int[]]$ProcessIds)

    # Raw GPU engine counters expose cumulative RunningTime in 100 ns units per
    # pid/engine instance. Deltas give exact utilization and, unlike Get-Counter
    # paths, the CIM class name is not localized.
    $pidSet = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($id in $ProcessIds) { [void]$pidSet.Add($id) }
    # Keyed by full instance name so deltas are taken per engine instance; summing by
    # type first would let one instance exiting cancel out another's usage.
    $byInstance = @{}
    foreach ($engine in Get-CimInstance Win32_PerfRawData_GPUPerformanceCounters_GPUEngine) {
        if ($engine.Name -notmatch '^pid_(\d+)_.*_engtype_(.+)$') { continue }
        if (-not $pidSet.Contains([int]$Matches[1])) { continue }
        $byInstance[$engine.Name] = [pscustomobject]@{
            EngineType = $Matches[2]
            RunningTime = [long]$engine.RunningTime
            Timestamp = [long]$engine.Timestamp_Sys100NS
            Frequency = [long]$engine.Frequency_Sys100NS
        }
    }
    return $byInstance
}

function Get-GpuUtilization {
    param([hashtable]$Previous, [hashtable]$Current)

    $threeD = 0.0
    $allEngines = 0.0
    foreach ($name in $Current.Keys) {
        $before = $Previous[$name]
        if ($null -eq $before) { continue }
        $after = $Current[$name]
        $ticks = $after.Timestamp - $before.Timestamp
        if ($ticks -le 0 -or $after.Frequency -le 0 -or $after.Frequency -ne $before.Frequency) { continue }
        # RunningTime and Timestamp_Sys100NS use the provider's 100 ns counter clock.
        # Use its declared Frequency_Sys100NS to express the interval in seconds.
        $elapsedSeconds = $ticks / [double]$after.Frequency
        $percent = [Math]::Max(0, ($after.RunningTime - $before.RunningTime) / 10000000.0 / $elapsedSeconds * 100)
        $allEngines += $percent
        if ($after.EngineType -eq '3D') { $threeD += $percent }
    }
    return [pscustomobject]@{ ThreeD = $threeD; AllEngines = $allEngines }
}

function Write-BenchmarkReport {
    param([string]$Path, [string]$Json)

    # Even an unexpected filename collision must fail without truncating prior data.
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
    try {
        $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
        try { $writer.Write($Json) } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
}
