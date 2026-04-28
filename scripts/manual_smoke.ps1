param(
    [string]$BaseUrl = "http://127.0.0.1:5187"
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Invoke-JsonPost {
    param(
        [string]$Url,
        [hashtable]$Body
    )

    $jsonBody = $Body | ConvertTo-Json -Depth 20
    return Invoke-RestMethod -Uri $Url -Method Post -ContentType "application/json" -Body $jsonBody
}

function Assert-Status {
    param(
        [object]$Actual,
        [string]$Expected,
        [string]$Context
    )

    $actualText = [string]$Actual
    if ($actualText -eq "1") { $actualText = "InProgress" }
    elseif ($actualText -eq "2") { $actualText = "Review" }
    elseif ($actualText -eq "3") { $actualText = "Completed" }

    if ($actualText -ne $Expected) {
        throw "$Context failed. Expected status $Expected but got $Actual"
    }
}

Write-Step "Checking health endpoint"
$health = Invoke-RestMethod -Uri "$BaseUrl/health" -Method Get
if ($health.status -ne "ok") {
    throw "Health endpoint did not return status=ok"
}
Write-Host "Health check passed." -ForegroundColor Green

Write-Step "Running answer-path flow"
$fromTextAnswer = @{
    userId = "smoke-answer"
    inputText = "Arrangement in Germany between Alpha GmbH and Beta SARL requiring disclosure."
}
$r1 = Invoke-JsonPost -Url "$BaseUrl/draft/from-text" -Body $fromTextAnswer
Assert-Status -Actual $r1.status -Expected "InProgress" -Context "from-text answer-path"

$respondAnswer = @{
    sessionId = $r1.sessionId
    answer = "MDR-SMOKE-0001"
    skip = $false
}
$r2 = Invoke-JsonPost -Url "$BaseUrl/draft/respond" -Body $respondAnswer
Assert-Status -Actual $r2.status -Expected "Review" -Context "respond answer-path"

$confirmAnswer = @{
    sessionId = $r2.sessionId
}
$r3 = Invoke-JsonPost -Url "$BaseUrl/draft/confirm" -Body $confirmAnswer
Assert-Status -Actual $r3.status -Expected "Completed" -Context "confirm answer-path"
Write-Host "Answer-path flow passed." -ForegroundColor Green

Write-Step "Running skip-path flow"
$fromTextSkip = @{
    userId = "smoke-skip"
    inputText = "Need MDR draft quickly."
}
$s1 = Invoke-JsonPost -Url "$BaseUrl/draft/from-text" -Body $fromTextSkip
Assert-Status -Actual $s1.status -Expected "InProgress" -Context "from-text skip-path"

$sessionId = $s1.sessionId
for ($i = 0; $i -lt 4; $i++) {
    $skipBody = @{
        sessionId = $sessionId
        skip = $true
    }

    $skipResult = Invoke-JsonPost -Url "$BaseUrl/draft/respond" -Body $skipBody
    if ($skipResult.status -eq "Review" -or $skipResult.status -eq 2) {
        break
    }

    if ($skipResult.status -ne "InProgress" -and $skipResult.status -ne 1) {
        throw "Unexpected status during skip-path: $($skipResult.status)"
    }
}

Assert-Status -Actual $skipResult.status -Expected "Review" -Context "respond skip-path"

$confirmSkip = @{
    sessionId = $sessionId
}
$s3 = Invoke-JsonPost -Url "$BaseUrl/draft/confirm" -Body $confirmSkip
Assert-Status -Actual $s3.status -Expected "Completed" -Context "confirm skip-path"
Write-Host "Skip-path flow passed." -ForegroundColor Green

Write-Step "Smoke test completed"
$summary = [ordered]@{
    baseUrl = $BaseUrl
    answerPathSessionId = $r3.sessionId
    skipPathSessionId = $s3.sessionId
    result = "PASS"
}
$summary | ConvertTo-Json -Depth 5
