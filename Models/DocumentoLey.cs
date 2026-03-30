namespace TwinSeguridad.Models;

/// <summary>
/// Representa un documento de ley/norma de seguridad extraído de un PDF.
/// </summary>
public class DocumentoLey
{
    /// <summary>Nombre del archivo PDF de origen.</summary>
    public string ArchivoOrigen { get; set; } = string.Empty;

    /// <summary>Ruta completa del archivo PDF.</summary>
    public string RutaArchivo { get; set; } = string.Empty;

    /// <summary>Fecha y hora de la extracción.</summary>
    public DateTime FechaExtraccion { get; set; } = DateTime.UtcNow;

    /// <summary>Número total de páginas del documento.</summary>
    public int TotalPaginas { get; set; }

    /// <summary>Total de imágenes extraídas del documento.</summary>
    public int TotalImagenes { get; set; }

    /// <summary>Metadatos del documento PDF (título, autor, etc.).</summary>
    public MetadatosDocumento Metadatos { get; set; } = new();

    /// <summary>Lista de páginas con su contenido extraído.</summary>
    public List<PaginaLey> Paginas { get; set; } = [];
}

/// <summary>
/// Metadatos del documento PDF.
/// </summary>
public class MetadatosDocumento
{
    public string? Titulo { get; set; }
    public string? Autor { get; set; }
    public string? Asunto { get; set; }
    public string? Creador { get; set; }
    public string? Productor { get; set; }
    public string? FechaCreacion { get; set; }
    public string? FechaModificacion { get; set; }
    public Dictionary<string, string> Otros { get; set; } = [];
}

/// <summary>
/// Representa una página extraída del PDF con sus líneas y tablas.
/// </summary>
public class PaginaLey
{
    /// <summary>Número de página (1-based).</summary>
    public int NumeroPagina { get; set; }

    /// <summary>Ancho de la página en puntos.</summary>
    public double AnchoPagina { get; set; }

    /// <summary>Alto de la página en puntos.</summary>
    public double AltoPagina { get; set; }

    /// <summary>Texto completo de la página.</summary>
    public string TextoCompleto { get; set; } = string.Empty;

    /// <summary>Líneas de texto extraídas con su posición.</summary>
    public List<LineaTexto> Lineas { get; set; } = [];

    /// <summary>Tablas detectadas en la página.</summary>
    public List<TablaExtraida> Tablas { get; set; } = [];

    /// <summary>Bloques/secciones de contenido detectados.</summary>
    public List<BloqueContenido> Bloques { get; set; } = [];

    /// <summary>Imágenes extraídas de esta página.</summary>
    public List<ImagenExtraida> Imagenes { get; set; } = [];
}

/// <summary>
/// Representa una línea de texto extraída con metadata de posición.
/// </summary>
public class LineaTexto
{
    /// <summary>Número de línea dentro de la página (1-based).</summary>
    public int NumeroLinea { get; set; }

    /// <summary>Contenido textual de la línea.</summary>
    public string Texto { get; set; } = string.Empty;

    /// <summary>Posición Y (vertical) de la línea en la página.</summary>
    public double PosicionY { get; set; }

    /// <summary>Posición X (horizontal) del inicio de la línea.</summary>
    public double PosicionX { get; set; }

    /// <summary>Tamaño de fuente predominante en la línea.</summary>
    public double TamanoFuente { get; set; }

    /// <summary>Nombre de la fuente predominante.</summary>
    public string? NombreFuente { get; set; }

    /// <summary>Indica si la línea parece ser un encabezado/título.</summary>
    public bool EsEncabezado { get; set; }

    /// <summary>Indica si la línea parece ser parte de un artículo de ley.</summary>
    public bool EsArticulo { get; set; }
}

/// <summary>
/// Representa una tabla detectada en una página del PDF.
/// </summary>
public class TablaExtraida
{
    /// <summary>Índice de la tabla dentro de la página (0-based).</summary>
    public int IndiceTabla { get; set; }

    /// <summary>Número de filas de la tabla.</summary>
    public int NumeroFilas { get; set; }

    /// <summary>Número de columnas de la tabla.</summary>
    public int NumeroColumnas { get; set; }

    /// <summary>Filas de la tabla con sus celdas.</summary>
    public List<FilaTabla> Filas { get; set; } = [];
}

/// <summary>
/// Representa una fila de una tabla extraída.
/// </summary>
public class FilaTabla
{
    /// <summary>Índice de la fila (0-based).</summary>
    public int IndiceFila { get; set; }

    /// <summary>Indica si es la fila de encabezado.</summary>
    public bool EsEncabezado { get; set; }

    /// <summary>Celdas de la fila.</summary>
    public List<string> Celdas { get; set; } = [];
}

/// <summary>
/// Representa un bloque o sección de contenido detectado en la página.
/// </summary>
public class BloqueContenido
{
    /// <summary>Índice del bloque (0-based).</summary>
    public int IndiceBloque { get; set; }

    /// <summary>Tipo de bloque: Titulo, Articulo, Parrafo, Lista, etc.</summary>
    public string TipoBloque { get; set; } = "Parrafo";

    /// <summary>Contenido textual del bloque.</summary>
    public string Texto { get; set; } = string.Empty;

    /// <summary>Posición Y del bloque en la página.</summary>
    public double PosicionY { get; set; }
}

/// <summary>
/// Representa una imagen extraída de una página del PDF.
/// </summary>
public class ImagenExtraida
{
    /// <summary>Índice de la imagen dentro de la página (0-based).</summary>
    public int IndiceImagen { get; set; }

    /// <summary>Ruta del archivo de imagen guardado en disco.</summary>
    public string RutaArchivo { get; set; } = string.Empty;

    /// <summary>Nombre del archivo de imagen.</summary>
    public string NombreArchivo { get; set; } = string.Empty;

    /// <summary>Ancho de la imagen en píxeles.</summary>
    public int Ancho { get; set; }

    /// <summary>Alto de la imagen en píxeles.</summary>
    public int Alto { get; set; }

    /// <summary>Posición Y de la imagen en la página.</summary>
    public double PosicionY { get; set; }

    /// <summary>Posición X de la imagen en la página.</summary>
    public double PosicionX { get; set; }

    /// <summary>Descripción generada por GPT-4 mini (visión) de lo que contiene la imagen.</summary>
    public string? DescripcionIA { get; set; }

    /// <summary>Formato de la imagen (png, jpg, etc.).</summary>
    public string Formato { get; set; } = "png";

    /// <summary>Tamaño del archivo en bytes.</summary>
    public long TamanoBytes { get; set; }
}
