param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))

# The mod version lives in exactly one place; deriving it here keeps the
# archive name from silently drifting behind a release.
$protocolPath = Join-Path $repositoryRoot "CraftQueue.Shared\CraftQueueProtocol.cs"
$protocolText = Get-Content -LiteralPath $protocolPath -Raw
$versionMatch = [regex]::Match($protocolText, 'Version\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $versionMatch.Success) {
    throw "The mod version was not found in $protocolPath"
}
$modVersion = $versionMatch.Groups[1].Value

$stageRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot "CraftQueue-v$modVersion"))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot "maschine-CraftQueue-v$modVersion.zip"))
$serverProjectPath = Join-Path $repositoryRoot "CraftQueue.Server\CraftQueue.Server.csproj"
[xml]$serverProject = Get-Content -LiteralPath $serverProjectPath -Raw
$sptModName = [string](
    $serverProject.Project.PropertyGroup |
        ForEach-Object { $_.SptModName } |
        Where-Object { $_ } |
        Select-Object -First 1
)
if ([string]::IsNullOrWhiteSpace($sptModName)) {
    throw "SptModName is missing from $serverProjectPath"
}

if (-not $stageRoot.StartsWith($artifactRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Release staging path escaped the repository artifact directory."
}

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

if (-not $SkipTests) {
    dotnet test (Join-Path $repositoryRoot "CraftQueue.Tests\CraftQueue.Tests.csproj") -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "CraftQueue tests failed."
    }
}

dotnet build $serverProjectPath -c $Configuration --no-incremental
if ($LASTEXITCODE -ne 0) {
    throw "CraftQueue server build failed."
}

dotnet build (Join-Path $repositoryRoot "CraftQueue.Client\CraftQueue.Client.csproj") -c $Configuration --no-incremental
if ($LASTEXITCODE -ne 0) {
    throw "CraftQueue client build failed."
}

$clientDirectory = Join-Path $stageRoot "BepInEx\plugins"
$serverDirectory = Join-Path $stageRoot "SPT\user\mods\$sptModName"
$configDirectory = Join-Path $serverDirectory "config"
New-Item -ItemType Directory -Path $clientDirectory, $serverDirectory, $configDirectory -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $repositoryRoot "CraftQueue.Client\bin\$Configuration\maschine-CraftQueue.Client.dll") -Destination $clientDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "CraftQueue.Server\bin\$Configuration\maschine-CraftQueue.Server.dll") -Destination $serverDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "CraftQueue.Server\config\config.json") -Destination $configDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "README.md") -Destination $serverDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE") -Destination $serverDirectory

Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $archivePath -CompressionLevel Optimal
Write-Host "Created $archivePath"
