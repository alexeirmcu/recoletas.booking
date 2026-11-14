[CmdletBinding()]
param(
    [switch] $ValidateOnly
)

$ErrorActionPreference = 'Stop'

function Invoke-AzureCli {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,
        [switch] $CaptureOutput
    )

    if ($CaptureOutput) {
        $result = & az @Arguments
    }
    else {
        & az @Arguments
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed with exit code $LASTEXITCODE."
    }

    if ($CaptureOutput) {
        return ($result -join [Environment]::NewLine).Trim()
    }
}

function New-DeploymentParameterFile {
    param([hashtable] $Parameters)

    $document = @{
        '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters = @{}
    }

    foreach ($entry in $Parameters.GetEnumerator()) {
        $document.parameters[$entry.Key] = @{ value = $entry.Value }
    }

    $path = [IO.Path]::Combine([IO.Path]::GetTempPath(), [IO.Path]::GetRandomFileName())
    $json = $document | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
    if ($env:OS -ne 'Windows_NT') {
        & chmod 600 $path
        if ($LASTEXITCODE -ne 0) {
            Remove-Item -LiteralPath $path -Force
            throw 'Could not restrict permissions on the temporary deployment parameter file.'
        }
    }

    return $path
}

function Assert-RequiredEnvironment {
    param([string[]] $Names)

    foreach ($name in $Names) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
            throw "Required environment variable '$name' is not configured."
        }
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$infraPath = Join-Path $repositoryRoot 'infra'

if ($ValidateOnly) {
    foreach ($template in @('registry.bicep', 'platform.bicep', 'app.bicep', 'modules/registry-pull-role.bicep')) {
        Invoke-AzureCli -Arguments @(
            'bicep', 'build', '--file', (Join-Path $infraPath $template), '--stdout'
        ) | Out-Null
    }
    Write-Output 'Bicep templates compiled successfully.'
    exit 0
}

Assert-RequiredEnvironment -Names @(
    'DEPLOY_ENV',
    'AZURE_SUBSCRIPTION_ID',
    'AZURE_LOCATION',
    'RESOURCE_GROUP',
    'ACR_NAME',
    'ACR_RESOURCE_GROUP',
    'IMAGE_DIGEST',
    'KEY_VAULT_NAME',
    'VNET_ADDRESS_PREFIX',
    'POSTGRES_ADMIN_PASSWORD',
    'JWT_SECRET',
    'JWT_ISSUER',
    'JWT_AUDIENCE',
    'RABBITMQ_HOSTNAME',
    'RABBITMQ_VHOST',
    'RABBITMQ_USERNAME',
    'RABBITMQ_PASSWORD'
)

if ($env:DEPLOY_ENV -notin @('dev', 'prod')) {
    throw "DEPLOY_ENV must be 'dev' or 'prod'."
}
if ($env:VNET_ADDRESS_PREFIX -notmatch '^(?:\d{1,3}\.){3}\d{1,3}/16$') {
    throw 'VNET_ADDRESS_PREFIX must be a non-overlapping IPv4 /16 CIDR.'
}
if ($env:JWT_SECRET.Length -lt 32) {
    throw 'JWT_SECRET must contain at least 32 characters.'
}
if ($env:POSTGRES_ADMIN_PASSWORD.Length -lt 16) {
    throw 'POSTGRES_ADMIN_PASSWORD must contain at least 16 characters.'
}
if ($env:POSTGRES_ADMIN_PASSWORD -notmatch '^[A-Za-z0-9!#%+_-]{16,128}$') {
    throw 'POSTGRES_ADMIN_PASSWORD must use 16-128 letters, digits, or !#%+_- characters (no connection-string delimiters).'
}
if ($env:IMAGE_DIGEST -notmatch '^sha256:[0-9a-f]{64}$') {
    throw 'IMAGE_DIGEST must be a sha256 digest obtained from a successful CI run.'
}

Invoke-AzureCli -Arguments @('account', 'set', '--subscription', $env:AZURE_SUBSCRIPTION_ID)
$registryLoginServer = Invoke-AzureCli -CaptureOutput -Arguments @(
    'acr', 'show', '--name', $env:ACR_NAME, '--resource-group', $env:ACR_RESOURCE_GROUP,
    '--query', 'loginServer', '--output', 'tsv'
)
$imageReference = "$registryLoginServer/recoletas-booking-api@$($env:IMAGE_DIGEST)"

$commonParameters = @{
    deploymentEnvironment = $env:DEPLOY_ENV
    location = $env:AZURE_LOCATION
    registryName = $env:ACR_NAME
    registryResourceGroupName = $env:ACR_RESOURCE_GROUP
    imageReference = $imageReference
    keyVaultName = $env:KEY_VAULT_NAME
    rabbitHostName = $env:RABBITMQ_HOSTNAME
    rabbitTlsServerName = [Environment]::GetEnvironmentVariable('RABBITMQ_TLS_SERVER_NAME')
}
if ([string]::IsNullOrWhiteSpace($commonParameters.rabbitTlsServerName)) {
    $commonParameters.rabbitTlsServerName = ''
}

