using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TwinSeguridad.Models;

namespace TwinSeguridad.Services;

/// <summary>
/// Servicio que almacena los subíndices de la norma en Cosmos DB con vector embeddings
/// para búsqueda semántica usando VectorDistance.
///
/// Flujo:
///   1. Lee norma-seguridad-estructurada.json
///   2. Genera embeddings con Azure OpenAI (text-embedding-ada-002)
///   3. Crea/verifica el container con VectorEmbeddingPolicy + VectorIndex
///   4. Inserta cada subíndice como documento con su vector
///   5. Expone búsqueda semántica por VectorDistance
///
/// Configuración (local.settings.json):
///   COSMOS_ENDPOINT              = https://flatbitdb.documents.azure.com:443/
///   COSMOS_KEY                   = ...
///   COSMOS_DATABASE_NAME         = TwinHumanDB
///   AZURE_OPENAI_ENDPOINT        = https://flatbitai.openai.azure.com
///   AZURE_OPENAI_API_KEY         = ...
///   AZURE_OPENAI_EMBEDDING_DEPLOYMENT_NAME = text-embedding-ada-002
/// </summary>
public class NormaCosmosVectorService
{
    private readonly ILogger<NormaCosmosVectorService> _logger;
    private readonly CosmosClient _cosmosClient;
    private readonly string _databaseName;
    private readonly string _containerName;
    private readonly string _openAiEndpoint;
    private readonly string _openAiApiKey;
    private readonly string _embeddingDeployment;
    private readonly HttpClient _httpClient;

