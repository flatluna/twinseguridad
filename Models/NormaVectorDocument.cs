using System.Text.Json.Serialization;

namespace TwinSeguridad.Models;

/// <summary>
/// Documento que se almacena en Cosmos DB con vector embedding para búsqueda semántica.
/// Cada documento representa un subíndice (chunk) de la norma de seguridad.
///
/// Container: norma-seguridad
/// Partition Key: /normaId
/// Vector Path: /contentVector (1536 dimensions, float32, cosine)
///
/// Ejemplo:
///   id = "NOM-002-STPS-2010_4_4.1"
///   normaId = "NOM-002-STPS-2010"
///   indice = 4
///   tituloIndice = "Definiciones"
///   subindice = "4.1"
///   tituloSubindice = "4.1 Agente extintor; Agente extinguidor"
///   texto = "Es la sustancia o mezcla de ellas que apaga un fuego..."
///   contentVector = [0.0123, -0.045, ...]  (1536 floats del embedding)
/// </summary>
public class NormaVectorDocument
{
    /// <summary>ID único: {normaId}_{indice}_{subindice} — ej: "NOM-002-STPS-2010_4_4.1"</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Partition key — ID de la norma: "NOM-002-STPS-2010"</summary>
    [JsonPropertyName("normaId")]
    public string NormaId { get; set; } = string.Empty;

    /// <summary>Tipo de documento: "seccion" o "subindice"</summary>
    [JsonPropertyName("tipo")]
    public string Tipo { get; set; } = "subindice";

    /// <summary>Número del índice principal (1-16, 17=Apéndice)</summary>
    [JsonPropertyName("indice")]
    public int Indice { get; set; }

    /// <summary>Título del índice: "Objetivo", "Definiciones", etc.</summary>
    [JsonPropertyName("tituloIndice")]
    public string TituloIndice { get; set; } = string.Empty;

    /// <summary>Número del subíndice: "4.1", "5.2", etc. Vacío si es sección sin subíndices.</summary>
    [JsonPropertyName("subindice")]
    public string Subindice { get; set; } = string.Empty;

    /// <summary>Título del subíndice: "4.1 Agente extintor", etc.</summary>
    [JsonPropertyName("tituloSubindice")]
    public string TituloSubindice { get; set; } = string.Empty;

    /// <summary>Sumario ejecutivo de la sección principal.</summary>
    [JsonPropertyName("sumarioEjecutivo")]
    public string SumarioEjecutivo { get; set; } = string.Empty;

    /// <summary>Texto completo del subíndice (el contenido que se embedea).</summary>
    [JsonPropertyName("texto")]
    public string Texto { get; set; } = string.Empty;

    /// <summary>Página donde inicia este contenido.</summary>
    [JsonPropertyName("pagina")]
    public int Pagina { get; set; }

    /// <summary>Total de tokens del texto.</summary>
    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; set; }

    /// <summary>Vector embedding del texto (1536 dimensiones, text-embedding-ada-002).</summary>
    [JsonPropertyName("contentVector")]
    public float[] ContentVector { get; set; } = [];

    /// <summary>Fecha de inserción.</summary>
    [JsonPropertyName("fechaCreacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    /// <summary>Imágenes asociadas a esta sección (si las hay).</summary>
    [JsonPropertyName("imagenes")]
    public List<ImagenReferenciaVector>? Imagenes { get; set; }
}

/// <summary>
/// Referencia a imagen para almacenar en Cosmos DB.
/// </summary>
public class ImagenReferenciaVector
{
    [JsonPropertyName("nombreArchivo")]
    public string NombreArchivo { get; set; } = string.Empty;

    [JsonPropertyName("descripcionIA")]
    public string? DescripcionIA { get; set; }

    [JsonPropertyName("pagina")]
    public int Pagina { get; set; }
}

/// <summary>
/// Resultado de una búsqueda vectorial en Cosmos DB.
/// </summary>
public class VectorSearchResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("normaId")]
    public string NormaId { get; set; } = string.Empty;

    [JsonPropertyName("indice")]
    public int Indice { get; set; }

    [JsonPropertyName("tituloIndice")]
    public string TituloIndice { get; set; } = string.Empty;

    [JsonPropertyName("tituloSubindice")]
    public string TituloSubindice { get; set; } = string.Empty;

    [JsonPropertyName("texto")]
    public string Texto { get; set; } = string.Empty;

    [JsonPropertyName("pagina")]
    public int Pagina { get; set; }

    [JsonPropertyName("similarityScore")]
    public double SimilarityScore { get; set; }
}
