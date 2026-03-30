using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TwinSeguridad.Services;

namespace TwinSeguridad.Agent;

/// <summary>
/// Agente AI de Seguridad y Normas — con memoria de sesión y búsqueda vectorial.
///
/// Usa Microsoft.Agents.AI framework:
///   - AIAgent + AgentSession para continuidad de diálogo
///   - AIContextProvider (SeguridadNormaMemory) para cargar índice del manual una sola vez
///   - MCP Tools (SeguridadNormaMcpTools) para búsqueda vectorial en Cosmos DB
///
/// Endpoints:
///   POST   /api/seguridad/agent/chat              — Chat con el agente
///   GET    /api/seguridad/agent/{sessionId}/history — Info de la sesión
///   DELETE /api/seguridad/agent/{sessionId}        — Terminar sesión
///   GET    /api/seguridad/agent/info               — Info del agente
/// </summary>
public class AgentSeguridadNormaFx
{
    private readonly ILogger<AgentSeguridadNormaFx> _logger;
    private readonly IConfiguration _configuration;
    private readonly LeySeguridadCosmosVectorService _vectorService;
    private readonly ManualSeguridadService _manualService;
    private readonly string _azureOpenAIEndpoint;
    private readonly string _deploymentName;
    private readonly string _openAiApiKey;

    /// <summary>Cache de sesiones en memoria por sessionId.</summary>
    private static readonly Dictionary<string, AgentSeguridadSessionData> _sessions = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AgentSeguridadNormaFx(
        ILogger<AgentSeguridadNormaFx> logger,
        IConfiguration configuration,
        LeySeguridadCosmosVectorService vectorService,
        ManualSeguridadService manualService)
    {
        _logger = logger;
        _configuration = configuration;
        _vectorService = vectorService;
        _manualService = manualService;

        _azureOpenAIEndpoint = configuration["AZURE_OPENAI_ENDPOINT"]
                               ?? configuration["Values:AZURE_OPENAI_ENDPOINT"]
                               ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT no configurado.");

        _deploymentName = configuration["AZURE_OPENAI_CHAT_DEPLOYMENT_NAME"]
                          ?? configuration["Values:AZURE_OPENAI_CHAT_DEPLOYMENT_NAME"]
                          ?? "gpt-4o-mini";

        _openAiApiKey = configuration["AZURE_OPENAI_API_KEY"]
                        ?? configuration["Values:AZURE_OPENAI_API_KEY"]
                        ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY no configurado.");

        _logger.LogInformation("?? AgentSeguridadNormaFx inicializado — modelo: {Model}", _deploymentName);
    }

    // ?????????????????????????????????????????????????????????????????????
    //  POST /api/seguridad/agent/chat
    // ?????????????????????????????????????????????????????????????????????

