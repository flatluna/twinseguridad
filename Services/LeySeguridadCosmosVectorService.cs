using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TwinSeguridad.Models;

namespace TwinSeguridad.Services;

/// <summary>
/// Servicio que lee norma-seguridad-estructurada.json, divide subíndices grandes en
/// chunks de ~800 caracteres, genera embeddings con text-embedding-ada-002, y almacena
/// cada documento en Cosmos DB con vector search habilitado.
///
/// Cosmos DB:
///   Account:   cdbseguridadindaccount
///   Database:  leyesdeseguridaddb
///   Container: leyesseguridadcontainer
///   Partition: /nombreley
///   Vector:    /VectorText (float32, cosine, 1536 dims)
///
/// Configuración (local.settings.json):
///   SEGURIDAD_COSMOS_ENDPOINT
///   SEGURIDAD_COSMOS_KEY
///   SEGURIDAD_COSMOS_DATABASE
///   SEGURIDAD_COSMOS_CONTAINER
///   AZURE_OPENAI_ENDPOINT
///   AZURE_OPENAI_API_KEY
///   AZURE_OPENAI_EMBEDDING_DEPLOYMENT_NAME
/// </summary>
public class LeySeguridadCosmosVectorService
{
    private readonly ILogger<LeySeguridadCosmosVectorService> _logger;
    private readonly CosmosClient _cosmosClient;
    private readonly string _databaseName;
    private readonly string _containerName;
    private readonly string _openAiEndpoint;
    private readonly string _openAiApiKey;
    private readonly string _embeddingDeployment;
    private readonly HttpClient _httpClient;

    private const string NormaId = "NOM-002-STPS-2010";
    private const int EmbeddingDimensions = 1536;
    private const int ChunkSizeChars = 800;

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LeySeguridadCosmosVectorService(ILogger<LeySeguridadCosmosVectorService> logger, IConfiguration configuration)
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
        _containerName = configuration["SEGURIDAD_COSMOS_CONTAINER"]
                         ?? configuration["Values:SEGURIDAD_COSMOS_CONTAINER"]
                         ?? "leyesseguridadcontainer";

        _openAiEndpoint = configuration["AZURE_OPENAI_ENDPOINT"]
                          ?? configuration["Values:AZURE_OPENAI_ENDPOINT"]
                          ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT no configurado.");
        _openAiApiKey = configuration["AZURE_OPENAI_API_KEY"]
                        ?? configuration["Values:AZURE_OPENAI_API_KEY"]
                        ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY no configurado.");
        _embeddingDeployment = configuration["AZURE_OPENAI_EMBEDDING_DEPLOYMENT_NAME"]
                               ?? configuration["Values:AZURE_OPENAI_EMBEDDING_DEPLOYMENT_NAME"]
                               ?? "text-embedding-ada-002";

