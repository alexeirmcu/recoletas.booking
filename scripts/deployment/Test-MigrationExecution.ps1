$ErrorActionPreference = 'Stop'

# Exercise the actual migration gate without Azure, resource changes, or waits.
$source = Get-Content -Raw (Join-Path $PSScriptRoot 'Deploy-Azure.ps1')
$start = $source.IndexOf('    $migrationJob = ')
$end = $source.IndexOf('    Write-Output "Database migration execution', $start)
if ($start -lt 0 -or $end -lt 0) {
    throw 'Could not locate the deployment migration gate.'
}
$gate = [scriptblock]::Create($source.Substring($start, $end - $start))

function Invoke-AzureCli {
    param([string[]] $Arguments, [switch] $CaptureOutput)

    $script:Calls.Add(($Arguments -join ' '))
    if ($Arguments[2] -eq 'start') {
        $queryIndex = [Array]::IndexOf($Arguments, '--query')
        if (-not $CaptureOutput -or $queryIndex -lt 0 -or $Arguments[$queryIndex + 1] -ne 'name') {
            throw 'Job start must capture its exact returned execution name.'
        }
        return $script:ReturnedName
    }
    if ($Arguments[3] -eq 'list') {
        # Simulate an eventually consistent list showing only an old success.
        return 'old-success'
    }
    if ($Arguments[3] -eq 'show') {
        $index = [Array]::IndexOf($Arguments, '--job-execution-name')
        if ($index -lt 0 -or $Arguments[$index + 1] -ne 'new-execution') {
            return 'Succeeded'
        }
        $statusIndex = [Math]::Min($script:Polls, $script:Statuses.Count - 1)
        $script:Polls++
        return $script:Statuses[$statusIndex]
    }
    throw 'Unexpected Azure CLI call.'
}

function Start-Sleep {
    param([int] $Seconds)
    $script:Sleeps++
}

$cases = @(
    @{ Name = 'Old success cannot satisfy new Running job'; ReturnedName = 'new-execution'; Statuses = @('Running'); ExpectedError = 'did not succeed'; Polls = 120 },
    @{ Name = 'New Running then Succeeded'; ReturnedName = 'new-execution'; Statuses = @('Running', 'Succeeded'); ExpectedError = ''; Polls = 2 },
    @{ Name = 'New execution already Succeeded'; ReturnedName = 'new-execution'; Statuses = @('Succeeded'); ExpectedError = ''; Polls = 1 },
    @{ Name = 'Old success cannot satisfy new Failed job'; ReturnedName = 'new-execution'; Statuses = @('Failed'); ExpectedError = "status 'Failed'"; Polls = 1 },
    @{ Name = 'Missing execution name fails'; ReturnedName = ''; Statuses = @('Succeeded'); ExpectedError = 'execution name'; Polls = 0 },
    @{ Name = 'Ambiguous execution names fail'; ReturnedName = "new-execution`nold-success"; Statuses = @('Succeeded'); ExpectedError = 'execution name'; Polls = 0 }
)

foreach ($case in $cases) {
    $script:ReturnedName = $case.ReturnedName
    $script:Statuses = $case.Statuses
    $script:Polls = 0
    $script:Sleeps = 0
    $script:Calls = [Collections.Generic.List[string]]::new()
    $failure = ''

    try {
        & $gate
    }
    catch {
        $failure = $_.Exception.Message
    }

    if ($case.ExpectedError -eq '') {
        if ($failure -ne '') { throw "$($case.Name): unexpected failure: $failure" }
    }
    elseif (-not $failure.Contains($case.ExpectedError)) {
        throw "$($case.Name): expected '$($case.ExpectedError)', received '$failure'."
    }
    if ($script:Polls -ne $case.Polls) {
        throw "$($case.Name): expected $($case.Polls) exact-execution polls, got $script:Polls."
    }
    if ($script:Calls | Where-Object { $_ -match 'execution list|old-success' }) {
        throw "$($case.Name): consulted a prior execution."
    }
    if ($script:Sleeps -gt 120) {
        throw "$($case.Name): polling exceeded its bound."
    }
    Write-Output "PASS: $($case.Name)"
}

Write-Output "$($cases.Count) migration gate regression cases passed."
