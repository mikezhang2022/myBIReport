using System.Text.Json;
using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using BiDataAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host.StartsWith("10.") || uri.Host.StartsWith("192.168.") || uri.Host.StartsWith("172.16.") || uri.Host.StartsWith("172.17.") || uri.Host.StartsWith("172.18.") || uri.Host.StartsWith("172.19.") || uri.Host.StartsWith("172.2") || uri.Host.StartsWith("172.30.") || uri.Host.StartsWith("172.31.")))
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ProductionProcessBi.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return context.Response.WriteAsJsonAsync(new { message = "登录状态已失效，请重新登录。" });
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new { message = "当前账号没有执行此操作的权限。" });
        };
    });
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
var dataProtection = builder.Services.AddDataProtection().PersistKeysToFileSystem(
    new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "data", "auth-keys")));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();
var app = builder.Build();

app.UseCors();
app.UseAuthentication();

app.MapGet("/", () => Results.Ok(new
{
    service = "ProductionProcessBi API",
    status = "running",
    adminUi = "http://127.0.0.1:5174",
    endpoints = new[]
    {
        "/api/report-definitions",
        "/api/reports/product-trace?sn={SN}",
        "/api/reports/capacity?startDate=yyyy-MM-dd&endDate=yyyy-MM-dd"
    }
})).AllowAnonymous();

var dataPath = Path.Combine(app.Environment.ContentRootPath, "data", "process-records.json");
var sourceSettingsPath = Path.Combine(app.Environment.ContentRootPath, "data", "data-source.json");
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var definitions = new ReportDefinitionStore(Path.Combine(app.Environment.ContentRootPath, "data", "report-definitions.json"), options);
var dataSources = new DataSourceStore(Path.Combine(app.Environment.ContentRootPath, "data", "data-sources.json"), sourceSettingsPath, options);
var users = new UserStore(Path.Combine(app.Environment.ContentRootPath, "data", "users.json"), options);
definitions.EnsureSqlText();

app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var account = userId is null ? null : users.FindById(userId);
        var roleClaim = context.User.FindFirstValue(ClaimTypes.Role);
        if (account is null || !account.Active || !string.Equals(roleClaim, account.Role, StringComparison.Ordinal))
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }
    }
    await next();
});
app.UseAuthorization();

app.MapGet("/api/auth/status", () => Results.Ok(new { setupRequired = users.GetAll().Count == 0 })).AllowAnonymous();
app.MapPost("/api/auth/setup", async (InitialAdminRequest request, HttpContext context) =>
{
    if (!context.Request.IsHttps && (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)))
        return Results.BadRequest(new { message = "请在服务器本机完成首次管理员初始化，或先为服务启用 HTTPS。" });
    var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.Username : request.DisplayName;
    var validation = ValidateNewUser(request.Username, displayName, request.Password, RoleNames.SystemAdmin);
    if (validation is not null) return Results.BadRequest(new { message = validation });
    var account = users.CreateFirstAdmin(request.Username!, displayName!, request.Password!);
    if (account is null) return Results.Conflict(new { message = "管理员已创建，请使用登录页面登录。" });
    await SignIn(context, account);
    return Results.Ok(ToPublicUser(account));
}).AllowAnonymous();
app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context) =>
{
    if (!context.Request.IsHttps && (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)))
        return Results.BadRequest(new { message = "跨电脑登录前请先为服务启用 HTTPS，避免密码在网络中明文传输。" });
    var account = users.Authenticate(request.Username, request.Password);
    if (account is null) return Results.Unauthorized();
    await SignIn(context, account);
    return Results.Ok(ToPublicUser(account));
}).AllowAnonymous();
app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "已退出登录。" });
});
app.MapGet("/api/auth/me", (HttpContext context) =>
{
    var account = CurrentUser(context, users);
    return account is null ? Results.Unauthorized() : Results.Ok(ToPublicUser(account));
});

