using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TwinSeguridad.Services;

namespace TwinSeguridad;

/// <summary>
/// Azure Function HTTP endpoints para la extracción de normas de seguridad desde PDF.
/// ???????????????????????????????????????????????????????????????????????????????????
///
/// Endpoints:
///   GET/POST /api/seguridad/extraer         ? Extrae PDF ? norma-seguridad.json
///   GET/POST /api/seguridad/indice          ? Genera índice jerárquico con tokens
///   GET/POST /api/seguridad/exportar-texto  ? Genera TXT completo desde el JSON
///   GET/POST /api/seguridad/estructurar     ? Genera JSON estructurado con sumarios y tokens
///   GET/POST /api/seguridad/indexar         ? Indexa la norma en Cosmos DB with vectores
///   POST     /api/seguridad/buscar          ? Búsqueda semántica vectorial
/// </summary>
public class SeguridadNormaFx
{
    private readonly ILogger<SeguridadNormaFx> _logger;
    private readonly PdfExtractionService _pdfExtractionService;
    private readonly IndiceExtractionService _indiceExtractionService;
    private readonly TextExportService _textExportService;
    private readonly NormaEstructuradaService _normaEstructuradaService;
    private readonly NormaCosmosVectorService _normaCosmosVectorService;
    private readonly NormaTextoEstructuradoService _normaTextoEstructuradoService;
    private readonly NormaApendixService _normaApendixService;
    private readonly LeySeguridadCosmosVectorService _leySeguridadCosmosVectorService;
    private readonly LeySeguridadTrainingService _leySeguridadTrainingService;
    private readonly LeySeguridadTrainingReadService _leySeguridadTrainingReadService;
    private readonly ManualSeguridadService _manualSeguridadService;

    private const string DocumentsPath = @"D:\TwiNetAI\Source\TwinSeguridad\Documents";
    private const string PdfFileName = "norma-seguridad.pdf";

    // Cosmos DB config read from settings
    private readonly string _seguridadCosmosEndpoint;
    private readonly string _seguridadCosmosDatabase;
    private readonly string _seguridadCosmosContainer;
    private readonly string _cosmosEndpoint;
    private readonly string _cosmosDatabase;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SeguridadNormaFx(
        ILogger<SeguridadNormaFx> logger,
        IConfiguration configuration,
        PdfExtractionService pdfExtractionService,
        IndiceExtractionService indiceExtractionService,
        TextExportService textExportService,
        NormaEstructuradaService normaEstructuradaService,
        NormaCosmosVectorService normaCosmosVectorService,
        NormaTextoEstructuradoService normaTextoEstructuradoService,
        NormaApendixService normaApendixService,
        LeySeguridadCosmosVectorService leySeguridadCosmosVectorService,
        LeySeguridadTrainingService leySeguridadTrainingService,
        LeySeguridadTrainingReadService leySeguridadTrainingReadService,
        ManualSeguridadService manualSeguridadService)
    {
        _logger = logger;
        _pdfExtractionService = pdfExtractionService;
        _indiceExtractionService = indiceExtractionService;
        _textExportService = textExportService;
        _normaEstructuradaService = normaEstructuradaService;
        _normaCosmosVectorService = normaCosmosVectorService;
        _normaTextoEstructuradoService = normaTextoEstructuradoService;
        _normaApendixService = normaApendixService;
        _leySeguridadCosmosVectorService = leySeguridadCosmosVectorService;
        _leySeguridadTrainingService = leySeguridadTrainingService;
        _leySeguridadTrainingReadService = leySeguridadTrainingReadService;
        _manualSeguridadService = manualSeguridadService;

        // Read Cosmos config from settings (for response metadata only)
        _seguridadCosmosEndpoint = configuration["SEGURIDAD_COSMOS_ENDPOINT"]
                                   ?? configuration["Values:SEGURIDAD_COSMOS_ENDPOINT"] ?? "";
        _seguridadCosmosDatabase = configuration["SEGURIDAD_COSMOS_DATABASE"]
                                   ?? configuration["Values:SEGURIDAD_COSMOS_DATABASE"] ?? "leyesdeseguridaddb";
        _seguridadCosmosContainer = configuration["SEGURIDAD_COSMOS_CONTAINER"]
                                    ?? configuration["Values:SEGURIDAD_COSMOS_CONTAINER"] ?? "leyesseguridadcontainer";
        _cosmosEndpoint = configuration["COSMOS_ENDPOINT"]
                          ?? configuration["Values:COSMOS_ENDPOINT"] ?? "";
        _cosmosDatabase = configuration["COSMOS_DATABASE_NAME"]
                          ?? configuration["Values:COSMOS_DATABASE_NAME"] ?? "TwinHumanDB";
    }

