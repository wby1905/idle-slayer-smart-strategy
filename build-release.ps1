param(
    [string]$GameDirectory = $env:IDLE_SLAYER_GAME_DIR,
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'SmartStrategy\SmartStrategy.csproj'
$readme = Join-Path $PSScriptRoot 'README.md'
$changelog = Join-Path $PSScriptRoot 'CHANGELOG.md'
$license = Join-Path $PSScriptRoot 'LICENSE'
$hero = Join-Path $PSScriptRoot 'docs\images\smart-strategy-hero.png'

$buildArguments = @('build', $project, '-c', $Configuration)
if (![string]::IsNullOrWhiteSpace($GameDirectory))
{
    $resolvedGameDirectory = [System.IO.Path]::GetFullPath($GameDirectory)
    if (!(Test-Path -LiteralPath (Join-Path $resolvedGameDirectory 'Idle Slayer.exe')))
    {
        throw "GameDirectory does not point to an Idle Slayer installation: $resolvedGameDirectory"
    }
    $buildArguments += "-p:IdleSlayerGameDir=$resolvedGameDirectory"
}

& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$dll = Join-Path $PSScriptRoot "SmartStrategy\bin\$Configuration\net6.0\SmartStrategy.dll"
$version = [System.Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString(3)
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$stage = [System.IO.Path]::GetFullPath((Join-Path $outputRoot "SmartStrategy-$version"))
$zip = [System.IO.Path]::GetFullPath((Join-Path $outputRoot "SmartStrategy-$version.zip"))
$rootPrefix = $outputRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
              [System.IO.Path]::DirectorySeparatorChar

if (!$stage.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase))
{
    throw "Refusing to prepare a release outside the configured output directory: $stage"
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

$mods = Join-Path $stage 'Mods'
$images = Join-Path $stage 'docs\images'
New-Item -ItemType Directory -Path $mods -Force | Out-Null
New-Item -ItemType Directory -Path $images -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $mods 'SmartStrategy.dll')
Copy-Item -LiteralPath $readme -Destination (Join-Path $stage 'README.md')
Copy-Item -LiteralPath $changelog -Destination (Join-Path $stage 'CHANGELOG.md')
Copy-Item -LiteralPath $license -Destination (Join-Path $stage 'LICENSE')
Copy-Item -LiteralPath $hero -Destination (Join-Path $images 'smart-strategy-hero.png')

$hash = (Get-FileHash -LiteralPath (Join-Path $mods 'SmartStrategy.dll') -Algorithm SHA256).Hash
Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding utf8NoBOM `
    -Value "$hash  Mods/SmartStrategy.dll"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash

[PSCustomObject]@{
    Version = $version
    Dll = (Join-Path $mods 'SmartStrategy.dll')
    Zip = $zip
    DllSha256 = $hash
    ZipSha256 = $zipHash
}
