param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\VibeMeters.csproj'
$output = Join-Path $PSScriptRoot '..\dist\VibeMeters-win-x64'
& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\README.md') -Destination $output
$packageFiles = @((Join-Path $output 'VibeMeters.exe'), (Join-Path $output 'README.md'))
Compress-Archive -LiteralPath $packageFiles -DestinationPath (Join-Path $PSScriptRoot '..\dist\VibeMeters-win-x64.zip') -Force
Write-Host "Ready: $output"
