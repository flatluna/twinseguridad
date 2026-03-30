using System.Text;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharpToken;
using TwinSeguridad.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using Container = Microsoft.Azure.Cosmos.Container;

namespace TwinSeguridad.Services;

/// <summary>
/// Servicio que extrae el Manual de Procedimientos y Reglas de Negocio desde un PDF,
/// identifica cada sección del índice buscando línea por línea en cada página,
/// y guarda un documento por índice en Cosmos DB.
///
/// NOTA IMPORTANTE: El PDF fue generado por ChatGPT y tiene un defecto:
/// las secciones 4-17 todas empiezan con "1." en vez del número correcto.
/// Por eso buscamos por el TÍTULO en mayúsculas, no por el número.
///
/// PDF:      Documents/ManualSeguridad.pdf
/// Cosmos DB:
///   Account:   cdbseguridadindaccount
///   Database:  leyesdeseguridaddb
///   Container: manualseguridadcontainer
///   Partition: /nombreManual
/// </summary>
public class ManualSeguridadService
{
    private readonly ILogger<ManualSeguridadService> _logger;
    private readonly CosmosClient _cosmosClient;
    private readonly string _databaseName;
    private readonly GptEncoding _encoding;

    private const string ContainerName = "manualseguridadcontainer";
    private const string ManualName = "ManualProcedimientos_NOM-002-STPS-2010";

    /// <summary>
    /// Índice del manual con el título REAL tal como aparece en el cuerpo del PDF (en MAYÚSCULAS).
    /// El PDF tiene un bug: secciones 4-17 todas dicen "1." en vez de su número correcto.
    /// Por eso buscamos por título, no por número.
    ///
    /// Formato en el PDF:
    ///   L0039: "1. OBJETIVO"
    ///   L0044: "2. ALCANCE"
    ///   L0049: "3. REFERENCIAS NORMATIVAS"
    ///   L0059: "1. DEFINICIONES (selección)"         ? debería ser 4
    ///   L0068: "1. RESPONSABILIDADES Y ORGANIZACIÓN" ? debería ser 5
    ///   L0097: "1. CLASIFICACIÓN DEL RIESGO DE INCENDIO (procedimiento)" ? debería ser 6
    ///   etc.
    /// </summary>
    private static readonly List<(int Numero, string TituloBuscar, string TituloLimpio)> IndiceManual =
    [
        (1,  "OBJETIVO",                           "Objetivo"),
        (2,  "ALCANCE",                            "Alcance"),
        (3,  "REFERENCIAS NORMATIVAS",             "Referencias normativas"),
        (4,  "DEFINICIONES",                       "Definiciones esenciales"),
        (5,  "RESPONSABILIDADES Y ORGANIZACIÓN",   "Responsabilidades y organización"),
        (6,  "CLASIFICACIÓN DEL RIESGO DE INCENDIO", "Clasificación del riesgo de incendio"),
        (7,  "PLAN DE ATENCIÓN A EMERGENCIAS",     "Plan de atención a emergencias de incendio"),
        (8,  "BRIGADAS CONTRA INCENDIO",           "Brigadas contra incendio"),
        (9,  "SIMULACROS",                         "Simulacros"),
        (10, "PREVENCIÓN Y CONTROLES OPERATIVOS",  "Prevención y controles operativos"),
        (11, "EQUIPOS Y SISTEMAS CONTRA INCENDIO", "Equipos y sistemas contra incendio"),
        (12, "INSPECCIONES A INSTALACIONES ELÉCTRICAS", "Inspecciones a instalaciones eléctricas y de gas"),
        (13, "CAPACITACIÓN Y ADIESTRAMIENTO",      "Capacitación y adiestramiento"),
        (14, "REGISTRO, CONTROL DOCUMENTAL",       "Registro, control documental y conservación de evidencias"),
        (15, "EVALUACIÓN DE CONFORMIDAD",          "Evaluación de conformidad"),
        (16, "REGLAS DE NEGOCIO",                  "Reglas de negocio"),
        (17, "IMPLEMENTACIÓN",                     "Implementación, cronograma y seguimiento"),
        (18, "ANEXOS",                             "Anexos"),
    ];

