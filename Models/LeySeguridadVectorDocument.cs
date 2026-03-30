using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace TwinSeguridad.Models;

/// <summary>
/// Documento que se almacena en Cosmos DB (leyesdeseguridaddb / leyesseguridadcontainer)
/// con vector embedding para búsqueda semántica.
///
/// Cada documento representa un subíndice (o un chunk de 800 chars de un subíndice grande)
/// de la norma de seguridad estructurada.
///
/// Cosmos Account: cdbseguridadindaccount
/// Database:       leyesdeseguridaddb
/// Container:      leyesseguridadcontainer
/// Partition Key:  /nombreley
/// Vector Path:    /VectorText (1536 dimensions, float32, cosine)
/// Embedding Model: text-embedding-ada-002
/// </summary>
public class LeySeguridadVectorDocument
{
    /// <summary>ID único: {normaId}_{indice}_{subNum}[_chunkN]</summary>
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Partition key — nombre de la ley/norma: "NOM-002-STPS-2010"</summary>
    [JsonProperty("nombreley")]
    [JsonPropertyName("nombreley")]
    public string Nombreley { get; set; } = string.Empty;

    /// <summary>ID de la norma (legacy, se mantiene por compatibilidad).</summary>
    [JsonProperty("normaId")]
    [JsonPropertyName("normaId")]
    public string NormaId { get; set; } = string.Empty;

    /// <summary>Tipo: "subindice" o "subindice-chunk"</summary>
    [JsonProperty("tipo")]
    [JsonPropertyName("tipo")]
    public string Tipo { get; set; } = "subindice";

    /// <summary>Número del índice principal (1-16, 17=Apéndice)</summary>
    [JsonProperty("indice")]
    [JsonPropertyName("indice")]
    public int Indice { get; set; }

    /// <summary>Título del índice: "Objetivo", "Definiciones", etc.</summary>
    [JsonProperty("tituloIndice")]
    [JsonPropertyName("tituloIndice")]
    public string TituloIndice { get; set; } = string.Empty;

    /// <summary>Sumario ejecutivo de la sección principal.</summary>
    [JsonProperty("sumarioEjecutivo")]
    [JsonPropertyName("sumarioEjecutivo")]
    public string SumarioEjecutivo { get; set; } = string.Empty;

    /// <summary>Título del subíndice.</summary>
    [JsonProperty("tituloSubindice")]
    [JsonPropertyName("tituloSubindice")]
    public string TituloSubindice { get; set; } = string.Empty;

    /// <summary>Texto completo del subíndice (o chunk de ~800 chars).</summary>
    [JsonProperty("texto")]
    [JsonPropertyName("texto")]
    public string Texto { get; set; } = string.Empty;

    /// <summary>Página donde inicia este contenido.</summary>
    [JsonProperty("pagina")]
    [JsonPropertyName("pagina")]
    public int Pagina { get; set; }

    /// <summary>Total de tokens del subíndice original.</summary>
    [JsonProperty("totalTokensSubindice")]
    [JsonPropertyName("totalTokensSubindice")]
    public int TotalTokensSubindice { get; set; }

    /// <summary>Número de chunk (0 si no se dividió, 1..N si se dividió).</summary>
    [JsonProperty("chunkIndex")]
    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; set; }

    /// <summary>Total de chunks para este subíndice.</summary>
    [JsonProperty("totalChunks")]
    [JsonPropertyName("totalChunks")]
    public int TotalChunks { get; set; } = 1;

    /// <summary>Vector embedding del texto (1536 dimensiones, text-embedding-ada-002).</summary>
    [JsonProperty("VectorText")]
    [JsonPropertyName("VectorText")]
    public float[] VectorText { get; set; } = [];

    /// <summary>Fecha de inserción.</summary>
    [JsonProperty("fechaCreacion")]
    [JsonPropertyName("fechaCreacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Resultado de búsqueda vectorial en leyesseguridadcontainer.
/// </summary>
public class LeySeguridadSearchResult
{
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("nombreley")]
    [JsonPropertyName("nombreley")]
    public string Nombreley { get; set; } = string.Empty;

    [JsonProperty("normaId")]
    [JsonPropertyName("normaId")]
    public string NormaId { get; set; } = string.Empty;

    [JsonProperty("indice")]
    [JsonPropertyName("indice")]
    public int Indice { get; set; }

    [JsonProperty("tituloIndice")]
    [JsonPropertyName("tituloIndice")]
    public string TituloIndice { get; set; } = string.Empty;

    [JsonProperty("tituloSubindice")]
    [JsonPropertyName("tituloSubindice")]
    public string TituloSubindice { get; set; } = string.Empty;

    [JsonProperty("sumarioEjecutivo")]
    [JsonPropertyName("sumarioEjecutivo")]
    public string SumarioEjecutivo { get; set; } = string.Empty;

    [JsonProperty("texto")]
    [JsonPropertyName("texto")]
    public string Texto { get; set; } = string.Empty;

    [JsonProperty("pagina")]
    [JsonPropertyName("pagina")]
    public int Pagina { get; set; }

    [JsonProperty("chunkIndex")]
    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; set; }

    [JsonProperty("totalChunks")]
    [JsonPropertyName("totalChunks")]
    public int TotalChunks { get; set; }

    [JsonProperty("similarityScore")]
    [JsonPropertyName("similarityScore")]
    public double SimilarityScore { get; set; }
}
