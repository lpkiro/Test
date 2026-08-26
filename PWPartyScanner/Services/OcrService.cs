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

    private sealed record RegionRecognition(IReadOnlyList<string> Players, int DetectedCount);

    private sealed class RowCluster
    {
        public List<double> Centers { get; } = new();
        public HashSet<int> Thresholds { get; } = new();
        public int Top { get; private set; } = int.MaxValue;
        public int Bottom { get; private set; } = int.MinValue;
        public double Center => Centers.Count == 0 ? 0 : Centers.Average();
        public int Support => Thresholds.Count;

        public void Add(double center, int top, int bottom, int thresholdIndex)
        {
            Centers.Add(center);
            Thresholds.Add(thresholdIndex);
            Top = Math.Min(Top, top);
            Bottom = Math.Max(Bottom, bottom);
        }
    }

    public OcrService(string baseDir)
    {
        var tessdata = Path.Combine(baseDir, "tessdata");
        var players = Path.Combine(baseDir, "players_list.txt");

        if (!Directory.Exists(tessdata))
            throw new DirectoryNotFoundException("Не знайдено папку tessdata поруч із програмою.");

        _matcher = new GuildMatcher(players);
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

        var unique = nearby.Players
            .Concat(party.Players)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ScanResult
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            NearbyPlayers = nearby.Players,
            PartyPlayers = party.Players,
            UniquePlayers = unique,
            NearbyDetectedCount = nearby.DetectedCount,
            PartyDetectedCount = party.DetectedCount,
            Status = $"Готово. Область: {regions.Source}. Рядків на скріні — поряд: {nearby.DetectedCount}, група: {party.DetectedCount}."
        };
    }

    private (CvRect Nearby, CvRect Party, string Source) FindListRegions(Mat source)
    {
        // Best case: both column headers were recognized. Their positions describe the columns
        // directly and do not depend on the window position on screen.
        var headers = TryFindHeaders(source);
        if (headers is not null)
        {
            var (near, party) = headers.Value;
            var nearCenter = (near.X + near.Right) / 2.0;
            var partyCenter = (party.X + party.Right) / 2.0;
            var gap = Math.Max(60.0, partyCenter - nearCenter);
            var boundary = (int)Math.Round((nearCenter + partyCenter) / 2.0);
            var row = Math.Max(10, Math.Max(near.Height, party.Height));
            var top = Math.Max(near.Bottom, party.Bottom) + Math.Max(2, row / 6);

            var nearLeft = (int)Math.Round(near.X - gap * 0.10);
            var nearbyRect = Clamp(new CvRect(
                nearLeft,
                top,
                boundary - nearLeft,
                row * 21), source.Size());

            var partyLeft = boundary;
            var partyRight = (int)Math.Round(party.X + gap * 0.78);
            var partyRect = Clamp(new CvRect(
                partyLeft,
                top,
                partyRight - partyLeft,
                row * 8), source.Size());

            if (IsUsableRegion(nearbyRect, partyRect))
                return (nearbyRect, partyRect, "OCR-заголовки");
        }

        // On several real PW screenshots Tesseract reads the large title "Група" perfectly,
        // but cannot read the much smaller gold column headers.  The title is an excellent
        // anchor: its centre is the centre of this dialog and its glyph width scales together
        // with the game UI.  This fixes the regression where image2.png returned zero players.
        var title = TryFindDialogTitle(source);
        if (title is not null)
        {
            var regions = BuildRegionsFromDialogTitle(title.Value, source.Size());
            if (regions is not null)
                return (regions.Value.Nearby, regions.Value.Party, "OCR-заголовок «Група»");
        }

        // Last resort only. Hough lines are less reliable because spell/boss UI can create a
        // longer horizontal line than the dialog border.
        var fallback = FindByWindowTopLine(source);
        if (fallback is not null)
            return fallback.Value;

        throw new InvalidOperationException("Не вдалося знайти вікно «Група». Переконайся, що на скріні повністю видно таблиці «Гравці поряд» і «Члени групи».");
    }

    private static bool IsUsableRegion(CvRect nearby, CvRect party) =>
        nearby.Width > 40 && nearby.Height > 40 && party.Width > 40 && party.Height > 30;

    private (CvRect Near, CvRect Party)? TryFindHeaders(Mat source)
    {
        using var enlarged = new Mat();
        Cv2.Resize(source, enlarged, new Size(), 1.8, 1.8, InterpolationFlags.Cubic);
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

            var r = ScaleBack(box, 1.8);

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

    private CvRect? TryFindDialogTitle(Mat source)
    {
        const double scale = 1.8;
        using var enlarged = new Mat();
        Cv2.Resize(source, enlarged, new Size(), scale, scale, InterpolationFlags.Cubic);
        using var pix = Pix.LoadFromMemory(EncodePng(enlarged));
        using var page = _engine.Process(pix, PageSegMode.SparseText);
        using var iterator = page.GetIterator();

        (CvRect Rect, double Score)? best = null;

        iterator.Begin();
        do
        {
            var text = iterator.GetText(PageIteratorLevel.TextLine)?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out TessRect box)) continue;

            var r = ScaleBack(box, scale);
            var centerX = r.X + r.Width / 2.0;

            // Ignore the party HUD title at the far left of the screen.
            if (centerX < source.Width * 0.18 || centerX > source.Width * 0.78) continue;
            if (r.Y > source.Height * 0.38) continue;
            if (r.Width < 20 || r.Width > 100 || r.Height < 7 || r.Height > 36) continue;

            var normalized = NormalizeOcrText(text);
            var score = Similarity(normalized, "група");
            if (normalized.Contains("група", StringComparison.Ordinal)) score = 1.0;

            if (score < 0.68) continue;

            // Prefer a larger glyph box when more than one candidate survived.
            score += Math.Min(0.10, r.Width / 1000.0);
            if (best is null || score > best.Value.Score)
                best = (r, score);
        }
        while (iterator.Next(PageIteratorLevel.TextLine));

        return best?.Rect;
    }

    private static (CvRect Nearby, CvRect Party)? BuildRegionsFromDialogTitle(CvRect title, Size sourceSize)
    {
        // Measurements from the supplied Perfect World screenshots are extremely consistent:
        // dialog width / rendered title width ~= 9.3 on both UI scales.
        var dialogWidth = (int)Math.Round(title.Width * 9.35);
        if (dialogWidth < 240 || dialogWidth > sourceSize.Width * 0.62)
            return null;

        var titleCenter = title.X + title.Width / 2.0;
        var dialogX = (int)Math.Round(titleCenter - dialogWidth / 2.0);
        var dialogY = (int)Math.Round(title.Y - dialogWidth * 0.025);

        var listTop = dialogY + (int)Math.Round(dialogWidth * 0.130);
        var nearLeft = dialogX + (int)Math.Round(dialogWidth * 0.235);
        var boundary = dialogX + (int)Math.Round(dialogWidth * 0.590);
        var right = dialogX + (int)Math.Round(dialogWidth * 0.985);

        var nearby = Clamp(new CvRect(
            nearLeft,
            listTop,
            boundary - nearLeft,
            (int)Math.Round(dialogWidth * 0.62)), sourceSize);

        var party = Clamp(new CvRect(
            boundary,
            listTop,
            right - boundary,
            (int)Math.Round(dialogWidth * 0.27)), sourceSize);

        return IsUsableRegion(nearby, party) ? (nearby, party) : null;
    }

    private static CvRect ScaleBack(TessRect box, double scale) => new(
        (int)Math.Round(box.X1 / scale),
        (int)Math.Round(box.Y1 / scale),
        Math.Max(1, (int)Math.Round(box.Width / scale)),
        Math.Max(1, (int)Math.Round(box.Height / scale)));

    private static string NormalizeOcrText(string raw) =>
        new(raw.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static double HeaderScore(string raw, string whole, string word1, string word2)
    {
        var s = NormalizeOcrText(raw);
        if (s.Contains(word1) && s.Contains(word2)) return 1.0;
        return Similarity(s, whole);
    }

    private (CvRect Nearby, CvRect Party, string Source)? FindByWindowTopLine(Mat source)
    {
        using var gray = new Mat();
        using var edges = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Canny(gray, edges, 45, 130);

        var lines = Cv2.HoughLinesP(
            edges,
            1,
            Math.PI / 180,
            70,
            minLineLength: Math.Max(150, source.Width * 0.18),
            maxLineGap: 12);

        var candidates = lines
            .Where(l => Math.Abs(l.P2.Y - l.P1.Y) <= 2)
            .Select(l => new
            {
                Line = l,
                Len = Math.Abs(l.P2.X - l.P1.X),
                Y = (l.P1.Y + l.P2.Y) / 2.0
            })
            .Where(x => x.Y < source.Height * 0.45)
            .Where(x => ((x.Line.P1.X + x.Line.P2.X) / 2.0) > source.Width * 0.18 &&
                        ((x.Line.P1.X + x.Line.P2.X) / 2.0) < source.Width * 0.72)
            .Where(x => x.Len > source.Width * 0.22 && x.Len < source.Width * 0.48)
            .OrderByDescending(x => x.Len)
            .ToArray();

        if (candidates.Length == 0) return null;

        var top = candidates[0];
        var lineLeft = Math.Min(top.Line.P1.X, top.Line.P2.X);
        var estimatedWidth = Math.Min(source.Width, (int)Math.Round(top.Len * 1.02));
        var dialogX = Math.Max(0, lineLeft - (int)Math.Round(estimatedWidth * 0.015));
        var dialogY = Math.Max(0, (int)Math.Round(top.Y));

        var listTop = dialogY + (int)Math.Round(estimatedWidth * 0.125);
        var nearLeft = dialogX + (int)Math.Round(estimatedWidth * 0.235);
        var boundary = dialogX + (int)Math.Round(estimatedWidth * 0.585);
        var right = dialogX + (int)Math.Round(estimatedWidth * 0.985);

        var nearRect = Clamp(new CvRect(
            nearLeft,
            listTop,
            boundary - nearLeft,
            (int)Math.Round(estimatedWidth * 0.62)), source.Size());

        var partyRect = Clamp(new CvRect(
            boundary,
            listTop,
            right - boundary,
            (int)Math.Round(estimatedWidth * 0.25)), source.Size());

        if (!IsUsableRegion(nearRect, partyRect)) return null;
        return (nearRect, partyRect, "візуальний fallback");
    }

    private RegionRecognition RecognizeRegion(Mat source, CvRect rect, int maxExpected)
    {
        using var crop = new Mat(source, rect);
        var results = new List<string>();

        var rows = DetectTextRows(crop, maxExpected);

        foreach (var rowRect in rows)
        {
            using var rowCrop = new Mat(crop, rowRect);
            var match = RecognizeSingleRow(rowCrop);
            if (match is not null && !results.Contains(match, StringComparer.Ordinal))
                results.Add(match);
        }

        var maxRawLines = 0;
        foreach (var variant in BuildVariants(crop))
        {
            using (variant)
            using (var pix = Pix.LoadFromMemory(EncodePng(variant)))
            using (var page = _engine.Process(pix, PageSegMode.SingleColumn))
            {
                var rawLines = (page.GetText() ?? "")
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(IsLikelyNicknameLine)
                    .ToArray();

                maxRawLines = Math.Max(maxRawLines, Math.Min(maxExpected, rawLines.Length));

                foreach (var rawLine in rawLines)
                {
                    var match = _matcher.MatchLine(rawLine);
                    if (match is not null && !results.Contains(match, StringComparer.Ordinal))
                        results.Add(match);
                }
            }
        }

        var detected = Math.Max(results.Count, Math.Max(rows.Count, maxRawLines));
        detected = Math.Min(maxExpected, detected);

        return new RegionRecognition(results.Take(maxExpected).ToArray(), detected);
    }

    private string? RecognizeSingleRow(Mat rowCrop)
    {
        foreach (var variant in BuildVariants(rowCrop, rowMode: true))
        {
            using (variant)
            using (var pix = Pix.LoadFromMemory(EncodePng(variant)))
            using (var page = _engine.Process(pix, PageSegMode.SingleLine))
            {
                var raw = (page.GetText() ?? "").Trim();
                if (raw.Length == 0) continue;
                var match = _matcher.MatchLine(raw);
                if (match is not null) return match;
            }
        }

        return null;
    }

    private static IReadOnlyList<CvRect> DetectTextRows(Mat crop, int maxExpected)
    {
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);

        var thresholds = new[] { 70, 82, 94, 106, 118, 132, 148, 166 };
        var clusters = new List<RowCluster>();
        var minPixels = Math.Max(3, crop.Width / 55);

        for (var thresholdIndex = 0; thresholdIndex < thresholds.Length; thresholdIndex++)
        {
            using var mask = new Mat();
            Cv2.Threshold(gray, mask, thresholds[thresholdIndex], 255, ThresholdTypes.Binary);

            var counts = new int[mask.Rows];
            for (var y = 0; y < mask.Rows; y++)
            {
                using var row = mask.Row(y);
                counts[y] = Cv2.CountNonZero(row);
            }

            var groups = BuildActiveGroups(counts, minPixels);
            foreach (var (top, bottom) in groups)
            {
                var height = bottom - top + 1;
                if (height < 2 || height > 12) continue;

                var center = (top + bottom) / 2.0;
                if (center < 2 || center > crop.Height - 3) continue;

                var cluster = clusters.FirstOrDefault(c => Math.Abs(c.Center - center) <= 3.5);
                if (cluster is null)
                {
                    cluster = new RowCluster();
                    clusters.Add(cluster);
                }
                cluster.Add(center, top, bottom, thresholdIndex);
            }
        }

        var confirmed = clusters
            .Where(c => c.Support >= 2)
            .OrderBy(c => c.Center)
            .ToList();

        var compact = new List<RowCluster>();
        foreach (var cluster in confirmed)
        {
            if (compact.Count == 0 || cluster.Center - compact[^1].Center >= 7)
            {
                compact.Add(cluster);
                continue;
            }

            if (cluster.Support > compact[^1].Support)
                compact[^1] = cluster;
        }

        if (compact.Count > maxExpected)
        {
            compact = compact
                .OrderByDescending(c => c.Support)
                .ThenBy(c => c.Center)
                .Take(maxExpected)
                .OrderBy(c => c.Center)
                .ToList();
        }

        return compact
            .Select(c =>
            {
                var y = Math.Max(0, c.Top - 3);
                var bottom = Math.Min(crop.Height, c.Bottom + 4);
                return new CvRect(0, y, crop.Width, Math.Max(1, bottom - y));
            })
            .ToArray();
    }

    private static IReadOnlyList<(int Top, int Bottom)> BuildActiveGroups(int[] counts, int minPixels)
    {
        var groups = new List<(int Top, int Bottom)>();
        var start = -1;
        var lastActive = -1;

        for (var y = 0; y < counts.Length; y++)
        {
            if (counts[y] >= minPixels)
            {
                if (start < 0) start = y;
                lastActive = y;
                continue;
            }

            if (start >= 0 && y - lastActive > 2)
            {
                groups.Add((start, lastActive));
                start = -1;
                lastActive = -1;
            }
        }

        if (start >= 0)
            groups.Add((start, lastActive));

        return groups;
    }

    private static bool IsLikelyNicknameLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length > 28) return false;

        var lower = value.ToLowerInvariant();
        if (lower.Contains("гравц") || lower.Contains("поряд") || lower.Contains("член") ||
            lower.Contains("груп") || lower.Contains("виключ") || lower.Contains("вийти") ||
            lower.Contains("зміна") || lower.Contains("розпуст"))
            return false;

        return value.Count(char.IsLetterOrDigit) >= 2;
    }

    private static IEnumerable<Mat> BuildVariants(Mat crop, bool rowMode = false)
    {
        var scale = rowMode ? 4.5 : 3.2;

        var color = new Mat();
        Cv2.Resize(crop, color, new Size(), scale, scale, InterpolationFlags.Cubic);
        yield return color;

        using var gray0 = new Mat();
        Cv2.CvtColor(crop, gray0, ColorConversionCodes.BGR2GRAY);

        using var claheSource = new Mat();
        using (var clahe = Cv2.CreateCLAHE(2.7, new Size(8, 8)))
            clahe.Apply(gray0, claheSource);
        var claheLarge = new Mat();
        Cv2.Resize(claheSource, claheLarge, new Size(), scale, scale, InterpolationFlags.Cubic);
        yield return claheLarge;

        using var otsuSource = new Mat();
        Cv2.Threshold(gray0, otsuSource, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        if (Cv2.Mean(otsuSource).Val0 < 128)
            Cv2.BitwiseNot(otsuSource, otsuSource);
        var otsuLarge = new Mat();
        Cv2.Resize(otsuSource, otsuLarge, new Size(), scale, scale, InterpolationFlags.Nearest);
        yield return otsuLarge;

        using var adaptiveSource = new Mat();
        Cv2.AdaptiveThreshold(
            gray0,
            adaptiveSource,
            255,
            AdaptiveThresholdTypes.GaussianC,
            ThresholdTypes.Binary,
            15,
            4);
        var adaptiveLarge = new Mat();
        Cv2.Resize(adaptiveSource, adaptiveLarge, new Size(), scale, scale, InterpolationFlags.Nearest);
        yield return adaptiveLarge;
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