app.MapGet("/api/users", (HttpContext context) =>
{
    if (!IsSystemAdmin(context.User)) return Results.Forbid();
    return Results.Ok(users.GetAll().Select(ToPublicUser));
});
app.MapPost("/api/users", (UserUpsertRequest request, HttpContext context) =>
{
    if (!IsSystemAdmin(context.User)) return Results.Forbid();
    var validation = ValidateNewUser(request.Username, request.DisplayName, request.Password, request.Role);
    if (validation is not null) return Results.BadRequest(new { message = validation });
    try
    {
        var account = users.Create(request.Username!, request.DisplayName!, request.Password!, request.Role!, request.ReportIds ?? []);
        return Results.Created($"/api/users/{account.Id}", ToPublicUser(account));
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
});
app.MapPut("/api/users/{id}", (string id, UserUpsertRequest request, HttpContext context) =>
{
    if (!IsSystemAdmin(context.User)) return Results.Forbid();
    var current = CurrentUser(context, users);
    var validation = ValidateNewUser(request.Username, request.DisplayName, request.Password, request.Role, allowEmptyPassword: true);
    if (validation is not null) return Results.BadRequest(new { message = validation });
    if (current?.Id == id && (!request.Active || request.Role != RoleNames.SystemAdmin))
        return Results.BadRequest(new { message = "不能停用或降级当前登录的最后一道管理员权限，请先使用其他管理员账号。" });
    try
    {
        var account = users.Update(id, request.Username!, request.DisplayName!, request.Password, request.Role!, request.ReportIds ?? [], request.Active);
        return account is null ? Results.NotFound(new { message = "用户不存在。" }) : Results.Ok(ToPublicUser(account));
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
});
app.MapDelete("/api/users/{id}", (string id, HttpContext context) =>
{
    if (!IsSystemAdmin(context.User)) return Results.Forbid();
    if (CurrentUser(context, users)?.Id == id) return Results.BadRequest(new { message = "不能删除当前登录账号。" });
    try { return users.Delete(id) ? Results.NoContent() : Results.NotFound(new { message = "用户不存在。" }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
});

IReadOnlyList<ProcessRecord> ReadRecords()
{
    var sqlitePath = ReadSqlitePath();
    if (!string.IsNullOrWhiteSpace(sqlitePath) && File.Exists(sqlitePath))
    {
        var client = new DatabaseClient(DatabaseProvider.Sqlite, $"Data Source={sqlitePath};Mode=ReadOnly;");
        return client.QueryAsync("""
            SELECT PRO_SN, PRO_QTY, MODEL_CODE, PROJECT_NO, MO_NUMBER, WORK_STATIONNAME,
                   GROUP_NAME, GROUP_SEQ, IN_TIME, ERROR_FLAG, IS_SCRAP, IS_REWORK,
                   ROUTE_END, NG_QTY, SCRAP_QTY
            FROM t_co_detail
            """, reader => new ProcessRecord(
                ReadRequiredText(reader, "PRO_SN"),
                ReadNumber(reader, "PRO_QTY"),
                ReadText(reader, "MODEL_CODE"),
                ReadText(reader, "PROJECT_NO"),
                ReadText(reader, "MO_NUMBER"),
                ReadText(reader, "WORK_STATIONNAME"),
                ReadText(reader, "GROUP_NAME"),
                ReadNullableNumber(reader, "GROUP_SEQ"),
                DateTime.Parse(ReadRequiredText(reader, "IN_TIME"), CultureInfo.InvariantCulture),
                ReadText(reader, "ERROR_FLAG"),
                ReadText(reader, "IS_SCRAP"),
                ReadText(reader, "IS_REWORK"),
                ReadText(reader, "ROUTE_END"),
                ReadNumber(reader, "NG_QTY"),
                ReadNumber(reader, "SCRAP_QTY"))).GetAwaiter().GetResult();
    }

    if (!File.Exists(dataPath))
        return [];
    return JsonSerializer.Deserialize<List<ProcessRecord>>(File.ReadAllText(dataPath), options) ?? [];
}

string? ReadSqlitePath()
{
    var active = dataSources.GetActive();
    if (active?.Provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase) == true)
        return active.ConnectionString.Replace("Data Source=", "", StringComparison.OrdinalIgnoreCase).Split(';')[0].Trim();
    var environmentPath = Environment.GetEnvironmentVariable("PROCESS_BI_SQLITE_PATH");
    if (!string.IsNullOrWhiteSpace(environmentPath)) return environmentPath;
    if (!File.Exists(sourceSettingsPath)) return null;
    var settings = JsonSerializer.Deserialize<DataSourceSettings>(File.ReadAllText(sourceSettingsPath), options);
    return settings?.Provider?.Equals("sqlite", StringComparison.OrdinalIgnoreCase) == true ? settings.FilePath : null;
}

DatabaseClient CreateClient(DataSourceDefinition source) => new(source.Provider.ToLowerInvariant() switch
{
    "sqlite" => DatabaseProvider.Sqlite,
    "sqlserver" => DatabaseProvider.SqlServer,
    "oracle" => DatabaseProvider.Oracle,
    _ => throw new InvalidOperationException("不支持的数据源类型。")
}, source.ConnectionString);

static string? ReadText(System.Data.Common.DbDataReader reader, string column) =>
    reader[column] is DBNull ? null : Convert.ToString(reader[column]);
static string ReadRequiredText(System.Data.Common.DbDataReader reader, string column) =>
    ReadText(reader, column) ?? throw new InvalidOperationException($"字段 {column} 为空。");
static int ReadNumber(System.Data.Common.DbDataReader reader, string column) =>
    reader[column] is DBNull ? 0 : Convert.ToInt32(reader[column], CultureInfo.InvariantCulture);
static int? ReadNullableNumber(System.Data.Common.DbDataReader reader, string column) =>
    reader[column] is DBNull ? null : Convert.ToInt32(reader[column], CultureInfo.InvariantCulture);

app.MapGet("/api/summary", (HttpContext context) =>
{
    if (!CanManageReports(context.User)) return Results.Forbid();
    var records = ReadRecords();
    var serials = records.GroupBy(x => x.ProSn).ToList();
    return Results.Ok(new
    {
        records = records.Count,
        serials = serials.Count,
        completedSerials = serials.Count(group => group.Any(x => x.RouteEnd == "Y")),
        pendingSerials = serials.Count(group => group.All(x => x.RouteEnd != "Y")),
        errorRecords = records.Count(x => x.ErrorFlag == "1"),
        ngQuantity = records.Sum(x => x.NgQuantity),
        scrapQuantity = records.Sum(x => x.ScrapQuantity),
        workOrders = records.Select(x => x.MoNumber).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Order().ToArray(),
        modelCodes = records.Select(x => x.ModelCode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Order().ToArray(),
        startTime = records.MinBy(x => x.InTime)?.InTime,
        endTime = records.MaxBy(x => x.InTime)?.InTime
    });
});

app.MapGet("/api/daily", (HttpContext context) => !CanManageReports(context.User) ? Results.Forbid() : Results.Ok(ReadRecords()
    .GroupBy(x => DateOnly.FromDateTime(x.InTime))
    .OrderBy(group => group.Key)
    .Select(group => new
    {
        date = group.Key,
        records = group.Count(),
        serials = group.Select(x => x.ProSn).Distinct().Count(),
        completedSerials = group.Where(x => x.RouteEnd == "Y").Select(x => x.ProSn).Distinct().Count(),
        errors = group.Count(x => x.ErrorFlag == "1"),
        ngQuantity = group.Sum(x => x.NgQuantity)
    })));

app.MapGet("/api/stations", (HttpContext context) => !CanManageReports(context.User) ? Results.Forbid() : Results.Ok(ReadRecords()
    .GroupBy(x => string.IsNullOrWhiteSpace(x.GroupName) ? x.WorkStationName : x.GroupName)
    .Select(group => new
    {
        station = group.Key,
        records = group.Count(),
        serials = group.Select(x => x.ProSn).Distinct().Count(),
        errors = group.Count(x => x.ErrorFlag == "1"),
        ngQuantity = group.Sum(x => x.NgQuantity)
    })
    .OrderByDescending(x => x.ngQuantity)
    .ThenByDescending(x => x.errors)
    .Take(12)));

app.MapGet("/api/serials", (HttpContext context) => !CanManageReports(context.User) ? Results.Forbid() : Results.Ok(ReadRecords()
    .GroupBy(x => x.ProSn)
    .OrderBy(group => group.Key)
    .Select(group => new
    {
        serial = group.Key,
        status = group.Any(x => x.RouteEnd == "Y") ? "已完成" : "进行中",
        lastTime = group.Max(x => x.InTime),
        errors = group.Count(x => x.ErrorFlag == "1"),
        ngQuantity = group.Sum(x => x.NgQuantity)
    })));

app.MapGet("/api/serials/{serial}", (string serial, HttpContext context) =>
{
    if (!CanReadQueryType(context.User, "product-trace", definitions, users)) return Results.Forbid();
    var records = ReadRecords().Where(x => string.Equals(x.ProSn, serial, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.InTime).ToList();
    return records.Count == 0 ? Results.NotFound() : Results.Ok(records);
});

app.MapGet("/api/reports/product-trace", (string? sn, HttpContext context) =>
{
    if (!CanReadQueryType(context.User, "product-trace", definitions, users)) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(sn))
        return Results.BadRequest(new { message = "请输入 SN。" });

    var normalizedSn = sn.Trim();
    var records = ReadRecords()
        .Where(x => string.Equals(x.ProSn, normalizedSn, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.InTime)
        .ToList();

    if (records.Count == 0)
        return Results.NotFound(new { message = $"未找到 SN：{normalizedSn}" });

    var first = records[0];
    return Results.Ok(new
    {
        product = new
        {
            sn = first.ProSn,
            modelCode = first.ModelCode,
            projectNo = first.ProjectNo,
            moNumber = first.MoNumber,
            quantity = first.ProQty,
            firstInTime = records[0].InTime,
            lastInTime = records[^1].InTime,
            status = records.Any(x => x.RouteEnd == "Y") ? "已完成" : "进行中",
            routeEnd = records.Any(x => x.RouteEnd == "Y"),
            errorRecords = records.Count(x => x.ErrorFlag == "1"),
            ngQuantity = records.Sum(x => x.NgQuantity),
            scrapQuantity = records.Sum(x => x.ScrapQuantity),
            reworkRecords = records.Count(x => x.IsRework == "Y")
        },
        records = records.Select(x => new
        {
            sequence = x.GroupSequence,
            process = x.GroupName,
            station = x.WorkStationName,
            inTime = x.InTime,
            error = x.ErrorFlag == "1",
            ngQuantity = x.NgQuantity,
            scrap = x.IsScrap == "Y" || x.ScrapQuantity > 0,
            rework = x.IsRework == "Y",
            routeEnd = x.RouteEnd == "Y"
        })
    });
});

app.MapGet("/api/reports/capacity", (DateOnly? startDate, DateOnly? endDate, HttpContext context) =>
{
    if (!CanReadQueryType(context.User, "capacity", definitions, users)) return Results.Forbid();
    var records = ReadRecords();
    if (records.Count == 0)
        return Results.Ok(new { rows = Array.Empty<object>() });

    var firstDate = DateOnly.FromDateTime(records.MinBy(x => x.InTime)!.InTime);
    var lastDate = DateOnly.FromDateTime(records.MaxBy(x => x.InTime)!.InTime);
    var start = startDate ?? firstDate;
    var end = endDate ?? lastDate;
    if (start > end)
        return Results.BadRequest(new { message = "开始日期不能晚于结束日期。" });

    var selected = records.Where(x => DateOnly.FromDateTime(x.InTime) >= start && DateOnly.FromDateTime(x.InTime) <= end).ToList();
    var daily = selected.GroupBy(x => DateOnly.FromDateTime(x.InTime)).OrderBy(x => x.Key).Select(group => new
    {
        date = group.Key,
        passRecords = group.Count(),
        serials = group.Select(x => x.ProSn).Distinct().Count(),
        completedSerials = group.Where(x => x.RouteEnd == "Y").Select(x => x.ProSn).Distinct().Count(),
        errorRecords = group.Count(x => x.ErrorFlag == "1"),
        ngQuantity = group.Sum(x => x.NgQuantity)
    }).ToList();

    return Results.Ok(new
    {
        startDate = start,
        endDate = end,
        summary = new
        {
            passRecords = selected.Count,
            serials = selected.Select(x => x.ProSn).Distinct().Count(),
            completedSerials = selected.Where(x => x.RouteEnd == "Y").Select(x => x.ProSn).Distinct().Count(),
            errorRecords = selected.Count(x => x.ErrorFlag == "1"),
            ngQuantity = selected.Sum(x => x.NgQuantity),
            activeDays = daily.Count,
            averageCompletedPerActiveDay = daily.Count == 0 ? 0 : Math.Round((decimal)daily.Sum(x => x.completedSerials) / daily.Count, 2)
        },
        rows = daily
    });
});

app.MapGet("/api/report-definitions", (HttpContext context) =>
{
    var all = definitions.GetAll();
    if (CanManageReports(context.User)) return Results.Ok(all);
    var visible = all.Where(x => x.Enabled && CanReadReport(context.User, x.Id, users))
        .Select(ToReportView)
        .ToList();
    return Results.Ok(visible);
});
app.MapGet("/api/data-sources", (HttpContext context) =>
    IsSystemAdmin(context.User) ? Results.Ok(dataSources.GetAll()) : Results.Forbid());
app.MapPost("/api/data-sources", (DataSourceDefinition source, HttpContext context) =>
{
    if (!IsSystemAdmin(context.User)) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.ConnectionString)) return Results.BadRequest(new { message = "请填写数据源名称和连接信息。" });
    if (source.Provider.ToLowerInvariant() is not ("sqlite" or "sqlserver" or "oracle")) return Results.BadRequest(new { message = "请选择 SQLite、SQL Server 或 Oracle。" });
    var saved = source with { Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString("N") : source.Id, ConnectionString = source.ConnectionString.Trim() };
    dataSources.Save(saved);
    return Results.Ok(saved);
});
app.MapPost("/api/data-sources/{id}/test", async (string id, HttpContext context) =>
{
    if (!IsSystemAdmin(context.User)) return Results.Forbid();
    var source = dataSources.GetAll().FirstOrDefault(x => x.Id == id); if (source is null) return Results.NotFound();
    try { var sql = source.Provider.Equals("oracle", StringComparison.OrdinalIgnoreCase) ? "SELECT 1 FROM DUAL" : "SELECT 1"; await CreateClient(source).ScalarAsync<int>(sql); return Results.Ok(new { message = "连接成功。" }); }
    catch (Exception ex) { return Results.BadRequest(new { message = $"连接失败：{ex.Message}" }); }
});
app.MapPost("/api/data-sources/{id}/activate", (string id, HttpContext context) =>
    !IsSystemAdmin(context.User) ? Results.Forbid() : dataSources.Activate(id) ? Results.Ok(new { message = "已设为当前数据源。" }) : Results.NotFound());
app.MapPost("/api/report-definitions/validate-sql", async (SqlValidationRequest request, HttpContext context) =>
{
    if (!CanManageReports(context.User)) return Results.Forbid();
    var sql = request.SqlText?.Trim() ?? string.Empty;
    if (!IsReadOnlySelect(sql)) return Results.BadRequest(new { message = "只允许编写单条 SELECT 查询，不允许包含分号或数据修改语句。" });
    var source = dataSources.GetActive();
    if (source is null)
        return Results.BadRequest(new { message = "请先在数据源管理中设置当前数据源。" });

    try
    {
        var client = CreateClient(source);
        var parameterNames = System.Text.RegularExpressions.Regex.Matches(sql, "[@:]([A-Za-z_][A-Za-z0-9_]*)")
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parameters = parameterNames.ToDictionary(name => name, name => (object?)string.Empty, StringComparer.OrdinalIgnoreCase);
        var columns = await client.GetColumnsAsync(sql, parameters);
        return Results.Ok(new { message = "SQL 校验通过。", columns, parameters = parameterNames });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"SQL 校验失败：{ex.Message}" });
    }
});
app.MapPost("/api/report-definitions/preview-sql", async (SqlPreviewRequest request, HttpContext context) =>
{
    if (!CanManageReports(context.User)) return Results.Forbid();
    var sql = request.SqlText?.Trim() ?? string.Empty;
    if (!IsReadOnlySelect(sql)) return Results.BadRequest(new { message = "只允许试运行单条只读 SELECT 查询。" });
    var source = dataSources.GetActive();
    if (source is null) return Results.BadRequest(new { message = "请先在数据源管理中设置当前数据源。" });

    var parameterNames = System.Text.RegularExpressions.Regex.Matches(sql, "[@:]([A-Za-z_][A-Za-z0-9_]*)")
        .Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var previewParameters = parameterNames.ToDictionary(name => name, name => (object?)(request.Parameters?.GetValueOrDefault(name) ?? string.Empty), StringComparer.OrdinalIgnoreCase);
    var parameters = new Dictionary<string, object?>(previewParameters, StringComparer.OrdinalIgnoreCase);
    parameters["biPageLimit"] = 50;
    parameters["biPageOffset"] = 0;
    var client = CreateClient(source);
    var previewSql = BuildPagedSql(sql, client.Provider);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

    try
    {
        var stopwatch = Stopwatch.StartNew();
        var columns = (await client.GetColumnsAsync(previewSql, parameters, timeout.Token)).Where(column => !column.Equals("bi_rownum", StringComparison.OrdinalIgnoreCase)).ToArray();
        var rows = await client.QueryAsync(previewSql, reader =>
        {
            var row = new Dictionary<string, object?>();
            foreach (var column in columns) row[column] = reader[column] is DBNull ? null : reader[column];
            return row;
        }, parameters, timeout.Token);
        stopwatch.Stop();
        IReadOnlyList<string> plan = [];
        string? planMessage = null;
        try { plan = await client.ExplainAsync(sql, previewParameters, timeout.Token); }
        catch (Exception ex) { planMessage = $"无法读取执行计划：{ex.Message}"; }
        var bindings = parameters.Select(pair => new { name = pair.Key, value = Convert.ToString(pair.Value) ?? string.Empty });
        return Results.Ok(new { message = $"试运行完成，返回 {rows.Count} 条（最多 50 条）。", elapsedMs = stopwatch.ElapsedMilliseconds, columns, rows, plan, planMessage, executedSql = client.PrepareSql(previewSql), bindings });
    }
    catch (OperationCanceledException) { return Results.BadRequest(new { message = "试运行超时（10 秒），请收紧查询条件或优化 SQL。" }); }
    catch (Exception ex) { return Results.BadRequest(new { message = $"试运行失败：{ex.Message}" }); }
});
app.MapPost("/api/report-definitions", (ReportDefinition definition, HttpContext context) =>
{
    if (!CanManageReports(context.User)) return Results.Forbid();
    var validation = ValidateDefinition(definition);
    if (validation is not null) return Results.BadRequest(new { message = validation });
    if (definitions.GetAll().Any(x => x.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new { message = "报表标识已存在。" });
    definitions.Save(definition);
    return Results.Created($"/api/report-definitions/{definition.Id}", definition);
});
app.MapPut("/api/report-definitions/{id}", (string id, ReportDefinition definition, HttpContext context) =>
{
    if (!CanManageReports(context.User)) return Results.Forbid();
    if (!id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = "报表标识不能修改。" });
    var validation = ValidateDefinition(definition);
    if (validation is not null) return Results.BadRequest(new { message = validation });
    if (!definitions.GetAll().Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        return Results.NotFound(new { message = "报表不存在。" });
    definitions.Save(definition);
    return Results.Ok(definition);
});
app.MapDelete("/api/report-definitions/{id}", (string id, HttpContext context) =>
{
    if (!CanManageReports(context.User)) return Results.Forbid();
    if (!definitions.Delete(id)) return Results.NotFound(new { message = "报表不存在。" });
    return Results.NoContent();
});
app.MapGet("/api/reports/{id}/query", async (string id, HttpRequest request) =>
{
    var definition = definitions.GetAll().FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && x.Enabled);
    if (definition is null) return Results.NotFound(new { message = "未找到可用报表。" });
    if (!CanReadReport(request.HttpContext.User, definition.Id, users)) return Results.NotFound(new { message = "未找到可用报表或当前账号无权查看。" });
    if (!IsReadOnlySelect(definition.SqlText)) return Results.BadRequest(new { message = "报表 SQL 无效。" });
    var source = dataSources.GetActive();
    if (source is null) return Results.BadRequest(new { message = "请先在数据源管理中设置当前数据源。" });
    // 查询条件是可选的：空值对应的 AND 条件会从 SQL 中移除，而不是按空字符串过滤。
    // 例如：WHERE t.ERROR_FLAG = @errorFlag AND t.PRO_SN = @sn，未传 errorFlag 时只保留 SN 条件。
    var executableSql = RemoveEmptyOptionalConditions(definition.SqlText, request.Query);
    var names = System.Text.RegularExpressions.Regex.Matches(executableSql, "[@:]([A-Za-z_][A-Za-z0-9_]*)")
        .Select(x => x.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var page = int.TryParse(request.Query["page"], out var requestedPage) ? Math.Max(1, requestedPage) : 1;
    var pageSize = int.TryParse(request.Query["pageSize"], out var requestedSize) ? Math.Clamp(requestedSize, 1, 100) : 50;
    var parameters = names.ToDictionary(name => name, name => (object?)(request.Query[name].FirstOrDefault() ?? string.Empty), StringComparer.OrdinalIgnoreCase);
    var countParameters = new Dictionary<string, object?>(parameters, StringComparer.OrdinalIgnoreCase);
    // 多取一行只用于判断是否存在下一页，任何一次查询最多从数据库读 101 行。
    parameters["biPageLimit"] = pageSize + 1;
    parameters["biPageOffset"] = checked((page - 1) * pageSize);
    var client = CreateClient(source);
    var pagedSql = BuildPagedSql(executableSql, client.Provider);
    var columns = (await client.GetColumnsAsync(pagedSql, parameters)).Where(column => !column.Equals("bi_rownum", StringComparison.OrdinalIgnoreCase)).ToArray();
    var visible = definition.DisplayFields.Where(columns.Contains).ToArray();
    if (visible.Length == 0) visible = columns.ToArray();
    var rows = await client.QueryAsync(pagedSql, reader =>
    {
        var row = new Dictionary<string, object?>();
        foreach (var column in visible) row[column] = reader[column] is DBNull ? null : reader[column];
        return row;
    }, parameters);
    var hasMore = rows.Count > pageSize;
    if (hasMore) rows = rows.Take(pageSize).ToList();
    long? totalRows = null;
    long? totalPages = null;
    string? countMessage = null;
    try
    {
        totalRows = await client.ScalarAsync<long>(BuildCountSql(executableSql, client.Provider), countParameters);
        totalPages = Math.Max(1, (totalRows.Value + pageSize - 1) / pageSize);
    }
    catch (Exception ex) { countMessage = $"总记录数统计失败：{ex.Message}"; }
    if (CanManageReports(request.HttpContext.User))
        return Results.Ok(new { columns = visible, rows, page, pageSize, hasMore, totalRows, totalPages, countMessage, executedSql = client.PrepareSql(pagedSql) });
    return Results.Ok(new { columns = visible, rows, page, pageSize, hasMore, totalRows, totalPages, countMessage });
});
app.MapGet("/api/reports/{id}/filter-options/{name}", async (string id, string name, HttpContext context) =>
{
    if (!CanReadReport(context.User, id, users)) return Results.NotFound();
    var filter = definitions.GetAll().FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Filters?.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (filter?.ControlType != "select" || !IsReadOnlySelect(filter.OptionsSql)) return Results.NotFound();
    var source = dataSources.GetActive(); if (source is null) return Results.BadRequest(new { message = "请先设置当前数据源。" });
    try
    {
        var rows = await CreateClient(source).QueryAsync(filter.OptionsSql!, reader => new { value = reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0)), label = reader.FieldCount > 1 && !reader.IsDBNull(1) ? Convert.ToString(reader.GetValue(1)) : Convert.ToString(reader.GetValue(0)) });
        return Results.Ok(rows);
    }
    catch (Exception ex) { return Results.BadRequest(new { message = $"读取下拉选项失败：{ex.Message}" }); }
});

