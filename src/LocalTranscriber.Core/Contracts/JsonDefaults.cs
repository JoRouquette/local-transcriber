using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalTranscriber.Core.Contracts;

/// <summary>
/// Options JSON partagees. Le moteur Python echange en snake_case ;
/// on aligne la serialisation .NET dessus pour que les contrats soient identiques
/// des deux cotes (EngineRequest ecrit par le service, EngineResult lu du moteur).
/// </summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