    public ManualSeguridadService(
        ILogger<ManualSeguridadService> logger,
        IConfiguration configuration)
    {
        _logger = logger;

        var cosmosEndpoint = configuration["SEGURIDAD_COSMOS_ENDPOINT"]
                             ?? configuration["Values:SEGURIDAD_COSMOS_ENDPOINT"]
                             ?? throw new InvalidOperationException("SEGURIDAD_COSMOS_ENDPOINT no configurado.");
        var cosmosKey = configuration["SEGURIDAD_COSMOS_KEY"]
                        ?? configuration["Values:SEGURIDAD_COSMOS_KEY"]
                        ?? throw new InvalidOperationException("SEGURIDAD_COSMOS_KEY no configurado.");

        _databaseName = configuration["SEGURIDAD_COSMOS_DATABASE"]
                        ?? configuration["Values:SEGURIDAD_COSMOS_DATABASE"]
                        ?? "leyesdeseguridaddb";

        _cosmosClient = new CosmosClient(cosmosEndpoint, cosmosKey, new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.Default
            }
        });

        _encoding = GptEncoding.GetEncoding("cl100k_base");
    }

    /// <summary>
    /// Extrae el PDF, busca cada sección del índice línea por línea,
    /// y guarda un documento por índice en Cosmos DB.
    /// </summary>
    public async Task<(int TotalSecciones, int TotalPaginas, int TotalTokens, List<string> Errores)>
        IndexarManualAsync(string pdfFilePath)
    {
        _logger.LogInformation("?? Iniciando extracción del Manual de Seguridad: {Path}", pdfFilePath);

        var errores = new List<string>();

        if (!File.Exists(pdfFilePath))
        {
            errores.Add($"Archivo no encontrado: {pdfFilePath}");
            return (0, 0, 0, errores);
        }

        // 1) Extraer TODAS las líneas del PDF con su número de página
        var (lineas, totalPaginas) = ExtractAllLines(pdfFilePath);
        _logger.LogInformation("?? PDF extraído: {Pages} páginas, {Lines} líneas", totalPaginas, lineas.Count);

        // 2) Buscar cada sección por su título en MAYÚSCULAS, saltando el TOC (página 1)
        var secciones = FindSectionsLineByLine(lineas);
        _logger.LogInformation("?? Secciones encontradas: {Count}/{Total}",
            secciones.Count, IndiceManual.Count);

        if (secciones.Count == 0)
        {
            errores.Add("No se encontraron secciones del índice en el PDF. Verifique el archivo.");
            return (0, totalPaginas, 0, errores);
        }

        // 3) Crear container en Cosmos DB
        Container container;
        try
        {
            container = await CreateContainerAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error creando container en Cosmos DB");
            errores.Add($"Error creando container: {ex.Message}");
            return (0, totalPaginas, 0, errores);
        }

        // 4) Guardar cada sección como documento
        var totalSecciones = 0;
        var totalTokens = 0;

        foreach (var seccion in secciones)
        {
            try
            {
                seccion.ArchivoOrigen = Path.GetFileName(pdfFilePath);
                seccion.TotalCaracteres = seccion.TextoCompleto.Length;
                seccion.TotalTokens = CountTokens(seccion.TextoCompleto);

                _logger.LogInformation(
                    "?? Guardando sección {Indice}: {Titulo} ({Chars} chars, {Tokens} tokens, págs {PI}-{PF})",
                    seccion.Indice, seccion.TituloIndice, seccion.TotalCaracteres,
                    seccion.TotalTokens, seccion.PaginaInicio, seccion.PaginaFin);

                await container.UpsertItemAsync(seccion, new PartitionKey(seccion.NombreManual));

                totalSecciones++;
                totalTokens += seccion.TotalTokens;
            }
            catch (Exception ex)
            {
                var msg = $"Sección {seccion.Indice} ({seccion.TituloIndice}): {ex.Message}";
                _logger.LogError(ex, "? Error guardando sección {Indice}", seccion.Indice);
                errores.Add(msg);
            }
        }

        _logger.LogInformation(
            "?? Manual indexado: {Secciones} secciones, {Tokens} tokens, {Errores} errores",
            totalSecciones, totalTokens, errores.Count);

        return (totalSecciones, totalPaginas, totalTokens, errores);
    }

    /// <summary>
    /// Obtiene todos los documentos del manual ordenados por índice.
    /// </summary>
    public async Task<List<ManualSeguridadDocument>> ObtenerTodosAsync()
    {
        _logger.LogInformation("?? Leyendo todos los documentos del manual...");

        var container = _cosmosClient.GetContainer(_databaseName, ContainerName);

        var query = new QueryDefinition(
            "SELECT c.id, c.nombreManual, c.indice, c.tituloIndice, " +
            "c.paginaInicio, c.paginaFin, c.totalCaracteres, c.totalTokens, " +
            "c.fechaCreacion, c.archivoOrigen " +
            "FROM c ORDER BY c.indice");

        var results = new List<ManualSeguridadDocument>();

        using var feed = container.GetItemQueryIterator<ManualSeguridadDocument>(query);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            results.AddRange(response);
        }

        _logger.LogInformation("?? Documentos del manual leídos: {Count}", results.Count);
        return results;
    }

    /// <summary>
    /// Obtiene un documento del manual por número de índice (completo con texto).
    /// </summary>
    public async Task<ManualSeguridadDocument?> ObtenerPorIndiceAsync(int indice)
    {
        _logger.LogInformation("?? Leyendo manual sección {Indice}...", indice);

        var container = _cosmosClient.GetContainer(_databaseName, ContainerName);

        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.indice = @indice")
            .WithParameter("@indice", indice);

        using var feed = container.GetItemQueryIterator<ManualSeguridadDocument>(query);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            var doc = response.FirstOrDefault();
            if (doc != null)
            {
                _logger.LogInformation("?? Sección encontrada: {Indice} - {Titulo}",
                    doc.Indice, doc.TituloIndice);
                return doc;
            }
        }

        _logger.LogWarning("?? No se encontró sección {Indice} del manual", indice);
        return null;
    }

    #region PDF Extraction — Line by Line

    private record LineaConPagina(string Texto, int Pagina, int IndiceLinea);

    /// <summary>
    /// Extrae TODAS las líneas del PDF, normalizando espacios múltiples a uno solo.
    /// </summary>
    private (List<LineaConPagina> Lineas, int TotalPaginas) ExtractAllLines(string pdfFilePath)
    {
        var todasLineas = new List<LineaConPagina>();
        var lineIndex = 0;

        using var pdf = PdfDocument.Open(pdfFilePath);
        for (var i = 1; i <= pdf.NumberOfPages; i++)
        {
            try
            {
                var page = pdf.GetPage(i);
                var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();

                if (words.Count > 0)
                {
                    var lineGroups = words
                        .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
                        .OrderByDescending(g => g.Key);

                    foreach (var lineGroup in lineGroups)
                    {
                        var lineWords = lineGroup.OrderBy(w => w.BoundingBox.Left);
                        var rawText = string.Join(" ", lineWords.Select(w => w.Text));
                        var normalized = NormalizeSpaces(rawText).Trim();

                        if (!string.IsNullOrWhiteSpace(normalized))
                        {
                            todasLineas.Add(new LineaConPagina(normalized, i, lineIndex++));
                        }
                    }
                }
                else
                {
                    var rawText = page.Text ?? string.Empty;
                    var lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var normalized = NormalizeSpaces(line).Trim();
                        if (!string.IsNullOrWhiteSpace(normalized))
                        {
                            todasLineas.Add(new LineaConPagina(normalized, i, lineIndex++));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "?? Error extrayendo página {Page}", i);
            }
        }

        return (todasLineas, pdf.NumberOfPages);
    }

    #endregion

    #region Section Detection — By title in UPPERCASE, skipping TOC

    /// <summary>
    /// Busca cada sección por su título en MAYÚSCULAS.
    /// 
    /// Estrategia:
    /// - Las páginas 1-2 son el TOC (ÍNDICE) — las ignoramos para la búsqueda
    /// - En el cuerpo (página 2+), buscamos líneas que CONTENGAN el título en mayúsculas
    /// - Para las secciones 1-3 que sí tienen el número correcto ("1. OBJETIVO", "2. ALCANCE", "3. REFERENCIAS"):
    ///   buscamos después del TOC (línea > 38 aprox)
    /// - Para las secciones 4-17 que tienen "1." como bug del PDF:
    ///   buscamos por el título en mayúsculas que viene DESPUÉS de un "1." erróneo
    /// - Para Anexos: buscamos "ANEXOS" en mayúsculas
    /// 
    /// Tomamos la PRIMERA aparición DESPUÉS del TOC (no la última, porque el TOC
    /// tiene los mismos títulos pero sin contenido).
    /// </summary>
    private List<ManualSeguridadDocument> FindSectionsLineByLine(List<LineaConPagina> lineas)
    {
        // Encontrar dónde termina el TOC: buscar la primera línea que dice "1. OBJETIVO" 
        // en página >= 2 (el contenido real empieza ahí)
        var tocEndLine = 0;
        for (var i = 0; i < lineas.Count; i++)
        {
            if (lineas[i].Pagina >= 2 && ContainsTitle(lineas[i].Texto, "OBJETIVO"))
            {
                tocEndLine = i;
                break;
            }
        }

        _logger.LogInformation("?? TOC termina en línea {Line} (página {Page})",
            tocEndLine, tocEndLine < lineas.Count ? lineas[tocEndLine].Pagina : 0);

        // Buscar cada sección DESPUÉS del TOC
        var sectionStarts = new Dictionary<int, int>(); // numero ? índice de línea

        for (var i = tocEndLine; i < lineas.Count; i++)
        {
            var texto = lineas[i].Texto;

            foreach (var (numero, tituloBuscar, _) in IndiceManual)
            {
                // Si ya encontramos esta sección, no buscar más
                if (sectionStarts.ContainsKey(numero))
                    continue;

                if (ContainsTitle(texto, tituloBuscar))
                {
                    sectionStarts[numero] = i;
                    _logger.LogInformation("?? Sección {Num} encontrada en línea {Idx} (p{Page}): \"{Text}\"",
                        numero, i, lineas[i].Pagina, texto);
                    break; // una línea solo puede ser de una sección
                }
            }
        }

        _logger.LogInformation("?? Secciones encontradas: {Count}", sectionStarts.Count);

        if (sectionStarts.Count == 0) return [];

        // Ordenar por posición en el documento
        var seccionesOrdenadas = sectionStarts
            .OrderBy(kv => kv.Value)
            .ToList();

        // Extraer el texto de cada sección
        var documentos = new List<ManualSeguridadDocument>();

        for (var s = 0; s < seccionesOrdenadas.Count; s++)
        {
            var (numero, startLineIdx) = seccionesOrdenadas[s];
            var endLineIdx = (s + 1 < seccionesOrdenadas.Count)
                ? seccionesOrdenadas[s + 1].Value
                : lineas.Count;

            // Título limpio (del índice, no del PDF con mayúsculas)
            var tituloLimpio = IndiceManual.First(x => x.Numero == numero).TituloLimpio;
            var tituloConNumero = $"{numero}. {tituloLimpio}";

            var paginaInicio = lineas[startLineIdx].Pagina;
            var paginaFin = lineas[Math.Min(endLineIdx - 1, lineas.Count - 1)].Pagina;

            // Construir el texto completo: TODAS las líneas desde startLineIdx hasta endLineIdx-1
            var sb = new StringBuilder();
            for (var li = startLineIdx; li < endLineIdx; li++)
            {
                sb.AppendLine(lineas[li].Texto);
            }

            var textoCompleto = sb.ToString().Trim();

            _logger.LogInformation(
                "?? Sección {Num}: \"{Titulo}\" — líneas {Start}-{End}, págs {P1}-{P2}, {Len} chars",
                numero, tituloConNumero, startLineIdx, endLineIdx - 1,
                paginaInicio, paginaFin, textoCompleto.Length);

            documentos.Add(new ManualSeguridadDocument
            {
                Id = $"manual_{numero}",
                NombreManual = ManualName,
                Indice = numero,
                TituloIndice = tituloConNumero,
                TextoCompleto = textoCompleto,
                PaginaInicio = paginaInicio,
                PaginaFin = paginaFin,
                FechaCreacion = DateTime.UtcNow
            });
        }

        return documentos;
    }

    /// <summary>
    /// Verifica si una línea CONTIENE un título dado (case-insensitive).
    /// Busca el título completo dentro de la línea (no solo StartsWith,
    /// porque la línea puede empezar con "1. " antes del título real).
    /// </summary>
    private static bool ContainsTitle(string linea, string titulo)
    {
        return linea.Contains(titulo, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Helpers

    private static string NormalizeSpaces(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var prevWasSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevWasSpace)
                {
                    sb.Append(' ');
                    prevWasSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                prevWasSpace = false;
            }
        }
        return sb.ToString();
    }

    private int CountTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return _encoding.Encode(text).Count;
    }

    #endregion

    #region Cosmos DB

    private async Task<Container> CreateContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseName);
        _logger.LogInformation("??? Database: {Db}", _databaseName);

        var containerProperties = new ContainerProperties(ContainerName, "/nombreManual");

        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(containerProperties);
        _logger.LogInformation("?? Container: {Container} (status: {Status})",
            ContainerName, containerResponse.StatusCode);

        return containerResponse.Container;
    }

    #endregion
}