app.Run();

static async Task SignIn(HttpContext context, UserAccount account)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, account.Id),
        new(ClaimTypes.Name, account.Username),
        new(ClaimTypes.Role, account.Role),
        new("display_name", account.DisplayName)
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
    {
        IsPersistent = true,
        AllowRefresh = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
    });
}

static PublicUser ToPublicUser(UserAccount account) => new(account.Id, account.Username, account.DisplayName, account.Role, account.ReportIds, account.Active);
static ReportDefinitionView ToReportView(ReportDefinition definition) => new(
    definition.Id, definition.Category, definition.Name, definition.QueryType, definition.Conditions, definition.DisplayFields,
    definition.Enabled, string.Empty, definition.ReportStyle, definition.EnableCsvExport, definition.DashboardWidgets,
    definition.Filters?.Select(filter => new ReportFilterView(filter.Name, filter.Label, filter.ControlType, !string.IsNullOrWhiteSpace(filter.OptionsSql))).ToList());
static UserAccount? CurrentUser(HttpContext context, UserStore users)
{
    var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    return id is null ? null : users.FindById(id);
}
static bool IsSystemAdmin(ClaimsPrincipal principal) => principal.IsInRole(RoleNames.SystemAdmin);
static bool CanManageReports(ClaimsPrincipal principal) => IsSystemAdmin(principal) || principal.IsInRole(RoleNames.ReportAdmin);
static bool CanReadReport(ClaimsPrincipal principal, string reportId, UserStore users)
{
    if (CanManageReports(principal)) return true;
    var account = CurrentUser(new DefaultHttpContext { User = principal }, users);
    return account is { Active: true, Role: RoleNames.ReportUser } && account.ReportIds.Contains(reportId, StringComparer.OrdinalIgnoreCase);
}
static bool CanReadQueryType(ClaimsPrincipal principal, string queryType, ReportDefinitionStore definitions, UserStore users) =>
    definitions.GetAll().Any(x => x.Enabled && x.QueryType.Equals(queryType, StringComparison.OrdinalIgnoreCase) && (CanManageReports(principal) || CanReadReport(principal, x.Id, users)));

