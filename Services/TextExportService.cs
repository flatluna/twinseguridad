using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TwinSeguridad.Models;

namespace TwinSeguridad.Services;

/// <summary>
/// Servicio que lee el JSON de extracción (norma-seguridad.json) y genera
/// UN SOLO archivo TXT (norma-seguridad-texto.txt) con todo el documento limpio:
///   - Líneas de texto por página (solo texto, sin bloques)
///   - Tablas con encabezado [TABLA N] y celdas como renglones planos
///   - Imágenes con encabezado [IMAGEN] y su descripción IA
///
/// Endpoint: GET /api/seguridad/exportar-texto
/// </summary>
public class TextExportService
{
    private readonly ILogger<TextExportService> _logger;

    private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TextExportService(ILogger<TextExportService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Lee el JSON de extracción y genera UN SOLO archivo TXT con todo el documento.
    /// Retorna la ruta del archivo generado y el total de líneas.
    /// </summary>
    public async Task<(string OutputFile, int TotalPaginas, int TotalLineas)> ExportarTextoCompletoAsync(string jsonFilePath)
    {
        if (!File.Exists(jsonFilePath))
            throw new FileNotFoundException($"No se encontró el JSON: {jsonFilePath}");

        _logger.LogInformation("?? Leyendo JSON para exportar texto completo: {Path}", jsonFilePath);

        var jsonContent = await File.ReadAllTextAsync(jsonFilePath, Encoding.UTF8);
        var documento = JsonSerializer.Deserialize<DocumentoLey>(jsonContent, JsonOptions)
            ?? throw new InvalidOperationException("No se pudo deserializar el JSON.");

        var directory = Path.GetDirectoryName(jsonFilePath)!;
        var outputFile = Path.Combine(directory,
            Path.GetFileNameWithoutExtension(jsonFilePath) + "-texto.txt");

        var sb = new StringBuilder();
        var totalLineas = 0;
        var paginasEscritas = 0;

        // Encabezado del documento
        sb.AppendLine($"??????????????????????????????????????????????????????????????");
        sb.AppendLine($"  {documento.ArchivoOrigen}");
        sb.AppendLine($"  Total páginas: {documento.TotalPaginas}");
        sb.AppendLine($"  Fecha extracción: {documento.FechaExtraccion:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"??????????????????????????????????????????????????????????????");
        sb.AppendLine();

        foreach (var pagina in documento.Paginas)
        {
            var (pageContent, lineCount) = BuildPageText(pagina);

            if (string.IsNullOrWhiteSpace(pageContent))
            {
                _logger.LogDebug("Página {Page} vacía, saltando.", pagina.NumeroPagina);
                continue;
            }

            sb.Append(pageContent);
            sb.AppendLine();

            totalLineas += lineCount;
            paginasEscritas++;
        }

        await File.WriteAllTextAsync(outputFile, sb.ToString(), Encoding.UTF8);

        _logger.LogInformation("? Texto completo exportado: {Paginas} páginas, {Lineas} líneas ? {File}",
            paginasEscritas, totalLineas, outputFile);

        return (outputFile, paginasEscritas, totalLineas);
    }

    /// <summary>
    /// Construye el texto limpio de una página:
    ///   1) Líneas de texto (limpiando espacios múltiples)
    ///   2) [TABLA N] con las celdas como renglones planos
    ///   3) [IMAGEN N] con descripción IA
    /// NO incluye bloques (se omiten).
    /// </summary>
    private (string Content, int LineCount) BuildPageText(PaginaLey pagina)
    {
        var sb = new StringBuilder();
        var lineCount = 0;

        // Encabezado de página
        sb.AppendLine($"??? PÁGINA {pagina.NumeroPagina} ???");
        sb.AppendLine();

        // 1) Líneas de texto (contenido principal)
        if (pagina.Lineas.Count > 0)
        {
            foreach (var linea in pagina.Lineas)
            {
                var textoLimpio = CleanText(linea.Texto);
                if (string.IsNullOrWhiteSpace(textoLimpio)) continue;

                sb.AppendLine(textoLimpio);
                lineCount++;
            }
        }
        else if (!string.IsNullOrWhiteSpace(pagina.TextoCompleto))
        {
            // Fallback: usar textoCompleto si no hay líneas estructuradas
            // Pero quitar las secciones de [IMAGEN:...] que ya se insertaron
            var textoSinImagenes = Regex.Replace(pagina.TextoCompleto,
                @"\[IMAGEN:.*?\[FIN IMAGEN\]", "", RegexOptions.Singleline);

            foreach (var rawLine in textoSinImagenes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var textoLimpio = CleanText(rawLine);
                if (string.IsNullOrWhiteSpace(textoLimpio)) continue;

                sb.AppendLine(textoLimpio);
                lineCount++;
            }
        }

        // 2) Tablas — celdas como renglones planos
        if (pagina.Tablas.Count > 0)
        {
            sb.AppendLine();

            for (var t = 0; t < pagina.Tablas.Count; t++)
            {
                var tabla = pagina.Tablas[t];
                sb.AppendLine($"[TABLA {t + 1} — {tabla.NumeroFilas} filas, {tabla.NumeroColumnas} columnas]");

                foreach (var fila in tabla.Filas)
                {
                    // Unir todas las celdas en un solo renglón limpio
                    var celdas = fila.Celdas
                        .Select(c => CleanText(c))
                        .Where(c => !string.IsNullOrWhiteSpace(c));

                    var renglonTabla = string.Join(" ", celdas);
                    if (!string.IsNullOrWhiteSpace(renglonTabla))
                    {
                        sb.AppendLine(renglonTabla);
                        lineCount++;
                    }
                }

                sb.AppendLine($"[FIN TABLA {t + 1}]");
                sb.AppendLine();
            }
        }

        // 3) Imágenes — con descripción IA
        if (pagina.Imagenes.Count > 0)
        {
            sb.AppendLine();

            for (var i = 0; i < pagina.Imagenes.Count; i++)
            {
                var img = pagina.Imagenes[i];
                sb.AppendLine($"[IMAGEN {i + 1}: {img.NombreArchivo}]");

                if (!string.IsNullOrWhiteSpace(img.DescripcionIA))
                {
                    foreach (var descLine in img.DescripcionIA.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var limpio = CleanText(descLine);
                        if (!string.IsNullOrWhiteSpace(limpio))
                        {
                            sb.AppendLine(limpio);
                            lineCount++;
                        }
                    }
                }
                else
                {
                    sb.AppendLine("[Sin descripción disponible]");
                }

                sb.AppendLine($"[FIN IMAGEN {i + 1}]");
                sb.AppendLine();
            }
        }

        return (sb.ToString(), lineCount);
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
