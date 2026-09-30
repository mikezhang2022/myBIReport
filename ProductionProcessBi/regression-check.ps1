# Regression check for the single-server ProductionProcessBi deployment.
# Launches the built API on an isolated temporary content root (copies of Frontend/Admin/data),
# so no external database and no real data files are touched. Verifies routing, static assets,
# path-traversal / source non-exposure, authentication endpoints, and no connection-string disclosure.

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$results = @()

function Record($name, $ok, $detail) {
    $status = if ($ok) { 'PASS' } else { 'FAIL' }
    $script:results += [PSCustomObject]@{ Name = $name; Status = $status; Detail = $detail }
    Write-Host ("[{0}] {1} - {2}" -f $status, $name, $detail)
}

function CallApi($Method, $Url, $Body, $Session, $MaxRedirect = 5) {
    $params = @{ Method = $Method; Uri = $Url; UseBasicParsing = $true; ErrorAction = 'Stop'; MaximumRedirection = $MaxRedirect }
    if ($Session) { $params['WebSession'] = $Session }
    if ($Body) { $params['Body'] = $Body; $params['ContentType'] = 'application/json; charset=utf-8' }
    try {
        $r = Invoke-WebRequest @params
        return @{ StatusCode = [int]$r.StatusCode; Content = $r.Content; ContentType = ($r.Headers['Content-Type'] -join ',') }
    }
    catch {
        $resp = $_.Exception.Response
        if ($resp -ne $null) {
            try { $sr = New-Object System.IO.StreamReader($resp.GetResponseStream()); $bodyText = $sr.ReadToEnd() } catch { $bodyText = '' }
            return @{ StatusCode = [int]$resp.StatusCode; Content = $bodyText; ContentType = '' }
        }
        # No HTTP response (e.g. redirect loop, name resolution) - treat as not-served.
        return @{ StatusCode = 0; Content = $_.Exception.Message; ContentType = '' }
    }
}

# Locate the built assembly.
$dll = Resolve-Path (Join-Path $projectRoot 'bin/Debug/net8.0/ProductionProcessBi.dll') -ErrorAction SilentlyContinue
if (-not $dll) {
    Write-Host 'Built assembly not found; building...'
    dotnet build -v q | Out-Null
    $dll = Resolve-Path (Join-Path $projectRoot 'bin/Debug/net8.0/ProductionProcessBi.dll')
}
$dll = $dll.Path

# Pick a free loopback port.
$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = (($listener.LocalEndpoint) -as [System.Net.IPEndPoint]).Port
$listener.Stop()
$base = "http://127.0.0.1:$port"

