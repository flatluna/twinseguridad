using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace TwinSeguridad.Models;

/// <summary>
/// Documento del Manual de Procedimientos y Reglas de Negocio almacenado en Cosmos DB.
/// Un documento por cada índice/sección del manual.
///
/// Cosmos Account: cdbseguridadindaccount
/// Database:       leyesdeseguridaddb
/// Container:      manualseguridadcontainer
/// Partition Key:  /nombreManual
///
/// Índice del manual:
///   1.  Objetivo
///   2.  Alcance
///   3.  Referencias normativas
///   4.  Definiciones esenciales
///   5.  Responsabilidades y organización
///   6.  Clasificación del riesgo de incendio (procedimiento)
///   7.  Plan de atención a emergencias de incendio (procedimiento y plantilla)
///   8.  Brigadas contra incendio (organización, funciones y selección)
///   9.  Simulacros (planeación, ejecución y registro)
///   10. Prevención y controles operativos (incluye trabajos en caliente)
///   11. Equipos y sistemas contra incendio: inspección, mantenimiento y registros
///   12. Inspecciones a instalaciones eléctricas y de gas
///   13. Capacitación y adiestramiento (programa anual)
///   14. Registro, control documental y conservación de evidencias
///   15. Evaluación de conformidad y preparación para unidad de verificación
///   16. Reglas de negocio (políticas operativas y criterios de acción)
///   17. Implementación, cronograma y seguimiento
///   18. Anexos
/// </summary>
public class ManualSeguridadDocument
{
    /// <summary>ID único: manual_{indice}</summary>
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Partition key — nombre del manual.</summary>
    [JsonProperty("nombreManual")]
    [JsonPropertyName("nombreManual")]
    public string NombreManual { get; set; } = "ManualProcedimientos_NOM-002-STPS-2010";

    /// <summary>Número del índice (1-17, 18=Anexos).</summary>
    [JsonProperty("indice")]
    [JsonPropertyName("indice")]
    public int Indice { get; set; }

    /// <summary>Título del índice tal como aparece en el manual.</summary>
    [JsonProperty("tituloIndice")]
    [JsonPropertyName("tituloIndice")]
    public string TituloIndice { get; set; } = string.Empty;

    /// <summary>Texto completo de esta sección extraído del PDF.</summary>
    [JsonProperty("textoCompleto")]
    [JsonPropertyName("textoCompleto")]
    public string TextoCompleto { get; set; } = string.Empty;

    /// <summary>Página del PDF donde inicia esta sección.</summary>
    [JsonProperty("paginaInicio")]
    [JsonPropertyName("paginaInicio")]
    public int PaginaInicio { get; set; }

    /// <summary>Página del PDF donde termina esta sección.</summary>
    [JsonProperty("paginaFin")]
    [JsonPropertyName("paginaFin")]
    public int PaginaFin { get; set; }

    /// <summary>Total de caracteres del texto de esta sección.</summary>
    [JsonProperty("totalCaracteres")]
    [JsonPropertyName("totalCaracteres")]
    public int TotalCaracteres { get; set; }

    /// <summary>Total de tokens (cl100k_base) del texto de esta sección.</summary>
    [JsonProperty("totalTokens")]
    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; set; }

    /// <summary>Fecha de extracción e indexación.</summary>
    [JsonProperty("fechaCreacion")]
    [JsonPropertyName("fechaCreacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    /// <summary>Archivo PDF de origen.</summary>
    [JsonProperty("archivoOrigen")]
    [JsonPropertyName("archivoOrigen")]
    public string ArchivoOrigen { get; set; } = string.Empty;
}