        _cosmosClient = new CosmosClient(cosmosEndpoint, cosmosKey, new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.Default
            }
        });

        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Lee la norma estructurada, genera embeddings, crea el container con vector policy
    /// e inserta todos los documentos (dividiendo subíndices grandes en chunks de ~800 chars).
    /// </summary>
    public async Task<(int TotalDocumentos, int TotalChunks)> IndexarLeyAsync(string estructuradaJsonPath)
    {
        if (!File.Exists(estructuradaJsonPath))
            throw new FileNotFoundException($"No se encontró: {estructuradaJsonPath}");

        _logger.LogInformation("?? Leyendo norma estructurada: {Path}", estructuradaJsonPath);

        var json = await File.ReadAllTextAsync(estructuradaJsonPath, Encoding.UTF8);
        var norma = JsonSerializer.Deserialize<NormaEstructurada>(json, JsonReadOptions)
                    ?? throw new InvalidOperationException("No se pudo deserializar la norma estructurada.");

        // 1) Crear database y container con vector policy
        var container = await CreateContainerWithVectorPolicyAsync();

        // 2) Construir documentos con chunking de subíndices grandes
        var documentos = BuildDocumentsWithChunking(norma);
        _logger.LogInformation("?? Total documentos/chunks a indexar: {Count}", documentos.Count);

        // 3) Generar embeddings y subir a Cosmos DB
        var inserted = 0;

        foreach (var doc in documentos)
        {
            try
            {
                // Generar embedding del texto
                var embedding = await GenerateEmbeddingAsync(doc.Texto);
                doc.VectorText = embedding;

                // Upsert en Cosmos DB
                await container.UpsertItemAsync(doc, new PartitionKey(doc.Nombreley));
                inserted++;

                _logger.LogDebug("? Insertado: {Id} (chunk {Chunk}/{Total})",
                    doc.Id, doc.ChunkIndex, doc.TotalChunks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "?? Error al indexar documento {Id}", doc.Id);
            }
        }

        _logger.LogInformation("?? Indexación completada: {Inserted}/{Total} documentos",
            inserted, documentos.Count);

        return (inserted, documentos.Count);
    }

    /// <summary>
    /// Búsqueda semántica: genera embedding de la query y busca los TOP N documentos más similares.
    /// </summary>
    public async Task<List<LeySeguridadSearchResult>> BuscarAsync(string query, int topN = 10)
    {
        _logger.LogInformation("?? Búsqueda vectorial ley seguridad: \"{Query}\" (top {N})", query, topN);

        var queryEmbedding = await GenerateEmbeddingAsync(query);

        var container = _cosmosClient.GetContainer(_databaseName, _containerName);

        var queryDef = new QueryDefinition(
            "SELECT TOP @topN c.id, c.nombreley, c.normaId, c.indice, c.tituloIndice, c.tituloSubindice, " +
            "c.sumarioEjecutivo, c.texto, c.pagina, c.chunkIndex, c.totalChunks, " +
            "VectorDistance(c.VectorText, @embedding) AS similarityScore " +
            "FROM c " +
            "ORDER BY VectorDistance(c.VectorText, @embedding)")
            .WithParameter("@topN", topN)
            .WithParameter("@embedding", queryEmbedding);

        var results = new List<LeySeguridadSearchResult>();

        using var feed = container.GetItemQueryIterator<LeySeguridadSearchResult>(queryDef);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            results.AddRange(response);
        }

        _logger.LogInformation("?? Encontrados {Count} resultados", results.Count);
        return results;
    }

    /// <summary>
    /// Crea el database y container con VectorEmbeddingPolicy en /VectorText y VectorIndex.
    /// </summary>
    private async Task<Container> CreateContainerWithVectorPolicyAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseName);
        _logger.LogInformation("??? Database: {Db}", _databaseName);

        var embeddings = new Collection<Embedding>
        {
            new()
            {
                Path = "/VectorText",
                DataType = VectorDataType.Float32,
                DistanceFunction = DistanceFunction.Cosine,
                Dimensions = EmbeddingDimensions
            }
        };

        var containerProperties = new ContainerProperties(_containerName, "/nombreley")
        {
            VectorEmbeddingPolicy = new(embeddings),
            IndexingPolicy = new IndexingPolicy
            {
                VectorIndexes =
                {
                    new VectorIndexPath
                    {
                        Path = "/VectorText",
                        Type = VectorIndexType.QuantizedFlat
                    }
                }
            }
        };

        containerProperties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/VectorText/*" });

        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(containerProperties);
        _logger.LogInformation("?? Container: {Container} (status: {Status})",
            _containerName, containerResponse.StatusCode);

        return containerResponse.Container;
    }

    /// <summary>
    /// Convierte la NormaEstructurada en documentos. Si un subíndice tiene
    /// totalTokensSubindice > 800, divide su texto en chunks lógicos de ~800 caracteres.
    /// Cada documento lleva: indice, tituloIndice, sumarioEjecutivo, pagina del índice padre,
    /// más tituloSubindice, texto (o chunk), pagina y totalTokensSubindice del subíndice.
    /// </summary>
    private static List<LeySeguridadVectorDocument> BuildDocumentsWithChunking(NormaEstructurada norma)
    {
        var docs = new List<LeySeguridadVectorDocument>();

        foreach (var indice in norma.Indices)
        {
            if (indice.ListaSubindices.Count == 0)
            {
                // Sección sin subíndices ? un documento con el sumario
                docs.Add(new LeySeguridadVectorDocument
                {
                    Id = $"{NormaId}_{indice.Indice}",
                    Nombreley = NormaId,
                    NormaId = NormaId,
                    Tipo = "seccion",
                    Indice = indice.Indice,
                    TituloIndice = indice.TituloIndice,
                    SumarioEjecutivo = indice.SumarioEjecutivo,
                    TituloSubindice = indice.TituloIndice,
                    Texto = indice.SumarioEjecutivo,
                    Pagina = indice.Pagina,
                    TotalTokensSubindice = indice.TotalTokensIndice,
                    ChunkIndex = 0,
                    TotalChunks = 1
                });
            }
            else
            {
                foreach (var sub in indice.ListaSubindices)
                {
                    var subNum = sub.TituloSubindice.Split(' ')[0];

                    if (sub.TotalTokensSubindice > 800)
                    {
                        // Dividir en chunks lógicos de ~800 caracteres
                        var chunks = SplitIntoLogicalChunks(sub.Texto, ChunkSizeChars);

                        for (int i = 0; i < chunks.Count; i++)
                        {
                            docs.Add(new LeySeguridadVectorDocument
                            {
                                Id = $"{NormaId}_{indice.Indice}_{subNum}_chunk{i + 1}",
                                Nombreley = NormaId,
                                NormaId = NormaId,
                                Tipo = "subindice-chunk",
                                Indice = indice.Indice,
                                TituloIndice = indice.TituloIndice,
                                SumarioEjecutivo = indice.SumarioEjecutivo,
                                TituloSubindice = sub.TituloSubindice,
                                Texto = chunks[i],
                                Pagina = sub.Pagina,
                                TotalTokensSubindice = sub.TotalTokensSubindice,
                                ChunkIndex = i + 1,
                                TotalChunks = chunks.Count
                            });
                        }
                    }
                    else
                    {
                        // Subíndice pequeño ? un solo documento
                        docs.Add(new LeySeguridadVectorDocument
                        {
                            Id = $"{NormaId}_{indice.Indice}_{subNum}",
                            Nombreley = NormaId,
                            NormaId = NormaId,
                            Tipo = "subindice",
                            Indice = indice.Indice,
                            TituloIndice = indice.TituloIndice,
                            SumarioEjecutivo = indice.SumarioEjecutivo,
                            TituloSubindice = sub.TituloSubindice,
                            Texto = sub.Texto,
                            Pagina = sub.Pagina,
                            TotalTokensSubindice = sub.TotalTokensSubindice,
                            ChunkIndex = 0,
                            TotalChunks = 1
                        });
                    }
                }
            }
        }

        return docs;
    }

    /// <summary>
    /// Divide un texto largo en secciones lógicas de ~maxChars caracteres.
    /// Intenta cortar en saltos de línea dobles (\n\n), luego en salto de línea (\n),
    /// luego en punto seguido de espacio (. ), y finalmente en espacio.
    /// </summary>
    private static List<string> SplitIntoLogicalChunks(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxChars)
            return [text];

        var chunks = new List<string>();
        var remaining = text;

        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxChars)
            {
                chunks.Add(remaining.Trim());
                break;
            }

            // Buscar el mejor punto de corte dentro del rango permitido
            var cutPoint = FindBestCutPoint(remaining, maxChars);

            var chunk = remaining[..cutPoint].Trim();
            if (chunk.Length > 0)
                chunks.Add(chunk);

            remaining = remaining[cutPoint..].Trim();
        }

        return chunks;
    }

    /// <summary>
    /// Encuentra el mejor punto de corte para dividir texto de forma lógica.
    /// Prioridad: \n\n ? \n ? ". " ? " "
    /// </summary>
    private static int FindBestCutPoint(string text, int maxChars)
    {
        // 1) Buscar doble salto de línea
        var idx = text.LastIndexOf("\n\n", maxChars, StringComparison.Ordinal);
        if (idx > maxChars / 4)
            return idx + 2;

        // 2) Buscar salto de línea simple
        idx = text.LastIndexOf('\n', maxChars);
        if (idx > maxChars / 4)
            return idx + 1;

        // 3) Buscar punto seguido de espacio
        idx = text.LastIndexOf(". ", maxChars, StringComparison.Ordinal);
        if (idx > maxChars / 4)
            return idx + 2;

        // 4) Buscar espacio
        idx = text.LastIndexOf(' ', maxChars);
        if (idx > maxChars / 4)
            return idx + 1;

        // 5) Corte duro
        return maxChars;
    }

    /// <summary>
    /// Genera un vector embedding usando Azure OpenAI (text-embedding-ada-002) vía REST API.
    /// </summary>
    private async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[EmbeddingDimensions];

        var apiVersion = "2024-02-01";
        var url = $"{_openAiEndpoint.TrimEnd('/')}/openai/deployments/{_embeddingDeployment}/embeddings?api-version={apiVersion}";

        var requestBody = new { input = text };
        var jsonBody = JsonSerializer.Serialize(requestBody);

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        request.Headers.Add("api-key", _openAiApiKey);

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("? Embedding error {Status}: {Body}",
                response.StatusCode, responseBody[..Math.Min(300, responseBody.Length)]);
            throw new InvalidOperationException($"Error al generar embedding: {response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var embeddingArray = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding");

        var vector = new float[embeddingArray.GetArrayLength()];
        var i = 0;
        foreach (var val in embeddingArray.EnumerateArray())
        {
            vector[i++] = val.GetSingle();
        }

        return vector;
    }
}
