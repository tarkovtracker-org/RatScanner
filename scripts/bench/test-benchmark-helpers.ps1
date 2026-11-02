$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'benchmark-helpers.ps1')

function Sample($time, $ticks, $frequency = 3000000, $type = '3D') {
    return [pscustomobject]@{ RunningTime = $time; Timestamp = $ticks; Frequency = $frequency; EngineType = $type }
}
function Equal($expected, $actual) {
    if ([double]::IsNaN($actual) -or [double]::IsInfinity($actual) -or [Math]::Abs($expected - $actual) -gt 0.000001) { throw "Expected $expected; got $actual" }
}
# Match the live GPU provider: PerfTime fields are zero; Sys100NS is populated.
# Exercise the actual CIM projection, including PID filtering and clock preservation.
function Get-CimInstance {
    param($ClassName)
    if ($ClassName -ne 'Win32_PerfRawData_GPUPerformanceCounters_GPUEngine') { throw 'Unexpected CIM class' }
    [pscustomobject]@{ Name = 'pid_42_luid_0_engtype_3D'; RunningTime = 15000000; Timestamp_PerfTime = 0; Frequency_PerfTime = 0; Timestamp_Sys100NS = 134380624145982626; Frequency_Sys100NS = 10000000 }
    [pscustomobject]@{ Name = 'pid_99_luid_0_engtype_3D'; RunningTime = 999999999; Timestamp_PerfTime = 0; Frequency_PerfTime = 0; Timestamp_Sys100NS = 134380624145982626; Frequency_Sys100NS = 10000000 }
}
$projected = Get-GpuRunningTime -ProcessIds @(42)
Equal 1 $projected.Count
$projectedGpu = Get-GpuUtilization -Previous @{ 'pid_42_luid_0_engtype_3D' = (Sample 10000000 134380624125982626 10000000) } -Current $projected
Equal 25 $projectedGpu.ThreeD
Equal 134380624145982626 $projected['pid_42_luid_0_engtype_3D'].Timestamp
Equal 10000000 $projected['pid_42_luid_0_engtype_3D'].Frequency
# The GPU interval is 2 seconds at 3 MHz, unrelated to query/wall-clock duration.
$before = @{ a = (Sample 10000000 9000000); b = (Sample 0 9000000 3000000 'Copy'); exited = (Sample 0 9000000) }
$after = @{ a = (Sample 15000000 15000000); b = (Sample 2000000 15000000 3000000 'Copy'); new = (Sample 999999999 15000000) }
$gpu = Get-GpuUtilization -Previous $before -Current $after
Equal 25 $gpu.ThreeD
Equal 35 $gpu.AllEngines
# Reset counters, non-forward clocks, and changed/invalid frequencies contribute nothing.
foreach ($sample in @((Sample 0 15000000), (Sample 15000000 9000000), (Sample 15000000 8000000), (Sample 15000000 15000000 0), (Sample 15000000 15000000 4000000))) {
    $gpu = Get-GpuUtilization -Previous @{ a = $before.a } -Current @{ a = $sample }
    Equal 0 $gpu.AllEngines
}
$path = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N') + '.json')
try {
    Write-BenchmarkReport -Path $path -Json '{"value":1}'
    $rejected = $false
    try { Write-BenchmarkReport -Path $path -Json '{"value":2}' } catch [System.IO.IOException] { $rejected = $true }
    if (-not $rejected) { throw 'Existing report was overwritten' }
    if ([System.IO.File]::ReadAllText($path) -ne '{"value":1}') { throw 'Original report changed' }
} finally { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
# Drive the actual snapshot loader with a fake monotonic clock. CPU reads take 2s;
# the GPU queries take 10s then 30s. Neither query moves that snapshot's CPU time.
$script:cpuClock = [long]0
$script:gpuQueries = 0
$script:cpuReads = 0
$script:clockFrequency = [System.Diagnostics.Stopwatch]::Frequency
function Get-ProcessTreeIds { param($RootProcessId) return @(42) }
function Get-Process {
    param($Id)
    $script:cpuClock += 2 * $script:clockFrequency
    $script:cpuReads++
    return [pscustomobject]@{
        Id = $Id
        TotalProcessorTime = [TimeSpan]::FromMilliseconds(($script:cpuReads - 1) * 140)
        PrivateMemorySize64 = 123
        WorkingSet64 = 456
    }
}
function Get-GpuRunningTime {
    param($ProcessIds)
    $script:gpuQueries++
    $script:cpuClock += $(if ($script:gpuQueries -eq 1) { 10 } else { 30 }) * $script:clockFrequency
    return @{}
}
$previousCpu = Get-ProcessTreeSnapshot -RootProcessId 42 -Clock { $script:cpuClock }
$oldPreviousAt = $script:cpuClock
$script:cpuClock += 2 * $script:clockFrequency
$oldNow = $script:cpuClock
$currentCpu = Get-ProcessTreeSnapshot -RootProcessId 42 -Clock { $script:cpuClock }
$cpu = Get-CpuSample -Previous $previousCpu -Current $currentCpu
Equal (2 * $script:clockFrequency) $previousCpu.CpuTimestamp
Equal (16 * $script:clockFrequency) $currentCpu.CpuTimestamp
Equal 14000 $cpu.ElapsedMilliseconds
Equal 10 $cpu.MillisecondsPerSecond
# The old caller-side before/after interval was only 2s, incorrectly yielding 70ms/s.
Equal 2000 (($oldNow - $oldPreviousAt) / [double]$script:clockFrequency * 1000)
Equal 123 $currentCpu.PrivateBytes
Equal 456 $currentCpu.WorkingSetBytes
$invalidCpu = Get-CpuSample -Previous $currentCpu -Current $currentCpu
Equal 0 $invalidCpu.MillisecondsPerSecond
Write-Output 'Benchmark helper regressions passed'
