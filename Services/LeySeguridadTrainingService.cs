using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharpToken;
using TwinSeguridad.Models;
using Container = Microsoft.Azure.Cosmos.Container;

namespace TwinSeguridad.Services;

/// <summary>
/// Servicio AI Agent que usa Microsoft.Agents.AI + IChatClient para generar training.
/// 
/// Lee documentos de leyesseguridadcontainer, agrupa por índice, y para cada índice
/// usa un AIAgent con AIContextProvider para generar:
///   1) 20 preguntas frecuentes con respuestas
///   2) Curso completo dividido en lecciones para AI Agent avatar
///
/// Arquitectura (igual que AgentDoctor):
///   - IChatClient via AzureOpenAIClient + DefaultAzureCredential
///   - AIAgent con ChatClientAgentOptions
///   - AIContextProvider para inyectar el texto de la norma como contexto
///   - AgentSession para cada índice
///
/// Cosmos DB:
///   Account:   cdbseguridadindaccount
///   Database:  leyesdeseguridaddb
///   Source:    leyesseguridadcontainer  (lectura)
///   Target:    leyesseguridadtraining   (escritura)
///   Partition: /nombreley
///
/// AI:
///   Endpoint:   AZURE_OPENAI_ENDPOINT
///   Model:      AZURE_OPENAI_TRAINING_DEPLOYMENT_NAME (gpt-4o-mini)
/// </summary>
public class LeySeguridadTrainingService
{
    private readonly ILogger<LeySeguridadTrainingService> _logger;
    private readonly CosmosClient _cosmosClient;
    private readonly string _databaseName;
    private readonly string _sourceContainerName;
    private readonly string _trainingContainerName;
    private readonly string _openAiEndpoint;
    private readonly string _openAiApiKey;
    private readonly string _chatDeployment;
    private readonly GptEncoding _encoding;

    private const string TrainingContainerName = "leyesseguridadtraining";

