using System.Text;
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
        // Byte-level rewrite: lines that are kept keep their exact bytes, so a
        // non-UTF-8 comment or key is never replaced or corrupted.
        var bytes = File.ReadAllBytes(path);
        var segments = new List<(int Start, int Length)>();
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            segments.Add((start, i - start));
            start = i + 1;
        }
        if (start < bytes.Length) segments.Add((start, bytes.Length - start));

        var text = Encoding.UTF8.GetString(bytes);
        var remove = Parse(text)
            .Where(e => e.Tokens.Any(tokens.Contains))
            .Select(e => e.Index)
            .ToHashSet();
        if (remove.Count == 0) return 0;

        using var output = new MemoryStream();
        var first = true;
        for (var i = 0; i < segments.Count; i++)
        {
            if (remove.Contains(i)) continue;
            if (!first) output.WriteByte((byte)'\n');
            output.Write(bytes, segments[i].Start, segments[i].Length);
            first = false;
        }
        if (!first && bytes.Length > 0 && bytes[^1] == (byte)'\n') output.WriteByte((byte)'\n');
        File.WriteAllBytes(path, output.ToArray());
        return remove.Count;
    }
}

public sealed class ConfigParseResult
{
    public List<ConfigBlock> Blocks { get; } = new();
    public List<string> Includes { get; } = new();
    public bool HasInclude { get; set; }
}

public static partial class ConfigParser
{
    [GeneratedRegex(@"^\s*(host|match|include)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BlockStartRegex();

    public static ConfigParseResult Parse(string text, string sourceFile = "", bool included = false)
    {
        var result = new ConfigParseResult();
        var lines = TextIO.SplitLines(text);
        var start = -1;
        var header = "";
        var isMatch = false;

        void Close(int end)
        {
            if (start < 0) return;
            result.Blocks.Add(Build(lines, start, end, header, isMatch, sourceFile, included));
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
                    var tail = trimmed.Length > 7 ? trimmed[7..].Trim() : "";
                    result.Includes.AddRange(tail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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

    private static ConfigBlock Build(List<string> lines, int start, int end, string header, bool isMatch,
        string sourceFile, bool included)
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
            SourceFile = sourceFile,
            IsIncluded = included,
        };
    }

    public static ConfigParseResult ParseFile(string path, bool followIncludes = false)
    {
        var result = Parse(TextIO.ReadText(path), path, included: false);
        if (!followIncludes) return result;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(path) };
        LoadIncludes(result, Path.GetDirectoryName(path) ?? PlatformInfo.SshDir, 0, visited);
        return result;
    }

    private static void LoadIncludes(ConfigParseResult result, string baseDirectory, int depth,
        HashSet<string> visited)
    {
        if (depth > 4) return;
        var patterns = result.Includes.ToList();
        result.Includes.Clear();
        foreach (var pattern in patterns)
        {
            foreach (var file in ExpandInclude(baseDirectory, pattern))
            {
                string full;
                try { full = Path.GetFullPath(file); } catch { continue; }
                if (!visited.Add(full) || !File.Exists(full)) continue;
                var included = ParseFile(full, followIncludes: false);
                foreach (var block in included.Blocks)
                {
                    result.Blocks.Add(new ConfigBlock
                    {
                        StartLine = block.StartLine,
                        EndLine = block.EndLine,
                        Header = block.Header,
                        Patterns = block.Patterns,
                        Options = block.Options,
                        IsMatch = block.IsMatch,
                        IsGlobal = block.IsGlobal,
                        SourceFile = full,
                        IsIncluded = true,
                    });
                }
                result.HasInclude = true;
                var nested = included.Includes.ToList();
                if (nested.Count > 0)
                {
                    var child = new ConfigParseResult();
                    child.Includes.AddRange(nested);
                    LoadIncludes(child, Path.GetDirectoryName(full) ?? baseDirectory, depth + 1, visited);
                    result.Blocks.AddRange(child.Blocks);
                }
            }
        }
    }

    private static IEnumerable<string> ExpandInclude(string baseDirectory, string pattern)
    {
        var value = pattern.Trim().Trim('"');
        if (value.StartsWith("~/", StringComparison.Ordinal))
            value = Path.Combine(PlatformInfo.Home, value[2..]);
        else if (!Path.IsPathRooted(value))
            value = Path.Combine(baseDirectory, value);

        var directory = Path.GetDirectoryName(value);
        var filePattern = Path.GetFileName(value);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) yield break;
        if (filePattern.IndexOfAny(new[] { '*', '?' }) >= 0)
        {
            foreach (var match in Directory.EnumerateFiles(directory, filePattern)) yield return match;
        }
        else if (File.Exists(value))
        {
            yield return value;
        }
    }
}
