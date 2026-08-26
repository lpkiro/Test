using System.IO;
using System.Text;

namespace PWPartyScanner.Services;

public sealed class GuildMatcher
{
    private readonly IReadOnlyList<string> _canonicalNames;
    private const double Threshold = 0.90;

    public GuildMatcher(string playersFile)
    {
        if (!File.Exists(playersFile))
            throw new FileNotFoundException("Не знайдено players_list.txt поруч із програмою.", playersFile);

        _canonicalNames = File.ReadAllLines(playersFile, Encoding.UTF8)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (_canonicalNames.Count == 0)
            throw new InvalidOperationException("players_list.txt порожній.");
    }

    public string? MatchLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var candidates = BuildOcrCandidates(raw).Distinct().ToArray();
        string? bestName = null;
        double bestScore = 0;

        foreach (var canonical in _canonicalNames)
        {
            var canonicalVariants = BuildVariants(canonical).Distinct().ToArray();
            foreach (var candidate in candidates)
            foreach (var target in canonicalVariants)
            {
                if (candidate.Length == 0 || target.Length == 0) continue;
                var score = Similarity(candidate, target);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestName = canonical;
                }
            }
        }

        return bestScore >= Threshold ? bestName : null;
    }

    private static IEnumerable<string> BuildOcrCandidates(string raw)
    {
        foreach (var variant in BuildVariants(raw))
            yield return variant;

        foreach (var token in raw.Split(
                     new[] { ' ', '\t', '|', ':', ';', ',', '[', ']', '(', ')', '{', '}', '/', '\\' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var variant in BuildVariants(token))
                yield return variant;
        }
    }

    private static IEnumerable<string> BuildVariants(string value)
    {
        var basic = Normalize(value, false);
        if (basic.Length > 0) yield return basic;

        var visual = Normalize(value, true);
        if (visual.Length > 0 && visual != basic) yield return visual;
    }

    private static string Normalize(string value, bool visual)
    {
        value = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(value.Length);

        foreach (var ch0 in value)
        {
            if (!char.IsLetterOrDigit(ch0)) continue;

            // Confusions that are common in the tiny Perfect World font.
            var ch = ch0 switch
            {
                '0' => 'o',
                '1' => 'l',
                '5' => 's',
                _ => ch0
            };

            if (visual)
            {
                ch = ch switch
                {
                    // Cyrillic glyphs frequently read as their Latin lookalikes.
                    'а' => 'a', 'е' => 'e', 'і' => 'i', 'ї' => 'i', 'й' => 'i', 'о' => 'o',
                    'р' => 'p', 'с' => 'c', 'у' => 'y', 'х' => 'x', 'к' => 'k',
                    'м' => 'm', 'т' => 't', 'в' => 'b', 'н' => 'h', 'ё' => 'e',

                    // Short Latin nicknames need equivalence classes before the 90% rule.
                    // Example: m1d9 is commonly OCR-ed as midg; both normalize to mldg.
                    'i' => 'l', '9' => 'g', 'q' => 'g',
                    _ => ch
                };
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        var max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1.0;
        return 1.0 - (double)Levenshtein(a, b) / max;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}