    public LeySeguridadTrainingService(
        ILogger<LeySeguridadTrainingService> logger,
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
        _sourceContainerName = configuration["SEGURIDAD_COSMOS_CONTAINER"]
                               ?? configuration["Values:SEGURIDAD_COSMOS_CONTAINER"]
                               ?? "leyesseguridadcontainer";
        _trainingContainerName = TrainingContainerName;

        _openAiEndpoint = configuration["AZURE_OPENAI_ENDPOINT"]
                          ?? configuration["Values:AZURE_OPENAI_ENDPOINT"]
                          ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT no configurado.");
        _openAiApiKey = configuration["AZURE_OPENAI_API_KEY"]
                        ?? configuration["Values:AZURE_OPENAI_API_KEY"]
                        ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY no configurado.");
        _chatDeployment = configuration["AZURE_OPENAI_TRAINING_DEPLOYMENT_NAME"]
                          ?? configuration["Values:AZURE_OPENAI_TRAINING_DEPLOYMENT_NAME"]
                          ?? "gpt-4o-mini";

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
    /// Crea un IChatClient usando AzureOpenAIClient + ApiKeyCredential.
    /// </summary>
    private IChatClient CreateChatClient()
    {
        return new AzureOpenAIClient(
                new Uri(_openAiEndpoint),
                new System.ClientModel.ApiKeyCredential(_openAiApiKey))
            .GetChatClient(_chatDeployment)
            .AsIChatClient();
    }

    /// <summary>
    /// Crea un AIAgent con instrucciones de sistema y el texto de la norma como contexto.
    /// </summary>
    private AIAgent CreateAgentForTask(IChatClient chatClient, string systemInstructions, NormaContextProvider contextProvider)
    {
        return chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                Instructions = systemInstructions,
                ResponseFormat = ChatResponseFormat.Json
            },
            AIContextProviders = [contextProvider]
        });
    }

    /// <summary>
    /// Lee todos los documentos, agrupa por índice, genera training con AIAgent y guarda.
    /// </summary>
    public async Task<(int TotalIndices, int TotalLecciones, List<string> Errores)> GenerarTrainingAsync()
    {
        _logger.LogInformation("?? Iniciando generación de training con Microsoft.Agents.AI (deployment: {Deploy})...", _chatDeployment);

        var errores = new List<string>();

        // 1) Leer todos los documentos del source container
        List<LeySeguridadVectorDocument> allDocs;
        try
        {
            allDocs = await ReadAllDocumentsAsync();
            _logger.LogInformation("?? Documentos leídos: {Count}", allDocs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error leyendo documentos de {Container}", _sourceContainerName);
            errores.Add($"Error leyendo source container: {ex.Message}");
            return (0, 0, errores);
        }

        if (allDocs.Count == 0)
        {
            errores.Add("No hay documentos en leyesseguridadcontainer. Ejecuta /api/seguridad/indexar-ley primero.");
            return (0, 0, errores);
        }

        // 2) Agrupar por índice
        var grouped = allDocs
            .GroupBy(d => d.Indice)
            .OrderBy(g => g.Key)
            .ToList();

        _logger.LogInformation("?? Índices encontrados: {Count}", grouped.Count);

        // 3) Crear el container de training
        Container trainingContainer;
        try
        {
            trainingContainer = await CreateTrainingContainerAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error creando training container");
            errores.Add($"Error creando training container: {ex.Message}");
            return (0, 0, errores);
        }

        // 4) Crear el IChatClient una sola vez
        IChatClient chatClient;
        try
        {
            chatClient = CreateChatClient();
            _logger.LogInformation("?? IChatClient creado: {Endpoint} / {Deploy}", _openAiEndpoint, _chatDeployment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error creando IChatClient");
            errores.Add($"Error creando IChatClient: {ex.Message}");
            return (0, 0, errores);
        }

        // 5) Para cada índice, generar el training con AIAgent
        var totalLecciones = 0;
        var totalIndices = 0;

        foreach (var group in grouped)
        {
            var indice = group.Key;
            var docs = group.OrderBy(d => d.TituloSubindice).ThenBy(d => d.ChunkIndex).ToList();

            try
            {
                _logger.LogInformation("?? Procesando índice {Indice}: {Titulo} ({Docs} documentos)...",
                    indice, docs.First().TituloIndice, docs.Count);

                // Concatenar todo el texto de este índice
                var textoCompleto = BuildTextoCompleto(docs);
                var totalTokens = CountTokens(textoCompleto);
                var totalSubsecciones = docs.Select(d => d.TituloSubindice).Distinct().Count();

                _logger.LogInformation("?? Índice {Indice}: {Chars} chars, {Tokens} tokens, {Subs} subsecciones",
                    indice, textoCompleto.Length, totalTokens, totalSubsecciones);

                // ?? Paso 1: Generar preguntas frecuentes con AIAgent ??
                _logger.LogInformation("?? AIAgent: Generando preguntas para índice {Indice}...", indice);
                var preguntas = await GenerarPreguntasConAgentAsync(
                    chatClient, docs.First().TituloIndice, textoCompleto);
                _logger.LogInformation("? Preguntas generadas: {Count}", preguntas.Count);

                // ?? Paso 2: Generar curso con AIAgent (pasando preguntas) ??
                _logger.LogInformation("?? AIAgent: Generando curso para índice {Indice}...", indice);
                var curso = await GenerarCursoConAgentAsync(
                    chatClient, docs.First().TituloIndice, textoCompleto, preguntas);
                _logger.LogInformation("? Curso generado: {Lecciones} lecciones", curso.TotalLecciones);

                // Construir documento de training
                var trainingDoc = new LeySeguridadTrainingDocument
                {
                    Id = $"{docs.First().Nombreley}_training_{indice}",
                    Nombreley = docs.First().Nombreley,
                    Indice = indice,
                    TituloIndice = docs.First().TituloIndice,
                    SumarioEjecutivo = docs.First().SumarioEjecutivo,
                    TextoCompletoIndice = textoCompleto,
                    TotalSubsecciones = totalSubsecciones,
                    TotalTokens = totalTokens,
                    PreguntasFrecuentes = preguntas,
                    Curso = curso,
                    FechaGeneracion = DateTime.UtcNow,
                    ModeloAI = _chatDeployment
                };

                // Upsert en Cosmos DB
                _logger.LogInformation("?? Guardando training doc para índice {Indice}...", indice);
                await trainingContainer.UpsertItemAsync(trainingDoc, new PartitionKey(trainingDoc.Nombreley));

                totalIndices++;
                totalLecciones += curso.TotalLecciones;

                _logger.LogInformation("? Índice {Indice} completado: {Preguntas} preguntas, {Lecciones} lecciones, duración: {Duracion}",
                    indice, preguntas.Count, curso.TotalLecciones, curso.DuracionEstimada);
            }
            catch (Exception ex)
            {
                var errorMsg = $"Índice {indice} ({docs.FirstOrDefault()?.TituloIndice ?? "?"}): {ex.Message}";
                _logger.LogError(ex, "? Error procesando índice {Indice}: {Error}", indice, ex.Message);
                errores.Add(errorMsg);
            }
        }

        _logger.LogInformation("?? Training completado: {Indices}/{Total} índices, {Lecciones} lecciones, {Errores} errores",
            totalIndices, grouped.Count, totalLecciones, errores.Count);

        return (totalIndices, totalLecciones, errores);
    }

    #region AIAgent — Preguntas Frecuentes

    /// <summary>
    /// Usa AIAgent + AIContextProvider para generar 20 preguntas frecuentes.
    /// Crea un agente con sesión, inyecta el texto de la norma como contexto,
    /// y ejecuta el prompt.
    /// </summary>
    private async Task<List<PreguntaRespuesta>> GenerarPreguntasConAgentAsync(
        IChatClient chatClient, string tituloIndice, string textoCompleto)
    {
        var systemInstructions = @"Eres un experto en seguridad laboral y normatividad mexicana. 
Tu tarea es generar exactamente 20 preguntas frecuentes con respuestas claras y precisas 
basadas ÚNICAMENTE en el texto de la norma que se te proporciona en el contexto.

Las preguntas deben ser las que más comúnmente haría un trabajador, patrón o inspector.
Las respuestas deben ser completas, citando lo que dice la norma cuando sea posible.

Responde SIEMPRE con un JSON object con la key ""preguntas"" que contenga el array.
Formato: {""preguntas"": [{""numero"": 1, ""pregunta"": ""..."", ""respuesta"": ""...""}]}.";

        // Crear AIContextProvider con el texto de la norma
        var contextProvider = new NormaContextProvider(tituloIndice, textoCompleto);

        // Crear agente y sesión
        var agent = CreateAgentForTask(chatClient, systemInstructions, contextProvider);
        var session = await agent.CreateSessionAsync();

        // Ejecutar el agente
        var userPrompt = $@"Sección de la norma: {tituloIndice}

Genera exactamente 20 preguntas frecuentes con respuestas detalladas basadas en el texto de esta sección.";

        var agentResponse = await agent.RunAsync(userPrompt, session);
        var responseText = ExtractResponseText(agentResponse);

        try
        {
            var cleaned = CleanJsonResponseForArray(responseText);
            var preguntas = JsonSerializer.Deserialize<List<PreguntaRespuesta>>(cleaned,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return preguntas ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "?? Error deserializando preguntas para '{Titulo}': {Raw}",
                tituloIndice, responseText[..Math.Min(300, responseText.Length)]);
            return CreateFallbackPreguntas(tituloIndice);
        }
    }

    #endregion

    #region AIAgent — Curso de Training

    /// <summary>
    /// Usa AIAgent + AIContextProvider para generar un curso completo.
    /// Inyecta el texto de la norma + las preguntas ya generadas como contexto.
    /// </summary>
    private async Task<CursoTraining> GenerarCursoConAgentAsync(
        IChatClient chatClient, string tituloIndice, string textoCompleto,
        List<PreguntaRespuesta> preguntasGeneradas)
    {
        // Serializar preguntas para incluirlas en el contexto
        var preguntasResumen = new StringBuilder();
        foreach (var p in preguntasGeneradas)
        {
            preguntasResumen.AppendLine($"  P{p.Numero}: {p.Pregunta}");
            preguntasResumen.AppendLine($"  R{p.Numero}: {p.Respuesta}");
            preguntasResumen.AppendLine();
        }

        var systemInstructions = @"Eres un diseñador instruccional experto en seguridad laboral mexicana.
Tu tarea es crear un curso de capacitación COMPLETO y DETALLADO basado en el texto de una 
sección de una norma de seguridad y en las preguntas frecuentes ya generadas.

El curso será impartido por un AI Agent avatar (instructor virtual) que hablará directamente 
al trabajador. Reglas para las lecciones:
- Sencillas y claras, como si hablaras con un trabajador de fábrica
- Cada lección entre 5-15 minutos
- El campo ""contenido"" debe tener el TEXTO COMPLETO que el avatar dirá al trabajador,
  NO un resumen ni una referencia. Debe ser el guión real del instructor, de al menos 
  200 palabras por lección, con explicaciones, ejemplos y lenguaje amigable.
- Incluir puntos clave que el avatar debe enfatizar con gestos o cambio de tono
- Integrar las preguntas frecuentes como parte de las lecciones cuando sea relevante
- Crear tantas lecciones como sean necesarias para cubrir todo el contenido

Responde con un JSON object con esta estructura exacta:
{
  ""tituloCurso"": ""..."",
  ""descripcionCurso"": ""..."",
  ""duracionEstimada"": ""X horas Y minutos"",
  ""totalLecciones"": N,
  ""lecciones"": [
    {
      ""numeroLeccion"": 1,
      ""tituloLeccion"": ""..."",
      ""objetivoLeccion"": ""..."",
      ""contenido"": ""Guión completo que el avatar dirá al trabajador con al menos 200 palabras..."",
      ""duracionMinutos"": 10,
      ""puntosClaveParaAvatar"": [""punto 1"", ""punto 2"", ""punto 3""]
    }
  ]
}";
        // Crear AIContextProvider con el texto + las preguntas
        var contextoCompleto = $@"{textoCompleto}

???????????????????????????????????????????????????????????????????????????????
              PREGUNTAS FRECUENTES YA GENERADAS (intégralas en las lecciones)
???????????????????????????????????????????????????????????????????????????????
{preguntasResumen}";

        var contextProvider = new NormaContextProvider(tituloIndice, contextoCompleto);

        // Crear agente y sesión
        var agent = CreateAgentForTask(chatClient, systemInstructions, contextProvider);
        var session = await agent.CreateSessionAsync();

        // Ejecutar el agente
        var userPrompt = $@"Sección de la norma: {tituloIndice}

Diseña un curso de capacitación COMPLETO con todas las lecciones necesarias.
Cada lección debe tener un guión COMPLETO y DETALLADO (campo ""contenido"") 
de al menos 200 palabras que el avatar pueda leer directamente al trabajador.
NO pongas resúmenes ni referencias, pon el texto real que el instructor dirá.";

        var agentResponse = await agent.RunAsync(userPrompt, session);
        var responseText = ExtractResponseText(agentResponse);

        try
        {
            _logger.LogDebug("?? Curso raw response ({Len} chars): {Preview}",
                responseText.Length, responseText[..Math.Min(200, responseText.Length)]);

            var curso = DeserializeCurso(responseText);
            if (curso != null && curso.Lecciones.Count > 0)
                return curso;

            _logger.LogWarning("?? Curso deserializado pero vacío para '{Titulo}'", tituloIndice);
            return CreateFallbackCurso(tituloIndice);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "?? Error deserializando curso para '{Titulo}': {Raw}",
                tituloIndice, responseText[..Math.Min(500, responseText.Length)]);
            return CreateFallbackCurso(tituloIndice);
        }
    }

    #endregion

    #region AIContextProvider — Inyecta texto de la norma como contexto

    /// <summary>
    /// AIContextProvider que inyecta el texto completo de una sección de la norma
    /// como contexto del agente. Mismo patrón que DoctorPatientMemory en AgentDoctor.
    /// </summary>
    private sealed class NormaContextProvider : AIContextProvider
    {
        private readonly string _tituloIndice;
        private readonly string _textoCompleto;

        public NormaContextProvider(string tituloIndice, string textoCompleto)
        {
            _tituloIndice = tituloIndice;
            _textoCompleto = textoCompleto;
        }

        public override IReadOnlyList<string> StateKeys => [];

        protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
        {
            var instructions = $@"
???????????????????????????????????????????????????????????????????????????????
              TEXTO COMPLETO DE LA SECCIÓN: {_tituloIndice}
???????????????????????????????????????????????????????????????????????????????

{_textoCompleto}

???????????????????????????????????????????????????????????????????????????????
                         FIN DEL CONTEXTO
???????????????????????????????????????????????????????????????????????????????";

            return ValueTask.FromResult(new AIContext { Instructions = instructions });
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Extrae el texto de la respuesta del agente (mismo patrón que AgentDoctor).
    /// </summary>
    private string ExtractResponseText(AgentResponse agentResponse)
    {
        if (agentResponse?.Messages != null && agentResponse.Messages.Count > 0)
        {
            var lastMessage = agentResponse.Messages.LastOrDefault();
            if (lastMessage?.Contents != null)
            {
                foreach (var content in lastMessage.Contents)
                {
                    if (content is TextContent textContent)
                        return textContent.Text ?? "";
                }
            }
        }

        return agentResponse?.ToString() ?? "";
    }

    /// <summary>
    /// Lee todos los documentos del container source.
    /// </summary>
    private async Task<List<LeySeguridadVectorDocument>> ReadAllDocumentsAsync()
    {
        var container = _cosmosClient.GetContainer(_databaseName, _sourceContainerName);
        var query = new QueryDefinition(
            "SELECT c.id, c.nombreley, c.normaId, c.tipo, c.indice, c.tituloIndice, " +
            "c.sumarioEjecutivo, c.tituloSubindice, c.texto, c.pagina, " +
            "c.totalTokensSubindice, c.chunkIndex, c.totalChunks " +
            "FROM c");

        var results = new List<LeySeguridadVectorDocument>();

        using var feed = container.GetItemQueryIterator<LeySeguridadVectorDocument>(query);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    /// <summary>
    /// Concatena el texto de todos los documentos de un índice en orden lógico.
    /// </summary>
    private static string BuildTextoCompleto(List<LeySeguridadVectorDocument> docs)
    {
        var sb = new StringBuilder();
        string? lastSubtitle = null;

        foreach (var doc in docs)
        {
            if (doc.TituloSubindice != lastSubtitle)
            {
                if (lastSubtitle != null)
                    sb.AppendLine();
                sb.AppendLine($"--- {doc.TituloSubindice} ---");
                lastSubtitle = doc.TituloSubindice;
            }

            sb.AppendLine(doc.Texto);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Deserializa la respuesta del curso con múltiples estrategias.
    /// </summary>
    private static CursoTraining? DeserializeCurso(string responseText)
    {
        var trimmed = responseText.Trim();

        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Intento 1: parsear directo
        try
        {
            var curso = JsonSerializer.Deserialize<CursoTraining>(trimmed, options);
            if (curso is { Lecciones.Count: > 0 })
                return curso;
        }
        catch { }

        // Intento 2: wrapper object
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var inner = JsonSerializer.Deserialize<CursoTraining>(prop.Value.GetRawText(), options);
                    if (inner is { Lecciones.Count: > 0 })
                        return inner;
                }
            }

            if (root.TryGetProperty("lecciones", out _) || root.TryGetProperty("Lecciones", out _))
            {
                var curso = JsonSerializer.Deserialize<CursoTraining>(trimmed, options);
                if (curso is { Lecciones.Count: > 0 })
                    return curso;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Limpia JSON y extrae array de un objeto wrapper (para preguntas).
    /// </summary>
    private static string CleanJsonResponseForArray(string response)
    {
        var trimmed = response.Trim();

        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
                trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```"))
                trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }

        if (trimmed.StartsWith("["))
            return trimmed;

        if (trimmed.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                        return prop.Value.GetRawText();
                }
            }
            catch { }
        }

        return trimmed;
    }

    /// <summary>
    /// Crea el container de training en Cosmos DB si no existe.
    /// </summary>
    private async Task<Container> CreateTrainingContainerAsync()
    {
        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseName);
        _logger.LogInformation("??? Database: {Db}", _databaseName);

        var containerProperties = new ContainerProperties(_trainingContainerName, "/nombreley");

        var containerResponse = await database.Database.CreateContainerIfNotExistsAsync(containerProperties);
        _logger.LogInformation("?? Training container: {Container} (status: {Status})",
            _trainingContainerName, containerResponse.StatusCode);

        return containerResponse.Container;
    }

    private int CountTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return _encoding.Encode(text).Count;
    }

    private static List<PreguntaRespuesta> CreateFallbackPreguntas(string titulo)
    {
        return
        [
            new PreguntaRespuesta
            {
                Numero = 1,
                Pregunta = $"¿Qué establece la sección '{titulo}'?",
                Respuesta = "No se pudo generar la respuesta automáticamente. Consulte el texto completo de la sección."
            }
        ];
    }

    private static CursoTraining CreateFallbackCurso(string titulo)
    {
        return new CursoTraining
        {
            TituloCurso = $"Curso: {titulo}",
            DescripcionCurso = "Curso generado automáticamente. No se pudo completar la generación con AI.",
            DuracionEstimada = "Por determinar",
            TotalLecciones = 1,
            Lecciones =
            [
                new LeccionTraining
                {
                    NumeroLeccion = 1,
                    TituloLeccion = $"Introducción a {titulo}",
                    ObjetivoLeccion = "Conocer los aspectos generales de esta sección.",
                    Contenido = "Consulte el texto completo de la sección para mayor detalle.",
                    DuracionMinutos = 15,
                    PuntosClaveParaAvatar = ["Revisar el texto completo de la norma"]
                }
            ]
        };
    }

    #endregion
}
