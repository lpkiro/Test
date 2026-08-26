using System.IO;
using OpenCvSharp;
using PWPartyScanner.Models;
using Tesseract;
using CvRect = OpenCvSharp.Rect;
using TessRect = Tesseract.Rect;

namespace PWPartyScanner.Services;

public sealed class OcrService : IDisposable
{
    private readonly TesseractEngine _engineMulti;
    private readonly TesseractEngine _engineEng;
    private readonly GuildMatcher _matcher;

    public OcrService(string baseDir)
    {
        var tessdata = Path.Combine(baseDir, "tessdata");
        var players = Path.Combine(baseDir, "players_list.txt");

        if (!Directory.Exists(tessdata))
            throw new DirectoryNotFoundException("Не знайдено папку tessdata поруч із програмою.");

        _matcher = new GuildMatcher(players);

        // Two engines are intentional:
        //  - eng+ukr+rus is better for mixed/cyrillic names;
        //  - eng is noticeably better for short latin names such as m1d9.
        _engineMulti = new TesseractEngine(tessdata, "eng+ukr+rus");
        _engineMulti.DefaultPageSegMode = PageSegMode.SparseText;
        _engineMulti.SetVariable("preserve_interword_spaces", "1");

        _engineEng = new TesseractEngine(tessdata, "eng");
        _engineEng.DefaultPageSegMode = PageSegMode.SparseText;
        _engineEng.SetVariable("preserve_interword_spaces", "1");
    }

