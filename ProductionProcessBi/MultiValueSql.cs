using System.Text.RegularExpressions;

public static class MultiValueSql
{
    // Values remain bound parameters; only SQL placeholders are expanded.
    public static string Expand(string sql, Dictionary<string, object?> parameters, IEnumerable<string> multilineNames)
    {
        foreach (var name in multilineNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!parameters.TryGetValue(name, out var raw)) continue;
            var values = Convert.ToString(raw)?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray() ?? [];
            if (values.Length == 0) continue;
            if (values.Length == 1) { parameters[name] = values[0]; continue; }
            if (values.Length > 900 || parameters.Count + values.Length > 2000)
                throw new ArgumentException($"{name} 的输入值过多，请减少每行输入的数量（单个条件最多 900 个值）。");

            var masked = Regex.Replace(sql, @"'(?:''|[^'])*'|--[^\r\n]*|/\*[\s\S]*?\*/", match => new string(' ', match.Length));
            var parameterPattern = @"[@:]" + Regex.Escape(name) + @"\b";
            const string identifier = "(?:[A-Za-z_][A-Za-z0-9_]*|\\[[^\\]]+\\]|\"[^\"]+\"|`[^`]+`)";
            var pattern = @"(?<column>" + identifier + @"(?:\s*\.\s*" + identifier + @")*)\s*(?<![!<>=])=\s*(?<parameter>" + parameterPattern + @")(?=\s*(?:$|\)|;|AND\b|OR\b|ORDER\b|GROUP\b|HAVING\b|UNION\b|LIMIT\b|OFFSET\b|FETCH\b))";
            var matches = Regex.Matches(masked, pattern, RegexOptions.IgnoreCase);
            var occurrences = Regex.Matches(masked, parameterPattern);
            if (matches.Count == 0 || matches.Count != occurrences.Count)
                throw new ArgumentException($"{name} 的多行输入只支持“字段 = @{name}”条件，请调整 SQL。");

            var keys = values.Select((_, index) => $"bi_multi_{name}_{index}").ToArray();
            if (keys.Any(key => parameters.ContainsKey(key) || Regex.IsMatch(masked, @"[@:]" + Regex.Escape(key) + @"\b", RegexOptions.IgnoreCase)))
                throw new ArgumentException($"{name} 的展开参数名称与已有 SQL 参数冲突，请调整参数名称。");
            foreach (Match match in matches.Cast<Match>().Reverse())
            {
                var prefix = sql[match.Groups["parameter"].Index];
                var column = sql.Substring(match.Groups["column"].Index, match.Groups["column"].Length);
                sql = sql.Remove(match.Index, match.Length).Insert(match.Index, $"{column} IN ({string.Join(", ", keys.Select(key => prefix + key))})");
            }
            parameters.Remove(name);
            for (var index = 0; index < keys.Length; index++) parameters[keys[index]] = values[index];
        }
        return sql;
    }
}
