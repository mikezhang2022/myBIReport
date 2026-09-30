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

    $failed = ($results | Where-Object { $_.Status -eq 'FAIL' }).Count
    Write-Host ''
    Write-Host "TOTAL: $($results.Count)  PASS: $(($results | Where-Object { $_.Status -eq 'PASS' }).Count)  FAIL: $failed"
    exit $failed
}
finally {
    if ($proc -and -not $proc.HasExited) { try { $proc.Kill() } catch {} try { $proc.WaitForExit(2000) } catch {} }
    if (Test-Path $temp) { try { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue } catch {} }
}
