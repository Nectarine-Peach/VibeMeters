param(
    [Parameter(Mandatory)][string]$BaselineZip,
    [Parameter(Mandatory)][string]$CurrentZip,
    [string]$ReportPath = (Join-Path $PSScriptRoot '..\dist\comparison\package-size-comparison.json')
)
$ErrorActionPreference = 'Stop'
function Get-PackageMetrics([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $archive = [IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $groups = @{}
        $total = [long]0
        $files = 0
        $nodeSha256 = $null
        foreach ($entry in $archive.Entries) {
            if (!$entry.Name) { continue }
            $name = $entry.FullName.Replace('\', '/')
            $group = if ($name -eq 'VibeMeters.exe') { 'dotnetApp' }
                elseif ($name -match '/node.exe$') { 'nodeExecutable' }
                elseif ($name -match '/node_modules/') { 'nodeModules' }
                elseif ($name -like 'runtime/*') { 'runtimeOther' }
                elseif ($name -like 'docs/*') { 'documentationAssets' }
                else { 'documentationOther' }
            if (!$groups.ContainsKey($group)) { $groups[$group] = @{ bytes = [long]0; compressedBytes = [long]0; files = 0 } }
            $groups[$group].bytes += $entry.Length
            $groups[$group].compressedBytes += $entry.CompressedLength
            $groups[$group].files++
            if ($group -eq 'nodeExecutable') {
                $stream = $entry.Open()
                try { $nodeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
                finally { $stream.Dispose() }
            }
            $total += $entry.Length
            $files++
        }
        return [ordered]@{ path = $resolved; zipBytes = (Get-Item -LiteralPath $resolved).Length;
            uncompressedBytes = $total; files = $files; groups = $groups; nodeSha256 = $nodeSha256;
            sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant() }
    } finally { $archive.Dispose() }
}
$baseline = Get-PackageMetrics $BaselineZip
$current = Get-PackageMetrics $CurrentZip
$result = [ordered]@{ baseline = $baseline; current = $current;
    identicalNodeBinary = $baseline.nodeSha256 -eq $current.nodeSha256;
    zipReductionPercent = [Math]::Round((1 - $current.zipBytes / $baseline.zipBytes) * 100, 2);
    uncompressedReductionPercent = [Math]::Round((1 - $current.uncompressedBytes / $baseline.uncompressedBytes) * 100, 2);
    note = 'Baseline is the official v1.4.3 release; current is this checkout. Uncompressed bytes sum ZIP file entries. Node identity is checked by SHA-256. The .NET SDK/build environment may differ.' }
$report = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Force -Path (Split-Path $report) | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding utf8NoBOM
$result | ConvertTo-Json -Depth 8
