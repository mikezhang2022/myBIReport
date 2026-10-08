using System.Text.RegularExpressions;
static void Check(bool result, string name) { if (!result) throw new Exception(name); Console.WriteLine("PASS " + name); }
static Dictionary<string, object?> Params(string value) => new(StringComparer.OrdinalIgnoreCase) { ["sn"] = value };
var values = Params("sn1\r\nsn2\n\nsn1");
var sql = MultiValueSql.Expand("SELECT * FROM t WHERE t.sn=@sn AND t.flag=@flag ORDER BY t.sn", values, ["sn"]);
Check(sql.Contains("t.sn IN (@bi_multi_sn_0, @bi_multi_sn_1)"), "equality expands into IN");
Check((string?)values["bi_multi_sn_0"] == "sn1" && (string?)values["bi_multi_sn_1"] == "sn2" && !values.ContainsKey("sn"), "values split, deduplicated and bound");
values = Params("a' OR 1=1 --\nb");
sql = MultiValueSql.Expand("SELECT * FROM t WHERE t.sn=@sn", values, ["sn"]);
Check(!sql.Contains("OR 1=1") && (string?)values["bi_multi_sn_0"] == "a' OR 1=1 --", "input never interpolated into SQL");
values = Params("one\n");
Check(MultiValueSql.Expand("SELECT * FROM t WHERE t.sn=@sn", values, ["sn"]).Contains("t.sn=@sn") && (string?)values["sn"] == "one", "single line retains equality");
values = Params("sn1\nsn2");
sql = MultiValueSql.Expand("SELECT '@sn' FROM t WHERE t.sn=@sn OR t.parent_sn=@sn -- t.sn=@sn", values, ["sn"]);
Check(Regex.Matches(sql," IN ").Count == 2 && sql.Contains("'@sn'") && sql.Contains("-- t.sn=@sn"), "repeated conditions, comments and strings");
values = Params("sn1\nsn2");
sql = MultiValueSql.Expand("SELECT * FROM t WHERE t.\"sn\"=:sn", values, ["sn"]);
Check(sql.Contains("t.\"sn\" IN (:bi_multi_sn_0, :bi_multi_sn_1)"), "quoted identifier and Oracle placeholders");
foreach (var condition in new[] { "t.sn LIKE @sn", "t.sn != @sn", "t.sn = @sn + 'x'" }) {
    var rejected = false;
    try { MultiValueSql.Expand("SELECT * FROM t WHERE " + condition, Params("a\nb"), ["sn"]); } catch (ArgumentException) { rejected=true; }
    Check(rejected, "unsupported condition rejected: " + condition);
}
values = Params("a\nb");
Check(MultiValueSql.Expand("SELECT * FROM t WHERE t.sn=@sn", values, []).Contains("t.sn=@sn"), "single-line configured controls unchanged");
