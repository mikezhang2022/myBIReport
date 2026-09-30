param([Parameter(Mandatory = $true)][string]$LanIp)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$assembly = Join-Path $projectRoot 'bin/Debug/net8.0/ProductionProcessBi.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Build the project first.' }

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$loopback = "http://127.0.0.1:$port"
$lan = "http://${LanIp}:$port"
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("ppbi-lan-check-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'Frontend') -Destination (Join-Path $temporary 'Frontend') -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'Admin') -Destination (Join-Path $temporary 'Admin') -Recurse
New-Item -ItemType Directory -Path (Join-Path $temporary 'data') | Out-Null
Set-Content -LiteralPath (Join-Path $temporary 'data/users.json') -Value '[]' -Encoding utf8

$reportPath = Join-Path $temporary 'data/report-definitions.json'
$reports = @([PSCustomObject]@{
    Id = 'lan-fixture'; Category = '整机'; Name = '整机查询部件信息';
    QueryType = 'standard'; Conditions = @('sn'); DisplayFields = @(); Enabled = $true;
    SqlText = "SELECT 'x' AS demo FROM dual"; ReportStyle = 'standard';
    EnableCsvExport = $true; DashboardWidgets = @(); Filters = @()
})
ConvertTo-Json -InputObject $reports -Depth 20 | Set-Content -LiteralPath $reportPath -Encoding utf8

$process = $null
try {
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet', "`"$assembly`" --urls http://0.0.0.0:$port --contentroot `"$temporary`"")
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($start)

    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if ($process.HasExited) { break }
        try {
            $response = Invoke-RestMethod -Uri "$loopback/api/auth/status" -TimeoutSec 2
            if ($null -ne $response.setupRequired) { $ready = $true; break }
        } catch {}
    }
    if (-not $ready) {
        if ($process.HasExited) {
            $startupError = $process.StandardError.ReadToEnd()
            throw "Temporary server exited before readiness: $startupError"
        }
        throw 'Temporary server did not start.'
    }

    $adminLocal = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $adminLan = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $userLan = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $jsonHeaders = @{ 'Content-Type' = 'application/json' }
    $adminCredentials = '{"username":"testadmin","displayName":"Test Admin","password":"ab"}'
    Invoke-RestMethod -Uri "$loopback/api/auth/setup" -Method Post -Body $adminCredentials -Headers $jsonHeaders -WebSession $adminLocal | Out-Null
    Invoke-RestMethod -Uri "$lan/api/auth/login" -Method Post -Body '{"username":"testadmin","password":"ab"}' -Headers $jsonHeaders -WebSession $adminLan -TimeoutSec 5 | Out-Null

    $localReports = Invoke-RestMethod -Uri "$loopback/api/report-definitions" -WebSession $adminLocal
    $lanReports = Invoke-RestMethod -Uri "$lan/api/report-definitions" -WebSession $adminLan
    $localIds = @($localReports | ForEach-Object id | Sort-Object)
    $lanIds = @($lanReports | ForEach-Object id | Sort-Object)
    if (($localIds -join ',') -ne ($lanIds -join ',') -or $localIds -notcontains 'lan-fixture') {
        throw 'Admin sees different report lists at loopback and LAN addresses.'
    }

    $account = @{ username='lanuser'; displayName='LAN User'; password='ab'; role='report-user'; reportIds=@('lan-fixture'); active=$true } | ConvertTo-Json -Compress
    Invoke-RestMethod -Uri "$loopback/api/users" -Method Post -Body $account -Headers $jsonHeaders -WebSession $adminLocal | Out-Null
    Invoke-RestMethod -Uri "$lan/api/auth/login" -Method Post -Body '{"username":"lanuser","password":"ab"}' -Headers $jsonHeaders -WebSession $userLan -TimeoutSec 5 | Out-Null
    $visible = Invoke-RestMethod -Uri "$lan/api/report-definitions" -WebSession $userLan
    if ($visible.Count -ne 1 -or $visible[0].id -ne 'lan-fixture') {
        throw 'Authorized report user did not see exactly the fixture report over the LAN address.'
    }
    Write-Output "PASS: one temporary server, loopback=$loopback, LAN=$lan, admin report IDs equal, authorized LAN user sees the fixture."
}
finally {
    if ($process -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(2000) | Out-Null
    }
    $resolvedTemp = [System.IO.Path]::GetFullPath($temporary)
    if ($resolvedTemp.StartsWith([System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $resolvedTemp -Leaf) -like 'ppbi-lan-check-*') {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