    // ???????????????????????????????????????????????????????????????????
    //  GET/POST /api/seguridad/extraer
    // ???????????????????????????????????????????????????????????????????

    [Function("SeguridadNormaExtraer")]
    public async Task<HttpResponseData> Extraer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "seguridad/extraer")]
        HttpRequestData req)
    {
        _logger.LogInformation("?? {Method} /api/seguridad/extraer", req.Method);

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        var pdfFilePath = Path.Combine(DocumentsPath, PdfFileName);

        try
        {
            if (!File.Exists(pdfFilePath))
            {
                return await WriteJson(response, HttpStatusCode.NotFound, new
                {
                    success = false,
                    error = $"Archivo no encontrado: {pdfFilePath}"
                });
            }

            var (documento, jsonPath) = await _pdfExtractionService.ExtractAndSaveAsync(pdfFilePath);

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                mensaje = "Extracción completada exitosamente.",
                archivoOrigen = documento.ArchivoOrigen,
                totalPaginas = documento.TotalPaginas,
                totalLineas = documento.Paginas.Sum(p => p.Lineas.Count),
                totalTablas = documento.Paginas.Sum(p => p.Tablas.Count),
                totalBloques = documento.Paginas.Sum(p => p.Bloques.Count),
                totalImagenes = documento.TotalImagenes,
                paginasConImagenes = documento.Paginas.Count(p => p.Imagenes.Count > 0),
                jsonGenerado = jsonPath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al procesar el PDF.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ???????????????????????????????????????????????????????????????????
    //  GET/POST /api/seguridad/indice
    // ???????????????????????????????????????????????????????????????????

    [Function("SeguridadNormaIndice")]
    public async Task<HttpResponseData> Indice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "seguridad/indice")]
        HttpRequestData req)
    {
        _logger.LogInformation("?? {Method} /api/seguridad/indice", req.Method);

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        var jsonFilePath = Path.Combine(DocumentsPath,
            Path.GetFileNameWithoutExtension(PdfFileName) + ".json");

        try
        {
            if (!File.Exists(jsonFilePath))
            {
                return await WriteJson(response, HttpStatusCode.NotFound, new
                {
                    success = false,
                    error = $"Primero ejecuta GET /api/seguridad/extraer. No existe: {jsonFilePath}"
                });
            }

            var (indice, outputPath) = await _indiceExtractionService.ExtractIndiceAsync(jsonFilePath);

            var resumenSecciones = indice.Secciones.Select(s => new
            {
                numero = s.Numero,
                nombre = s.Nombre,
                tokens = s.Tokens,
                caracteres = s.Caracteres,
                paginas = s.Paginas,
                subsecciones = s.Subsecciones.Count
            }).ToList();

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                mensaje = "Índice jerárquico generado exitosamente.",
                modeloTokenizacion = indice.ModeloTokenizacion,
                totalTokensDocumento = indice.TotalTokensDocumento,
                totalSecciones = indice.TotalSecciones,
                totalSubsecciones = indice.TotalSubsecciones,
                jsonGenerado = outputPath,
                secciones = resumenSecciones
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al generar el índice.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ???????????????????????????????????????????????????????????????????
    //  GET/POST /api/seguridad/exportar-texto
    // ???????????????????????????????????????????????????????????????????

    /// <summary>
    /// Lee norma-seguridad.json y genera UN SOLO archivo TXT con todo el documento limpio.
    /// El TXT contiene por cada página:
    ///   - Líneas de texto limpias (sin bloques)
    ///   - [TABLA N] con celdas como renglones planos
    ///   - [IMAGEN N] con descripción IA
    ///
    /// Archivo generado: Documents/norma-seguridad-texto.txt
    ///
    /// Prerrequisito: ejecutar /api/seguridad/extraer primero.
    /// </summary>
    [Function("SeguridadNormaExportarTexto")]
    public async Task<HttpResponseData> ExportarTexto(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "seguridad/exportar-texto")]
        HttpRequestData req)
    {
        _logger.LogInformation("?? {Method} /api/seguridad/exportar-texto", req.Method);

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        var jsonFilePath = Path.Combine(DocumentsPath,
            Path.GetFileNameWithoutExtension(PdfFileName) + ".json");

        try
        {
            if (!File.Exists(jsonFilePath))
            {
                return await WriteJson(response, HttpStatusCode.NotFound, new
                {
                    success = false,
                    error = $"Primero ejecuta GET /api/seguridad/extraer. No existe: {jsonFilePath}"
                });
            }

            var (outputFile, totalPaginas, totalLineas) =
                await _textExportService.ExportarTextoCompletoAsync(jsonFilePath);

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                mensaje = "Texto completo exportado exitosamente en un solo archivo.",
                archivoGenerado = outputFile,
                totalPaginas,
                totalLineas,
                formato = "Un solo .txt con todas las páginas, tablas como renglones y descripciones de imágenes."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al exportar texto.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ???????????????????????????????????????????????????????????????????
    //  GET/POST /api/seguridad/estructurar
    // ???????????????????????????????????????????????????????????????????

    /// <summary>
    /// Lee norma-seguridad.json + norma-seguridad-indice.json y genera
    /// norma-seguridad-estructurada.json con:
    ///   - Índices con sumario ejecutivo y totalTokensIndice
    ///   - Subíndices con texto completo y totalTokensSubindice
    ///   - Imágenes asociadas por sección
    ///
    /// Prerrequisito: ejecutar /api/seguridad/extraer y /api/seguridad/indice primero.
    /// </summary>
    [Function("SeguridadNormaEstructurar")]
    public async Task<HttpResponseData> Estructurar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "seguridad/estructurar")]
        HttpRequestData req)
    {
        _logger.LogInformation("??? {Method} /api/seguridad/estructurar", req.Method);

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        var baseName = Path.GetFileNameWithoutExtension(PdfFileName);
        var documentoJsonPath = Path.Combine(DocumentsPath, baseName + ".json");
        var indiceJsonPath = Path.Combine(DocumentsPath, baseName + "-indice.json");

        try
        {
            if (!File.Exists(documentoJsonPath))
            {
                return await WriteJson(response, HttpStatusCode.NotFound, new
                {
                    success = false,
                    error = $"Primero ejecuta GET /api/seguridad/extraer. No existe: {documentoJsonPath}"
                });
            }

            if (!File.Exists(indiceJsonPath))
            {
                return await WriteJson(response, HttpStatusCode.NotFound, new
                {
                    success = false,
                    error = $"Primero ejecuta GET /api/seguridad/indice. No existe: {indiceJsonPath}"
                });
            }

            var (norma, outputPath) =
                await _normaEstructuradaService.GenerarEstructuraAsync(documentoJsonPath, indiceJsonPath);

            // Resumen de índices para la respuesta
            var resumenIndices = norma.Indices.Select(i => new
            {
                indice = i.Indice,
                titulo = i.TituloIndice,
                pagina = i.Pagina,
                totalTokensIndice = i.TotalTokensIndice,
                subindices = i.ListaSubindices.Count,
                imagenes = i.Imagenes.Count
            }).ToList();

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                mensaje = "Estructura generada exitosamente.",
                archivoGenerado = outputPath,
                modeloTokenizacion = norma.ModeloTokenizacion,
                totalTokensDocumento = norma.TotalTokensDocumento,
                totalIndices = norma.TotalIndices,
                totalSubindices = norma.TotalSubindices,
                indices = resumenIndices
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al estructurar la norma.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ???????????????????????????????????????????????????????????????????
    //  GET/POST /api/seguridad/estructurar-texto
    // ???????????????????????????????????????????????????????????????????

    /// <summary>
    /// Lee norma-seguridad-texto.txt (texto plano exportado) y genera
    /// norma-seguridad-estructurada.json buscando los encabezados del índice conocido
    /// directamente en el texto (sin regex). Corta de una sección a otra.
    ///
    /// Prerrequisito: ejecutar /api/seguridad/exportar-texto primero.
    /// </summary>
    [Function("SeguridadNormaEstructurarTexto")]
    public async Task<HttpResponseData> EstructurarTexto(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "seguridad/estructurar-texto")]
        HttpRequestData req)
    {
        _logger.LogInformation("??? {Method} /api/seguridad/estructurar-texto", req.Method);

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        var baseName = Path.GetFileNameWithoutExtension(PdfFileName);
        var txtFilePath = Path.Combine(DocumentsPath, baseName + "-texto.txt");

        try
        {
            if (!File.Exists(txtFilePath))
            {
                return await WriteJson(response, HttpStatusCode.NotFound, new
                {
                    success = false,
                    error = $"Primero ejecuta GET /api/seguridad/exportar-texto. No existe: {txtFilePath}"
                });
            }

            var (norma, outputPath) =
                await _normaTextoEstructuradoService.GenerarDesdeTextoAsync(txtFilePath);

            var resumenIndices = norma.Indices.Select(i => new
            {
                indice = i.Indice,
                titulo = i.TituloIndice,
                pagina = i.Pagina,
                totalTokensIndice = i.TotalTokensIndice,
                subindices = i.ListaSubindices.Count
            }).ToList();

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                mensaje = "Estructura generada desde texto plano exitosamente.",
                archivoGenerado = outputPath,
                modeloTokenizacion = norma.ModeloTokenizacion,
                totalTokensDocumento = norma.TotalTokensDocumento,
                totalIndices = norma.TotalIndices,
                totalSubindices = norma.TotalSubindices,
                indices = resumenIndices
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al estructurar desde texto.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ???????????????????????????????????????????????????????????????????
    //  GET/POST /api/seguridad/indexar
    // ???????????????????????????????????????????????????????????????????

    /// <summary>
    /// Lee norma-seguridad-estructurada.json, genera embeddings con text-embedding-ada-002,
    /// crea el container en Cosmos DB con VectorEmbeddingPolicy + VectorIndex (QuantizedFlat),
    /// e inserta todos los subíndices como documentos con su vector.
    ///
    /// Prerrequisito: ejecutar /api/seguridad/estructurar primero.
    /// </summary>
    [Function("SeguridadNormaIndexar")]
    public async Task<HttpResponseData> Indexar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "seguridad/indexar")]
        HttpRequestData req)
    {
        _logger.LogInformation("?? {Method} /api/seguridad/indexar", req.Method);

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        var estructuradaPath = Path.Combine(DocumentsPath, "norma-seguridad-estructurada.json");

        try
        {
            if (!File.Exists(estructuradaPath))
            {
                return await WriteJson(response, HttpStatusCode.NotFound, new
                {
                    success = false,
                    error = $"Primero ejecuta GET /api/seguridad/estructurar. No existe: {estructuradaPath}"
                });
            }

            var (totalDocumentos, totalTokens) =
                await _normaCosmosVectorService.IndexarNormaAsync(estructuradaPath);

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                mensaje = "Norma indexada exitosamente en Cosmos DB con vectores.",
                totalDocumentos,
                totalTokens,
                cosmosEndpoint = _cosmosEndpoint,
                database = _cosmosDatabase,
                container = "norma-seguridad",
                embeddingModel = "text-embedding-ada-002",
                vectorDimensions = 1536,
                vectorIndex = "QuantizedFlat",
                distanceFunction = "Cosine"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al indexar la norma.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ?????????????????????????????????????????????????????????????????????
    //  GET/POST /api/seguridad/generar-training
    // ?????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Lee todos los documentos de leyesseguridadcontainer agrupados por índice,
    /// genera con AI Agent (GPT-4 mini) para cada índice:
    ///   - Texto completo (todos los subíndices concatenados)
    ///   - 20 preguntas frecuentes con respuestas
    ///   - Curso de capacitación dividido en lecciones para AI Agent avatar
    /// Guarda un documento por índice en leyesseguridadtraining.
    ///
    /// Prerrequisito: ejecutar /api/seguridad/indexar-ley primero.
    /// </summary>
    [Function("SeguridadLeyGenerarTraining")]
    public async Task<HttpResponseData> GenerarTraining(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "seguridad/generar-training")]
        HttpRequestData req)
    {
        _logger.LogInformation("?? {Method} /api/seguridad/generar-training", req.Method);

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        try
        {
            var (totalIndices, totalLecciones, errores) =
                await _leySeguridadTrainingService.GenerarTrainingAsync();

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = totalIndices > 0,
                mensaje = totalIndices > 0
                    ? $"Training generado: {totalIndices} índices, {totalLecciones} lecciones."
                    : "No se pudo generar ningún índice. Revise los errores.",
                totalIndices,
                totalLecciones,
                totalErrores = errores.Count,
                errores = errores.Count > 0 ? errores : null,
                cosmosEndpoint = _seguridadCosmosEndpoint,
                database = _seguridadCosmosDatabase,
                sourceContainer = _seguridadCosmosContainer,
                trainingContainer = "leyesseguridadtraining",
                modeloAI = "gpt-4o-mini"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al generar training.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ?????????????????????????????????????????????????????????????????????
    //  GET/POST /api/seguridad/indexar-ley
    // ?????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Lee norma-seguridad-estructurada.json, divide subíndices grandes en chunks
    /// de ~800 caracteres, genera embeddings con text-embedding-ada-002, crea el
    /// container en Cosmos DB con VectorEmbeddingPolicy y VectorIndex (QuantizedFlat),
    /// e inserta todos los documentos.
    ///
    /// Prerrequisito: ejecutar /api/seguridad/estructurar o /api/seguridad/estructurar-texto primero.
    /// </summary>
    [Function("SeguridadLeyIndexar")]
    public async Task<HttpResponseData> IndexarLey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "seguridad/indexar-ley")]
        HttpRequestData req)
    {
        _logger.LogInformation("?? {Method} /api/seguridad/indexar-ley", req.Method);

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        var estructuradaPath = Path.Combine(DocumentsPath, "norma-seguridad-estructurada.json");

        try
        {
            if (!File.Exists(estructuradaPath))
            {
                return await WriteJson(response, HttpStatusCode.NotFound, new
                {
                    success = false,
                    error = $"Primero ejecuta GET /api/seguridad/estructurar. No existe: {estructuradaPath}"
                });
            }

            var (totalDocumentos, totalChunks) =
                await _leySeguridadCosmosVectorService.IndexarLeyAsync(estructuradaPath);

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                mensaje = "Ley de seguridad indexada exitosamente en Cosmos DB con vectores y chunking.",
                totalDocumentos,
                totalChunks,
                cosmosEndpoint = _seguridadCosmosEndpoint,
                database = _seguridadCosmosDatabase,
                container = _seguridadCosmosContainer,
                vectorPath = "/VectorText",
                embeddingModel = "text-embedding-ada-002",
                vectorDimensions = 1536,
                vectorDataType = "Float32",
                distanceFunction = "Cosine",
                vectorIndex = "QuantizedFlat",
                chunkSize = "800 caracteres"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al indexar la ley de seguridad.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ?????????????????????????????????????????????????????????????????????
    //  POST /api/seguridad/buscar-ley
    // ?????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Búsqueda semántica vectorial en Cosmos DB (leyesseguridadcontainer).
    /// Genera embedding de la query y usa VectorDistance para encontrar los documentos más relevantes.
    ///
    /// Body JSON: { "query": "evaluación de la conformidad", "topN": 5 }
    /// </summary>
    [Function("SeguridadLeyBuscar")]
    public async Task<HttpResponseData> BuscarLey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
            Route = "seguridad/buscar-ley")]
        HttpRequestData req)
    {
        _logger.LogInformation("?? POST /api/seguridad/buscar-ley");

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        try
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<BuscarRequest>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (request == null || string.IsNullOrWhiteSpace(request.Query))
            {
                return await WriteJson(response, HttpStatusCode.BadRequest, new
                {
                    success = false,
                    error = "El campo 'query' es requerido. Ejemplo: { \"query\": \"evaluación de conformidad\", \"topN\": 5 }"
                });
            }

            var topN = request.TopN > 0 ? request.TopN : 5;

            var resultados = await _leySeguridadCosmosVectorService.BuscarAsync(request.Query, topN);

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                query = request.Query,
                totalResultados = resultados.Count,
                cosmosEndpoint = _seguridadCosmosEndpoint,
                database = _seguridadCosmosDatabase,
                container = _seguridadCosmosContainer,
                resultados = resultados.Select(r => new
                {
                    r.Id,
                    r.Indice,
                    r.TituloIndice,
                    r.TituloSubindice,
                    r.SumarioEjecutivo,
                    r.Texto,
                    r.Pagina,
                    r.ChunkIndex,
                    r.TotalChunks,
                    r.SimilarityScore
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error en búsqueda vectorial de ley de seguridad.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ?????????????????????????????????????????????????????????????????????
    //  GET /api/seguridad/training/indices — Solo índice y título (ligero)
    // ?????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Lee solo el número de índice y título de cada documento de training.
    /// Endpoint ultraligero para poblar listas/menús sin descargar todo el contenido.
    ///
    /// Ejemplo respuesta:
    ///   { "success": true, "total": 16, "indices": [ { "indice": 1, "tituloIndice": "Objetivo" }, ... ] }
    /// </summary>
    [Function("SeguridadLeyTrainingGetIndices")]
    public async Task<HttpResponseData> ObtenerIndicesTraining(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
            Route = "seguridad/training/indices")]
        HttpRequestData req)
    {
        _logger.LogInformation("?? GET /api/seguridad/training/indices");

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        try
        {
            var indices = await _leySeguridadTrainingReadService.ObtenerIndicesAsync();

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                total = indices.Count,
                indices
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al obtener índices de training.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ?????????????????????????????????????????????????????????????????????
    //  GET /api/seguridad/training — Todos los documentos de training
    // ?????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Lee todos los documentos de leyesseguridadtraining ordenados por índice.
    /// Devuelve resumen de cada índice: título, preguntas, curso, lecciones.
    /// NO incluye textoCompletoIndice para reducir el payload.
    ///
    /// Prerrequisito: ejecutar /api/seguridad/generar-training primero.
    /// </summary>
    [Function("SeguridadLeyTrainingGetAll")]
    public async Task<HttpResponseData> ObtenerTodosTraining(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
            Route = "seguridad/training")]
        HttpRequestData req)
    {
        _logger.LogInformation("?? GET /api/seguridad/training");

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        try
        {
            var documentos = await _leySeguridadTrainingReadService.ObtenerTodosAsync();

            var resumen = documentos.Select(d => new
            {
                d.Id,
                d.Nombreley,
                d.Indice,
                d.TituloIndice,
                d.SumarioEjecutivo,
                d.TotalSubsecciones,
                d.TotalTokens,
                totalPreguntas = d.PreguntasFrecuentes.Count,
                curso = new
                {
                    d.Curso.TituloCurso,
                    d.Curso.DescripcionCurso,
                    d.Curso.DuracionEstimada,
                    d.Curso.TotalLecciones
                },
                d.FechaGeneracion,
                d.ModeloAI
            }).ToList();

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                totalDocumentos = documentos.Count,
                totalPreguntas = documentos.Sum(d => d.PreguntasFrecuentes.Count),
                totalLecciones = documentos.Sum(d => d.Curso.TotalLecciones),
                cosmosEndpoint = _seguridadCosmosEndpoint,
                database = _seguridadCosmosDatabase,
                container = "leyesseguridadtraining",
                documentos = resumen
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al obtener documentos de training.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ?????????????????????????????????????????????????????????????????????
    //  GET /api/seguridad/training/{indice} — Un documento por índice
    // ?????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Lee un documento de training por número de índice.
    /// Devuelve el documento COMPLETO incluyendo:
    ///   - textoCompletoIndice
    ///   - Todas las preguntas frecuentes con respuestas
    ///   - Curso completo con todas las lecciones y guiones
    ///
    /// Ejemplo: GET /api/seguridad/training/5
    /// </summary>
    [Function("SeguridadLeyTrainingGetByIndice")]
    public async Task<HttpResponseData> ObtenerTrainingPorIndice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
            Route = "seguridad/training/{indice:int}")]
        HttpRequestData req,
        int indice)
    {
        _logger.LogInformation("?? GET /api/seguridad/training/{Indice}", indice);

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        try
        {
            var documento = await _leySeguridadTrainingReadService.ObtenerPorIndiceAsync(indice);

            if (documento == null)
            {
                return await WriteJson(response, HttpStatusCode.NotFound, new
                {
                    success = false,
                    error = $"No se encontró training para el índice {indice}. Ejecuta /api/seguridad/generar-training primero."
                });
            }

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                documento = new
                {
                    documento.Id,
                    documento.Nombreley,
                    documento.Indice,
                    documento.TituloIndice,
                    documento.SumarioEjecutivo,
                    documento.TextoCompletoIndice,
                    documento.TotalSubsecciones,
                    documento.TotalTokens,
                    totalPreguntas = documento.PreguntasFrecuentes.Count,
                    documento.PreguntasFrecuentes,
                    documento.Curso,
                    documento.FechaGeneracion,
                    documento.ModeloAI
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al obtener training para índice {Indice}.", indice);
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ?????????????????????????????????????????????????????????????????????
    //  GET/POST /api/seguridad/indexar-manual — Extrae PDF y guarda en Cosmos DB
    // ?????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Extrae ManualSeguridad.pdf, identifica las 17 secciones + Anexos del índice,
    /// y guarda un documento por sección en Cosmos DB (manualseguridadcontainer).
    ///
    /// El PDF se lee de Documents/ManualSeguridad.pdf.
    /// </summary>
    [Function("SeguridadManualIndexar")]
    public async Task<HttpResponseData> IndexarManual(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "seguridad/indexar-manual")]
        HttpRequestData req)
    {
        _logger.LogInformation("?? {Method} /api/seguridad/indexar-manual", req.Method);

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        var pdfFilePath = Path.Combine(DocumentsPath, "ManualSeguridad.pdf");

        try
        {
            if (!File.Exists(pdfFilePath))
            {
                return await WriteJson(response, HttpStatusCode.NotFound, new
                {
                    success = false,
                    error = $"Archivo no encontrado: {pdfFilePath}. Coloque ManualSeguridad.pdf en la carpeta Documents."
                });
            }

            var (totalSecciones, totalPaginas, totalTokens, errores) =
                await _manualSeguridadService.IndexarManualAsync(pdfFilePath);

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = totalSecciones > 0,
                mensaje = totalSecciones > 0
                    ? $"Manual indexado: {totalSecciones} secciones de {totalPaginas} páginas."
                    : "No se pudo indexar el manual. Revise los errores.",
                totalSecciones,
                totalPaginas,
                totalTokens,
                totalErrores = errores.Count,
                errores = errores.Count > 0 ? errores : null,
                cosmosEndpoint = _seguridadCosmosEndpoint,
                database = _seguridadCosmosDatabase,
                container = "manualseguridadcontainer",
                partitionKey = "/nombreManual",
                archivoOrigen = "ManualSeguridad.pdf"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al indexar el manual.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ?????????????????????????????????????????????????????????????????????
    //  GET /api/seguridad/manual — Todos los documentos del manual (resumen)
    // ?????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Lee todos los documentos del manual ordenados por índice.
    /// Devuelve resumen sin textoCompleto para ahorrar banda.
    ///
    /// Prerrequisito: ejecutar /api/seguridad/indexar-manual primero.
    /// </summary>
    [Function("SeguridadManualGetAll")]
    public async Task<HttpResponseData> ObtenerTodosManual(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
            Route = "seguridad/manual")]
        HttpRequestData req)
    {
        _logger.LogInformation("?? GET /api/seguridad/manual");

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        try
        {
            var documentos = await _manualSeguridadService.ObtenerTodosAsync();

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                totalSecciones = documentos.Count,
                totalTokens = documentos.Sum(d => d.TotalTokens),
                cosmosEndpoint = _seguridadCosmosEndpoint,
                database = _seguridadCosmosDatabase,
                container = "manualseguridadcontainer",
                secciones = documentos.Select(d => new
                {
                    d.Id,
                    d.Indice,
                    d.TituloIndice,
                    d.PaginaInicio,
                    d.PaginaFin,
                    d.TotalCaracteres,
                    d.TotalTokens,
                    d.FechaCreacion,
                    d.ArchivoOrigen
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al obtener documentos del manual.");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ?????????????????????????????????????????????????????????????????????
    //  GET /api/seguridad/manual/{indice} — Un documento del manual completo
    // ?????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Lee un documento del manual por número de índice.
    /// Devuelve el documento COMPLETO incluyendo textoCompleto.
    ///
    /// Ejemplo: GET /api/seguridad/manual/6
    /// </summary>
    [Function("SeguridadManualGetByIndice")]
    public async Task<HttpResponseData> ObtenerManualPorIndice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
            Route = "seguridad/manual/{indice:int}")]
        HttpRequestData req,
        int indice)
    {
        _logger.LogInformation("?? GET /api/seguridad/manual/{Indice}", indice);

        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        try
        {
            var documento = await _manualSeguridadService.ObtenerPorIndiceAsync(indice);

            if (documento == null)
            {
                return await WriteJson(response, HttpStatusCode.NotFound, new
                {
                    success = false,
                    error = $"No se encontró la sección {indice} del manual. Ejecuta /api/seguridad/indexar-manual primero."
                });
            }

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                documento
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error al obtener sección {Indice} del manual.", indice);
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ???????????????????????????????????????????????????????????????????
    //  OPTIONS /api/seguridad/* — CORS preflight
    // ???????????????????????????????????????????????????????????????????

    [Function("SeguridadCors")]
    public HttpResponseData HandleCors(
        [HttpTrigger(AuthorizationLevel.Anonymous, "options",
            Route = "seguridad/{*rest}")]
        HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        AddCorsHeaders(response, req);
        return response;
    }

    // ???????????????????????????????????????????????????????????????????
    //  Helpers
    // ???????????????????????????????????????????????????????????????????

    private static void AddCorsHeaders(HttpResponseData response, HttpRequestData req)
    {
        var origin = req.Headers.TryGetValues("Origin", out var origins)
            ? origins.FirstOrDefault() : "*";
        response.Headers.Add("Access-Control-Allow-Origin", origin ?? "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
        response.Headers.Add("Access-Control-Allow-Credentials", "true");
    }

    private static async Task<HttpResponseData> WriteJson(
        HttpResponseData response, HttpStatusCode status, object data)
    {
        response.StatusCode = status;
        response.Headers.Add("Content-Type", "application/json");
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        await response.WriteStringAsync(json);
        return response;
    }
}

/// <summary>
/// Request model para búsqueda vectorial.
/// </summary>
public class BuscarRequest
{
    public string Query { get; set; } = string.Empty;
    public int TopN { get; set; } = 5;
}
