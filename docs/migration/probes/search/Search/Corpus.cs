using System.Text;

namespace Probe.Search;

/// <summary>One search document as every engine receives it.</summary>
public sealed record Doc(Guid Tenant, Guid Id, string Type, string Title, string Body);

/// <summary>
/// A deterministic synthetic corpus: 20,000 pseudo-words with a Zipf (s = 1) frequency, titles of 3–6 words, bodies of
/// 15–40 words. Document i is the same at every size (Random(i)), so a corpus grows 10k → 100k → 1M by appending.
/// Tenants: i % 10 == 0 → Big (10 %), i % 1000 == 1 → Small (0.1 %), otherwise one of 48 others. Types: 3/5 auction,
/// 1/5 artist, 1/5 lot (an entity of an auction).
/// </summary>
public static class Corpus
{
    public const int Words = 20_000;
    public static readonly Guid Big = Tenant(0), Small = Tenant(1);
    public static readonly string[] Vocabulary = BuildVocabulary();
    private static readonly double[] Cumulative = BuildCumulative();
    private static readonly string[] Types = ["auction", "auction", "auction", "artist", "lot"];

    public static Guid Tenant(int n) => new(n + 1, 0x7a7a, 0x4000, [0x80, 0, 0, 0, 0, 0, 0, 1]);

    public static Guid IdOf(long i) => new((int)(i >> 16), (short)(i & 0xffff), 0x4000, [0x81, 0, 0, 0, 0, 0, 0, 2]);

    public static Guid TenantOf(long i) => i % 10 == 0 ? Big : i % 1000 == 1 ? Small : Tenant(2 + (int)(i % 48));

    public static Doc Make(long i)
    {
        var r = new Random((int)i);
        return new Doc(TenantOf(i), IdOf(i), Types[i % 5], Text(r, 3 + r.Next(4)), Text(r, 15 + r.Next(26)));
    }

    public static IEnumerable<Doc> Range(long from, long to)
    {
        for (var i = from; i < to; i++)
            yield return Make(i);
    }

    /// <summary>The word at frequency rank <paramref name="rank"/> (1 = the most frequent).</summary>
    public static string Rank(int rank) => Vocabulary[rank - 1];

    private static string Text(Random r, int words)
    {
        var sb = new StringBuilder(words * 8);
        for (var w = 0; w < words; w++)
        {
            if (w > 0) sb.Append(' ');
            var x = r.NextDouble();
            var k = Array.BinarySearch(Cumulative, x);
            sb.Append(Vocabulary[k < 0 ? Math.Min(~k, Words - 1) : k]);
        }
        return sb.ToString();
    }

    private static string[] BuildVocabulary()
    {
        string[] syllables = ["ka", "ve", "lo", "ri", "mun", "tas", "pe", "dor", "si", "gal", "nu", "bre", "ot", "fi", "zan", "mi",
            "ul", "che", "rad", "po", "les", "ta", "wen", "ko", "dri", "sa", "mel", "on", "gu", "ber", "ni", "hal"];
        var r = new Random(7);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var words = new string[Words];
        for (var n = 0; n < Words;)
        {
            var parts = 2 + r.Next(3);
            var sb = new StringBuilder();
            for (var p = 0; p < parts; p++) sb.Append(syllables[r.Next(syllables.Length)]);
            var w = sb.ToString();
            if (w.Length >= 4 && seen.Add(w)) words[n++] = w;
        }
        return words;
    }

    private static double[] BuildCumulative()
    {
        var c = new double[Words];
        double sum = 0;
        for (var k = 0; k < Words; k++) sum += 1.0 / (k + 1);
        double acc = 0;
        for (var k = 0; k < Words; k++) { acc += 1.0 / (k + 1) / sum; c[k] = acc; }
        c[^1] = 1.0;
        return c;
    }
}
