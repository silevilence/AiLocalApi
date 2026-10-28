param([ValidateSet('huggingface.co','hf-mirror.com')][string]$DownloadHost = 'hf-mirror.com', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixtures = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts/fixtures' }
New-Item -ItemType Directory -Path $fixtures -Force | Out-Null
$manifest = Get-Content (Join-Path $PSScriptRoot 'multilingual-fixtures.json') -Raw | ConvertFrom-Json
foreach ($sample in $manifest) {
    $target = Join-Path $fixtures $sample.file
    if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -eq $sample.sha256) { continue }
    # Each pinned sample is the first regular WAV entry. Read at most 2 MiB of the archive.
    $client = [Net.Http.HttpClient]::new()
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(60))
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $sample.source.Replace('huggingface.co',$DownloadHost))
    $request.Headers.Range = [Net.Http.Headers.RangeHeaderValue]::new(0,2097151)
    $response = $null; $input = $null; $gzip = $null; $tar = $null
    try {
        $response = $client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$timeout.Token).GetAwaiter().GetResult()
        $response.EnsureSuccessStatusCode() | Out-Null
        $input = $response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
        $buffer = [byte[]]::new(2097152); $count = 0
        while ($count -lt $buffer.Length) {
            $read = $input.ReadAsync($buffer,$count,$buffer.Length-$count,$timeout.Token).GetAwaiter().GetResult()
            if ($read -eq 0) { break }; $count += $read
        }
        $compressed = [IO.MemoryStream]::new($buffer,0,$count,$false)
        $gzip = [IO.Compression.GZipStream]::new($compressed,[IO.Compression.CompressionMode]::Decompress)
        $tar = [System.Formats.Tar.TarReader]::new($gzip)
        do { $entry = $tar.GetNextEntry($false) } while ($null -ne $entry -and !$entry.Name.EndsWith('.wav'))
        if ($null -eq $entry -or $entry.Name -ne $sample.archiveEntry) { throw "Unexpected archive entry for $($sample.language)" }
        if ($entry.Length -gt 2097152) { throw 'Fixture entry exceeds the expected size limit' }
        $wave = [IO.MemoryStream]::new()
        try {
            $entry.DataStream.CopyTo($wave)
            $bytes = $wave.ToArray()
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            if ($hash -ne $sample.sha256) { throw "Fixture checksum mismatch: $($sample.language)" }
            [IO.File]::WriteAllBytes($target,$bytes)
        } finally { $wave.Dispose() }
        Write-Output "Verified FLEURS $($sample.language): $($sample.file)"
    } finally {
        if ($tar) { $tar.Dispose() }; if ($gzip) { $gzip.Dispose() }; if ($input) { $input.Dispose() }; if ($response) { $response.Dispose() }
        $request.Dispose(); $client.Dispose(); $timeout.Dispose()
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'multilingual-fixtures.json') -Destination (Join-Path $fixtures 'multilingual.json')