    public ScanResult Scan(string filePath)
    {
        using var source = Cv2.ImRead(filePath, ImreadModes.Color);
        if (source.Empty())
            throw new InvalidOperationException("Не вдалося відкрити зображення.");

        var regions = FindListRegions(source);
        var nearby = RecognizeRegion(source, regions.Nearby, 24, regions.RowHeight);
        var party = RecognizeRegion(source, regions.Party, 6, regions.RowHeight);

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

    private (CvRect Nearby, CvRect Party, string Source, double RowHeight) FindListRegions(Mat source)
    {
        // IMPORTANT: this is now the primary locator. It reproduces the stable
        // pixel-border locator from v1.5, which was already validated on the
        // supplied Perfect World screenshots. The previous Hough calibration
        // overestimated the dialog width by ~25%, which moved the list top down
        // (losing Вай/Asterita) and moved the party crop too far right.
        var border = FindByDialogBorderPixels(source);
        if (border is not null)
            return border.Value;

        // OCR headers are only a fallback now.
        var headers = TryFindHeaders(source);
        if (headers is not null)
        {
            var (near, party) = headers.Value;
            var nearCenter = (near.X + near.Right) / 2.0;
            var partyCenter = (party.X + party.Right) / 2.0;
            var gap = Math.Max(60.0, partyCenter - nearCenter);
            var boundary = (int)Math.Round((nearCenter + partyCenter) / 2.0);
            var headerHeight = Math.Max(10, Math.Max(near.Height, party.Height));
            var rowHeight = Math.Max(12.0, headerHeight * 1.55);
            var top = Math.Max(near.Bottom, party.Bottom) + Math.Max(1, (int)Math.Round(rowHeight * 0.10));

            var nearLeft = (int)Math.Round(near.X - gap * 0.16);
            var nearbyRect = Clamp(new CvRect(
                nearLeft,
                top,
                boundary - nearLeft,
                (int)Math.Round(rowHeight * 17.0)), source.Size());

            var partyLeft = (int)Math.Round(boundary - rowHeight * 0.8);
            var partyRight = (int)Math.Round(boundary + gap * 1.10);
            var partyRect = Clamp(new CvRect(
                partyLeft,
                top,
                partyRight - partyLeft,
                (int)Math.Round(rowHeight * 8.0)), source.Size());

            if (nearbyRect.Width > 40 && nearbyRect.Height > 40 && partyRect.Width > 40 && partyRect.Height > 40)
                return (nearbyRect, partyRect, "OCR-заголовки", rowHeight);
        }

        throw new InvalidOperationException("Не вдалося знайти вікно «Група». Переконайся, що на скріні повністю видно таблиці «Гравці поряд» і «Члени групи».");
    }

    private (CvRect Nearby, CvRect Party, string Source, double RowHeight)? FindByDialogBorderPixels(Mat source)
    {
        if (source.Width < 200 || source.Height < 120)
            return null;

        var scanY1 = Math.Min(source.Height - 1, (int)Math.Round(source.Height * 0.48));
        var scanX0 = Math.Max(0, (int)Math.Round(source.Width * 0.02));
        const int stepX = 2;

        var bestWidth = 0;
        var bestX = 0;
        var bestY = 0;

        for (var y = 0; y <= scanY1; y++)
        {
            var runX = -1;
            var runSamples = 0;

            for (var x = scanX0; x < source.Width; x += stepX)
            {
                var bgr = source.At<Vec3b>(y, x);
                var ok = IsDialogBorderPixel(bgr);

                if (ok)
                {
                    if (runX < 0) runX = x;
                    runSamples++;
                    continue;
                }

                if (runSamples > 0)
                {
                    var runWidth = runSamples * stepX;
                    if (runWidth > bestWidth)
                    {
                        bestWidth = runWidth;
                        bestX = runX;
                        bestY = y;
                    }

                    runX = -1;
                    runSamples = 0;
                }
            }

            if (runSamples > 0)
            {
                var runWidth = runSamples * stepX;
                if (runWidth > bestWidth)
                {
                    bestWidth = runWidth;
                    bestX = runX;
                    bestY = y;
                }
            }
        }

        var minimum = Math.Max(140.0, source.Width * 0.18);
        if (bestWidth < minimum)
            return null;

        // The detected run begins a few pixels inside the decorative corners.
        var padding = Math.Max(4.0, bestWidth * 0.017);
        var left = Math.Max(0.0, bestX - padding);
        var dialogWidth = Math.Min(source.Width - left, bestWidth + 2.0 * padding);
        var top = (double)bestY;

        // These proportions are tied to the PW dialog itself, not screen size.
        // For the 1074x477 supplied test screenshot this yields approximately:
        // nearby: x=416..587, y=100..367
        // party : x=573..753, y=100..237
        var nearX0 = left + 0.22 * dialogWidth;
        var nearX1 = left + 0.60 * dialogWidth;
        var nearY0 = top + 0.125 * dialogWidth;
        var nearY1 = top + 0.72 * dialogWidth;

        var partyX0 = left + 0.57 * dialogWidth;
        var partyX1 = left + 0.97 * dialogWidth;
        var partyY0 = top + 0.125 * dialogWidth;
        var partyY1 = top + 0.43 * dialogWidth;

        var nearby = Clamp(RectFromBounds(nearX0, nearY0, nearX1, nearY1), source.Size());
        var party = Clamp(RectFromBounds(partyX0, partyY0, partyX1, partyY1), source.Size());

        if (nearby.Width < 40 || nearby.Height < 40 || party.Width < 40 || party.Height < 40)
            return null;

        return (nearby, party, "рамка вікна", dialogWidth / 27.0);
    }

    private static bool IsDialogBorderPixel(Vec3b bgr)
    {
        var b = (int)bgr.Item0;
        var g = (int)bgr.Item1;
        var r = (int)bgr.Item2;

        return r >= 118 && r <= 198
            && g >= 105 && g <= 188
            && b >= 82 && b <= 172
            && Math.Abs(r - g) <= 42
            && r - b >= -10 && r - b <= 62
            && g - b >= -12 && g - b <= 58;
    }

    private static CvRect RectFromBounds(double x0, double y0, double x1, double y1)
    {
        var x = (int)Math.Floor(x0);
        var y = (int)Math.Floor(y0);
        var right = (int)Math.Ceiling(x1);
        var bottom = (int)Math.Ceiling(y1);
        return new CvRect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    private (CvRect Near, CvRect Party)? TryFindHeaders(Mat source)
    {
        using var enlarged = new Mat();
        Cv2.Resize(source, enlarged, new Size(), 1.7, 1.7, InterpolationFlags.Cubic);
        using var pix = Pix.LoadFromMemory(EncodePng(enlarged));
        using var page = _engineMulti.Process(pix, PageSegMode.SparseText);
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

    private IReadOnlyList<string> RecognizeRegion(Mat source, CvRect rect, int maxExpected, double rowHeight)
    {
        using var crop = new Mat(source, rect);
        var results = new List<string>();

        // Whole-column passes provide broad coverage.
        foreach (var variant in BuildColumnVariants(crop))
        {
            using (variant)
            {
                AddMatchesFromImage(variant, _engineMulti, PageSegMode.SingleColumn, results);
                AddMatchesFromImage(variant, _engineMulti, PageSegMode.SparseText, results);
            }
        }

        // Row-by-row passes are important for two cases that whole-column OCR
        // struggles with in PW:
        //  1) highlighted rows in "Члени групи" (e.g. blaccsonic);
        //  2) very short latin names / digit-heavy names (e.g. Вай, m1d9).
        AddRowMatches(crop, rowHeight, maxExpected, _engineMulti, results);
        AddRowMatches(crop, rowHeight, maxExpected, _engineEng, results);

        return results.Take(maxExpected).ToArray();
    }

    private void AddRowMatches(Mat crop, double rowHeight, int maxExpected, TesseractEngine engine, List<string> results)
    {
        if (rowHeight < 8 || rowHeight > 40)
            return;

        var rowsByHeight = Math.Max(1, (int)Math.Ceiling(crop.Height / rowHeight));
        var rowCount = Math.Min(maxExpected, rowsByHeight + 1);
        var trimX = Math.Max(4, (int)Math.Round(rowHeight * 0.45));

        for (var i = 0; i < rowCount; i++)
        {
            var y0 = (int)Math.Round(i * rowHeight - 1.5);
            var y1 = (int)Math.Round((i + 1) * rowHeight + 2.0);
            y0 = Math.Clamp(y0, 0, Math.Max(0, crop.Height - 1));
            y1 = Math.Clamp(y1, y0 + 1, crop.Height);

            var x0 = Math.Clamp(trimX, 0, Math.Max(0, crop.Width - 2));
            var x1 = Math.Max(x0 + 1, crop.Width - Math.Max(2, trimX / 2));
            if (x1 <= x0 || y1 <= y0) continue;

            using var row = new Mat(crop, new CvRect(x0, y0, x1 - x0, y1 - y0));
            foreach (var variant in BuildRowVariants(row))
            {
                using (variant)
                    AddMatchesFromImage(variant, engine, PageSegMode.SingleLine, results);
            }
        }
    }

    private void AddMatchesFromImage(Mat image, TesseractEngine engine, PageSegMode mode, List<string> results)
    {
        using var pix = Pix.LoadFromMemory(EncodePng(image));
        using var page = engine.Process(pix, mode);
        var text = page.GetText() ?? string.Empty;

        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = _matcher.MatchLine(rawLine.Trim());
            if (match is not null && !results.Contains(match, StringComparer.Ordinal))
                results.Add(match);
        }
    }

    private static IEnumerable<Mat> BuildColumnVariants(Mat crop)
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

    private static IEnumerable<Mat> BuildRowVariants(Mat row)
    {
        var color = new Mat();
        Cv2.Resize(row, color, new Size(), 4.0, 4.0, InterpolationFlags.Cubic);
        yield return color;

        using var grayBase = new Mat();
        Cv2.CvtColor(row, grayBase, ColorConversionCodes.BGR2GRAY);

        var gray = new Mat();
        Cv2.Resize(grayBase, gray, new Size(), 4.0, 4.0, InterpolationFlags.Cubic);
        yield return gray;

        var adaptive = new Mat();
        using (var enlarged = new Mat())
        {
            Cv2.Resize(grayBase, enlarged, new Size(), 5.0, 5.0, InterpolationFlags.Cubic);
            Cv2.AdaptiveThreshold(enlarged, adaptive, 255,
                AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 31, 7);
        }
        yield return adaptive;
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

    public void Dispose()
    {
        _engineEng.Dispose();
        _engineMulti.Dispose();
    }
}
