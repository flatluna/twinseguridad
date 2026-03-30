using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SharpToken;
using TwinSeguridad.Models;

namespace TwinSeguridad.Services;

/// <summary>
/// Servicio que lee norma-seguridad.json (documento completo) y norma-seguridad-indice.json
/// y genera norma-seguridad-estructurada.json con la estructura:
///   - Indice (sección principal) con sumario ejecutivo y totalTokensIndice
///   - Subíndices con texto completo y totalTokensSubindice
///   - Imágenes asociadas por sección
///
/// Usa SharpToken (cl100k_base) para contar tokens por subíndice y por índice.
///
/// Endpoint: GET /api/seguridad/estructurar
/// </summary>
public class NormaEstructuradaService
{
    private readonly ILogger<NormaEstructuradaService> _logger;
    private readonly GptEncoding _encoding;

    private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public NormaEstructuradaService(ILogger<NormaEstructuradaService> logger)
    {
        _logger = logger;
        _encoding = GptEncoding.GetEncoding("cl100k_base");
    }

    /// <summary>
    /// Lee el JSON del documento y el índice, genera la estructura final con tokens.
    /// </summary>
    public async Task<(NormaEstructurada Norma, string OutputPath)> GenerarEstructuraAsync(
        string documentoJsonPath, string indiceJsonPath)
    {
        if (!File.Exists(documentoJsonPath))
            throw new FileNotFoundException($"No se encontró: {documentoJsonPath}");
        if (!File.Exists(indiceJsonPath))
            throw new FileNotFoundException($"No se encontró: {indiceJsonPath}");

        _logger.LogInformation("?? Leyendo documento JSON: {Path}", documentoJsonPath);
        var docJson = await File.ReadAllTextAsync(documentoJsonPath, Encoding.UTF8);
        var documento = JsonSerializer.Deserialize<DocumentoLey>(docJson, JsonReadOptions)
            ?? throw new InvalidOperationException("No se pudo deserializar el documento.");

        _logger.LogInformation("?? Leyendo índice JSON: {Path}", indiceJsonPath);
        var idxJson = await File.ReadAllTextAsync(indiceJsonPath, Encoding.UTF8);
        var indice = JsonSerializer.Deserialize<IndiceNorma>(idxJson, JsonReadOptions)
            ?? throw new InvalidOperationException("No se pudo deserializar el índice.");

        // Construir mapa de imágenes por página
        var imagenesPorPagina = BuildImageMap(documento);

        // Generar la estructura
        var norma = BuildEstructura(indice, documento, imagenesPorPagina);

        // Guardar
        var directory = Path.GetDirectoryName(documentoJsonPath)!;
        var outputPath = Path.Combine(directory, "norma-seguridad-estructurada.json");
        var outputJson = JsonSerializer.Serialize(norma, JsonWriteOptions);
        await File.WriteAllTextAsync(outputPath, outputJson, Encoding.UTF8);

        _logger.LogInformation(
            "? Estructura generada: {Indices} índices, {Subindices} subíndices, {Tokens} tokens ? {Path}",
            norma.TotalIndices, norma.TotalSubindices, norma.TotalTokensDocumento, outputPath);

        return (norma, outputPath);
    }

    /// <summary>
    /// Construye un mapa de imágenes por página desde el documento.
    /// </summary>
    private static Dictionary<int, List<ImagenExtraida>> BuildImageMap(DocumentoLey documento)
    {
        var map = new Dictionary<int, List<ImagenExtraida>>();
        foreach (var pagina in documento.Paginas)
        {
            if (pagina.Imagenes.Count > 0)
                map[pagina.NumeroPagina] = pagina.Imagenes;
        }
        return map;
    }

