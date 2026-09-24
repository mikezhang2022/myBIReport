using System.Text.Json;
using System.Globalization;
using System.Diagnostics;
using BiDataAccess;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host.StartsWith("10.") || uri.Host.StartsWith("192.168.") || uri.Host.StartsWith("172.16.") || uri.Host.StartsWith("172.17.") || uri.Host.StartsWith("172.18.") || uri.Host.StartsWith("172.19.") || uri.Host.StartsWith("172.2") || uri.Host.StartsWith("172.30.") || uri.Host.StartsWith("172.31.")))
    .AllowAnyHeader()
    .AllowAnyMethod()));
var app = builder.Build();

app.UseCors();

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
}));

var dataPath = Path.Combine(app.Environment.ContentRootPath, "data", "process-records.json");
var sourceSettingsPath = Path.Combine(app.Environment.ContentRootPath, "data", "data-source.json");
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var definitions = new ReportDefinitionStore(Path.Combine(app.Environment.ContentRootPath, "data", "report-definitions.json"), options);
var dataSources = new DataSourceStore(Path.Combine(app.Environment.ContentRootPath, "data", "data-sources.json"), sourceSettingsPath, options);
definitions.EnsureSeed();

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

app.MapGet("/api/summary", () =>
{
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

app.MapGet("/api/daily", () => Results.Ok(ReadRecords()
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

app.MapGet("/api/stations", () => Results.Ok(ReadRecords()
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

app.MapGet("/api/serials", () => Results.Ok(ReadRecords()
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

app.MapGet("/api/serials/{serial}", (string serial) =>
{
    var records = ReadRecords().Where(x => string.Equals(x.ProSn, serial, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.InTime).ToList();
    return records.Count == 0 ? Results.NotFound() : Results.Ok(records);
});

app.MapGet("/api/reports/product-trace", (string? sn) =>
{
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

app.MapGet("/api/reports/capacity", (DateOnly? startDate, DateOnly? endDate) =>
{
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

app.MapGet("/api/report-definitions", () => Results.Ok(definitions.GetAll()));
app.MapGet("/api/data-sources", () => Results.Ok(dataSources.GetAll()));
app.MapPost("/api/data-sources", (DataSourceDefinition source) =>
{
    if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.ConnectionString)) return Results.BadRequest(new { message = "请填写数据源名称和连接信息。" });
    if (source.Provider.ToLowerInvariant() is not ("sqlite" or "sqlserver" or "oracle")) return Results.BadRequest(new { message = "请选择 SQLite、SQL Server 或 Oracle。" });
    var saved = source with { Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString("N") : source.Id, ConnectionString = source.ConnectionString.Trim() };
    dataSources.Save(saved);
    return Results.Ok(saved);
});
app.MapPost("/api/data-sources/{id}/test", async (string id) =>
{
    var source = dataSources.GetAll().FirstOrDefault(x => x.Id == id); if (source is null) return Results.NotFound();
    try { var sql = source.Provider.Equals("oracle", StringComparison.OrdinalIgnoreCase) ? "SELECT 1 FROM DUAL" : "SELECT 1"; await CreateClient(source).ScalarAsync<int>(sql); return Results.Ok(new { message = "连接成功。" }); }
    catch (Exception ex) { return Results.BadRequest(new { message = $"连接失败：{ex.Message}" }); }
});
app.MapPost("/api/data-sources/{id}/activate", (string id) => dataSources.Activate(id) ? Results.Ok(new { message = "已设为当前数据源。" }) : Results.NotFound());
app.MapPost("/api/report-definitions/validate-sql", async (SqlValidationRequest request) =>
{
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
app.MapPost("/api/report-definitions/preview-sql", async (SqlPreviewRequest request) =>
{
    var sql = request.SqlText?.Trim() ?? string.Empty;
    if (!IsReadOnlySelect(sql)) return Results.BadRequest(new { message = "只允许试运行单条只读 SELECT 查询。" });
    var source = dataSources.GetActive();
    if (source is null) return Results.BadRequest(new { message = "请先在数据源管理中设置当前数据源。" });

    var parameterNames = System.Text.RegularExpressions.Regex.Matches(sql, "[@:]([A-Za-z_][A-Za-z0-9_]*)")
        .Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var previewParameters = parameterNames.ToDictionary(name => name, name => (object?)(request.Parameters?.GetValueOrDefault(name) ?? string.Empty), StringComparer.OrdinalIgnoreCase);
    var parameters = new Dictionary<string, object?>(previewParameters, StringComparer.OrdinalIgnoreCase);
    parameters["__biPageLimit"] = 50;
    parameters["__biPageOffset"] = 0;
    var client = CreateClient(source);
    var previewSql = BuildPagedSql(sql, client.Provider);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

    try
    {
        var stopwatch = Stopwatch.StartNew();
        var columns = (await client.GetColumnsAsync(previewSql, parameters, timeout.Token)).Where(column => !column.Equals("__bi_rownum", StringComparison.OrdinalIgnoreCase)).ToArray();
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
        return Results.Ok(new { message = $"试运行完成，返回 {rows.Count} 条（最多 50 条）。", elapsedMs = stopwatch.ElapsedMilliseconds, columns, rows, plan, planMessage });
    }
    catch (OperationCanceledException) { return Results.BadRequest(new { message = "试运行超时（10 秒），请收紧查询条件或优化 SQL。" }); }
    catch (Exception ex) { return Results.BadRequest(new { message = $"试运行失败：{ex.Message}" }); }
});
app.MapPost("/api/report-definitions", (ReportDefinition definition) =>
{
    var validation = ValidateDefinition(definition);
    if (validation is not null) return Results.BadRequest(new { message = validation });
    if (definitions.GetAll().Any(x => x.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new { message = "报表标识已存在。" });
    definitions.Save(definition);
    return Results.Created($"/api/report-definitions/{definition.Id}", definition);
});
app.MapPut("/api/report-definitions/{id}", (string id, ReportDefinition definition) =>
{
    if (!id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = "报表标识不能修改。" });
    var validation = ValidateDefinition(definition);
    if (validation is not null) return Results.BadRequest(new { message = validation });
    if (!definitions.GetAll().Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        return Results.NotFound(new { message = "报表不存在。" });
    definitions.Save(definition);
    return Results.Ok(definition);
});
app.MapDelete("/api/report-definitions/{id}", (string id) =>
{
    if (!definitions.Delete(id)) return Results.NotFound(new { message = "报表不存在。" });
    return Results.NoContent();
});
app.MapGet("/api/reports/{id}/query", async (string id, HttpRequest request) =>
{
    var definition = definitions.GetAll().FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && x.Enabled);
    if (definition is null) return Results.NotFound(new { message = "未找到可用报表。" });
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
    parameters["__biPageLimit"] = pageSize + 1;
    parameters["__biPageOffset"] = checked((page - 1) * pageSize);
    var client = CreateClient(source);
    var pagedSql = BuildPagedSql(executableSql, client.Provider);
    var columns = (await client.GetColumnsAsync(pagedSql, parameters)).Where(column => !column.Equals("__bi_rownum", StringComparison.OrdinalIgnoreCase)).ToArray();
    long totalRows;
    try { totalRows = await client.ScalarAsync<long>(BuildCountSql(executableSql, client.Provider), countParameters); }
    catch (Exception ex) { return Results.BadRequest(new { message = $"统计总记录数失败：{ex.Message}" }); }
    var totalPages = Math.Max(1, (totalRows + pageSize - 1) / pageSize);
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
    return Results.Ok(new { columns = visible, rows, page, pageSize, hasMore, totalRows, totalPages });
});
app.MapGet("/api/reports/{id}/filter-options/{name}", async (string id, string name) =>
{
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
        DatabaseProvider.Sqlite => $"{sql} LIMIT @__biPageLimit OFFSET @__biPageOffset",
        DatabaseProvider.SqlServer => $"{sql}{orderBy} OFFSET @__biPageOffset ROWS FETCH NEXT @__biPageLimit ROWS ONLY",
        // Oracle 11g 及部分兼容模式不支持 OFFSET / FETCH；ROWNUM 可兼容旧版本。
        DatabaseProvider.Oracle => $"SELECT * FROM (SELECT bi_inner.*, ROWNUM AS __bi_rownum FROM ({sql}) bi_inner WHERE ROWNUM <= @__biPageOffset + @__biPageLimit) WHERE __bi_rownum > @__biPageOffset",
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

    public void EnsureSeed()
    {
        var all = Read();
        if (all.Count == 0)
        {
            Write([
                new("product-trace", "生产", "产品追溯报表", "product-trace", ["sn"], ["sn", "status", "modelCode", "moNumber", "projectNo", "quantity", "firstInTime", "lastInTime", "errorRecords", "ngQuantity", "reworkRecords", "scrapQuantity"], true, DefaultSql("product-trace")),
                new("capacity", "计划", "产能报表", "capacity", ["dateRange"], ["passRecords", "serials", "completedSerials", "errorRecords", "ngQuantity", "activeDays", "averageCompletedPerActiveDay"], true, DefaultSql("capacity"))
            ]);
            return;
        }
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
