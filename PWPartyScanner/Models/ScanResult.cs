namespace PWPartyScanner.Models;

public sealed class ScanResult
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";

    // Only canonical nicknames from players_list.txt are stored here.
    public IReadOnlyList<string> NearbyPlayers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PartyPlayers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UniquePlayers { get; init; } = Array.Empty<string>();

    // Number of visually detected text rows before the whitelist is applied.
    // This makes it clear when a party contains somebody who is not in players_list.txt.
    public int NearbyDetectedCount { get; init; }
    public int PartyDetectedCount { get; init; }

    public string Status { get; init; } = "";

    public int NearbyCount => NearbyPlayers.Count;
    public int PartyCount => PartyPlayers.Count;
    public int UniqueCount => UniquePlayers.Count;
}
