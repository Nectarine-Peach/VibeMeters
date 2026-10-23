param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\VibeMeters.csproj'
$output = Join-Path $PSScriptRoot '..\dist\VibeMeters-win-x64'
& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\README.md') -Destination $output
Compress-Archive -Path (Join-Path $output '*') -DestinationPath (Join-Path $PSScriptRoot '..\dist\VibeMeters-win-x64.zip') -Force
Write-Host "Ready: $output"
