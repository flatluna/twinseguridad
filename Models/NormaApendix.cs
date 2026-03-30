using System.Text.Json.Serialization;

namespace TwinSeguridad.Models;

/// <summary>
/// Estructura del archivo norma-apendix.json (entrada).
/// Contiene document + entries con secciones, imágenes y cierre.
/// </summary>
public class NormaApendixInput
{
    [JsonPropertyName("document")]
    public ApendixDocument Document { get; set; } = new();

    [JsonPropertyName("entries")]
    public List<ApendixEntry> Entries { get; set; } = [];
}

public class ApendixDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("signature_date")]
    public string SignatureDate { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;
}

public class ApendixEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }
}

/// <summary>
/// Estructura del archivo seguridad-apendix.json (salida).
/// Igual que la entrada pero con totalTokens por cada entry.
/// </summary>
public class SeguridadApendix
{
    [JsonPropertyName("document")]
    public ApendixDocument Document { get; set; } = new();

    [JsonPropertyName("totalTokensDocumento")]
    public int TotalTokensDocumento { get; set; }

    [JsonPropertyName("totalEntries")]
    public int TotalEntries { get; set; }

    [JsonPropertyName("modeloTokenizacion")]
    public string ModeloTokenizacion { get; set; } = "cl100k_base";

    [JsonPropertyName("fechaGeneracion")]
    public DateTime FechaGeneracion { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("entries")]
    public List<SeguridadApendixEntry> Entries { get; set; } = [];
}

public class SeguridadApendixEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; set; }
}
