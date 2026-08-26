using System.IO;
using OpenCvSharp;
using PWPartyScanner.Models;
using Tesseract;
using CvRect = OpenCvSharp.Rect;
using TessRect = Tesseract.Rect;

namespace PWPartyScanner.Services;

public sealed class OcrService : IDisposable
{
    private readonly TesseractEngine _engine;
    private readonly GuildMatcher _matcher;

    public OcrService(string baseDir)
    {
        var tessdata = Path.Combine(baseDir, "tessdata");
        var players = Path.Combine(baseDir, "players_list.txt");

        if (!Directory.Exists(tessdata))
            throw new DirectoryNotFoundException("Не знайдено папку tessdata поруч із програмою.");

        _matcher = new GuildMatcher(players);

        // The NuGet Tesseract 5.x package supports this constructor directly.
        // Using the default engine mode is also compatible with tessdata_fast
        // (the supplied models are LSTM models).
        _engine = new TesseractEngine(tessdata, "eng+ukr+rus");
        _engine.DefaultPageSegMode = PageSegMode.SparseText;
        _engine.SetVariable("preserve_interword_spaces", "1");
    }

    public ScanResult Scan(string filePath)
    {
        using var source = Cv2.ImRead(filePath, ImreadModes.Color);
        if (source.Empty())
            throw new InvalidOperationException("Не вдалося відкрити зображення.");

        var regions = FindListRegions(source);
        var nearby = RecognizeRegion(source, regions.Nearby, 24);
        var party = RecognizeRegion(source, regions.Party, 6);

        var unique = nearby.Concat(party)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ScanResult
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            NearbyPlayers = nearby,
            PartyPlayers = party,
            UniquePlayers = unique,
            Status = $"Готово. Область: {regions.Source}."
        };
    }

    private (CvRect Nearby, CvRect Party, string Source) FindListRegions(Mat source)
    {
        var headers = TryFindHeaders(source);
        if (headers is not null)
        {
            var (near, party) = headers.Value;
            var nearCenter = (near.X + near.Right) / 2.0;
            var partyCenter = (party.X + party.Right) / 2.0;
            var gap = Math.Max(60.0, partyCenter - nearCenter);
            var boundary = (int)Math.Round((nearCenter + partyCenter) / 2.0);
            var row = Math.Max(11, Math.Max(near.Height, party.Height));
            var top = Math.Max(near.Bottom, party.Bottom) + Math.Max(2, row / 5);

            var nearLeft = (int)Math.Round(near.X - gap * 0.16);
            var nearbyRect = Clamp(new CvRect(
                nearLeft,
                top,
                boundary - nearLeft,
                row * 20), source.Size());

            var partyRight = (int)Math.Round(boundary + gap * 1.10);
            var partyRect = Clamp(new CvRect(
                boundary,
                top,
                partyRight - boundary,
                row * 9), source.Size());

            if (nearbyRect.Width > 40 && nearbyRect.Height > 40 && partyRect.Width > 40 && partyRect.Height > 40)
                return (nearbyRect, partyRect, "OCR-заголовки");
        }

        var fallback = FindByWindowTopLine(source);
        if (fallback is not null)
            return fallback.Value;

        throw new InvalidOperationException("Не вдалося знайти вікно «Група». Переконайся, що на скріні повністю видно таблиці «Гравці поряд» і «Члени групи».");
    }

    private (CvRect Near, CvRect Party)? TryFindHeaders(Mat source)
    {
        using var enlarged = new Mat();
        Cv2.Resize(source, enlarged, new Size(), 1.7, 1.7, InterpolationFlags.Cubic);
        using var pix = Pix.LoadFromMemory(EncodePng(enlarged));
        using var page = _engine.Process(pix, PageSegMode.SparseText);
        using var iterator = page.GetIterator();

        (CvRect Rect, double Score)? nearBest = null;
        (CvRect Rect, double Score)? partyBest = null;

        iterator.Begin();
        do
        {
            var text = iterator.GetText(PageIteratorLevel.TextLine)?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out TessRect box)) continue;

            var r = new CvRect(
                (int)Math.Round(box.X1 / 1.7),
                (int)Math.Round(box.Y1 / 1.7),
                Math.Max(1, (int)Math.Round(box.Width / 1.7)),
                Math.Max(1, (int)Math.Round(box.Height / 1.7)));

            var nearScore = HeaderScore(text, "гравціпоряд", "гравці", "поряд");
            if (nearScore > 0.52 && (nearBest is null || nearScore > nearBest.Value.Score))
                nearBest = (r, nearScore);

            var partyScore = HeaderScore(text, "членигрупи", "члени", "групи");
            if (partyScore > 0.52 && (partyBest is null || partyScore > partyBest.Value.Score))
                partyBest = (r, partyScore);
        }
        while (iterator.Next(PageIteratorLevel.TextLine));

        if (nearBest is null || partyBest is null) return null;
        if (partyBest.Value.Rect.X <= nearBest.Value.Rect.X) return null;
        return (nearBest.Value.Rect, partyBest.Value.Rect);
    }

    private static double HeaderScore(string raw, string whole, string word1, string word2)
    {
        var s = new string(raw.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (s.Contains(word1) && s.Contains(word2)) return 1.0;
        return Similarity(s, whole);
    }

    private (CvRect Nearby, CvRect Party, string Source)? FindByWindowTopLine(Mat source)
    {
        using var gray = new Mat();
        using var edges = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Canny(gray, edges, 45, 130);

        var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180, 70,
            minLineLength: Math.Max(150, source.Width * 0.18), maxLineGap: 12);

        var candidates = lines
            .Where(l => Math.Abs(l.P2.Y - l.P1.Y) <= 2)
            .Select(l => new { Line = l, Len = Math.Abs(l.P2.X - l.P1.X), Y = (l.P1.Y + l.P2.Y) / 2.0 })
            .Where(x => x.Y < source.Height * 0.45)
            .Where(x => ((x.Line.P1.X + x.Line.P2.X) / 2.0) > source.Width * 0.18 && ((x.Line.P1.X + x.Line.P2.X) / 2.0) < source.Width * 0.72)
            .Where(x => x.Len > source.Width * 0.22 && x.Len < source.Width * 0.48)
            .OrderByDescending(x => x.Len)
            .ToArray();

        if (candidates.Length == 0) return null;

        var top = candidates[0];
        var lineLeft = Math.Min(top.Line.P1.X, top.Line.P2.X);
        var estimatedWidth = Math.Min(source.Width, (int)Math.Round(top.Len * 1.25));
        var dialogX = Math.Max(0, lineLeft - (int)Math.Round(estimatedWidth * 0.08));
        var dialogY = Math.Max(0, (int)Math.Round(top.Y - estimatedWidth * 0.01));

        var listTop = dialogY + (int)Math.Round(estimatedWidth * 0.135);
        var nearLeft = dialogX + (int)Math.Round(estimatedWidth * 0.24);
        var boundary = dialogX + (int)Math.Round(estimatedWidth * 0.60);
        var right = dialogX + (int)Math.Round(estimatedWidth * 0.98);

        var nearRect = Clamp(new CvRect(nearLeft, listTop, boundary - nearLeft, (int)Math.Round(estimatedWidth * 0.58)), source.Size());
        var partyRect = Clamp(new CvRect(boundary, listTop, right - boundary, (int)Math.Round(estimatedWidth * 0.28)), source.Size());

        if (nearRect.Width < 40 || partyRect.Width < 40) return null;
        return (nearRect, partyRect, "візуальний fallback");
    }

    private IReadOnlyList<string> RecognizeRegion(Mat source, CvRect rect, int maxExpected)
    {
        using var crop = new Mat(source, rect);
        var results = new List<string>();

        foreach (var variant in BuildVariants(crop))
        {
            using (variant)
            using (var pix = Pix.LoadFromMemory(EncodePng(variant)))
            using (var page = _engine.Process(pix, PageSegMode.SingleColumn))
            {
                foreach (var rawLine in (page.GetText() ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = _matcher.MatchLine(rawLine.Trim());
                    if (match is not null && !results.Contains(match, StringComparer.Ordinal))
                        results.Add(match);
                }
            }
        }

        return results.Take(maxExpected).ToArray();
    }

    private static IEnumerable<Mat> BuildVariants(Mat crop)
    {
        var color = new Mat();
        Cv2.Resize(crop, color, new Size(), 3.0, 3.0, InterpolationFlags.Cubic);
        yield return color;

        using var gray0 = new Mat();
        Cv2.CvtColor(crop, gray0, ColorConversionCodes.BGR2GRAY);

        var gray = new Mat();
        using (var clahe = Cv2.CreateCLAHE(2.5, new Size(8, 8)))
            clahe.Apply(gray0, gray);
        Cv2.Resize(gray, gray, new Size(), 3.0, 3.0, InterpolationFlags.Cubic);
        yield return gray;

        using var thresholdBase = new Mat();
        Cv2.Threshold(gray0, thresholdBase, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        var threshold = thresholdBase.Clone();
        if (Cv2.Mean(threshold).Val0 < 128)
            Cv2.BitwiseNot(threshold, threshold);
        Cv2.Resize(threshold, threshold, new Size(), 3.2, 3.2, InterpolationFlags.Nearest);
        yield return threshold;
    }

    private static byte[] EncodePng(Mat mat)
    {
        Cv2.ImEncode(".png", mat, out var bytes);
        return bytes;
    }

    private static CvRect Clamp(CvRect r, Size size)
    {
        var x = Math.Clamp(r.X, 0, Math.Max(0, size.Width - 1));
        var y = Math.Clamp(r.Y, 0, Math.Max(0, size.Height - 1));
        var right = Math.Clamp(r.Right, x + 1, size.Width);
        var bottom = Math.Clamp(r.Bottom, y + 1, size.Height);
        return new CvRect(x, y, right - x, bottom - y);
    }

    private static double Similarity(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        var cur = new int[b.Length + 1];
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
        return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }

    public void Dispose() => _engine.Dispose();
}