# Isolated working directory.
$temp = Join-Path $env:TEMP ("ppbi-regress-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
Copy-Item (Join-Path $projectRoot 'Frontend') (Join-Path $temp 'Frontend') -Recurse
Copy-Item (Join-Path $projectRoot 'Admin') (Join-Path $temp 'Admin') -Recurse
Copy-Item (Join-Path $projectRoot 'data') (Join-Path $temp 'data') -Recurse
# Start with no users so the script can create its own admin (real data is never touched).
Set-Content -Path (Join-Path $temp 'data/users.json') -Value '[]' -Encoding utf8

# Seed an isolated fixture report so permission/visibility tests don't depend on production data.
# The real checkout's data/ is never modified; this only touches the temp copy that the app loads.
$fixtureDefsPath = Join-Path $temp 'data/report-definitions.json'
$fixtureDefs = Get-Content $fixtureDefsPath -Raw | ConvertFrom-Json
$fixtureDefs += [PSCustomObject]@{
    Id = 'jizhuanji-bujian'; Category = '整机'; Name = '整机查询部件信息';
    QueryType = 'standard'; Conditions = @('sn'); DisplayFields = @(); Enabled = $true;
    SqlText = "SELECT 'x' AS demo FROM dual"; ReportStyle = 'standard';
    EnableCsvExport = $true; DashboardWidgets = @(); Filters = @()
}
$fixtureDefs += [PSCustomObject]@{
    Id = 'disabled-secret'; Category = '整机'; Name = '未发布的隐藏报表';
    QueryType = 'standard'; Conditions = @(); DisplayFields = @(); Enabled = $false;
    SqlText = "SELECT 'secret' AS s FROM dual"; ReportStyle = 'standard';
    EnableCsvExport = $false; DashboardWidgets = @(); Filters = @()
}
$fixtureDefs | ConvertTo-Json -Depth 20 | Set-Content -Path $fixtureDefsPath -Encoding utf8
$logFile = Join-Path $temp 'app.log'

$proc = $null
try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo('dotnet', "$dll --urls $base --contentroot $temp")
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['DOTNET_ROLL_FORWARD'] = 'Major'
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $proc.Start() | Out-Null

    # Wait for readiness.
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        try { $s = CallApi GET "$base/api/auth/status" $null $null; if ($s.StatusCode -eq 200) { $ready = $true; break } } catch {}
    }
    if (-not $ready) {
        Write-Host "App did not become ready. Log:"; Write-Host ($proc.StandardOutput.ReadToEnd() + $proc.StandardError.ReadToEnd())
        throw 'App failed to start'
    }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

    # 1. Routing: Frontend at "/"
    $r = CallApi GET "$base/" $null $null
    Record 'routing.frontend-root' ($r.StatusCode -eq 200 -and $r.ContentType -match 'html' -and $r.Content.Contains('Production Process BI')) $r.StatusCode

    # 2. Routing: Admin at "/admin/"
    $r = CallApi GET "$base/admin/" $null $null
    $adminHtmlOk = $r.StatusCode -eq 200 -and $r.ContentType -match 'html' -and $r.Content.Contains('report-list-page') -and $r.Content.Contains('src="auth.js"') -and $r.Content.Contains('src="site.js"') -and $r.Content.Contains('src="permissions.js"') -and $r.Content.Contains('href="site.css"')
    Record 'routing.admin-root-and-relative-assets' $adminHtmlOk $r.StatusCode

    # 2b. Redirect for bare "/admin"
    $r = CallApi GET "$base/admin" $null $null 0
    Record 'routing.admin-redirect' ($r.StatusCode -ge 300 -and $r.StatusCode -lt 400) $r.StatusCode

    # 3. Static assets (Frontend)
    $r = CallApi GET "$base/auth.js" $null $null
    Record 'static.frontend-auth-js' ($r.StatusCode -eq 200 -and $r.ContentType -match 'javascript' -and $r.Content.StartsWith('(() =>')) $r.StatusCode
    $r = CallApi GET "$base/site.css" $null $null
    Record 'static.frontend-css' ($r.StatusCode -eq 200 -and $r.ContentType -match 'text/css') $r.StatusCode

    # 3b. Static assets (Admin)
    $r = CallApi GET "$base/admin/auth.js" $null $null
    Record 'static.admin-auth-js' ($r.StatusCode -eq 200 -and $r.ContentType -match 'javascript' -and $r.Content.StartsWith('(() =>') -and $r.Content.Contains('const roles =')) $r.StatusCode
    $r = CallApi GET "$base/admin/site.css" $null $null
    Record 'static.admin-css' ($r.StatusCode -eq 200 -and $r.ContentType -match 'text/css') $r.StatusCode
    $r = CallApi GET "$base/admin/site.js" $null $null
    Record 'static.admin-site-js' ($r.StatusCode -eq 200 -and $r.ContentType -match 'javascript' -and $r.Content.Contains('setupDataSources')) $r.StatusCode

    # 3c. api-config.js served (frontend asset)
    $r = CallApi GET "$base/api-config.js" $null $null
    Record 'static.api-config-js' ($r.StatusCode -eq 200 -and $r.Content.Contains('PROCESS_BI_API_BASE')) $r.StatusCode

    # 3d. Admin permissions.js must derive the API origin from the page (same origin /
    # PROCESS_BI_API_BASE) and must NOT hard-code a fixed host:port (the defect that broke the
    # admin console on LAN hosts/ports). This is the "across origins/hosts" frontend guard.
    $r = CallApi GET "$base/admin/permissions.js" $null $null
    $permSrc = $r.Content
    $permOk = $r.StatusCode -eq 200 -and $permSrc.Contains('/api') -and ($permSrc.Contains('window.PROCESS_BI_API_BASE') -or $permSrc.Contains('window.location.origin')) -and -not ($permSrc.Contains('5095'))
    Record 'static.admin-permissions-no-hardcoded-origin' $permOk $r.StatusCode

    # 3e. Frontend loads the report list only after auth resolves (listens for the auth-ready
    # event and retries on 401) instead of firing once and never recovering.
    $r = CallApi GET "$base/auth.js" $null $null
    Record 'static.frontend-auth-dispatches-ready' ($r.StatusCode -eq 200 -and $r.Content.Contains("'process-bi-auth-ready'")) $r.StatusCode
    $r = CallApi GET "$base/site.js" $null $null
    Record 'static.frontend-post-auth-load' ($r.StatusCode -eq 200 -and $r.Content.Contains("'process-bi-auth-ready'") -and $r.Content.Contains('ensureDefinitions')) $r.StatusCode

    # 4. No source/config/data exposure
    foreach ($path in @('/server.py', '/README.md', '/admin/start-admin.ps1', '/Program.cs', '/data/users.json', '/%2e%2e/Program.cs')) {
        $r = CallApi GET ($base + $path) $null $null 0
        Record "exposure.blocked[$path]" ($r.StatusCode -ne 200) $r.StatusCode
    }

    # 5. Auth endpoints
    $r = CallApi GET "$base/api/auth/status" $null $null
    $ok = $false; $detail = $r.StatusCode
    if ($r.StatusCode -eq 200) {
        try { $json = $r.Content | ConvertFrom-Json; $ok = $json.PSObject.Properties.Name -contains 'setupRequired' } catch {}
    }
    Record 'auth.status-endpoint' $ok $detail

    $r = CallApi POST "$base/api/auth/login" '{"username":"nope","password":"wrong-password-123"}' $null
    Record 'auth.login-rejected' ($r.StatusCode -eq 401) $r.StatusCode

    $r = CallApi GET "$base/api/users" $null $null
    Record 'auth.protected-requires-auth' ($r.StatusCode -eq 401) $r.StatusCode

    $r = CallApi GET "$base/api/summary" $null $null
    Record 'auth.summary-requires-auth' ($r.StatusCode -eq 401) $r.StatusCode

    # 6. No connection-string disclosure (needs admin)
    # Two characters is the temporary minimum accepted by both UI and API.
    $pw = 'ab'
    $r = CallApi POST "$base/api/auth/setup" ("{`"username`":`"testadmin`",`"displayName`":`"Test Admin`",`"password`":`"$pw`"}") $null
    Record 'auth.setup-creates-admin' ($r.StatusCode -eq 200 -and -not $r.Content.Contains('PasswordHash') -and -not $r.Content.Contains('PasswordSalt')) $r.StatusCode

    $r = CallApi POST "$base/api/auth/login" ("{`"username`":`"testadmin`",`"password`":`"$pw`"}") $session
    Record 'auth.login-as-admin' ($r.StatusCode -eq 200) $r.StatusCode

    $r = CallApi GET "$base/api/data-sources" $null $session
    $leak = $r.Content.Contains('"connectionString"')
    $hasDisplay = $r.Content.Contains('"display"')
    Record 'auth.no-connectionstring-disclosure' ($r.StatusCode -eq 200 -and -not $leak -and $hasDisplay) ("status=$($r.StatusCode) connectionStringField=$leak displayField=$hasDisplay")

    $r = CallApi GET "$base/api/users" $null $session
    Record 'auth.admin-lists-users' ($r.StatusCode -eq 200) $r.StatusCode

    # 7. Report visibility / role-based authorization (isolated fixture: 整机查询部件信息)
    # Create an authorized report-user and an unassigned report-user (min password kept at 2).
    $body = @{ username='authorized'; displayName='授权用户'; password='ab'; role='report-user'; reportIds=@('jizhuanji-bujian'); active=$true } | ConvertTo-Json -Compress
    $r = CallApi POST "$base/api/users" $body $session
    Record 'users.create-authorized' ($r.StatusCode -eq 201) $r.StatusCode
    $body = @{ username='unassigned'; displayName='未授权用户'; password='ab'; role='report-user'; reportIds=@(); active=$true } | ConvertTo-Json -Compress
    $r = CallApi POST "$base/api/users" $body $session
    Record 'users.create-unassigned' ($r.StatusCode -eq 201) $r.StatusCode

    $authSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $r = CallApi POST "$base/api/auth/login" '{"username":"authorized","password":"ab"}' $authSession
    Record 'auth.login-authorized' ($r.StatusCode -eq 200) $r.StatusCode
    $unSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $r = CallApi POST "$base/api/auth/login" '{"username":"unassigned","password":"ab"}' $unSession
    Record 'auth.login-unassigned' ($r.StatusCode -eq 200) $r.StatusCode

    # Admin (CanManageReports) sees every definition, including the disabled fixture.
    $r = CallApi GET "$base/api/report-definitions" $null $session
    $adminJson = $r.Content | ConvertFrom-Json
    $adminNames = ($adminJson | ForEach-Object { $_.name }) -join ','
    Record 'perms.admin-sees-fixture' (($adminJson | Where-Object { $_.name -eq '整机查询部件信息' }) -and ($adminJson | Where-Object { $_.name -eq '未发布的隐藏报表' })) $adminNames

    # Authorized report-user sees the enabled fixture but not the disabled one.
    $r = CallApi GET "$base/api/report-definitions" $null $authSession
    $authJson = $r.Content | ConvertFrom-Json
    $authNames = ($authJson | ForEach-Object { $_.name }) -join ','
    Record 'perms.authorized-sees-fixture-only' (($authJson | Where-Object { $_.name -eq '整机查询部件信息' }) -and -not ($authJson | Where-Object { $_.name -eq '未发布的隐藏报表' })) $authNames

    # No SQL leakage: the report-user view must not contain the raw SqlText.
    Record 'perms.no-sql-leak' (-not $r.Content.Contains('FROM dual')) 'report-user response omits SqlText'

    # Unassigned report-user sees neither fixture (filtered by Enabled and ReportIds).
    $r = CallApi GET "$base/api/report-definitions" $null $unSession
    $unJson = $r.Content | ConvertFrom-Json
    Record 'perms.unassigned-sees-nothing' (($unJson | Measure-Object).Count -eq 0) (($unJson | ForEach-Object { $_.name }) -join ',')

    # No unauthorized query leakage: unassigned user querying the fixture must 404 and must not
    # reveal the hidden report name in the response.
    $r = CallApi GET "$base/api/reports/jizhuanji-bujian/query" $null $unSession
    $leakName = $r.Content.Contains('整机查询部件信息')
    Record 'perms.unassigned-query-blocked' ($r.StatusCode -eq 404 -and -not $leakName) ("status=$($r.StatusCode) nameLeak=$leakName")

    # Report-user cannot manage reports (create is forbidden).
    $r = CallApi POST "$base/api/report-definitions" '{"id":"x","name":"x","queryType":"standard"}' $unSession
    Record 'perms.unassigned-cannot-manage' ($r.StatusCode -eq 403) $r.StatusCode

    # Visibility must not depend on the Host header (LAN clients may reach the server via a
    # different hostname/IP); the same authorized user must get identical results.
    $rSame = CallApi GET "$base/api/report-definitions" $null $authSession
    try {
        $params = @{ Method='GET'; Uri="$base/api/report-definitions"; UseBasicParsing=$true; ErrorAction='Stop'; WebSession=$authSession; Headers=@{ Host='bi.example.local' } }
        $rAlt = Invoke-WebRequest @params
        $hostAgnostic = $rSame.Content -eq $rAlt.Content
    } catch { $hostAgnostic = $false }
    Record 'perms.host-agnostic-visibility' $hostAgnostic 'same result across Host headers'

    $failed = ($results | Where-Object { $_.Status -eq 'FAIL' }).Count
    Write-Host ''
    Write-Host "TOTAL: $($results.Count)  PASS: $(($results | Where-Object { $_.Status -eq 'PASS' }).Count)  FAIL: $failed"
    exit $failed
}
finally {
    if ($proc -and -not $proc.HasExited) { try { $proc.Kill() } catch {} try { $proc.WaitForExit(2000) } catch {} }
    if (Test-Path $temp) { try { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue } catch {} }
}