static string? ValidateNewUser(string? username, string? displayName, string? password, string? role, bool allowEmptyPassword = false)
{
    if (string.IsNullOrWhiteSpace(username) || !System.Text.RegularExpressions.Regex.IsMatch(username, "^[A-Za-z0-9_.-]{3,64}$"))
        return "用户名需为 3–64 位字母、数字、点、下划线或连字符。";
    if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 80)
        return "请填写不超过 80 个字符的显示名称。";
    if (!RoleNames.All.Contains(role ?? string.Empty, StringComparer.Ordinal))
        return "请选择有效的账号角色。";
    if (string.IsNullOrEmpty(password) && allowEmptyPassword) return null;
    if (string.IsNullOrWhiteSpace(password) || password.Length < 2)
        return "密码至少需要 2 个字符。";
    return null;
}

static string RemoveEmptyOptionalConditions(string sql, IQueryCollection query)
{
    var parameterNames = System.Text.RegularExpressions.Regex.Matches(sql, "[@:]([A-Za-z_][A-Za-z0-9_]*)")
        .Select(x => x.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase);
    const string boundary = @"\s+(?:AND|OR|ORDER\s+BY|GROUP\s+BY|HAVING|FETCH|OFFSET|UNION)\b";

    foreach (var name in parameterNames)
    {
        if (!string.IsNullOrWhiteSpace(query[name].FirstOrDefault())) continue;

        var parameter = @"[@:]" + System.Text.RegularExpressions.Regex.Escape(name) + @"\b";
        // 先处理 WHERE 后的第一段条件；这样用户不必额外手写 "WHERE 1=1"。
        var firstCondition = @"(?is)\bWHERE\s+(?:(?!" + boundary + @").)*?" + parameter + @"(?:(?!" + boundary + @").)*?(?=" + boundary + @"|$)";
        sql = System.Text.RegularExpressions.Regex.Replace(sql, firstCondition, "WHERE ");

        // 再处理普通的 AND 条件。只删除包含该空参数的一整段条件，不影响其余条件。
        var andCondition = @"(?is)\s+AND\s+(?:(?!" + boundary + @").)*?" + parameter + @"(?:(?!" + boundary + @").)*?(?=" + boundary + @"|$)";
        sql = System.Text.RegularExpressions.Regex.Replace(sql, andCondition, string.Empty);
    }

    // 所有条件都未填写时，保留一个合法的恒真 WHERE，仍可执行报表 SQL。
    return System.Text.RegularExpressions.Regex.Replace(sql, @"(?is)\bWHERE\s*(?=(ORDER\s+BY|GROUP\s+BY|HAVING|FETCH|OFFSET|UNION)\b|$)", "WHERE 1=1 ");
}

