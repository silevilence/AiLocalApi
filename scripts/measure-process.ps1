param([int]$ProcessId, [string]$State, [string]$Output = 'artifacts/performance.jsonl')
$ErrorActionPreference = 'Stop'
$process = Get-Process -Id $ProcessId
$start = $process.TotalProcessorTime.TotalSeconds
$clock = [Diagnostics.Stopwatch]::StartNew()
$peak = $process.WorkingSet64
for ($sample = 0; $sample -lt 20; $sample++) { Start-Sleep -Milliseconds 250; $process.Refresh(); $peak = [Math]::Max($peak, $process.WorkingSet64) }
$result = [ordered]@{ State=$State; ProcessId=$ProcessId; Seconds=$clock.Elapsed.TotalSeconds; CpuOneCorePercent=100*($process.TotalProcessorTime.TotalSeconds-$start)/$clock.Elapsed.TotalSeconds; WorkingSetMiB=$process.WorkingSet64/1MB; PeakSampledMiB=$peak/1MB; PrivateMiB=$process.PrivateMemorySize64/1MB; Handles=$process.HandleCount }
$result | ConvertTo-Json -Compress | Add-Content -LiteralPath $Output
$result | ConvertTo-Json
