using System.Text.Json;
using System.Globalization;
using BiDataAccess;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback)
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
    var environmentPath = Environment.GetEnvironmentVariable("PROCESS_BI_SQLITE_PATH");
    if (!string.IsNullOrWhiteSpace(environmentPath)) return environmentPath;
    if (!File.Exists(sourceSettingsPath)) return null;
    var settings = JsonSerializer.Deserialize<DataSourceSettings>(File.ReadAllText(sourceSettingsPath), options);
    return settings?.Provider?.Equals("sqlite", StringComparison.OrdinalIgnoreCase) == true ? settings.FilePath : null;
}

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
app.MapPost("/api/report-definitions/validate-sql", async (SqlValidationRequest request) =>
{
    var sql = request.SqlText?.Trim() ?? string.Empty;
    if (!IsReadOnlySelect(sql)) return Results.BadRequest(new { message = "只允许编写单条 SELECT 查询，不允许包含分号或数据修改语句。" });
    var sqlitePath = ReadSqlitePath();
    if (string.IsNullOrWhiteSpace(sqlitePath) || !File.Exists(sqlitePath))
        return Results.BadRequest(new { message = "当前未配置可用的 SQLite 数据源。" });

    try
    {
        var client = new DatabaseClient(DatabaseProvider.Sqlite, $"Data Source={sqlitePath};Mode=ReadOnly;");
        var parameterNames = System.Text.RegularExpressions.Regex.Matches(sql, "[@:]([A-Za-z_][A-Za-z0-9_]*)")
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parameters = parameterNames.ToDictionary(name => name, name => (object?)string.Empty, StringComparer.OrdinalIgnoreCase);
        var columns = await client.GetColumnsAsync($"SELECT * FROM ({sql}) AS report_preview LIMIT 0", parameters);
        return Results.Ok(new { message = "SQL 校验通过。", columns, parameters = parameterNames });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"SQL 校验失败：{ex.Message}" });
    }
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
    var sqlitePath = ReadSqlitePath();
    if (string.IsNullOrWhiteSpace(sqlitePath) || !File.Exists(sqlitePath)) return Results.BadRequest(new { message = "当前未配置可用的数据源。" });
    var names = System.Text.RegularExpressions.Regex.Matches(definition.SqlText, "[@:]([A-Za-z_][A-Za-z0-9_]*)")
        .Select(x => x.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var parameters = names.ToDictionary(name => name, name => (object?)(request.Query[name].FirstOrDefault() ?? string.Empty), StringComparer.OrdinalIgnoreCase);
    var client = new DatabaseClient(DatabaseProvider.Sqlite, $"Data Source={sqlitePath};Mode=ReadOnly;");
    var columns = await client.GetColumnsAsync($"SELECT * FROM ({definition.SqlText}) AS report_result LIMIT 0", parameters);
    var visible = definition.DisplayFields.Where(columns.Contains).ToArray();
    if (visible.Length == 0) visible = columns.ToArray();
    var rows = await client.QueryAsync(definition.SqlText, reader =>
    {
        var row = new Dictionary<string, object?>();
        foreach (var column in visible) row[column] = reader[column] is DBNull ? null : reader[column];
        return row;
    }, parameters);
    return Results.Ok(new { columns = visible, rows });
});

app.Run();

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
    if (string.IsNullOrWhiteSpace(sql) || sql.Contains(';')) return false;
    var normalized = sql.TrimStart();
    return normalized.StartsWith("select", StringComparison.OrdinalIgnoreCase)
        || normalized.StartsWith("with", StringComparison.OrdinalIgnoreCase);
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
    List<DashboardWidget>? DashboardWidgets = null);

public sealed record DashboardWidget(string Id, string Type, string Title, string? XField = null, string? YField = null, int Width = 6);

public sealed record SqlValidationRequest(string? SqlText);

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
