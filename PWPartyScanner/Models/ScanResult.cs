namespace PWPartyScanner.Models;

public sealed class ScanResult
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public IReadOnlyList<string> NearbyPlayers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PartyPlayers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UniquePlayers { get; init; } = Array.Empty<string>();
    public string Status { get; init; } = "";

    public int NearbyCount => NearbyPlayers.Count;
    public int PartyCount => PartyPlayers.Count;
    public int UniqueCount => UniquePlayers.Count;
    public string PlayersText => string.Join(", ", UniquePlayers);
}