    [Function("AgentSeguridadChat")]
    public async Task<HttpResponseData> Chat(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "seguridad/agent/chat")]
        HttpRequestData req)
    {
        _logger.LogInformation("?? POST /api/seguridad/agent/chat");
        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        try
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<SeguridadChatRequest>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (request == null || string.IsNullOrWhiteSpace(request.Prompt))
            {
                return await WriteJson(response, HttpStatusCode.BadRequest, new
                {
                    success = false,
                    error = "El campo 'prompt' es requerido. Ejemplo: { \"prompt\": \"¿Qué dice la norma sobre extintores?\" }"
                });
            }

            var startTime = DateTime.UtcNow;

            // Obtener o crear sesión
            var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
                ? $"seg-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..6]}"
                : request.SessionId;

            var sessionData = await GetOrCreateSessionAsync(sessionId);

            // Ejecutar el agente con el prompt del usuario
            var result = await sessionData.Agent.RunAsync(request.Prompt, sessionData.Session);

            sessionData.MessageCount++;

            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                sessionId,
                response = result,
                contextLoaded = sessionData.Memory.IsContextLoaded(sessionData.Session),
                contextLoadedAt = sessionData.Memory.GetContextLoadedAt(sessionData.Session),
                seccionesEnMemoria = sessionData.Memory.GetSeccionesCount(sessionData.Session),
                messageCount = sessionData.MessageCount,
                processingTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error en AgentSeguridadChat");
            return await WriteJson(response, HttpStatusCode.InternalServerError, new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ?????????????????????????????????????????????????????????????????????
    //  GET /api/seguridad/agent/{sessionId}/history
    // ?????????????????????????????????????????????????????????????????????

    [Function("AgentSeguridadGetHistory")]
    public async Task<HttpResponseData> GetHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "seguridad/agent/{sessionId}/history")]
        HttpRequestData req,
        string sessionId)
    {
        _logger.LogInformation("?? GET /api/seguridad/agent/{SessionId}/history", sessionId);
        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        if (!_sessions.TryGetValue(sessionId, out var sessionData))
        {
            return await WriteJson(response, HttpStatusCode.NotFound, new
            {
                success = false,
                error = $"Sesión '{sessionId}' no encontrada."
            });
        }

        return await WriteJson(response, HttpStatusCode.OK, new
        {
            success = true,
            sessionId,
            contextLoaded = sessionData.Memory.IsContextLoaded(sessionData.Session),
            contextLoadedAt = sessionData.Memory.GetContextLoadedAt(sessionData.Session),
            seccionesEnMemoria = sessionData.Memory.GetSeccionesCount(sessionData.Session),
            messageCount = sessionData.MessageCount,
            createdAt = sessionData.CreatedAt
        });
    }

    // ?????????????????????????????????????????????????????????????????????
    //  DELETE /api/seguridad/agent/{sessionId}
    // ?????????????????????????????????????????????????????????????????????

    [Function("AgentSeguridadEndSession")]
    public async Task<HttpResponseData> EndSession(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "seguridad/agent/{sessionId}")]
        HttpRequestData req,
        string sessionId)
    {
        _logger.LogInformation("??? DELETE /api/seguridad/agent/{SessionId}", sessionId);
        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        if (_sessions.Remove(sessionId))
        {
            return await WriteJson(response, HttpStatusCode.OK, new
            {
                success = true,
                message = $"Sesión '{sessionId}' terminada."
            });
        }

        return await WriteJson(response, HttpStatusCode.NotFound, new
        {
            success = false,
            error = $"Sesión '{sessionId}' no encontrada."
        });
    }

    // ?????????????????????????????????????????????????????????????????????
    //  GET /api/seguridad/agent/info
    // ?????????????????????????????????????????????????????????????????????

    [Function("AgentSeguridadInfo")]
    public async Task<HttpResponseData> GetInfo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "seguridad/agent/info")]
        HttpRequestData req)
    {
        var response = req.CreateResponse();
        AddCorsHeaders(response, req);

        return await WriteJson(response, HttpStatusCode.OK, new
        {
            agent = "AgentSeguridadNorma",
            version = "1.0.0",
            description = "Agente AI experto en NOM-002-STPS-2010 y Manual de Procedimientos de Seguridad contra Incendios",
            architecture = "AIAgent + AIContextProvider (memoria) + MCP Tools (búsqueda vectorial)",
            model = _deploymentName,
            features = new[]
            {
                "Sesión en memoria con continuidad de diálogo",
                "Contexto del manual cargado una sola vez via AIContextProvider",
                "Búsqueda semántica vectorial en Cosmos DB (VectorDistance)",
                "MCP Tools: BuscarEnNormaSeguridad, ObtenerSeccionManual, ObtenerIndicesManual",
                "Multi-turn conversation nativo"
            },
            activeSessions = _sessions.Count,
            endpoints = new[]
            {
                "POST   /api/seguridad/agent/chat              — Chat con el agente",
                "GET    /api/seguridad/agent/{sessionId}/history — Info de la sesión",
                "DELETE /api/seguridad/agent/{sessionId}        — Terminar sesión",
                "GET    /api/seguridad/agent/info               — Info del agente"
            }
        });
    }

    // ?????????????????????????????????????????????????????????????????????
    //  CORS preflight
    // ?????????????????????????????????????????????????????????????????????

    [Function("AgentSeguridadCorsChat")]
    public HttpResponseData HandleCorsChat(
        [HttpTrigger(AuthorizationLevel.Anonymous, "options", Route = "seguridad/agent/chat")]
        HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        AddCorsHeaders(response, req);
        return response;
    }

    [Function("AgentSeguridadCorsHistory")]
    public HttpResponseData HandleCorsHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "options", Route = "seguridad/agent/{sessionId}/history")]
        HttpRequestData req, string sessionId)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        AddCorsHeaders(response, req);
        return response;
    }

    [Function("AgentSeguridadCorsSession")]
    public HttpResponseData HandleCorsSession(
        [HttpTrigger(AuthorizationLevel.Anonymous, "options", Route = "seguridad/agent/{sessionId}")]
        HttpRequestData req, string sessionId)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        AddCorsHeaders(response, req);
        return response;
    }

    // ?????????????????????????????????????????????????????????????????????
    //  Session Management
    // ?????????????????????????????????????????????????????????????????????

    private async Task<AgentSeguridadSessionData> GetOrCreateSessionAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            _logger.LogInformation("?? Reutilizando sesión existente: {SessionId}", sessionId);
            return existing;
        }

        _logger.LogInformation("?? Creando nueva sesión: {SessionId}", sessionId);

        // Crear IChatClient con Azure OpenAI usando API Key
        IChatClient chatClient = new AzureOpenAIClient(
                new Uri(_azureOpenAIEndpoint),
                new System.ClientModel.ApiKeyCredential(_openAiApiKey))
            .GetChatClient(_deploymentName)
            .AsIChatClient();

        // Crear MCP Tools con los servicios inyectados
        var mcpTools = new SeguridadNormaMcpTools(_logger, _vectorService, _manualService);

        // Crear Memory (AIContextProvider)
        var memory = new SeguridadNormaMemory(_logger, _manualService);

        // Crear el agente
        var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                Instructions = BuildSystemInstructions(),
                Tools = mcpTools.GetAllTools()
            },
            AIContextProviders = [memory]
        });

        // Crear la sesión
        var session = await agent.CreateSessionAsync();

        var sessionData = new AgentSeguridadSessionData
        {
            SessionId = sessionId,
            Agent = agent,
            Session = session,
            Memory = memory,
            CreatedAt = DateTime.UtcNow
        };

        _sessions[sessionId] = sessionData;
        return sessionData;
    }

    private static string BuildSystemInstructions()
    {
        return """
               Eres un Agente AI experto en seguridad laboral, especializado en la NOM-002-STPS-2010
               (Prevención y Protección contra Incendios en Centros de Trabajo) y en el Manual de
               Procedimientos y Reglas de Negocio basado en esa norma.

               ???????????????????????????????????????????????????????????????????
                   TU CONOCIMIENTO Y HERRAMIENTAS
               ???????????????????????????????????????????????????????????????????

               1. BÚSQUEDA SEMÁNTICA (herramienta principal):
                  - Usa "BuscarEnNormaSeguridad" para buscar en la NOM-002-STPS-2010
                    y en el Manual de Procedimientos mediante vectores semánticos.
                  - Devuelve los fragmentos más relevantes con texto completo.
                  - SIEMPRE usa esta herramienta cuando el usuario pregunte algo sobre
                    la norma, procedimientos, reglas o cualquier tema de seguridad.

               2. SECCIONES DEL MANUAL:
                  - Usa "ObtenerSeccionManual" para leer una sección completa por número.
                  - Usa "ObtenerIndicesManual" para listar las 18 secciones disponibles.

               3. CONTEXTO EN MEMORIA:
                  - Al inicio de la sesión se carga automáticamente el índice del manual.
                  - Tienes las 18 secciones listadas para referencia rápida.

               ???????????????????????????????????????????????????????????????????
                   REGLAS DE RESPUESTA
               ???????????????????????????????????????????????????????????????????

               1. SIEMPRE busca en la norma antes de responder preguntas de seguridad.
               2. CITA la sección, artículo o numeral de la norma cuando respondas.
               3. Si la búsqueda no devuelve resultados, dilo honestamente.
               4. NO inventes información que no esté en los documentos.
               5. Responde en español, de manera clara y estructurada.
               6. Usa viñetas y subtítulos para organizar respuestas largas.
               7. Si el usuario hace preguntas de seguimiento, mantén el contexto
                  de la conversación (tienes memoria de sesión).
               8. Para temas que NO están en la norma NOM-002-STPS-2010, indica
                  que tu especialidad es esa norma y sugiere consultar la norma aplicable.

               ???????????????????????????????????????????????????????????????????
                   TEMAS QUE PUEDES ABORDAR
               ???????????????????????????????????????????????????????????????????

               - Clasificación de riesgo de incendio (ordinario vs alto)
               - Equipos contra incendio (extintores, sistemas fijos, detectores, hidrantes)
               - Brigadas contra incendio (organización, selección, capacitación)
               - Simulacros de emergencia (planeación, frecuencia, registro)
               - Plan de atención a emergencias
               - Prevención y controles operativos (trabajos en caliente, almacenamiento)
               - Inspecciones eléctricas y de gas
               - Capacitación y adiestramiento
               - Evaluación de conformidad y unidades de verificación
               - Reglas de negocio (políticas operativas)
               - Registro documental y conservación de evidencias
               - Implementación y cronograma
               - Señalización y rutas de evacuación
               - Responsabilidades del patrón y trabajadores

               ???????????????????????????????????????????????????????????????????
                   FORMATO DE RESPUESTA
               ???????????????????????????????????????????????????????????????????

               Cuando respondas basándote en la búsqueda:
               1. Responde la pregunta de forma clara y directa
               2. Cita la fuente: "Según la sección X de la NOM-002..."
               3. Si hay procedimientos paso a paso, enuméralos
               4. Si aplica, menciona formatos o anexos relevantes del manual
               5. Al final sugiere secciones relacionadas si hay más información útil
               """;
    }

    // ?????????????????????????????????????????????????????????????????????
    //  Helpers
    // ?????????????????????????????????????????????????????????????????????

    private static void AddCorsHeaders(HttpResponseData response, HttpRequestData req)
    {
        var origin = req.Headers.TryGetValues("Origin", out var origins)
            ? origins.FirstOrDefault() : "*";
        response.Headers.Add("Access-Control-Allow-Origin", origin ?? "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
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

// ?????????????????????????????????????????????????????????????????????
//  Session Data
// ?????????????????????????????????????????????????????????????????????

internal class AgentSeguridadSessionData
{
    public string SessionId { get; set; } = "";
    public AIAgent Agent { get; set; } = null!;
    public AgentSession Session { get; set; } = null!;
    public SeguridadNormaMemory Memory { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public int MessageCount { get; set; }
}

// ?????????????????????????????????????????????????????????????????????
//  Request Model
// ?????????????????????????????????????????????????????????????????????

public class SeguridadChatRequest
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }
}
