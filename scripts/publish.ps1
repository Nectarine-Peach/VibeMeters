param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\VibeMeters.csproj'
$output = Join-Path $PSScriptRoot '..\dist\VibeMeters-win-x64'
$runtimeSource = Join-Path $PSScriptRoot '..\runtime\gemini-quota'
$runtimeTarget = Join-Path $output 'runtime\gemini-quota'
if (!(Test-Path -LiteralPath (Join-Path $runtimeSource 'node_modules\@google\gemini-cli-core\package.json'))) {
    & npm ci --prefix $runtimeSource --omit=dev --omit=optional --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Gemini official runtime installation failed.' }
}
& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
New-Item -ItemType Directory -Force -Path $runtimeTarget | Out-Null
& robocopy $runtimeSource $runtimeTarget /E /MT:8 /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Gemini runtime copy failed.' }
if (!(Test-Path -LiteralPath (Join-Path $runtimeTarget 'node.exe'))) {
    $nodeExecutable = (Get-Command node.exe -ErrorAction Stop).Source
    Copy-Item -LiteralPath $nodeExecutable -Destination (Join-Path $runtimeTarget 'node.exe')
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\README.md') -Destination $output
$packageFiles = @((Join-Path $output 'VibeMeters.exe'), (Join-Path $output 'README.md'))
$packageFiles += Join-Path $output 'runtime'
foreach ($document in @('RESEARCH.md', 'VERIFICATION.md')) {
    $source = Join-Path $PSScriptRoot "..\$document"
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination $output
        $packageFiles += Join-Path $output $document
    }
}
$archivePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\dist\VibeMeters-win-x64.zip'))
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
[IO.File]::Move($temporaryArchive, $archivePath, $true)
Write-Host "Ready: $output"
