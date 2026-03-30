using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TwinSeguridad.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace TwinSeguridad.Services;

/// <summary>
/// Servicio para extraer texto, líneas, tablas e imágenes de un documento PDF de leyes/normas.
/// Utiliza UglyToad.PdfPig para extracción de contenido e ImageVisionService para describir imágenes.
/// </summary>
public class PdfExtractionService
{
    private readonly ILogger<PdfExtractionService> _logger;
    private readonly ImageVisionService _imageVisionService;

    // Patrones para detectar artículos y encabezados típicos en normas/leyes
    private static readonly Regex ArticuloRegex = new(
        @"^\s*(Art[íi]culo|ART[ÍI]CULO)\s+\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EncabezadoRegex = new(
        @"^\s*(CAP[ÍI]TULO|T[ÍI]TULO|SECCI[ÓO]N|ANEXO|DISPOSICI[ÓO]N|LEY|DECRETO|NORMA|REGLAMENTO)\s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PdfExtractionService(ILogger<PdfExtractionService> logger, ImageVisionService imageVisionService)
    {
        _logger = logger;
        _imageVisionService = imageVisionService;
    }

    /// <summary>
    /// Extrae todo el contenido de un PDF (texto, tablas, imágenes) y lo guarda como JSON.
    /// Las imágenes se guardan en Documents/images/ y se describen con GPT-4 mini visión.
    /// </summary>
    public async Task<(DocumentoLey Documento, string JsonPath)> ExtractAndSaveAsync(string pdfFilePath)
    {
        if (!File.Exists(pdfFilePath))
            throw new FileNotFoundException($"No se encontró el archivo PDF: {pdfFilePath}");

        _logger.LogInformation("?? Iniciando extracción del PDF: {PdfPath}", pdfFilePath);

        var directory = Path.GetDirectoryName(pdfFilePath)!;

        // 1) Extraer texto, líneas, tablas, bloques
        var documento = ExtractDocument(pdfFilePath);

        // 2) Extraer imágenes y describirlas con GPT-4 mini visión
        _logger.LogInformation("??? Extrayendo imágenes y describiéndolas con GPT-4 mini visión...");
        try
        {
            var imagesByPage = await _imageVisionService.ExtractAndDescribeImagesAsync(pdfFilePath, directory);

            // Asignar imágenes a cada página y enriquecer el texto
            var totalImages = 0;
            foreach (var (pageNum, images) in imagesByPage)
            {
                var pagina = documento.Paginas.FirstOrDefault(p => p.NumeroPagina == pageNum);
                if (pagina != null)
                {
                    pagina.Imagenes = images;
                    totalImages += images.Count;

                    // Insertar descripciones de imágenes en el texto completo de la página
                    var imageTextBuilder = new StringBuilder();
                    foreach (var img in images)
                    {
                        if (!string.IsNullOrWhiteSpace(img.DescripcionIA))
                        {
                            imageTextBuilder.AppendLine();
                            imageTextBuilder.AppendLine($"[IMAGEN: {img.NombreArchivo}]");
                            imageTextBuilder.AppendLine(img.DescripcionIA);
                            imageTextBuilder.AppendLine($"[FIN IMAGEN]");
                        }
                    }

                    if (imageTextBuilder.Length > 0)
                    {
                        pagina.TextoCompleto += "\n" + imageTextBuilder.ToString().TrimEnd();
                    }
                }
            }

            documento.TotalImagenes = totalImages;
            _logger.LogInformation("??? Total imágenes procesadas: {Count}", totalImages);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "?? Error al extraer/describir imágenes. Se continúa sin ellas.");
            documento.TotalImagenes = 0;
        }

        // 3) Guardar JSON
        var jsonFileName = Path.GetFileNameWithoutExtension(pdfFilePath) + ".json";
        var jsonFilePath = Path.Combine(directory, jsonFileName);

        var jsonContent = JsonSerializer.Serialize(documento, JsonOptions);
        await File.WriteAllTextAsync(jsonFilePath, jsonContent, Encoding.UTF8);

        _logger.LogInformation(
            "? Extracción completada. {Paginas} páginas, {Imagenes} imágenes. JSON ? {JsonPath}",
            documento.TotalPaginas, documento.TotalImagenes, jsonFilePath);

        return (documento, jsonFilePath);
    }

