using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TwinSeguridad.Services;

namespace TwinSeguridad.Agent;

/// <summary>
/// AIContextProvider que carga un resumen del Manual de Procedimientos al inicio de la sesión.
/// Se carga UNA sola vez (primera invocación) y se mantiene en memoria.
/// </summary>
internal sealed class SeguridadNormaMemory : AIContextProvider
{
    private readonly ProviderSessionState<SeguridadNormaMemoryState> _sessionState;
    private IReadOnlyList<string>? _stateKeys;
    private readonly ILogger _logger;
    private readonly ManualSeguridadService _manualService;

    public SeguridadNormaMemory(ILogger logger, ManualSeguridadService manualService)
    {
        _logger = logger;
        _manualService = manualService;
        _sessionState = new ProviderSessionState<SeguridadNormaMemoryState>(
            _ => new SeguridadNormaMemoryState(),
            nameof(SeguridadNormaMemory));
    }

    public override IReadOnlyList<string> StateKeys => _stateKeys ??= [_sessionState.StateKey];

    public bool IsContextLoaded(AgentSession session)
        => _sessionState.GetOrInitializeState(session).IsLoaded;

    public DateTime? GetContextLoadedAt(AgentSession session)
        => _sessionState.GetOrInitializeState(session).LoadedAt;

    public int GetSeccionesCount(AgentSession session)
        => _sessionState.GetOrInitializeState(session).SeccionesCount;

    protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        await ValueTask.CompletedTask;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);

        if (!state.IsLoaded)
        {
            _logger.LogInformation("?? Cargando resumen del Manual de Seguridad en memoria...");

            try
            {
                var secciones = await _manualService.ObtenerTodosAsync();

                if (secciones.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("???????????????????????????????????????????????????????");
                    sb.AppendLine("     MANUAL DE PROCEDIMIENTOS Y REGLAS DE NEGOCIO");
                    sb.AppendLine("     Base: NOM-002-STPS-2010");
                    sb.AppendLine("     Prevención y protección contra incendios");
                    sb.AppendLine("???????????????????????????????????????????????????????");
                    sb.AppendLine($"Total secciones: {secciones.Count}");
                    sb.AppendLine();

                    foreach (var s in secciones)
                    {
                        sb.AppendLine($"  {s.Indice}. {s.TituloIndice} (págs {s.PaginaInicio}-{s.PaginaFin}, {s.TotalTokens} tokens)");
                    }

                    sb.AppendLine();
                    sb.AppendLine("Para consultar el contenido detallado de cada sección,");
                    sb.AppendLine("usa la herramienta ObtenerSeccionManual(indice).");
                    sb.AppendLine("Para buscar por tema, usa BuscarEnNormaSeguridad(query).");
                    sb.AppendLine("???????????????????????????????????????????????????????");

                    state.ContextText = sb.ToString();
                    state.SeccionesCount = secciones.Count;

                    _logger.LogInformation("?? Manual cargado: {Count} secciones", secciones.Count);
                }
                else
                {
                    state.ContextText = "No se encontraron secciones del manual. Ejecuta /api/seguridad/indexar-manual primero.";
                }

                state.IsLoaded = true;
                state.LoadedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "? Error cargando contexto del manual");
                state.ContextText = "Error al cargar el manual de seguridad.";
                state.IsLoaded = true;
                state.LoadedAt = DateTime.UtcNow;
            }

            _sessionState.SaveState(context.Session, state);
        }

        return new AIContext
        {
            Instructions = state.ContextText
        };
    }
}

internal class SeguridadNormaMemoryState
{
    public bool IsLoaded { get; set; }
    public DateTime? LoadedAt { get; set; }
    public string ContextText { get; set; } = string.Empty;
    public int SeccionesCount { get; set; }
}
