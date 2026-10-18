using System.Text.RegularExpressions;

namespace SshDashboard.Core;

public static partial class KnownHostsParser
{
    public static List<KnownHostLine> Parse(string text)
    {
        var result = new List<KnownHostLine>();
        var lines = TextIO.SplitLines(text);
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var isMarker = trimmed.StartsWith('@');
            var fields = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            var hostField = fields[0];
            var remainder = fields.Length > 1 ? fields[1] : "";

            var keyParts = remainder.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            var keyType = keyParts.Length > 0 ? keyParts[0] : "";
            var keyData = keyParts.Length > 1 ? keyParts[1] : "";
            var comment = keyParts.Length > 2 ? keyParts[2] : "";

            var tokens = hostField.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();
            var hashed = tokens.Any(t => t.StartsWith("|1|", StringComparison.Ordinal));

            result.Add(new KnownHostLine
            {
                Index = i,
                Raw = raw,
                HostField = hostField,
                Tokens = tokens,
                KeyType = keyType,
                KeyData = keyData,
                Comment = comment,
                IsMarker = isMarker,
                IsHashed = hashed,
            });
        }
        return result;
    }

    public static List<KnownHostLine> ParseFile(string path) =>
        Parse(TextIO.ReadText(path));

    public static int RemoveLines(string path, ISet<string> tokens)
    {
        var text = TextIO.ReadText(path);
        var newline = TextIO.Newline(text);
        var entries = Parse(text);
        var remove = entries
            .Where(e => e.Tokens.Any(tokens.Contains))
            .Select(e => e.Index)
            .ToHashSet();
        if (remove.Count == 0) return 0;

        var lines = TextIO.SplitLines(text);
        var kept = new List<string>();
        for (var i = 0; i < lines.Count; i++)
            if (!remove.Contains(i)) kept.Add(lines[i]);

        var joined = TextIO.JoinLines(kept, newline);
        if (text.EndsWith(newline, StringComparison.Ordinal) && !joined.EndsWith(newline, StringComparison.Ordinal))
            joined += newline;
        File.WriteAllText(path, joined);
        return remove.Count;
    }
}

public sealed class ConfigParseResult
{
    public List<ConfigBlock> Blocks { get; } = new();
    public bool HasInclude { get; set; }
}

public static partial class ConfigParser
{
    [GeneratedRegex(@"^\s*(host|match|include)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BlockStartRegex();

    public static ConfigParseResult Parse(string text)
    {
        var result = new ConfigParseResult();
        var lines = TextIO.SplitLines(text);
        var start = -1;
        var header = "";
        var isMatch = false;

        void Close(int end)
        {
            if (start < 0) return;
            result.Blocks.Add(Build(lines, start, end, header, isMatch));
            start = -1;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            var match = BlockStartRegex().Match(lines[i]);
            if (match.Success && !lines[i].StartsWith(' ') && !lines[i].StartsWith('\t'))
            {
                var keyword = match.Groups[1].Value.ToLowerInvariant();
                if (keyword == "include")
                {
                    result.HasInclude = true;
                    Close(i - 1);
                    continue;
                }
                Close(i - 1);
                start = i;
                header = trimmed;
                isMatch = keyword == "match";
                continue;
            }

            if (start >= 0 && trimmed.Length > 0 && !char.IsWhiteSpace(lines[i][0]))
            {
                Close(i - 1);
            }
        }
        Close(lines.Count - 1);
        return result;
    }

    private static ConfigBlock Build(List<string> lines, int start, int end, string header, bool isMatch)
    {
        var patterns = new List<string>();
        var headerText = lines[start].Trim();
        var firstSpace = headerText.IndexOfAny(new[] { ' ', '\t' });
        if (firstSpace > 0)
        {
            var tail = headerText[(firstSpace + 1)..].Trim();
            patterns.AddRange(tail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        var options = new List<ConfigOption>();
        for (var i = start + 1; i <= end && i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var split = trimmed.IndexOfAny(new[] { '=', ' ', '\t' });
            string key, value;
            if (split < 0)
            {
                key = trimmed;
                value = "";
            }
            else
            {
                key = trimmed[..split];
                value = trimmed[(split + 1)..].TrimStart('=', ' ', '\t');
            }
            options.Add(new ConfigOption(key, value, i));
        }

        var isGlobal = !isMatch && patterns.Count > 0 &&
                       patterns.All(p => p.IndexOfAny(new[] { '*', '?' }) >= 0) &&
                       patterns.Contains("*");
        return new ConfigBlock
        {
            StartLine = start,
            EndLine = Math.Min(end, lines.Count - 1),
            Header = header,
            Patterns = patterns,
            Options = options,
            IsMatch = isMatch,
            IsGlobal = isGlobal,
        };
    }

    public static ConfigParseResult ParseFile(string path) =>
        Parse(TextIO.ReadText(path));
}