    /// <summary>
    /// Extrae el contenido completo del documento PDF.
    /// </summary>
    private DocumentoLey ExtractDocument(string pdfFilePath)
    {
        using var pdfDocument = PdfDocument.Open(pdfFilePath);

        var documento = new DocumentoLey
        {
            ArchivoOrigen = Path.GetFileName(pdfFilePath),
            RutaArchivo = pdfFilePath,
            FechaExtraccion = DateTime.UtcNow,
            TotalPaginas = pdfDocument.NumberOfPages,
            Metadatos = ExtractMetadata(pdfDocument),
            Paginas = []
        };

        for (var i = 1; i <= pdfDocument.NumberOfPages; i++)
        {
            try
            {
                var page = pdfDocument.GetPage(i);
                var paginaLey = ExtractPage(page, i);
                documento.Paginas.Add(paginaLey);

                _logger.LogDebug("Página {PageNum}/{Total} extraída: {LineCount} líneas, {TableCount} tablas",
                    i, pdfDocument.NumberOfPages, paginaLey.Lineas.Count, paginaLey.Tablas.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al extraer página {PageNum}. Se continuará con las siguientes.", i);
                documento.Paginas.Add(new PaginaLey
                {
                    NumeroPagina = i,
                    TextoCompleto = $"[Error al extraer esta página: {ex.Message}]"
                });
            }
        }

        return documento;
    }

    /// <summary>
    /// Extrae los metadatos del documento PDF.
    /// </summary>
    private static MetadatosDocumento ExtractMetadata(PdfDocument pdfDocument)
    {
        var info = pdfDocument.Information;
        var metadatos = new MetadatosDocumento
        {
            Titulo = NullIfEmpty(info.Title),
            Autor = NullIfEmpty(info.Author),
            Asunto = NullIfEmpty(info.Subject),
            Creador = NullIfEmpty(info.Creator),
            Productor = NullIfEmpty(info.Producer),
            Otros = []
        };

        // Extraer keywords si existen
        if (!string.IsNullOrWhiteSpace(info.Keywords))
            metadatos.Otros["PalabrasClave"] = info.Keywords;

        return metadatos;
    }

    /// <summary>
    /// Extrae el contenido de una página individual.
    /// </summary>
    private PaginaLey ExtractPage(Page page, int pageNumber)
    {
        var pagina = new PaginaLey
        {
            NumeroPagina = pageNumber,
            AnchoPagina = page.Width,
            AltoPagina = page.Height
        };

        // Extraer palabras y agruparlas en líneas
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();

        if (words.Count == 0)
        {
            pagina.TextoCompleto = page.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(pagina.TextoCompleto))
            {
                // Fallback: split por líneas de texto plano
                var rawLines = pagina.TextoCompleto.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < rawLines.Length; i++)
                {
                    var lineText = rawLines[i].Trim();
                    if (string.IsNullOrWhiteSpace(lineText)) continue;

                    pagina.Lineas.Add(new LineaTexto
                    {
                        NumeroLinea = i + 1,
                        Texto = lineText,
                        EsArticulo = ArticuloRegex.IsMatch(lineText),
                        EsEncabezado = EncabezadoRegex.IsMatch(lineText)
                    });
                }
            }
            return pagina;
        }

