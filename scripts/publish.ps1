param(
    [string]$Dotnet = 'dotnet',
    [string]$Node = 'node.exe',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\VibeMeters-portable-win-x64')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\VibeMeters.csproj'
$destination = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
$parent = Split-Path $destination
$dist = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\dist'))
$packagePattern = '^VibeMeters-(?:portable|[0-9a-f]{7,40})-win-x64$'
$managed = $parent -eq $dist -and (Split-Path $destination -Leaf) -match $packagePattern
[IO.Directory]::CreateDirectory($parent) | Out-Null
if ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw "Output parent cannot be a reparse point: $parent"
}
function Remove-GeneratedItem([string]$path, [string]$expectedParent) {
    $item = Get-Item -Force -LiteralPath $path
    if ((Split-Path $item.FullName) -ne $expectedParent -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Unexpected generated artifact path: $path"
    }
    Remove-Item -LiteralPath $item.FullName -Recurse -Force
}
function Test-PackageFolder($item) {
    if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $false }
    $allowed = @('VibeMeters.exe','README.md','README.ko.md','README.en.md','RESEARCH.md','VERIFICATION.md','docs','runtime','artifacts')
    return (Test-Path -LiteralPath (Join-Path $item.FullName 'VibeMeters.exe') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $item.FullName 'runtime/gemini-quota/runtime-manifest.json') -PathType Leaf) -and
        !(Get-ChildItem -Force -LiteralPath $item.FullName | Where-Object Name -NotIn $allowed)
}
$lock = $null
$stage = $null
$retainStage = $false
try {
if ($managed) {
    $lock = [IO.FileStream]::new((Join-Path $dist '.publish.lock'), 'OpenOrCreate', 'ReadWrite', 'None', 4096, 'DeleteOnClose')
}
$targets = @($destination, "$destination.zip", "$destination.gemini-bundle-meta.json")
foreach ($target in $targets) {
    if (!(Test-Path -LiteralPath $target)) { continue }
    $existing = Get-Item -LiteralPath $target
    if (!$managed -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ($target -eq $destination -and !(Test-PackageFolder $existing)) -or
        ($target -ne $destination -and $existing.PSIsContainer)) { throw "Unmanaged output already exists: $target" }
}
$stage = Join-Path $parent ('.publish-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
$output = Join-Path $stage 'package'
$archivePath = $output + '.zip'
$nodeExecutable = (Get-Command $Node -ErrorAction Stop).Source
$runtimeConfig = Get-Content (Join-Path $PSScriptRoot '..\runtime\gemini-quota\package.json') -Raw | ConvertFrom-Json
if ((& $nodeExecutable --version) -ne "v$($runtimeConfig.bundledNodeVersion)") {
    throw "Expected Node $($runtimeConfig.bundledNodeVersion). Select it with -Node."
}
$nodeLicense = Join-Path (Split-Path $nodeExecutable) 'LICENSE'
if (!(Test-Path -LiteralPath $nodeLicense)) { throw 'The selected Node distribution must include its LICENSE file.' }
$runtimeSource = Join-Path $PSScriptRoot '..\runtime\gemini-quota'
$runtimeTarget = Join-Path $output 'runtime\gemini-quota'
# Always restore the lock, including the build-only esbuild binary. npm's install scripts stay disabled.
& npm ci --prefix $runtimeSource --ignore-scripts --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'Gemini locked build dependency installation failed.' }
New-Item -ItemType Directory -Force -Path $runtimeTarget | Out-Null
$bundleAudit = $output + '.gemini-bundle-meta.json'
& $nodeExecutable (Join-Path $runtimeSource 'build.mjs') $runtimeTarget $bundleAudit
if ($LASTEXITCODE -ne 0) { throw 'Gemini runtime bundling failed.' }
# Keep SDK credentials, model calls and browser login out of packaging diagnostics.
& $nodeExecutable (Join-Path $runtimeSource 'verify-runtime.mjs') $runtimeTarget
if ($LASTEXITCODE -ne 0) { throw 'Gemini packaged runtime check failed.' }
$buildOutput = Join-Path (Split-Path $output) ('dotnet-publish-' + [Guid]::NewGuid().ToString('N'))
& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $buildOutput
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $buildOutput 'VibeMeters.exe') -Destination $output
Copy-Item -LiteralPath $nodeExecutable -Destination (Join-Path $runtimeTarget 'node.exe')
Copy-Item -LiteralPath $nodeLicense -Destination (Join-Path $runtimeTarget 'NODE-LICENSE.txt')
$manifestFile = Join-Path $runtimeTarget 'runtime-manifest.json'
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
$manifest | Add-Member -NotePropertyName nodeSha256 -NotePropertyValue (Get-FileHash -LiteralPath $nodeExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestFile -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\README.md') -Destination $output
$packageFiles = @((Join-Path $output 'VibeMeters.exe'), (Join-Path $output 'README.md'))
foreach ($translatedReadme in @('README.en.md', 'README.ko.md')) {
    $translatedSource = Join-Path $PSScriptRoot "..\$translatedReadme"
    if (Test-Path -LiteralPath $translatedSource) {
        Copy-Item -LiteralPath $translatedSource -Destination $output
        $packageFiles += Join-Path $output $translatedReadme
    }
}
$packageFiles += Join-Path $output 'runtime'
$docsSource = Join-Path $PSScriptRoot '..\docs'
if (Test-Path -LiteralPath $docsSource) {
    $docsTarget = Join-Path $output 'docs'
    & robocopy $docsSource $docsTarget /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw 'Documentation copy failed.' }
    $packageFiles += Join-Path $output 'docs'
}
foreach ($document in @('RESEARCH.md', 'VERIFICATION.md')) {
    $source = Join-Path $PSScriptRoot "..\$document"
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination $output
        $packageFiles += Join-Path $output $document
    }
}
$temporaryArchive = $archivePath + '.tmp'
$outputRoot = [IO.Path]::GetFullPath($output)
$archiveStream = [IO.File]::Open($temporaryArchive, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($packagePath in $packageFiles) {
        $sourcePath = [IO.Path]::GetFullPath($packagePath)
        $sources = if ([IO.Directory]::Exists($sourcePath)) { [IO.Directory]::GetFiles($sourcePath, '*', [IO.SearchOption]::AllDirectories) } else { @($sourcePath) }
        foreach ($sourceFile in $sources) {
            $entryName = [IO.Path]::GetRelativePath($outputRoot, $sourceFile).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $sourceFile, $entryName, [IO.Compression.CompressionLevel]::Fastest) | Out-Null
        }
    }
} finally {
    $archive.Dispose()
    $archiveStream.Dispose()
}
[IO.File]::Move($temporaryArchive, $archivePath, $false)
$previous = @{}
$installed = @()
try {
    foreach ($target in $targets) {
        if (!(Test-Path -LiteralPath $target)) { continue }
        $backup = Join-Path $stage ('previous-' + (Split-Path $target -Leaf))
        Move-Item -LiteralPath $target -Destination $backup
        $previous[$target] = $backup
    }
    foreach ($suffix in @('', '.zip', '.gemini-bundle-meta.json')) {
        $target = $destination + $suffix
        Move-Item -LiteralPath ($output + $suffix) -Destination $target
        $installed += $target
    }
} catch {
    $retainStage = $true
    foreach ($target in $installed) { Remove-GeneratedItem $target $parent }
    foreach ($target in $previous.Keys) { Move-Item -LiteralPath $previous[$target] -Destination $target }
    $retainStage = $false
    throw
}
if ($managed) {
    foreach ($item in Get-ChildItem -LiteralPath $dist) {
        if ($item.FullName -in $targets -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        $remove = if ($item.PSIsContainer) {
            ($item.Name -match $packagePattern -and (Test-PackageFolder $item)) -or
                ($item.Name -match '^dotnet-publish-[0-9a-f]{32}$' -and
                 (Test-Path -LiteralPath (Join-Path $item.FullName 'VibeMeters.exe') -PathType Leaf) -and
                 !(Get-ChildItem -Force -LiteralPath $item.FullName | Where-Object Name -ne 'VibeMeters.exe'))
        } else {
            $item.Name -match '^VibeMeters-(?:portable|[0-9a-f]{7,40})-win-x64(?:\.zip|\.gemini-bundle-meta\.json)$'
        }
        if ($remove) {
            try { Remove-GeneratedItem $item.FullName $dist } catch { Write-Warning "Could not remove $($item.FullName): $_" }
        }
    }
}
Write-Host "Ready: $destination"
$global:LASTEXITCODE = 0
} finally {
    try {
        if ($stage -and !$retainStage -and (Test-Path -LiteralPath $stage)) { Remove-GeneratedItem $stage $parent }
    } finally { if ($lock) { $lock.Dispose() } }
}
