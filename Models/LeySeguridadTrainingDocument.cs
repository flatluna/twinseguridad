using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace TwinSeguridad.Models;

/// <summary>
/// Documento de training generado por AI Agent para cada índice de la ley de seguridad.
/// Se almacena en Cosmos DB (leyesdeseguridaddb / leyesseguridadtraining).
///
/// Cosmos Account: cdbseguridadindaccount
/// Database:       leyesdeseguridaddb
/// Container:      leyesseguridadtraining
/// Partition Key:  /nombreley
///
/// Cada documento contiene:
///   - Texto completo del índice (todos los subíndices concatenados)
///   - Total subsecciones y tokens
///   - 20 preguntas más comunes con respuestas
///   - Curso dividido en lecciones sencillas para AI Agent avatar
///   - Duración estimada del curso y total de lecciones
/// </summary>
public class LeySeguridadTrainingDocument
{
    /// <summary>ID único: {nombreley}_training_{indice}</summary>
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Partition key — nombre de la ley/norma.</summary>
    [JsonProperty("nombreley")]
    [JsonPropertyName("nombreley")]
    public string Nombreley { get; set; } = string.Empty;

    /// <summary>Número del índice (1, 2, 3... 16).</summary>
    [JsonProperty("indice")]
    [JsonPropertyName("indice")]
    public int Indice { get; set; }

    /// <summary>Título del índice: "5. Obligaciones del patrón", etc.</summary>
    [JsonProperty("tituloIndice")]
    [JsonPropertyName("tituloIndice")]
    public string TituloIndice { get; set; } = string.Empty;

    /// <summary>Sumario ejecutivo de la sección.</summary>
    [JsonProperty("sumarioEjecutivo")]
    [JsonPropertyName("sumarioEjecutivo")]
    public string SumarioEjecutivo { get; set; } = string.Empty;

    /// <summary>Texto completo del índice (todos los subíndices concatenados).</summary>
    [JsonProperty("textoCompletoIndice")]
    [JsonPropertyName("textoCompletoIndice")]
    public string TextoCompletoIndice { get; set; } = string.Empty;

    /// <summary>Total de subsecciones en este índice.</summary>
    [JsonProperty("totalSubsecciones")]
    [JsonPropertyName("totalSubsecciones")]
    public int TotalSubsecciones { get; set; }

    /// <summary>Total de tokens del texto completo.</summary>
    [JsonProperty("totalTokens")]
    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; set; }

    /// <summary>20 preguntas más comunes con respuestas generadas por AI.</summary>
    [JsonProperty("preguntasFrecuentes")]
    [JsonPropertyName("preguntasFrecuentes")]
    public List<PreguntaRespuesta> PreguntasFrecuentes { get; set; } = [];

    /// <summary>Curso estructurado en lecciones para AI Agent avatar.</summary>
    [JsonProperty("curso")]
    [JsonPropertyName("curso")]
    public CursoTraining Curso { get; set; } = new();

    /// <summary>Fecha de generación del training.</summary>
    [JsonProperty("fechaGeneracion")]
    [JsonPropertyName("fechaGeneracion")]
    public DateTime FechaGeneracion { get; set; } = DateTime.UtcNow;

    /// <summary>Modelo AI usado para generar el contenido.</summary>
    [JsonProperty("modeloAI")]
    [JsonPropertyName("modeloAI")]
    public string ModeloAI { get; set; } = string.Empty;
}

/// <summary>
/// Pregunta frecuente con respuesta generada por AI.
/// </summary>
public class PreguntaRespuesta
{
    [JsonProperty("numero")]
    [JsonPropertyName("numero")]
    public int Numero { get; set; }

    [JsonProperty("pregunta")]
    [JsonPropertyName("pregunta")]
    public string Pregunta { get; set; } = string.Empty;

    [JsonProperty("respuesta")]
    [JsonPropertyName("respuesta")]
    public string Respuesta { get; set; } = string.Empty;
}

/// <summary>
/// Curso de training estructurado en lecciones sencillas para AI Agent avatar.
/// </summary>
public class CursoTraining
{
    [JsonProperty("tituloCurso")]
    [JsonPropertyName("tituloCurso")]
    public string TituloCurso { get; set; } = string.Empty;

    [JsonProperty("descripcionCurso")]
    [JsonPropertyName("descripcionCurso")]
    public string DescripcionCurso { get; set; } = string.Empty;

    [JsonProperty("duracionEstimada")]
    [JsonPropertyName("duracionEstimada")]
    public string DuracionEstimada { get; set; } = string.Empty;

    [JsonProperty("totalLecciones")]
    [JsonPropertyName("totalLecciones")]
    public int TotalLecciones { get; set; }

    [JsonProperty("lecciones")]
    [JsonPropertyName("lecciones")]
    public List<LeccionTraining> Lecciones { get; set; } = [];
}

/// <summary>
/// Lección individual del curso de training.
/// </summary>
public class LeccionTraining
{
    [JsonProperty("numeroLeccion")]
    [JsonPropertyName("numeroLeccion")]
    public int NumeroLeccion { get; set; }

    [JsonProperty("tituloLeccion")]
    [JsonPropertyName("tituloLeccion")]
    public string TituloLeccion { get; set; } = string.Empty;

    [JsonProperty("objetivoLeccion")]
    [JsonPropertyName("objetivoLeccion")]
    public string ObjetivoLeccion { get; set; } = string.Empty;

    [JsonProperty("contenido")]
    [JsonPropertyName("contenido")]
    public string Contenido { get; set; } = string.Empty;

    [JsonProperty("duracionMinutos")]
    [JsonPropertyName("duracionMinutos")]
    public int DuracionMinutos { get; set; }

    [JsonProperty("puntosClaveParaAvatar")]
    [JsonPropertyName("puntosClaveParaAvatar")]
    public List<string> PuntosClaveParaAvatar { get; set; } = [];
}

/// <summary>
/// Modelo ligero para listar solo índice y título de cada training.
/// Ahorra ancho de banda al no traer texto, preguntas ni curso.
/// </summary>
public class TrainingIndiceResumen
{
    [JsonProperty("indice")]
    [JsonPropertyName("indice")]
    public int Indice { get; set; }

    [JsonProperty("tituloIndice")]
    [JsonPropertyName("tituloIndice")]
    public string TituloIndice { get; set; } = string.Empty;

    [JsonProperty("nombreley")]
    [JsonPropertyName("nombreley")]
    public string Nombreley { get; set; } = string.Empty;
}