        // Usar segmentación de layout para detectar bloques de texto
        var textBlocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);

        // Construir líneas de texto agrupadas por posición vertical
        var lineGroups = words
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
            .OrderByDescending(g => g.Key) // PDF tiene Y de abajo hacia arriba
            .ToList();

        var lineNumber = 1;
        var fullTextBuilder = new StringBuilder();

        foreach (var lineGroup in lineGroups)
        {
            var lineWords = lineGroup.OrderBy(w => w.BoundingBox.Left).ToList();
            var lineText = string.Join(" ", lineWords.Select(w => w.Text));

            if (string.IsNullOrWhiteSpace(lineText)) continue;

            fullTextBuilder.AppendLine(lineText);

            // Calcular tamaño de fuente predominante en la línea
            var letters = lineWords.SelectMany(w => w.Letters).ToList();
            var avgFontSize = letters.Count > 0
                ? letters.Average(l => l.PointSize)
                : 0;
            var predominantFont = letters.Count > 0
                ? letters.GroupBy(l => l.FontName)
                    .OrderByDescending(g => g.Count())
                    .First().Key
                : null;

            var linea = new LineaTexto
            {
                NumeroLinea = lineNumber++,
                Texto = lineText,
                PosicionY = lineGroup.Key,
                PosicionX = lineWords.First().BoundingBox.Left,
                TamanoFuente = Math.Round(avgFontSize, 1),
                NombreFuente = predominantFont,
                EsArticulo = ArticuloRegex.IsMatch(lineText),
                EsEncabezado = EncabezadoRegex.IsMatch(lineText) || IsLikelyHeader(lineText, avgFontSize, letters)
            };

            pagina.Lineas.Add(linea);
        }

        pagina.TextoCompleto = fullTextBuilder.ToString().TrimEnd();

        // Extraer bloques de contenido semántico
        var bloqueIndex = 0;
        foreach (var block in textBlocks)
        {
            var blockText = block.Text?.Trim();
            if (string.IsNullOrWhiteSpace(blockText)) continue;

            var tipoBloque = DetermineBlockType(blockText);

            pagina.Bloques.Add(new BloqueContenido
            {
                IndiceBloque = bloqueIndex++,
                TipoBloque = tipoBloque,
                Texto = blockText,
                PosicionY = block.BoundingBox.Bottom
            });
        }

        // Intentar detectar tablas basándose en alineación de columnas
        var tablas = DetectTables(pagina.Lineas);
        pagina.Tablas = tablas;

        return pagina;
    }

    /// <summary>
    /// Determina si una línea es probablemente un encabezado basándose en el tamaño de fuente
    /// y si está en mayúsculas.
    /// </summary>
    private static bool IsLikelyHeader(string text, double fontSize, List<Letter> letters)
    {
        // Si el texto está completamente en mayúsculas y tiene más de 3 caracteres
        if (text.Length > 3 && text == text.ToUpperInvariant() && text.Any(char.IsLetter))
            return true;

        // Si el tamaño de fuente es significativamente mayor al promedio (12pt)
        if (fontSize > 14)
            return true;

        // Si la fuente contiene "Bold" en el nombre y parece un título
        if (letters.Count > 0 && letters.Any(l => l.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true))
        {
            if (text.Length < 100) // Los encabezados suelen ser cortos
                return true;
        }

        return false;
    }

    /// <summary>
    /// Determina el tipo de bloque de contenido según su texto.
    /// </summary>
    private static string DetermineBlockType(string text)
    {
        if (ArticuloRegex.IsMatch(text))
            return "Articulo";

        if (EncabezadoRegex.IsMatch(text))
            return "Titulo";

        if (Regex.IsMatch(text, @"^\s*\d+[\.\)]\s"))
            return "Lista";

        if (Regex.IsMatch(text, @"^\s*[a-z][\.\)]\s", RegexOptions.IgnoreCase))
            return "Lista";

        if (text.Length < 80 && text == text.ToUpperInvariant() && text.Any(char.IsLetter))
            return "Titulo";

        return "Parrafo";
    }

    /// <summary>
    /// Intenta detectar tablas en la página basándose en la alineación de columnas
    /// y patrones repetitivos de posiciones X.
    /// </summary>
    private static List<TablaExtraida> DetectTables(List<LineaTexto> lineas)
    {
        var tablas = new List<TablaExtraida>();

        // Buscar grupos de líneas consecutivas que tienen separadores de columna
        var tableLines = new List<List<LineaTexto>>();
        var currentTableLines = new List<LineaTexto>();

        foreach (var linea in lineas)
        {
            var hasTablePattern = linea.Texto.Contains('\t')
                || linea.Texto.Contains('|')
                || Regex.IsMatch(linea.Texto, @"\s{3,}"); // 3+ espacios consecutivos

            if (hasTablePattern)
            {
                currentTableLines.Add(linea);
            }
            else
            {
                if (currentTableLines.Count >= 2) // Una tabla necesita al menos 2 filas
                {
                    tableLines.Add(new List<LineaTexto>(currentTableLines));
                }
                currentTableLines.Clear();
            }
        }

        // No olvidar la última tabla potencial
        if (currentTableLines.Count >= 2)
            tableLines.Add(currentTableLines);

        // Construir tablas detectadas
        var tablaIndex = 0;
        foreach (var tLines in tableLines)
        {
            var tabla = new TablaExtraida
            {
                IndiceTabla = tablaIndex++,
                Filas = []
            };

            var maxCols = 0;
            for (var i = 0; i < tLines.Count; i++)
            {
                var cells = SplitTableRow(tLines[i].Texto);
                maxCols = Math.Max(maxCols, cells.Count);

                tabla.Filas.Add(new FilaTabla
                {
                    IndiceFila = i,
                    EsEncabezado = i == 0,
                    Celdas = cells
                });
            }

            tabla.NumeroFilas = tabla.Filas.Count;
            tabla.NumeroColumnas = maxCols;
            tablas.Add(tabla);
        }

        return tablas;
    }

    /// <summary>
    /// Divide una fila de tabla en celdas usando delimitadores comunes.
    /// </summary>
    private static List<string> SplitTableRow(string rowText)
    {
        // Intentar split por pipe primero
        if (rowText.Contains('|'))
        {
            return rowText.Split('|', StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        // Intentar split por tab
        if (rowText.Contains('\t'))
        {
            return rowText.Split('\t', StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        // Split por múltiples espacios (3+)
        return Regex.Split(rowText, @"\s{3,}")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
