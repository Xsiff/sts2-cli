namespace Sts2Headless;

/// <summary>
/// English localization lookup for display names.
/// </summary>
internal class LocLookup
{
    private readonly Dictionary<string, Dictionary<string, string>> _eng = new();

    public LocLookup()
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        Load(Path.Combine(baseDir, "localization_eng"), _eng);
    }

    private static void Load(string dir, Dictionary<string, Dictionary<string, string>> target)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (data != null) target[name] = data;
            }
            catch
            {
            }
        }
    }

    /// <summary>Get English name or the key if not found.</summary>
    public string Name(string table, string key)
    {
        var en = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key);
        return en ?? key;
    }

    public string? En(string table, string key) => _eng.GetValueOrDefault(table)?.GetValueOrDefault(key);

    /// <summary>Strip BBCode tags like [gold], [/blue], [b], [sine], etc.</summary>
    private static string StripBBCode(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[a-zA-Z_][a-zA-Z0-9_=]*\]", "");
    }

    /// <summary>Return localized English string for JSON output.</summary>
    public string Localized(string table, string key)
    {
        var en = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key) ?? key;
        return StripBBCode(en);
    }

    public string Card(string entry) => Localized("cards", entry + ".title");

    public string Monster(string entry)
    {
        var key = entry + ".name";
        var result = Localized("monsters", key);
        if (result == key)
        {
            var lastUnderscore = entry.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                var baseEntry = entry[..lastUnderscore];
                var baseKey = baseEntry + ".name";
                var baseResult = Localized("monsters", baseKey);
                if (baseResult != baseKey) return baseResult;
            }
        }
        return result;
    }

    public string Relic(string entry) => Localized("relics", entry + ".title");
    public string Potion(string entry) => Localized("potions", entry + ".title");
    public string Power(string entry) => Localized("powers", entry + ".title");
    public string Event(string entry) => Localized("events", entry + ".title");
    public string Act(string entry) => Localized("acts", entry + ".title");

    /// <summary>Resolve a full loc key like "TABLE.KEY.SUB" by searching all tables.</summary>
    public string LocalizedFromKey(string locKey)
    {
        foreach (var tableName in _eng.Keys)
        {
            var en = _eng.GetValueOrDefault(tableName)?.GetValueOrDefault(locKey);
            if (en != null) return en;
        }
        return locKey;
    }

    public bool IsLoaded => _eng.Count > 0;
}
