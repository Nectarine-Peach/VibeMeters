param(
    [string]$Dotnet = 'dotnet',
    [string]$Node = 'node.exe',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\VibeMeters-portable-win-x64')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\VibeMeters.csproj'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$archivePath = $output + '.zip'
# A new staging directory prevents stale node_modules or files from older packages leaking in.
if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $archivePath)) {
    throw 'Output already exists. Choose a new -OutputDirectory; existing packages are never overwritten.'
}
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
Write-Host "Ready: $output"
$global:LASTEXITCODE = 0