    private const string NormaId = "NOM-002-STPS-2010";
    private const int EmbeddingDimensions = 1536; // text-embedding-ada-002

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public NormaCosmosVectorService(ILogger<NormaCosmosVectorService> logger, IConfiguration configuration)
    {
        _logger = logger;

        var cosmosEndpoint = configuration["COSMOS_ENDPOINT"]
                             ?? configuration["Values:COSMOS_ENDPOINT"]
                             ?? throw new InvalidOperationException("COSMOS_ENDPOINT no configurado.");
        var cosmosKey = configuration["COSMOS_KEY"]
                        ?? configuration["Values:COSMOS_KEY"]
                        ?? throw new InvalidOperationException("COSMOS_KEY no configurado.");

        _databaseName = configuration["COSMOS_DATABASE_NAME"]
                        ?? configuration["Values:COSMOS_DATABASE_NAME"]
                        ?? "TwinHumanDB";
        _containerName = "norma-seguridad";

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
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        });

        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Lee la norma estructurada, genera embeddings, crea el container con vector policy
    /// e inserta todos los documentos en Cosmos DB.
    /// </summary>
    public async Task<(int TotalDocumentos, int TotalTokens)> IndexarNormaAsync(string estructuradaJsonPath)
    {
        if (!File.Exists(estructuradaJsonPath))
            throw new FileNotFoundException($"No se encontró: {estructuradaJsonPath}");

        _logger.LogInformation("??? Leyendo norma estructurada: {Path}", estructuradaJsonPath);

        var json = await File.ReadAllTextAsync(estructuradaJsonPath, Encoding.UTF8);
        var norma = JsonSerializer.Deserialize<NormaEstructurada>(json, JsonReadOptions)
            ?? throw new InvalidOperationException("No se pudo deserializar la norma estructurada.");

        // 1) Crear database y container con vector policy
        var container = await CreateContainerWithVectorPolicyAsync();

        // 2) Convertir la norma en documentos vectorizables
        var documentos = BuildDocuments(norma);
        _logger.LogInformation("?? Total documentos a indexar: {Count}", documentos.Count);

        // 3) Generar embeddings y subir a Cosmos DB
        var totalTokens = 0;
        var inserted = 0;

        foreach (var doc in documentos)
        {
            try
            {
                // Generar embedding del texto
                var embedding = await GenerateEmbeddingAsync(doc.Texto);
                doc.ContentVector = embedding;

                // Upsert en Cosmos DB
                await container.UpsertItemAsync(doc, new PartitionKey(doc.NormaId));
                inserted++;
                totalTokens += doc.TotalTokens;

                _logger.LogDebug("? Insertado: {Id} ({Tokens} tokens)", doc.Id, doc.TotalTokens);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "?? Error al indexar documento {Id}", doc.Id);
            }
        }

        _logger.LogInformation("??? Indexación completada: {Inserted}/{Total} documentos, {Tokens} tokens",
            inserted, documentos.Count, totalTokens);

        return (inserted, totalTokens);
    }

    /// <summary>
    /// Búsqueda semántica: genera embedding de la query y busca los TOP N documentos más similares.
    /// </summary>
    public async Task<List<VectorSearchResult>> BuscarAsync(string query, int topN = 10)
    {
        _logger.LogInformation("?? Búsqueda vectorial: \"{Query}\" (top {N})", query, topN);

        // Generar embedding de la query
        var queryEmbedding = await GenerateEmbeddingAsync(query);

        var container = _cosmosClient.GetContainer(_databaseName, _containerName);

        // Query con VectorDistance
        var queryDef = new QueryDefinition(
            $"SELECT TOP @topN c.id, c.normaId, c.indice, c.tituloIndice, c.tituloSubindice, " +
            $"c.texto, c.pagina, VectorDistance(c.contentVector, @embedding) AS similarityScore " +
            $"FROM c " +
            $"ORDER BY VectorDistance(c.contentVector, @embedding)")
            .WithParameter("@topN", topN)
            .WithParameter("@embedding", queryEmbedding);

        var results = new List<VectorSearchResult>();

        using var feed = container.GetItemQueryIterator<VectorSearchResult>(queryDef);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            results.AddRange(response);
        }

        _logger.LogInformation("?? Encontrados {Count} resultados", results.Count);
        return results;
    }

    /// <summary>
    /// Crea el database y container con VectorEmbeddingPolicy y VectorIndex.
    /// Si ya existe, lo devuelve.
    /// </summary>
    private async Task<Container> CreateContainerWithVectorPolicyAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseName);
        _logger.LogInformation("??? Database: {Db}", _databaseName);

        // Definir vector embedding policy
        var embeddings = new Collection<Embedding>
        {
            new()
            {
                Path = "/contentVector",
                DataType = VectorDataType.Float32,
                DistanceFunction = DistanceFunction.Cosine,
                Dimensions = EmbeddingDimensions
            }
        };

        var containerProperties = new ContainerProperties(_containerName, "/normaId")
        {
            VectorEmbeddingPolicy = new(embeddings),
            IndexingPolicy = new IndexingPolicy
            {
                VectorIndexes =
                {
                    new VectorIndexPath
                    {
                        Path = "/contentVector",
                        Type = VectorIndexType.QuantizedFlat
                    }
                }
            }
        };

        // Incluir todos los paths excepto el vector
        containerProperties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/contentVector/*" });

        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(containerProperties);
        _logger.LogInformation("??? Container: {Container} (status: {Status})",
            _containerName, containerResponse.StatusCode);

        return containerResponse.Container;
    }

    /// <summary>
    /// Convierte la NormaEstructurada en una lista de NormaVectorDocument (un doc por subíndice).
    /// </summary>
    private static List<NormaVectorDocument> BuildDocuments(NormaEstructurada norma)
    {
        var docs = new List<NormaVectorDocument>();

        foreach (var indice in norma.Indices)
        {
            // Imágenes de la sección
            var imagenes = indice.Imagenes.Select(img => new ImagenReferenciaVector
            {
                NombreArchivo = img.NombreArchivo,
                DescripcionIA = img.DescripcionIA,
                Pagina = img.Pagina
            }).ToList();

            if (indice.ListaSubindices.Count == 0)
            {
                // Sección sin subíndices ? un documento
                docs.Add(new NormaVectorDocument
                {
                    Id = $"{NormaId}_{indice.Indice}",
                    NormaId = NormaId,
                    Tipo = "seccion",
                    Indice = indice.Indice,
                    TituloIndice = indice.TituloIndice,
                    Subindice = "",
                    TituloSubindice = indice.TituloIndice,
                    SumarioEjecutivo = indice.SumarioEjecutivo,
                    Texto = indice.SumarioEjecutivo,
                    Pagina = indice.Pagina,
                    TotalTokens = indice.TotalTokensIndice,
                    Imagenes = imagenes.Count > 0 ? imagenes : null
                });
            }
            else
            {
                // Un documento por cada subíndice
                foreach (var sub in indice.ListaSubindices)
                {
                    // Extraer número de subíndice del título: "4.1 Agente..." ? "4.1"
                    var subNum = sub.TituloSubindice.Split(' ')[0];

                    docs.Add(new NormaVectorDocument
                    {
                        Id = $"{NormaId}_{indice.Indice}_{subNum}",
                        NormaId = NormaId,
                        Tipo = "subindice",
                        Indice = indice.Indice,
                        TituloIndice = indice.TituloIndice,
                        Subindice = subNum,
                        TituloSubindice = sub.TituloSubindice,
                        SumarioEjecutivo = indice.SumarioEjecutivo,
                        Texto = sub.Texto,
                        Pagina = sub.Pagina,
                        TotalTokens = sub.TotalTokensSubindice,
                        Imagenes = imagenes.Count > 0 ? imagenes : null
                    });
                }
            }
        }

        return docs;
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
        var json = JsonSerializer.Serialize(requestBody);

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
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
        var idx = 0;
        foreach (var val in embeddingArray.EnumerateArray())
        {
            vector[idx++] = val.GetSingle();
        }

        return vector;
    }
}
