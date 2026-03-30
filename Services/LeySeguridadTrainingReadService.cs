using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TwinSeguridad.Models;

namespace TwinSeguridad.Services;

/// <summary>
/// Servicio de lectura para leyesseguridadtraining en Cosmos DB.
/// Lee los documentos de training generados por AI Agent.
///
/// Cosmos DB:
///   Account:   cdbseguridadindaccount
///   Database:  leyesdeseguridaddb
///   Container: leyesseguridadtraining
///   Partition: /nombreley
///
/// Endpoints que consume:
///   GET /api/seguridad/training           ? Todos los documentos de training (resumen)
///   GET /api/seguridad/training/{indice}  ? Un documento de training por índice (completo)
/// </summary>
public class LeySeguridadTrainingReadService
{
    private readonly ILogger<LeySeguridadTrainingReadService> _logger;
    private readonly CosmosClient _cosmosClient;
    private readonly string _databaseName;
    private readonly string _containerName;

    private const string TrainingContainerName = "leyesseguridadtraining";

    public LeySeguridadTrainingReadService(
        ILogger<LeySeguridadTrainingReadService> logger,
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
        _containerName = TrainingContainerName;

        _cosmosClient = new CosmosClient(cosmosEndpoint, cosmosKey, new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.Default
            }
        });
    }

    /// <summary>
    /// Obtiene todos los documentos de training ordenados por índice.
    /// Devuelve un resumen (sin textoCompletoIndice para reducir payload).
    /// </summary>
    public async Task<List<LeySeguridadTrainingDocument>> ObtenerTodosAsync()
    {
        _logger.LogInformation("?? Leyendo todos los documentos de training...");

        var container = _cosmosClient.GetContainer(_databaseName, _containerName);

        var query = new QueryDefinition(
            "SELECT c.id, c.nombreley, c.indice, c.tituloIndice, c.sumarioEjecutivo, " +
            "c.totalSubsecciones, c.totalTokens, c.preguntasFrecuentes, c.curso, " +
            "c.fechaGeneracion, c.modeloAI " +
            "FROM c ORDER BY c.indice");

        var results = new List<LeySeguridadTrainingDocument>();

        using var feed = container.GetItemQueryIterator<LeySeguridadTrainingDocument>(query);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            results.AddRange(response);
        }

        _logger.LogInformation("?? Documentos de training leídos: {Count}", results.Count);
        return results;
    }

    /// <summary>
    /// Obtiene un documento de training por número de índice (completo, incluyendo textoCompletoIndice).
    /// </summary>
    public async Task<LeySeguridadTrainingDocument?> ObtenerPorIndiceAsync(int indice)
    {
        _logger.LogInformation("?? Leyendo training para índice {Indice}...", indice);

        var container = _cosmosClient.GetContainer(_databaseName, _containerName);

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.indice = @indice")
            .WithParameter("@indice", indice);

        using var feed = container.GetItemQueryIterator<LeySeguridadTrainingDocument>(query);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            var doc = response.FirstOrDefault();
            if (doc != null)
            {
                _logger.LogInformation("?? Training encontrado: índice {Indice} - {Titulo}",
                    doc.Indice, doc.TituloIndice);
                return doc;
            }
        }

        _logger.LogWarning("?? No se encontró training para índice {Indice}", indice);
        return null;
    }

    /// <summary>
    /// Obtiene solo el índice y título de cada documento de training.
    /// Consulta ultraligera para ahorrar ancho de banda.
    /// </summary>
    public async Task<List<TrainingIndiceResumen>> ObtenerIndicesAsync()
    {
        _logger.LogInformation("?? Leyendo índices de training (ligero)...");

        var container = _cosmosClient.GetContainer(_databaseName, _containerName);

        var query = new QueryDefinition(
            "SELECT c.indice, c.tituloIndice, c.nombreley FROM c ORDER BY c.indice");

        var results = new List<TrainingIndiceResumen>();

        using var feed = container.GetItemQueryIterator<TrainingIndiceResumen>(query);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            results.AddRange(response);
        }

        _logger.LogInformation("?? Índices leídos: {Count}", results.Count);
        return results;
    }
}
