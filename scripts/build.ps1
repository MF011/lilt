[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    dotnet build Lilt.slnx -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    dotnet run --project tests/Lilt.Checks -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }

    dotnet run --project tests/Lilt.Spotify.Checks -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Spotify checks failed.' }

    $publishPath = Join-Path $repoRoot 'artifacts/Lilt-win-x64'
    $zipPath = Join-Path $repoRoot 'artifacts/Lilt-win-x64.zip'
    if (Test-Path $publishPath) { Remove-Item $publishPath -Recurse -Force }

    dotnet publish src/Lilt.App/Lilt.App.csproj -c $Configuration -r win-x64 `
        --self-contained true -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    Copy-Item README.md, LICENSE -Destination $publishPath
    Copy-Item docs -Destination $publishPath -Recurse
    Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $zipPath -Force
    Write-Host "Package: $zipPath"
}
finally {
    Pop-Location
}
