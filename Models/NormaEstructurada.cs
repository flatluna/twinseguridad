namespace TwinSeguridad.Models;

/// <summary>
/// Documento completo de la norma estructurado por índice, con sumario ejecutivo,
/// subíndices con texto completo, imágenes asociadas y conteo de tokens por sección.
/// 
/// Generado por NormaEstructuradaService desde norma-seguridad-indice.json + norma-seguridad.json
/// Archivo de salida: norma-seguridad-estructurada.json
/// </summary>
public class NormaEstructurada
{
    /// <summary>Nombre del archivo PDF de origen.</summary>
    public string ArchivoOrigen { get; set; } = string.Empty;

    /// <summary>Fecha de generación.</summary>
    public DateTime FechaGeneracion { get; set; } = DateTime.UtcNow;

    /// <summary>Modelo de tokenización usado (cl100k_base para GPT-4).</summary>
    public string ModeloTokenizacion { get; set; } = "cl100k_base";

    /// <summary>Total de tokens en todo el documento.</summary>
    public int TotalTokensDocumento { get; set; }

    /// <summary>Total de índices (secciones principales).</summary>
    public int TotalIndices { get; set; }

    /// <summary>Total de subíndices en todo el documento.</summary>
    public int TotalSubindices { get; set; }

    /// <summary>Lista de secciones estructuradas del índice.</summary>
    public List<IndiceEstructurado> Indices { get; set; } = [];
}

/// <summary>
/// Sección principal del índice (nivel 1).
/// Contiene sumario ejecutivo, subíndices con texto, imágenes y tokens.
/// </summary>
public class IndiceEstructurado
{
    /// <summary>Número secuencial del índice (1, 2, 3... 16, 17=Apéndice).</summary>
    public int Indice { get; set; }

    /// <summary>Título del índice: "Objetivo", "Campo de aplicación", etc.</summary>
    public string TituloIndice { get; set; } = string.Empty;

    /// <summary>Resumen ejecutivo de toda la sección (generado del texto).</summary>
    public string SumarioEjecutivo { get; set; } = string.Empty;

    /// <summary>Página inicial donde aparece esta sección.</summary>
    public int Pagina { get; set; }

    /// <summary>Total de tokens del texto completo de toda esta sección (incluye subíndices).</summary>
    public int TotalTokensIndice { get; set; }

    /// <summary>Lista de subíndices con texto completo.</summary>
    public List<SubindiceEstructurado> ListaSubindices { get; set; } = [];

    /// <summary>Imágenes asociadas a esta sección.</summary>
    public List<ImagenReferencia> Imagenes { get; set; } = [];
}

/// <summary>
/// Subíndice con título, texto completo, página y tokens.
/// </summary>
public class SubindiceEstructurado
{
    /// <summary>Título del subíndice: "4.1 Agente extintor", "5.2 Contar con...", etc.</summary>
    public string TituloSubindice { get; set; } = string.Empty;

    /// <summary>Texto completo del subíndice.</summary>
    public string Texto { get; set; } = string.Empty;

    /// <summary>Página donde inicia este subíndice.</summary>
    public int Pagina { get; set; }

    /// <summary>Total de tokens del texto de este subíndice.</summary>
    public int TotalTokensSubindice { get; set; }
}

/// <summary>
/// Referencia a una imagen asociada a una sección.
/// </summary>
public class ImagenReferencia
{
    /// <summary>Nombre del archivo de imagen.</summary>
    public string NombreArchivo { get; set; } = string.Empty;

    /// <summary>Descripción generada por GPT-4 mini visión.</summary>
    public string? DescripcionIA { get; set; }

    /// <summary>Página donde se encontró la imagen.</summary>
    public int Pagina { get; set; }
}
