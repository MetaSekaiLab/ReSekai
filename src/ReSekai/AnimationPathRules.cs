using System.Globalization;
using System.Text.RegularExpressions;

namespace ReSekai;

sealed class AnimationPathRules
{
    sealed class Candidate(string path)
    {
        public string Path { get; } = path;
        public readonly HashSet<string> Parents = new(StringComparer.Ordinal);
        public readonly HashSet<string> NumberedPaths = new(StringComparer.Ordinal);
        public readonly HashSet<string> NumberSeries = new(StringComparer.Ordinal);
        public bool OffsetWrapper;
        public bool Live2D;
    }

    readonly Dictionary<uint, Dictionary<string, Candidate>> matches = [];
    static readonly Regex numbers = new("[0-9]+");

    public AnimationPathRules(IEnumerable<string> paths, HashSet<uint> wanted)
    {
        if (wanted.Count == 0) return;
        Span<char> scratch = stackalloc char[512];
        foreach (string path in paths)
        {
            Span<char> buffer = path.Length <= scratch.Length ? scratch[..path.Length] : new char[path.Length];
            int first = path.IndexOf('/');
            for (int start = first; start >= 0;)
            {
                int end = path.IndexOf('/', start + 1);
                if (end < 0) break;
                var parent = path.AsSpan(start + 1, end - start - 1);
                int childEnd = path.IndexOf('/', end + 1);
                var child = path.AsSpan(end + 1, (childEnd < 0 ? path.Length : childEnd) - end - 1);
                path.AsSpan(0, start).CopyTo(buffer);
                path.AsSpan(end).CopyTo(buffer[start..]);
                var candidate = Add(buffer[..(path.Length - (end - start))]);
                if (candidate is not null)
                {
                    candidate.Parents.Add(path[..end]);
                    candidate.OffsetWrapper |= parent.EndsWith("_offset", StringComparison.Ordinal)
                        && child.StartsWith("EX_", StringComparison.Ordinal) && parent[..^7].SequenceEqual(child[3..]);
                }
                start = end;
            }

            path.AsSpan().CopyTo(buffer);
            bool live2d = path.StartsWith("Parameters/", StringComparison.Ordinal) || path.StartsWith("Parts/", StringComparison.Ordinal);
            foreach (var number in numbers.EnumerateMatches(path))
            {
                if (number.Length > 4) continue;
                var digits = path.AsSpan(number.Index, number.Length);
                int current = int.Parse(digits, CultureInfo.InvariantCulture);
                int limit = number.Length switch { 1 => 10, 2 => 100, 3 => 1000, _ => 10000 };
                for (int next = Math.Max(0, current - 2); next <= current + 2 && next < limit; next++)
                {
                    if (next == current) continue;
                    next.TryFormat(buffer.Slice(number.Index, number.Length), out _, number.Length switch
                    {
                        1 => "D1", 2 => "D2", 3 => "D3", _ => "D4"
                    }, CultureInfo.InvariantCulture);
                    if (Add(buffer) is { } candidate)
                    {
                        candidate.NumberedPaths.Add(path);
                        candidate.NumberSeries.Add(path[..number.Index] + new string('#', number.Length) + path[(number.Index + number.Length)..]);
                    }
                }
                digits.CopyTo(buffer[number.Index..]);
                if (live2d && number.Index + number.Length == path.Length)
                {
                    for (int next = Math.Max(0, current - 3); next <= current + 3; next++)
                    {
                        string value = next.ToString(CultureInfo.InvariantCulture);
                        if (value.Length != number.Length && Add(path[..number.Index] + value) is { } candidate)
                            candidate.Live2D = true;
                    }
                }
            }
        }

        Candidate? Add(ReadOnlySpan<char> text)
        {
            uint hash = AnimationNames.Hash(text);
            if (!wanted.Contains(hash)) return null;
            string path = text.ToString();
            if (!matches.TryGetValue(hash, out var pathsByName)) matches.Add(hash, pathsByName = new(StringComparer.Ordinal));
            if (!pathsByName.TryGetValue(path, out var candidate)) pathsByName.Add(path, candidate = new(path));
            return candidate;
        }
    }

