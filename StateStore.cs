using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelativisticRod;

public sealed class PersistedState
{
    public double VWave { get; init; } = 0.25;
    public double Freq { get; init; } = 0.60;
    public double ObsY { get; init; } = -25;
    public bool IsPlaying { get; init; } = true;
    public bool ShowPhotons { get; init; } = true;
}

[JsonSerializable(typeof(PersistedState))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class StateJsonContext : JsonSerializerContext { }

/// <summary>Replaces the HTML pageVault: keeps slider/toggle state in %LOCALAPPDATA% (works packaged or unpackaged).</summary>
internal static class StateStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelativisticRod", "state.json");

    public static PersistedState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize(File.ReadAllText(FilePath), StateJsonContext.Default.PersistedState);
                if (s is not null)
                {
                    return new PersistedState
                    {
                        VWave = Math.Clamp(s.VWave, 0.10, 0.80),
                        Freq = Math.Clamp(s.Freq, 0.10, 1.50),
                        ObsY = Math.Clamp(s.ObsY, -60, -10),
                        IsPlaying = s.IsPlaying,
                        ShowPhotons = s.ShowPhotons
                    };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new PersistedState();
    }

    public static void Save(PersistedState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, StateJsonContext.Default.PersistedState));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
