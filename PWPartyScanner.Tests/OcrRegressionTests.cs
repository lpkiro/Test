using System.Text;
using PWPartyScanner.Models;
using PWPartyScanner.Services;

namespace PWPartyScanner.Tests;

public sealed class OcrRegressionTests
{
    public static IEnumerable<object[]> Fixtures()
    {
        yield return new object[]
        {
            "boss_sem_large.webp.b64",
            new[]
            {
                "Trinity", "Fedotov", "Конвалія", "Pedant", "РАКЛО", "ТОВАРНЯК", "m1d0", "Bybl",
                "ЖоссіСкаллі", "Руф", "Snowflake", "Evanika", "ИтЬоО", "★HESH★", "Телль", "SABRINA"
            },
            new[] { "Sem", "SABRINA", "Evanika", "Pedant", "Телль", "Asterita" },
            6
        };

        yield return new object[]
        {
            "boss_sem_small.webp.b64",
            new[]
            {
                "Вай", "Asterita", "Trinity", "Fedotov", "Конвалія", "Pedant", "РАКЛО", "ТОВАРНЯК",
                "m1d0", "Bybl", "ЖоссіСкаллі", "Руф", "Snowflake", "Evanika", "ИтЬоО", "★HESH★"
            },
            new[] { "Sem", "SABRINA", "Evanika", "Pedant", "Телль", "Asterita" },
            6
        };

        yield return new object[]
        {
            "party_3_rows.webp.b64",
            new[]
            {
                "Вай", "Asterita", "Trinity", "blaccsonic", "Nyx", "mirana", "skorbik", "РАКЛО",
                "ТОВАРНЯК", "ЖоссіСкаллі", "Джина", "Руф", "Snowflake", "Evanika", "m1d9", "★HESH★"
            },
            new[] { "excess", "blaccsonic" },
            3
        };

        yield return new object[]
        {
            "party_3_rows_shifted.webp.b64",
            new[]
            {
                "Asterita", "Trinity", "blaccsonic", "Nyx", "mirana", "skorbik", "РАКЛО", "ТОВАРНЯК",
                "ЖоссіСкаллі", "Джина", "Руф", "Snowflake", "Evanika", "m1d9", "★HESH★", "SABRINA"
            },
            new[] { "excess", "blaccsonic" },
            3
        };

        yield return new object[]
        {
            "image2_regression.webp.b64",
            new[]
            {
                "Вай", "Asterita", "Trinity", "blaccsonic", "Nyx", "mirana", "РАКЛО", "ТОВАРНЯК",
                "ЖоссіСкаллі", "Джина", "Руф", "Snowflake", "Evanika", "m1d9", "★HESH★", "SABRINA"
            },
            new[] { "excess", "blaccsonic", "SABRINA" },
            4
        };
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SuppliedScreenshot_RecognizesExpectedGuildMembers(
        string fixture,
        string[] expectedNearby,
        string[] expectedParty,
        int expectedPartyRows)
    {
        var imagePath = DecodeFixture(fixture);
        try
        {
            using var ocr = new OcrService(FindApplicationDirectory());
            ScanResult result = ocr.Scan(imagePath);

            AssertSetEqual(expectedNearby, result.NearbyPlayers, "Гравці поряд", result);
            AssertSetEqual(expectedParty, result.PartyPlayers, "Члени групи", result);

            var expectedUnique = expectedNearby
                .Concat(expectedParty)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            AssertSetEqual(expectedUnique, result.UniquePlayers, "Унікальні", result);

            Assert.True(result.NearbyDetectedCount >= expectedNearby.Length,
                $"Очікувалось щонайменше {expectedNearby.Length} рядків у «Гравці поряд», але визначено {result.NearbyDetectedCount}. Status: {result.Status}");
            Assert.Equal(expectedPartyRows, result.PartyDetectedCount);
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    [Fact]
    public void Image2Regression_MustNeverReturnZeroPlayers()
    {
        var imagePath = DecodeFixture("image2_regression.webp.b64");
        try
        {
            using var ocr = new OcrService(FindApplicationDirectory());
            var result = ocr.Scan(imagePath);

            Assert.NotEmpty(result.NearbyPlayers);
            Assert.NotEmpty(result.PartyPlayers);
            Assert.True(result.UniqueCount >= 17,
                $"image2 regression: отримано лише {result.UniqueCount}. Status: {result.Status}");
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    private static string DecodeFixture(string fixtureName)
    {
        var testData = Path.Combine(AppContext.BaseDirectory, "TestData");
        var base64Path = Path.Combine(testData, fixtureName);
        string base64;

        if (File.Exists(base64Path))
        {
            base64 = File.ReadAllText(base64Path).Trim();
        }
        else
        {
            var stem = fixtureName.EndsWith(".b64", StringComparison.OrdinalIgnoreCase)
                ? fixtureName[..^4]
                : fixtureName;
            var parts = Directory
                .GetFiles(testData, stem + ".part*.b64")
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            Assert.NotEmpty(parts);
            var builder = new StringBuilder();
            foreach (var part in parts)
                builder.Append(File.ReadAllText(part).Trim());
            base64 = builder.ToString();
        }

        var bytes = Convert.FromBase64String(base64);
        var output = Path.Combine(Path.GetTempPath(), $"pwparty_{Guid.NewGuid():N}.webp");
        File.WriteAllBytes(output, bytes);
        return output;
    }

    private static string FindApplicationDirectory()
    {
        var githubWorkspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(githubWorkspace))
        {
            var candidate = Path.Combine(githubWorkspace, "PWPartyScanner");
            if (File.Exists(Path.Combine(candidate, "players_list.txt")))
                return candidate;
        }

        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "PWPartyScanner");
            if (File.Exists(Path.Combine(candidate, "players_list.txt")))
                return candidate;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Не вдалося знайти папку PWPartyScanner для інтеграційних OCR тестів.");
    }

    private static void AssertSetEqual(
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        string label,
        ScanResult result)
    {
        var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);
        var actualSet = new HashSet<string>(actual, StringComparer.Ordinal);

        var missing = expectedSet.Except(actualSet, StringComparer.Ordinal).OrderBy(x => x).ToArray();
        var unexpected = actualSet.Except(expectedSet, StringComparer.Ordinal).OrderBy(x => x).ToArray();

        Assert.True(missing.Length == 0 && unexpected.Length == 0,
            $"{label}: невірний результат. Missing=[{string.Join(", ", missing)}], Unexpected=[{string.Join(", ", unexpected)}]. " +
            $"Actual=[{string.Join(", ", actualSet)}]. Status: {result.Status}");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