    public Dictionary<uint, string> For(IEnumerable<uint> missing, IEnumerable<string>? known = null)
    {
        var targets = missing.ToHashSet();
        if (targets.Count == 0) return [];
        var anchors = known?.ToArray() ?? [];
        bool live2dClip = anchors.Length > 0 && anchors.All(p => p.Length == 0
            || p.StartsWith("Parameters/", StringComparison.Ordinal) || p.StartsWith("Parts/", StringComparison.Ordinal));
        var unique = targets.Where(h => matches.TryGetValue(h, out var paths) && paths.Count == 1)
            .ToDictionary(h => h, h => matches[h].Values.Single());
        var sharedParents = Shared(unique.Values.SelectMany(c => c.Parents));
        var sharedSeries = Shared(unique.Values.SelectMany(c => c.NumberSeries));
        var resolved = unique.Where(p => p.Value.OffsetWrapper || p.Value.NumberedPaths.Count > 1
                || p.Value.Parents.Overlaps(sharedParents) || p.Value.NumberSeries.Overlaps(sharedSeries)
                || p.Value.Live2D && live2dClip)
            .ToDictionary(p => p.Key, p => p.Value.Path);

        if (anchors.Length > 0)
            foreach (var (hash, paths) in ExtendSeries(targets, anchors))
            {
                if (paths.Count != 1 || resolved.TryGetValue(hash, out string? old) && !paths.Contains(old))
                    resolved.Remove(hash);
                else resolved.TryAdd(hash, paths.Single());
            }
        return resolved;

        static HashSet<string> Shared(IEnumerable<string> keys) => keys.GroupBy(p => p, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
    }

    static Dictionary<uint, HashSet<string>> ExtendSeries(HashSet<uint> targets, IEnumerable<string> known)
    {
        Dictionary<(string Prefix, string Suffix, int Width), HashSet<int>> series = [];
        Dictionary<(string Prefix, string Middle, string Suffix, int Width), HashSet<int>> linked = [];
        foreach (string path in known)
        {
            int lastSlash = path.LastIndexOf('/');
            int start = lastSlash > 0 ? path.LastIndexOf('/', lastSlash - 1) + 1 : 0;
            foreach (var number in numbers.EnumerateMatches(path, start))
            {
                if (number.Length > 4) continue;
                var key = (path[..number.Index], path[(number.Index + number.Length)..], number.Length);
                if (!series.TryGetValue(key, out var values)) series.Add(key, values = []);
                values.Add(int.Parse(path.AsSpan(number.Index, number.Length), CultureInfo.InvariantCulture));
            }
            int previous = -1, width = 0;
            foreach (var number in numbers.EnumerateMatches(path))
            {
                if (number.Length == width && width is > 0 and <= 3
                    && path.AsSpan(previous + width, number.Index - previous - width).Contains('/')
                    && path.AsSpan(previous, width).SequenceEqual(path.AsSpan(number.Index, width)))
                {
                    var key = (path[..previous], path[(previous + width)..number.Index], path[(number.Index + width)..], width);
                    if (!linked.TryGetValue(key, out var values)) linked.Add(key, values = []);
                    values.Add(int.Parse(path.AsSpan(previous, width), CultureInfo.InvariantCulture));
                }
                previous = number.Index;
                width = number.Length;
            }
        }

        Dictionary<uint, HashSet<string>> result = [];
        foreach (var (key, values) in series)
            Expand(values, key.Width, number => key.Prefix + number + key.Suffix);
        foreach (var (key, values) in linked)
            if (values.Count >= 3) Expand(values, key.Width, number => key.Prefix + number + key.Middle + number + key.Suffix);
        return result;

        void Expand(HashSet<int> values, int width, Func<string, string> formatPath)
        {
            int first = values.Min(), last = values.Max();
            if (last - first + 1 != values.Count) return;
            string format = "D" + width;
            int limit = width switch { 1 => 10, 2 => 100, 3 => 1000, _ => 10000 };
            int maxSteps = values.Count == 1 ? 4 : Math.Min(values.Count, 4);
            int minHits = values.Count == 1 ? 3 : 2;
            Extend(first, -1);
            Extend(last, 1);

            void Extend(int edge, int step)
            {
                List<(uint Hash, string Path)> hits = [];
                for (int i = 1; i <= maxSteps; i++)
                {
                    int n = edge + i * step;
                    if (n < 0 || n >= limit) break;
                    string path = formatPath(n.ToString(format, CultureInfo.InvariantCulture));
                    uint hash = AnimationNames.Hash(path);
                    if (!targets.Contains(hash)) break;
                    hits.Add((hash, path));
                }
                if (hits.Count < minHits) return;
                foreach (var (hash, path) in hits)
                {
                    if (!result.TryGetValue(hash, out var paths)) result.Add(hash, paths = new(StringComparer.Ordinal));
                    paths.Add(path);
                }
            }
        }
    }
}
