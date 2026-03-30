using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using TwinSeguridad.Services;

namespace TwinSeguridad.Agent;

/// <summary>
/// MCP Tools para el Agente de Seguridad.
/// Busca semánticamente en Cosmos DB (leyesseguridadcontainer) usando VectorDistance,
/// y consulta el Manual de Procedimientos (manualseguridadcontainer) por índice.
///
/// Herramientas:
///   1. BuscarEnNormaSeguridad  — búsqueda vectorial semántica, top 3
///   2. BuscarEnManual          — búsqueda vectorial semántica aplicada al manual
///   3. ObtenerSeccionManual    — lee una sección completa del manual por número
///   4. ObtenerIndicesManual    — lista las 18 secciones del manual (resumen)
/// </summary>
internal sealed class SeguridadNormaMcpTools
{
    private readonly ILogger _logger;
    private readonly LeySeguridadCosmosVectorService _vectorService;
    private readonly ManualSeguridadService _manualService;

    public SeguridadNormaMcpTools(
        ILogger logger,
        LeySeguridadCosmosVectorService vectorService,
        ManualSeguridadService manualService)
    {
        _logger = logger;
        _vectorService = vectorService;
        _manualService = manualService;
    }

    public IList<AITool> GetAllTools()
    {
        return
        [
            AIFunctionFactory.Create(BuscarEnNormaSeguridad),
            AIFunctionFactory.Create(ObtenerSeccionManual),
            AIFunctionFactory.Create(ObtenerIndicesManual),
        ];
    }

    [Description(
        "Busca semánticamente en la NOM-002-STPS-2010 (ley de seguridad contra incendios) " +
        "y en el Manual de Procedimientos usando vectores. Devuelve los 3 fragmentos más relevantes " +
        "con su texto completo, índice, título y score de similitud. " +
        "Usa esta herramienta SIEMPRE que el usuario pregunte sobre normas, procedimientos, " +
        "reglas de negocio, prevención de incendios, extintores, brigadas, simulacros, " +
        "clasificación de riesgo, capacitación, evaluación de conformidad, etc.")]
    public async Task<string> BuscarEnNormaSeguridad(
        [Description("La pregunta o tema a buscar. Debe ser descriptivo para obtener buenos resultados semánticos.")]
        string query,
        [Description("Número de resultados a devolver (default: 3, max: 10)")]
        int topN = 3,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("?? MCP Tool: BuscarEnNormaSeguridad — query: \"{Query}\", top: {Top}", query, topN);

        try
        {
            if (topN < 1) topN = 3;
            if (topN > 10) topN = 10;

            var resultados = await _vectorService.BuscarAsync(query, topN);

            if (resultados.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    totalResultados = 0,
                    mensaje = "No se encontraron resultados para esa consulta en la norma de seguridad."
                });
            }

            var items = resultados.Select((r, i) => new
            {
                posicion = i + 1,
                r.Id,
                r.Indice,
                r.TituloIndice,
                r.TituloSubindice,
                r.SumarioEjecutivo,
                textoCompleto = r.Texto,
                r.Pagina,
                r.ChunkIndex,
                r.TotalChunks,
                similaridad = Math.Round(r.SimilarityScore, 4)
            }).ToList();

            _logger.LogInformation("?? Encontrados {Count} resultados para \"{Query}\"", items.Count, query);

            return JsonSerializer.Serialize(new
            {
                success = true,
                totalResultados = items.Count,
                query,
                fuente = "NOM-002-STPS-2010 / leyesseguridadcontainer (Cosmos DB vector search)",
                resultados = items
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error en BuscarEnNormaSeguridad");
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [Description(
        "Obtiene una sección completa del Manual de Procedimientos y Reglas de Negocio " +
        "(NOM-002-STPS-2010) por número de índice. El manual tiene 18 secciones: " +
        "1=Objetivo, 2=Alcance, 3=Referencias, 4=Definiciones, 5=Responsabilidades, " +
        "6=Clasificación riesgo, 7=Plan emergencias, 8=Brigadas, 9=Simulacros, " +
        "10=Prevención, 11=Equipos, 12=Inspecciones, 13=Capacitación, " +
        "14=Registro documental, 15=Evaluación conformidad, 16=Reglas de negocio, " +
        "17=Implementación, 18=Anexos.")]
    public async Task<string> ObtenerSeccionManual(
        [Description("Número de sección del manual (1-18)")]
        int indice,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("?? MCP Tool: ObtenerSeccionManual — índice: {Indice}", indice);

        try
        {
            var doc = await _manualService.ObtenerPorIndiceAsync(indice);

            if (doc == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"No se encontró la sección {indice} del manual."
                });
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                seccion = new
                {
                    doc.Id,
                    doc.Indice,
                    doc.TituloIndice,
                    doc.TextoCompleto,
                    doc.PaginaInicio,
                    doc.PaginaFin,
                    doc.TotalCaracteres,
                    doc.TotalTokens
                }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error en ObtenerSeccionManual");
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [Description(
        "Lista las 18 secciones del Manual de Procedimientos con su número, título, " +
        "páginas y tokens. Útil para saber qué secciones existen antes de consultar una específica.")]
    public async Task<string> ObtenerIndicesManual(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("?? MCP Tool: ObtenerIndicesManual");

        try
        {
            var documentos = await _manualService.ObtenerTodosAsync();

            return JsonSerializer.Serialize(new
            {
                success = true,
                totalSecciones = documentos.Count,
                secciones = documentos.Select(d => new
                {
                    d.Indice,
                    d.TituloIndice,
                    d.PaginaInicio,
                    d.PaginaFin,
                    d.TotalCaracteres,
                    d.TotalTokens
                })
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error en ObtenerIndicesManual");
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }
}