$platformParameters = $commonParameters.Clone()
$platformParameters['vnetAddressPrefix'] = $env:VNET_ADDRESS_PREFIX
$platformParameters['postgresAdminPassword'] = $env:POSTGRES_ADMIN_PASSWORD
$platformParameters['jwtSecret'] = $env:JWT_SECRET
$platformParameters['jwtIssuer'] = $env:JWT_ISSUER
$platformParameters['jwtAudience'] = $env:JWT_AUDIENCE
$platformParameters['rabbitVirtualHost'] = $env:RABBITMQ_VHOST
$platformParameters['rabbitUserName'] = $env:RABBITMQ_USERNAME
$platformParameters['rabbitPassword'] = $env:RABBITMQ_PASSWORD

$platformFile = New-DeploymentParameterFile -Parameters $platformParameters
$appFile = New-DeploymentParameterFile -Parameters $commonParameters
try {
    Invoke-AzureCli -Arguments @(
        'deployment', 'group', 'create',
        '--resource-group', $env:RESOURCE_GROUP,
        '--template-file', (Join-Path $infraPath 'platform.bicep'),
        '--parameters', "@$platformFile",
        '--output', 'none'
    )

    $migrationJob = "recoletas-$($env:DEPLOY_ENV)-migrate"
    $executionName = Invoke-AzureCli -CaptureOutput -Arguments @(
        'containerapp', 'job', 'start',
        '--name', $migrationJob,
        '--resource-group', $env:RESOURCE_GROUP,
        '--query', 'name',
        '--output', 'tsv'
    )
    if ([string]::IsNullOrWhiteSpace($executionName) -or
        $executionName -cnotmatch '^[a-z0-9][a-z0-9-]*$') {
        throw 'Migration job start did not return a single valid execution name.'
    }

    $migrationSucceeded = $false
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        $status = Invoke-AzureCli -CaptureOutput -Arguments @(
            'containerapp', 'job', 'execution', 'show',
            '--name', $migrationJob,
            '--resource-group', $env:RESOURCE_GROUP,
            '--job-execution-name', $executionName,
            '--query', 'properties.status',
            '--output', 'tsv'
        )
        if ($status -eq 'Succeeded') {
            $migrationSucceeded = $true
            break
        }
        if ($status -in @('Failed', 'Stopped', 'Canceled')) {
            throw "Database migration execution '$executionName' ended with status '$status'."
        }

        Start-Sleep -Seconds 10
    }
    if (-not $migrationSucceeded) {
        throw "Database migration did not succeed within 20 minutes. Last execution: '$executionName'."
    }
    Write-Output "Database migration execution '$executionName' succeeded."

    Invoke-AzureCli -Arguments @(
        'deployment', 'group', 'create',
        '--resource-group', $env:RESOURCE_GROUP,
        '--template-file', (Join-Path $infraPath 'app.bicep'),
        '--parameters', "@$appFile",
        '--output', 'none'
    )

    $fqdn = Invoke-AzureCli -CaptureOutput -Arguments @(
        'containerapp', 'show',
        '--name', "recoletas-$($env:DEPLOY_ENV)-api",
        '--resource-group', $env:RESOURCE_GROUP,
        '--query', 'properties.configuration.ingress.fqdn',
        '--output', 'tsv'
    )
    if ([string]::IsNullOrWhiteSpace($fqdn)) {
        throw 'Azure did not return the deployed Container App ingress hostname.'
    }

    $httpClient = [System.Net.Http.HttpClient]::new()
    $httpClient.Timeout = [System.TimeSpan]::FromSeconds(5)
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            try {
                $response = $httpClient.GetAsync("https://$fqdn/health/ready").GetAwaiter().GetResult()
                if ($response.IsSuccessStatusCode) {
                    $response.Dispose()
                    $ready = $true
                    break
                }
                Write-Output "Readiness probe returned HTTP $([int]$response.StatusCode); retrying."
                $response.Dispose()
            }
            catch [System.Net.Http.HttpRequestException] {
                Write-Output 'Readiness endpoint is not reachable yet; retrying.'
            }
            catch [System.Threading.Tasks.TaskCanceledException] {
                Write-Output 'Readiness request timed out; retrying.'
            }

            Start-Sleep -Seconds 5
        }
        if (-not $ready) {
            throw "The deployed API at '$fqdn' did not become ready within 10 minutes."
        }
    }
    finally {
        $httpClient.Dispose()
    }

    Write-Output "API is ready at https://$fqdn."
}
finally {
    Remove-Item -LiteralPath $platformFile, $appFile -Force -ErrorAction SilentlyContinue
}
