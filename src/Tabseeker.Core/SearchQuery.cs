using System.Text;
using System.Text.RegularExpressions;

namespace Tabseeker.Core;

/// <summary>Lazily computed full path for the entry being matched; reused across entries to avoid allocations.</summary>
public sealed class PathCache
{
    private string? _value;
    private Func<string>? _source;
    private string _name = "";

    public void Reset(string name, Func<string>? source) { _name = name; _source = source; _value = null; }
    public string Get() => _value ??= _source?.Invoke() ?? _name;
}

/// <summary>
/// Everything-style query.
///   word            name contains word (case-insensitive)
///   a b             AND
///   a|b             OR
///   !word           NOT
///   "two words"     phrase
///   *.pdf  re?ort   wildcard, matched against the whole name
///   docs\report     a term with \ or / is matched against the full path
///   ext:pdf;docx    extension filter
///   file:  folder:  only files / only folders
///   regex:^a.*z$    regular expression on the name
/// </summary>
public sealed class SearchQuery
{
    private readonly Term[] _terms;
    private SearchQuery(List<Term> terms) =>
        _terms = terms.OrderBy(t => t.Cost).ToArray(); // cheap name tests first, full-path tests last

    public bool IsEmpty => _terms.Length == 0;
    public bool NeedsPath => _terms.Any(t => t.NeedsPath);

    public static SearchQuery Parse(string? text)
    {
        var terms = new List<Term>();
        foreach (var token in Tokenize(text ?? string.Empty))
        {
            var term = ParseTerm(token);
            if (term != null) terms.Add(term);
        }
        return new SearchQuery(terms);
    }

    public bool Matches(string name, bool isDirectory, Func<string>? fullPath = null)
    {
        var pc = new PathCache();
        pc.Reset(name, fullPath);
        return Matches(name, isDirectory, pc);
    }

    /// <summary>Allocation-free overload for scanning large indexes; <paramref name="path"/> must be Reset for this entry.</summary>
    public bool Matches(string name, bool isDirectory, PathCache path)
    {
        foreach (var t in _terms)
            if (t.Match(name, isDirectory, path) == t.Negated) return false;
        return true;
    }

    internal static List<string> Tokenize(string text)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    private static Term? ParseTerm(string token)
    {
        bool negated = false;
        while (token.StartsWith('!')) { negated = !negated; token = token[1..]; }
        if (token.Length == 0) return null;

        Term? term = ParseSingle(token);
        if (term == null) return null;
        term.Negated = negated;
        return term;
    }

    private static Term? ParseSingle(string token)
    {
        string lower = token.ToLowerInvariant();
        if (lower is "file:" or "files:") return new KindTerm(false);
        if (lower is "folder:" or "folders:" or "dir:") return new KindTerm(true);
        if (lower.StartsWith("ext:"))
        {
            var exts = token[4..].Split(';', ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Select(e => e.TrimStart('.')).ToArray();
            return exts.Length == 0 ? null : new ExtTerm(exts);
        }
        if (lower.StartsWith("regex:"))
        {
            try { return new RegexTerm(new Regex(token[6..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)); }
            catch (ArgumentException) { return null; }
        }
        if (token.Contains('|'))
        {
            var parts = token.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(ParseSingle).Where(p => p != null).ToList();
            return parts.Count == 0 ? null : new OrTerm(parts!);
        }
        bool onPath = token.Contains('\\') || token.Contains('/');
        if (onPath) token = token.Replace('/', '\\');
        if (token.Contains('*') || token.Contains('?'))
            return new WildcardTerm(onPath ? "*" + token : token, onPath); // path wildcards match the end of the path
        return new TextTerm(token, onPath);
    }

    /// <summary>Case-insensitive glob match of the whole string ('*' any run, '?' one char).</summary>
    public static bool Glob(ReadOnlySpan<char> s, ReadOnlySpan<char> p)
    {
        int si = 0, pi = 0, star = -1, mark = 0;
        while (si < s.Length)
        {
            if (pi < p.Length && (p[pi] == '?' || CharEq(p[pi], s[si]))) { si++; pi++; }
            else if (pi < p.Length && p[pi] == '*') { star = pi++; mark = si; }
            else if (star >= 0) { pi = star + 1; si = ++mark; }
            else return false;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }

    private static bool CharEq(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    private abstract class Term
    {
        public bool Negated;
        public virtual bool NeedsPath => false;
        public int Cost => NeedsPath ? 2 : this is RegexTerm ? 1 : 0;
        public abstract bool Match(string name, bool isDir, PathCache path);
    }

    private sealed class TextTerm(string text, bool onPath) : Term
    {
        public override bool NeedsPath => onPath;
        public override bool Match(string name, bool isDir, PathCache path) =>
            (onPath ? path.Get() : name).Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class WildcardTerm : Term
    {
        private readonly string _pattern;
        private readonly bool _onPath;
        private readonly string? _suffix; // "*.ext" fast path

        public WildcardTerm(string pattern, bool onPath)
        {
            _pattern = pattern;
            _onPath = onPath;
            if (pattern.Length > 1 && pattern[0] == '*' && pattern.IndexOfAny(new[] { '*', '?' }, 1) < 0) _suffix = pattern[1..];
        }

        public override bool NeedsPath => _onPath;
        public override bool Match(string name, bool isDir, PathCache path)
        {
            var s = _onPath ? path.Get() : name;
            return _suffix != null ? s.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase) : Glob(s, _pattern);
        }
    }

    private sealed class RegexTerm(Regex regex) : Term
    {
        public override bool Match(string name, bool isDir, PathCache path) => regex.IsMatch(name);
    }

    private sealed class KindTerm(bool folders) : Term
    {
        public override bool Match(string name, bool isDir, PathCache path) => isDir == folders;
    }

    private sealed class ExtTerm(string[] exts) : Term
    {
        public override bool Match(string name, bool isDir, PathCache path)
        {
            if (isDir) return false;
            int dot = name.LastIndexOf('.');
            if (dot < 0) return false;
            var ext = name.AsSpan(dot + 1);
            foreach (var e in exts)
                if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    private sealed class OrTerm(List<Term> parts) : Term
    {
        public override bool NeedsPath => parts.Any(p => p.NeedsPath);
        public override bool Match(string name, bool isDir, PathCache path)
        {
            foreach (var p in parts) if (p.Match(name, isDir, path)) return true;
            return false;
        }
    }
}
