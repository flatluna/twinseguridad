using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpToken;
using TwinSeguridad.Models;

namespace TwinSeguridad.Services;

/// <summary>
/// Servicio que lee norma-apendix.json (entrada), calcula totalTokens por cada entry
/// usando SharpToken (cl100k_base) y genera seguridad-apendix.json (salida).
///
/// Entrada:  Documents/norma-apendix.json
/// Salida:   Documents/seguridad-apendix.json
///
/// Endpoint: GET /api/seguridad/apendix
/// </summary>
public class NormaApendixService
{
    private readonly ILogger<NormaApendixService> _logger;
    private readonly GptEncoding _encoding;

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public NormaApendixService(ILogger<NormaApendixService> logger)
    {
        _logger = logger;
        _encoding = GptEncoding.GetEncoding("cl100k_base");
    }

    /// <summary>
    /// Lee norma-apendix.json, calcula tokens por entry y genera seguridad-apendix.json.
    /// </summary>
    public async Task<(SeguridadApendix Result, string OutputPath)> ProcesarAsync(string inputJsonPath)
    {
        if (!File.Exists(inputJsonPath))
            throw new FileNotFoundException($"No se encontró: {inputJsonPath}");

        _logger.LogInformation("?? Leyendo norma-apendix: {Path}", inputJsonPath);

        var json = await File.ReadAllTextAsync(inputJsonPath, Encoding.UTF8);

        // Limpiar posible basura después del JSON válido (texto extra del chat, BOM, etc.)
        var lastBrace = json.LastIndexOf('}');
        if (lastBrace >= 0 && lastBrace < json.Length - 1)
        {
            _logger.LogWarning("?? Texto extra detectado después del JSON (pos {Pos}/{Len}), recortando.",
                lastBrace, json.Length);
            json = json[..(lastBrace + 1)];
        }

        // Quitar BOM si existe
        if (json.Length > 0 && json[0] == '\uFEFF')
            json = json[1..];

        var input = JsonSerializer.Deserialize<NormaApendixInput>(json, JsonReadOptions)
            ?? throw new InvalidOperationException("No se pudo deserializar norma-apendix.json.");

        _logger.LogInformation("?? Entries encontradas: {Count}", input.Entries.Count);

        var entries = new List<SeguridadApendixEntry>();
        var totalTokens = 0;

        foreach (var entry in input.Entries)
        {
            var tokens = CountTokens(entry.Text);
            totalTokens += tokens;

            entries.Add(new SeguridadApendixEntry
            {
                Id = entry.Id,
                Title = entry.Title,
                Type = entry.Type,
                Text = entry.Text,
                Filename = entry.Filename,
                TotalTokens = tokens
            });

            _logger.LogDebug("  {Id} | {Type} | {Tokens} tokens | {Title}",
                entry.Id, entry.Type, tokens, entry.Title.Length > 60 ? entry.Title[..60] + "..." : entry.Title);
        }

        var result = new SeguridadApendix
        {
            Document = input.Document,
            TotalTokensDocumento = totalTokens,
            TotalEntries = entries.Count,
            ModeloTokenizacion = "cl100k_base",
            FechaGeneracion = DateTime.UtcNow,
            Entries = entries
        };

        // Guardar
        var directory = Path.GetDirectoryName(inputJsonPath)!;
        var outputPath = Path.Combine(directory, "seguridad-apendix.json");
        var outputJson = JsonSerializer.Serialize(result, JsonWriteOptions);
        await File.WriteAllTextAsync(outputPath, outputJson, Encoding.UTF8);

        _logger.LogInformation("? seguridad-apendix.json generado: {Entries} entries, {Tokens} tokens ? {Path}",
            entries.Count, totalTokens, outputPath);

        return (result, outputPath);
    }

    private int CountTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return _encoding.Encode(text).Count;
    }
}
