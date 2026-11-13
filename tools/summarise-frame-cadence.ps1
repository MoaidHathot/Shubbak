<# .SYNOPSIS
Summarize applied geometry on the foreign UI threads, clipped to measured input.
These are geometry-delivery intervals, not display presentation/photon timings.
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Root)
$ErrorActionPreference = 'Stop'
$meta = Get-Content (Join-Path $Root 'metadata.json') -Raw | ConvertFrom-Json
$period = 1000.0 / $meta.FrameRate
$rows = foreach ($result in (Get-Content (Join-Path $Root 'results.jsonl') | ConvertFrom-Json)) {
    if ($result.Label -notlike 'animation-*') { continue }
    $folder = Join-Path $Root $result.Label
    $edges = @(Import-Csv (Join-Path $folder 'events.csv') | Where-Object warmup -eq '0')
    $start = ($edges | ForEach-Object { [long]$_.sent_qpc } | Measure-Object -Minimum).Minimum
    $end = ($edges | ForEach-Object { [long]$_.received_qpc } | Measure-Object -Maximum).Maximum
    $files = @(Get-ChildItem $folder -Filter 'geometry-*.csv')
    if ($files.Count -ne 2) { throw "Missing geometry in $folder" }
    foreach ($file in $files) {
        $frames = @(Import-Csv $file.FullName | Where-Object { [long]$_.qpc -ge $start -and [long]$_.qpc -le $end })
        if ($frames.Count -lt 100) { throw "Insufficient applied geometry in $($file.FullName): $($frames.Count)" }
        $intervals = [double[]]::new($frames.Count - 1)
        $squared = 0.0
        for ($i = 1; $i -lt $frames.Count; $i++) {
            $delta = ([long]$frames[$i].qpc - [long]$frames[$i-1].qpc) * 1000.0 / $result.ClockFrequency
            $intervals[$i-1] = $delta
            $squared += [Math]::Pow($delta - $period, 2)
        }
        [Array]::Sort($intervals)
        function Percentile([double] $p) { $intervals[[Math]::Clamp([int][Math]::Ceiling($intervals.Length*$p)-1,0,$intervals.Length-1)] }
        [pscustomobject]@{
            Case=$result.Label;Window=$file.BaseName;Frames=$frames.Count;RequestedMs=$period
            MeanMs=($intervals | Measure-Object -Average).Average
            P50Ms=(Percentile .5);P99Ms=(Percentile .99);P999Ms=(Percentile .999);MaxMs=$intervals[-1]
            RmsErrorMs=[Math]::Sqrt($squared/$intervals.Length)
            OverOneAndHalf=@($intervals | Where-Object { $_ -gt $period*1.5 }).Count
            OverTwo=@($intervals | Where-Object { $_ -gt $period*2 }).Count
            UnderHalf=@($intervals | Where-Object { $_ -lt $period/2 }).Count
        }
    }
}
[IO.File]::WriteAllText((Join-Path $Root 'geometry-summary.json'), ($rows | ConvertTo-Json -Depth 5).Replace("`r", ''))
$rows | Format-Table Case,Window,Frames,MeanMs,P99Ms,P999Ms,RmsErrorMs,OverTwo,UnderHalf
