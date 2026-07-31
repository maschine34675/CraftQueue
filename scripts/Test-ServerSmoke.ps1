param(
    [Parameter(Mandatory)]
    [string]$SptRoot,
    [int]$StartupTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"

$serverDirectory = [System.IO.Path]::GetFullPath((Join-Path $SptRoot "SPT"))
$serverExecutable = [System.IO.Path]::GetFullPath((Join-Path $serverDirectory "SPT.Server.exe"))
if (-not (Test-Path -LiteralPath $serverExecutable)) {
    throw "SPT.Server.exe was not found at $serverExecutable"
}
if (Get-Process -Name "SPT.Server" -ErrorAction SilentlyContinue) {
    throw "SPT.Server is already running; the smoke test will not touch it."
}

$logPath = Join-Path $serverDirectory ("user\logs\spt\spt{0}.log" -f (Get-Date -Format "yyyyMMdd"))
$existingLogLength = if (Test-Path -LiteralPath $logPath) {
    (Get-Item -LiteralPath $logPath).Length
} else {
    0
}

function Get-NewServerLogText {
    if (-not (Test-Path -LiteralPath $logPath)) {
        return ""
    }

    $stream = [System.IO.File]::Open(
        $logPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite
    )
    try {
        if ($existingLogLength -le $stream.Length) {
            $stream.Seek($existingLogLength, [System.IO.SeekOrigin]::Begin) | Out-Null
        }
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$process = Start-Process `
    -FilePath $serverExecutable `
    -WorkingDirectory $serverDirectory `
    -WindowStyle Hidden `
    -PassThru

try {
    $webUrl = $null
    $backendUrl = $null
    $cacheReady = $false
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        Start-Sleep -Milliseconds 500
        if (-not (Test-Path -LiteralPath $logPath)) {
            continue
        }

        $text = Get-NewServerLogText
        $urlMatch = [regex]::Match($text, "http://127\.0\.0\.1:38473/\?token=[A-Za-z0-9_-]+")
        if ($urlMatch.Success) {
            $webUrl = $urlMatch.Value
        }
        $backendMatch = [regex]::Match($text, "https?://[^\s]+:\d+(?=\s+gestartet)")
        if ($backendMatch.Success) {
            $backendUrl = $backendMatch.Value
        }
        if ($text -match "\[CraftQueue\].*Prepared \d+ crafting recipes") {
            $cacheReady = $true
        }
        if ($webUrl -and $backendUrl -and $cacheReady) {
            break
        }
        if ($text -match "\[CraftQueue\].*Failed to start the web server") {
            break
        }
    }

    $process.Refresh()
    if ($process.HasExited) {
        throw "SPT.Server exited with code $($process.ExitCode)."
    }
    if (-not $webUrl) {
        throw "CraftQueue did not announce its web URL within $StartupTimeoutSeconds seconds."
    }
    if (-not $backendUrl) {
        throw "SPT did not announce its backend URL within $StartupTimeoutSeconds seconds."
    }

    $token = $webUrl.Substring($webUrl.IndexOf("token=", [StringComparison]::Ordinal) + 6)
    $headers = @{ "X-CraftQueue-Token" = $token }
    $unauthorized = Invoke-WebRequest `
        -Uri "http://127.0.0.1:38473/api/profiles" `
        -SkipHttpErrorCheck `
        -TimeoutSec 10
    $authorized = Invoke-WebRequest `
        -Uri "http://127.0.0.1:38473/api/profiles" `
        -Headers $headers `
        -TimeoutSec 10
    $missingProfile = Invoke-WebRequest `
        -Uri "http://127.0.0.1:38473/api/state?profile=0123456789abcdef01234567" `
        -Headers $headers `
        -SkipHttpErrorCheck `
        -TimeoutSec 10
    $crossOriginHeaders = @{
        "X-CraftQueue-Token" = $token
        "Origin" = "https://example.invalid"
    }
    $crossOrigin = Invoke-WebRequest `
        -Uri "http://127.0.0.1:38473/api/queue/cancel" `
        -Method Post `
        -Headers $crossOriginHeaders `
        -ContentType "application/json" `
        -Body '{"profileId":"0123456789abcdef01234567","queueId":"test"}' `
        -SkipHttpErrorCheck `
        -TimeoutSec 10
    $oversizedBodyStatus = try {
        $oversized = Invoke-WebRequest `
            -Uri "http://127.0.0.1:38473/api/client/poll" `
            -Method Post `
            -Headers $headers `
            -ContentType "application/json" `
            -Body ("x" * (257 * 1024)) `
            -SkipHttpErrorCheck `
            -TimeoutSec 10
        $oversized.StatusCode
    }
    catch {
        "ConnectionRejected"
    }
    $page = Invoke-WebRequest `
        -Uri $webUrl `
        -MaximumRedirection 5 `
        -SessionVariable browserSession `
        -TimeoutSec 10

    $profiles = $authorized.Content | ConvertFrom-Json
    $recipesResponse = Invoke-WebRequest `
        -Uri "http://127.0.0.1:38473/api/recipes" `
        -Headers $headers `
        -TimeoutSec 30
    $recipes = $recipesResponse.Content | ConvertFrom-Json
    $recipe = $recipes.PSObject.Properties |
        ForEach-Object { $_.Value } |
        ForEach-Object { $_ } |
        Select-Object -First 1
    if (-not $recipe) {
        throw "CraftQueue did not expose a recipe for the fallback protocol probe."
    }

    $profileId = @($profiles)[0].id
    $backendHeaders = @{
        "Cookie" = "PHPSESSID=$profileId"
        "SessionId" = $profileId
        "requestcompressed" = "0"
        "responsecompressed" = "0"
    }
    $activeAreas = 0..27 | ForEach-Object {
        @{
            recipeId = "craftqueue-smoke-$($_)"
            areaType = $_
            timeLeft = 1
            productionTime = 1
            instanceId = "craftqueue-smoke-$($_)"
        }
    }
    $pollBody = @{
        profileId = $profileId
        productions = $activeAreas
        availableRecipeIds = @($recipe.recipeId)
    } | ConvertTo-Json -Compress -Depth 6
    Invoke-WebRequest `
        -Uri "$backendUrl/craftqueue/Poll" `
        -Method Post `
        -SkipCertificateCheck `
        -Headers $backendHeaders `
        -ContentType "application/json" `
        -Body $pollBody `
        -TimeoutSec 15 | Out-Null
    $createCommand = Invoke-WebRequest `
        -Uri "http://127.0.0.1:38473/api/commands/enqueue" `
        -Method Post `
        -Headers $headers `
        -ContentType "application/json" `
        -Body (@{ profileId = $profileId; recipeId = $recipe.recipeId } | ConvertTo-Json -Compress) `
        -TimeoutSec 15
    if (-not ($createCommand.Content | ConvertFrom-Json).success) {
        throw "CraftQueue rejected the fallback protocol probe command."
    }
    $fallbackPoll = Invoke-WebRequest `
        -Uri "$backendUrl/craftqueue/Poll" `
        -Method Post `
        -SkipCertificateCheck `
        -Headers $backendHeaders `
        -ContentType "application/json" `
        -Body $pollBody `
        -TimeoutSec 15
    $fallbackPayload = $fallbackPoll.Content | ConvertFrom-Json
    $fallbackCommand = @($fallbackPayload.commands)[0]
    if (-not $fallbackCommand) {
        throw "The SPT fallback did not deliver the CraftQueue probe command."
    }
    if ($fallbackCommand.kind -isnot [string] -or $fallbackCommand.kind -ne "Enqueue") {
        throw "The SPT fallback serialized the CraftQueue command kind as '$($fallbackCommand.kind)' instead of 'Enqueue'."
    }
    $fallbackQueueStatuses = @($fallbackPayload.queueEntries | ForEach-Object { $_.status })
    if ($fallbackQueueStatuses | Where-Object { $_ -isnot [string] }) {
        throw "The SPT fallback serialized one or more CraftQueue queue statuses as numbers."
    }

    [pscustomobject]@{
        WebReady = $true
        RecipeCacheReady = $cacheReady
        UnauthorizedStatus = $unauthorized.StatusCode
        AuthorizedStatus = $authorized.StatusCode
        MissingProfileStatus = $missingProfile.StatusCode
        CrossOriginStatus = $crossOrigin.StatusCode
        OversizedBodyStatus = $oversizedBodyStatus
        PageStatus = $page.StatusCode
        ProfileCount = @($profiles).Count
        FallbackCommandKind = $fallbackCommand.kind
        FallbackQueueStatus = $fallbackQueueStatuses | Select-Object -First 1
        BoundAddresses = (
            Get-NetTCPConnection -LocalPort 38473 -State Listen -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty LocalAddress
        ) -join ","
    }
}
finally {
    $process.Refresh()
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit(10000) | Out-Null
    }

    if (Test-Path -LiteralPath $logPath) {
        (Get-NewServerLogText) -split "\r?\n" |
            Where-Object { $_ -notmatch "SptHttpListener.*craftqueue-smoke" } |
            Select-String -Pattern "CraftQueue|critical|exception|error" -CaseSensitive:$false |
            Select-Object -Last 40
    }
}