static string BuildPagedSql(string sql, DatabaseProvider provider)
{
    var orderBy = System.Text.RegularExpressions.Regex.IsMatch(sql, @"\bORDER\s+BY\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        ? string.Empty : " ORDER BY (SELECT NULL)";
    return provider switch
    {
        DatabaseProvider.Sqlite => $"{sql} LIMIT @biPageLimit OFFSET @biPageOffset",
        DatabaseProvider.SqlServer => $"{sql}{orderBy} OFFSET @biPageOffset ROWS FETCH NEXT @biPageLimit ROWS ONLY",
        // Oracle 11g 及部分兼容模式不支持 OFFSET / FETCH；ROWNUM 可兼容旧版本。
        DatabaseProvider.Oracle => $"SELECT * FROM (SELECT bi_inner.*, ROWNUM AS bi_rownum FROM ({sql}) bi_inner WHERE ROWNUM <= @biPageOffset + @biPageLimit) WHERE bi_rownum > @biPageOffset",
        _ => throw new InvalidOperationException("不支持的数据源类型。")
    };
}

static string BuildCountSql(string sql, DatabaseProvider provider)
{
    var body = RemoveOuterOrderBy(sql);
    return provider == DatabaseProvider.Oracle
        ? $"SELECT COUNT(1) FROM ({body}) bi_count"
        : $"SELECT COUNT(1) FROM ({body}) AS bi_count";
}

static string RemoveOuterOrderBy(string sql)
{
    var depth = 0;
    var quoted = false;
    var orderByIndex = -1;
    for (var index = 0; index < sql.Length; index++)
    {
        if (sql[index] == '\'') quoted = !quoted;
        if (quoted) continue;
        if (sql[index] == '(') depth++;
        else if (sql[index] == ')') depth = Math.Max(0, depth - 1);
        else if (depth == 0 && index + 8 <= sql.Length && sql.AsSpan(index).StartsWith("ORDER BY", StringComparison.OrdinalIgnoreCase))
        {
            var before = index == 0 || char.IsWhiteSpace(sql[index - 1]);
            var after = index + 8 == sql.Length || char.IsWhiteSpace(sql[index + 8]);
            if (before && after) orderByIndex = index;
        }
    }
    return orderByIndex >= 0 ? sql[..orderByIndex].TrimEnd() : sql;
}

static string? ValidateDefinition(ReportDefinition definition)
{
    if (string.IsNullOrWhiteSpace(definition.Id) || !System.Text.RegularExpressions.Regex.IsMatch(definition.Id, "^[a-z0-9-]+$"))
        return "报表标识只能使用小写字母、数字和连字符。";
    if (string.IsNullOrWhiteSpace(definition.Category) || string.IsNullOrWhiteSpace(definition.Name))
        return "请填写分类和报表名称。";
    if (definition.QueryType is not ("product-trace" or "capacity" or "standard"))
        return "报表模板无效。";
    if (!IsReadOnlySelect(definition.SqlText))
        return "请填写单条 SELECT 查询 SQL；不允许分号或数据修改语句。";
    return null;
}

static bool IsReadOnlySelect(string? sql)
{
    if (string.IsNullOrWhiteSpace(sql) || sql.Contains(';') || sql.Contains("--") || sql.Contains("/*") || sql.Contains("*/")) return false;
    var normalized = sql.TrimStart();
    if (!(normalized.StartsWith("select", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("with", StringComparison.OrdinalIgnoreCase))) return false;

    // 管理端 SQL 也只允许真正的只读查询；数据库账号仍应配置为只读账号，形成第二道保护。
    const string prohibited = @"\b(insert|update|delete|merge|create|alter|drop|truncate|grant|revoke|execute|exec|call|declare|begin|commit|rollback|vacuum|attach|detach|pragma|into|for\s+update)\b";
    return !System.Text.RegularExpressions.Regex.IsMatch(normalized, prohibited, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

public sealed record ProcessRecord(
    string ProSn,
    int ProQty,
    string? ModelCode,
    string? ProjectNo,
    string? MoNumber,
    string? WorkStationName,
    string? GroupName,
    int? GroupSequence,
    DateTime InTime,
    string? ErrorFlag,
    string? IsScrap,
    string? IsRework,
    string? RouteEnd,
    int NgQuantity,
    int ScrapQuantity);

public sealed record DataSourceSettings(string Provider, string FilePath);

public sealed record ReportDefinition(
    string Id,
    string Category,
    string Name,
    string QueryType,
    List<string> Conditions,
    List<string> DisplayFields,
    bool Enabled = true,
    string SqlText = "",
    string ReportStyle = "standard",
    bool EnableCsvExport = true,
    List<DashboardWidget>? DashboardWidgets = null,
    List<ReportFilter>? Filters = null);

public sealed record ReportFilter(string Name, string Label, string ControlType = "text", string? OptionsSql = null);
public sealed record ReportDefinitionView(
    string Id, string Category, string Name, string QueryType, List<string> Conditions, List<string> DisplayFields,
    bool Enabled, string SqlText, string ReportStyle, bool EnableCsvExport, List<DashboardWidget>? DashboardWidgets,
    List<ReportFilterView>? Filters);
public sealed record ReportFilterView(string Name, string Label, string ControlType, bool HasOptionsSql);

public sealed record DashboardWidget(
    string Id,
    string Type,
    string Title,
    string? XField = null,
    string? YField = null,
    int Width = 6,
    string Color = "blue",
    bool ShowLegend = true,
    bool ShowLabel = false,
    int Height = 300);

public sealed record SqlValidationRequest(string? SqlText);
public sealed record SqlPreviewRequest(string? SqlText, Dictionary<string, string>? Parameters);
public sealed record DataSourceDefinition(string Id, string Name, string Provider, string ConnectionString, bool Active = false);
public sealed record InitialAdminRequest(string? Username, string? DisplayName, string? Password);
public sealed record LoginRequest(string? Username, string? Password);
public sealed record UserUpsertRequest(string? Username, string? DisplayName, string? Password, string? Role, List<string>? ReportIds, bool Active = true);
public sealed record PublicUser(string Id, string Username, string DisplayName, string Role, List<string> ReportIds, bool Active);
public sealed record UserAccount(string Id, string Username, string DisplayName, string Role, List<string> ReportIds, bool Active, string PasswordSalt, string PasswordHash);

public static class RoleNames
{
    public const string SystemAdmin = "system-admin";
    public const string ReportAdmin = "report-admin";
    public const string ReportUser = "report-user";
    public static readonly string[] All = [SystemAdmin, ReportAdmin, ReportUser];
}

public sealed class UserStore(string path, JsonSerializerOptions options)
{
    private const int PasswordIterations = 310_000;
    private static readonly object Sync = new();

    public List<UserAccount> GetAll()
    {
        lock (Sync) return Read();
    }

    public UserAccount? FindById(string id)
    {
        lock (Sync) return Read().FirstOrDefault(user => user.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public UserAccount? Authenticate(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return null;
        lock (Sync)
        {
            var account = Read().FirstOrDefault(user => user.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase) && user.Active);
            if (account is null || !VerifyPassword(password, account.PasswordSalt, account.PasswordHash)) return null;
            return account;
        }
    }

    public UserAccount? CreateFirstAdmin(string username, string displayName, string password)
    {
        lock (Sync)
        {
            var all = Read();
            if (all.Count > 0) return null;
            var account = NewAccount(username, displayName, password, RoleNames.SystemAdmin, [], true);
            Write([account]);
            return account;
        }
    }

    public UserAccount Create(string username, string displayName, string password, string role, List<string> reportIds)
    {
        lock (Sync)
        {
            var all = Read();
            EnsureUsernameAvailable(all, username, null);
            var account = NewAccount(username, displayName, password, role, reportIds, true);
            all.Add(account);
            Write(all);
            return account;
        }
    }

    public UserAccount? Update(string id, string username, string displayName, string? password, string role, List<string> reportIds, bool active)
    {
        lock (Sync)
        {
            var all = Read();
            var index = all.FindIndex(user => user.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            EnsureUsernameAvailable(all, username, id);
            var current = all[index];
            if (current.Active && current.Role == RoleNames.SystemAdmin && (!active || role != RoleNames.SystemAdmin) && all.Count(user => user.Active && user.Role == RoleNames.SystemAdmin) <= 1)
                throw new InvalidOperationException("至少需要保留一个启用状态的系统管理员。" );
            var credentials = string.IsNullOrWhiteSpace(password) ? (current.PasswordSalt, current.PasswordHash) : HashPassword(password);
            var updated = current with
            {
                Username = username.Trim(),
                DisplayName = displayName.Trim(),
                Role = role,
                ReportIds = CleanReportIds(reportIds),
                Active = active,
                PasswordSalt = credentials.Item1,
                PasswordHash = credentials.Item2
            };
            all[index] = updated;
            Write(all);
            return updated;
        }
    }

    public bool Delete(string id)
    {
        lock (Sync)
        {
            var all = Read();
            var account = all.FirstOrDefault(user => user.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (account is null) return false;
            if (account.Active && account.Role == RoleNames.SystemAdmin && all.Count(user => user.Active && user.Role == RoleNames.SystemAdmin) <= 1)
                throw new InvalidOperationException("至少需要保留一个启用状态的系统管理员。" );
            all.Remove(account);
            Write(all);
            return true;
        }
    }

    private UserAccount NewAccount(string username, string displayName, string password, string role, List<string> reportIds, bool active)
    {
        var (salt, hash) = HashPassword(password);
        return new UserAccount(Guid.NewGuid().ToString("N"), username.Trim(), displayName.Trim(), role, CleanReportIds(reportIds), active, salt, hash);
    }

    private static List<string> CleanReportIds(IEnumerable<string>? reportIds) =>
        (reportIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void EnsureUsernameAvailable(List<UserAccount> all, string username, string? currentId)
    {
        if (all.Any(user => !string.Equals(user.Id, currentId, StringComparison.OrdinalIgnoreCase) && user.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("用户名已存在，请使用其他用户名。" );
    }

    private static (string Salt, string Hash) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    private static bool VerifyPassword(string password, string saltText, string hashText)
    {
        try
        {
            var salt = Convert.FromBase64String(saltText);
            var expected = Convert.FromBase64String(hashText);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private List<UserAccount> Read()
    {
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<UserAccount>>(File.ReadAllText(path), options) ?? []; }
        catch (JsonException ex) { throw new InvalidOperationException("用户权限文件损坏，已停止初始化以避免覆盖现有账号。请从备份恢复 users.json。", ex); }
    }

    private void Write(List<UserAccount> users)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(users, options));
        File.Move(temporary, path, true);
    }
}

public sealed class DataSourceStore(string path, string legacyPath, JsonSerializerOptions options)
{
    private readonly object sync = new();
    public List<DataSourceDefinition> GetAll() { lock (sync) return Read(); }
    public DataSourceDefinition? GetActive() { lock (sync) return Read().FirstOrDefault(x => x.Active); }
    public void Save(DataSourceDefinition source) { lock (sync) { var all = Read(); var i = all.FindIndex(x => x.Id == source.Id); if (i >= 0) all[i] = source; else all.Add(source); Write(all); } }
    public bool Activate(string id) { lock (sync) { var all = Read(); if (!all.Any(x => x.Id == id)) return false; Write(all.Select(x => x with { Active = x.Id == id }).ToList()); return true; } }
    private List<DataSourceDefinition> Read()
    {
        if (File.Exists(path)) return JsonSerializer.Deserialize<List<DataSourceDefinition>>(File.ReadAllText(path), options) ?? [];
        if (!File.Exists(legacyPath)) return [];
        var legacy = JsonSerializer.Deserialize<DataSourceSettings>(File.ReadAllText(legacyPath), options);
        return legacy is null ? [] : [new("sqlite-local", "本地 SQLite", legacy.Provider, $"Data Source={legacy.FilePath}", true)];
    }
    private void Write(List<DataSourceDefinition> all) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(all, new JsonSerializerOptions(options) { WriteIndented = true })); }
}

public sealed class ReportDefinitionStore(string path, JsonSerializerOptions options)
{
    private readonly object _sync = new();

    public void EnsureSqlText()
    {
        var all = Read();
        if (all.Count == 0) return;
        var migrated = all.Select(x => string.IsNullOrWhiteSpace(x.SqlText) ? x with { SqlText = DefaultSql(x.QueryType) } : x).ToList();
        if (!all.SequenceEqual(migrated)) Write(migrated);
    }

    private static string DefaultSql(string queryType) => queryType == "capacity"
        ? "SELECT date(IN_TIME) AS date, COUNT(*) AS passRecords, COUNT(DISTINCT PRO_SN) AS serials\nFROM t_co_detail\nWHERE date(IN_TIME) BETWEEN @startDate AND @endDate\nGROUP BY date(IN_TIME)\nORDER BY date"
        : "SELECT PRO_SN AS sn, MODEL_CODE AS modelCode, PROJECT_NO AS projectNo, MO_NUMBER AS moNumber,\n       GROUP_NAME AS process, WORK_STATIONNAME AS station, IN_TIME AS inTime\nFROM t_co_detail\nWHERE PRO_SN = @sn\nORDER BY IN_TIME";

    public IReadOnlyList<ReportDefinition> GetAll()
    {
        lock (_sync)
            return Read().OrderBy(x => x.Category).ThenBy(x => x.Name).ToList();
    }

    public void Save(ReportDefinition definition)
    {
        lock (_sync)
        {
            var all = Read();
            var index = all.FindIndex(x => x.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) all[index] = definition; else all.Add(definition);
            Write(all);
        }
    }

    public bool Delete(string id)
    {
        lock (_sync)
        {
            var all = Read();
            var removed = all.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Write(all);
            return true;
        }
    }

    private List<ReportDefinition> Read() => File.Exists(path)
        ? JsonSerializer.Deserialize<List<ReportDefinition>>(File.ReadAllText(path), options) ?? []
        : [];

    private void Write(List<ReportDefinition> definitions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(definitions, new JsonSerializerOptions(options) { WriteIndented = true }));
    }
}