    /// <summary>
    /// Construye la NormaEstructurada a partir del índice y documento.
    /// </summary>
    private NormaEstructurada BuildEstructura(
        IndiceNorma indice,
        DocumentoLey documento,
        Dictionary<int, List<ImagenExtraida>> imagenesPorPagina)
    {
        var indices = new List<IndiceEstructurado>();
        var indiceNum = 0;

        foreach (var seccion in indice.Secciones)
        {
            indiceNum++;

            var subindices = new List<SubindiceEstructurado>();

            if (seccion.Subsecciones.Count > 0)
            {
                foreach (var sub in seccion.Subsecciones)
                {
                    var textoLimpio = CleanText(sub.TextoCompleto);
                    var tokensSubindice = CountTokens(textoLimpio);

                    subindices.Add(new SubindiceEstructurado
                    {
                        TituloSubindice = $"{sub.Numero} {sub.Nombre}",
                        Texto = textoLimpio,
                        Pagina = sub.Paginas.Count > 0 ? sub.Paginas.First() : 0,
                        TotalTokensSubindice = tokensSubindice
                    });
                }
            }
            else
            {
                // Si no tiene subsecciones, el texto completo de la sección es el único subíndice
                var textoLimpio = CleanText(seccion.TextoCompleto);
                var tokensSubindice = CountTokens(textoLimpio);

                subindices.Add(new SubindiceEstructurado
                {
                    TituloSubindice = $"{seccion.Numero}. {seccion.Nombre}",
                    Texto = textoLimpio,
                    Pagina = seccion.Paginas.Count > 0 ? seccion.Paginas.First() : 0,
                    TotalTokensSubindice = tokensSubindice
                });
            }

            // Calcular tokens del índice completo (todo el texto de la sección)
            var textoCompletoSeccion = CleanText(seccion.TextoCompleto);
            var totalTokensIndice = CountTokens(textoCompletoSeccion);

            // Generar sumario ejecutivo (primeras ~300 caracteres del texto)
            var sumario = GenerarSumario(textoCompletoSeccion, seccion.Nombre);

            // Recopilar imágenes de las páginas de esta sección
            var imagenesSeccion = new List<ImagenReferencia>();
            foreach (var pag in seccion.Paginas)
            {
                if (imagenesPorPagina.TryGetValue(pag, out var imgs))
                {
                    foreach (var img in imgs)
                    {
                        imagenesSeccion.Add(new ImagenReferencia
                        {
                            NombreArchivo = img.NombreArchivo,
                            DescripcionIA = img.DescripcionIA,
                            Pagina = pag
                        });
                    }
                }
            }

            indices.Add(new IndiceEstructurado
            {
                Indice = indiceNum,
                TituloIndice = seccion.Nombre,
                SumarioEjecutivo = sumario,
                Pagina = seccion.Paginas.Count > 0 ? seccion.Paginas.First() : 0,
                TotalTokensIndice = totalTokensIndice,
                ListaSubindices = subindices,
                Imagenes = imagenesSeccion
            });
        }

        var norma = new NormaEstructurada
        {
            ArchivoOrigen = documento.ArchivoOrigen,
            FechaGeneracion = DateTime.UtcNow,
            ModeloTokenizacion = "cl100k_base",
            TotalTokensDocumento = indices.Sum(i => i.TotalTokensIndice),
            TotalIndices = indices.Count,
            TotalSubindices = indices.Sum(i => i.ListaSubindices.Count),
            Indices = indices
        };

        return norma;
    }

    /// <summary>
    /// Genera un sumario ejecutivo: toma el texto completo de la sección
    /// y extrae las primeras líneas significativas (hasta ~500 chars).
    /// </summary>
    private static string GenerarSumario(string textoCompleto, string nombreSeccion)
    {
        if (string.IsNullOrWhiteSpace(textoCompleto))
            return nombreSeccion;

        // Quitar el título de la sección del inicio si está ahí
        var texto = textoCompleto;
        var lines = texto.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var limpia = line.Trim();
            if (string.IsNullOrWhiteSpace(limpia)) continue;

            // Saltar la primera línea si es solo el número y título
            if (sb.Length == 0 && Regex.IsMatch(limpia, @"^\d{1,2}\.\s"))
            {
                // Si la línea tiene contenido después del título, incluirlo
                var sinNumero = Regex.Replace(limpia, @"^\d{1,2}\.\s+\S+\s*", "").Trim();
                if (sinNumero.Length > 20)
                    sb.Append(sinNumero).Append(' ');
                continue;
            }

            sb.Append(limpia).Append(' ');

            if (sb.Length >= 500)
                break;
        }

        var sumario = sb.ToString().Trim();
        if (sumario.Length > 600)
            sumario = sumario[..597] + "...";

        return string.IsNullOrWhiteSpace(sumario) ? nombreSeccion : sumario;
    }

    /// <summary>
    /// Cuenta tokens usando SharpToken (cl100k_base — GPT-4/GPT-4o).
    /// </summary>
    private int CountTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return _encoding.Encode(text).Count;
    }

    /// <summary>
    /// Limpia texto: normaliza espacios múltiples, trim.
    /// </summary>
    private static string CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return MultiSpaceRegex.Replace(text, " ").Trim();
    }
}
