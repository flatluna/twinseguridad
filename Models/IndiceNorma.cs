namespace TwinSeguridad.Models;

/// <summary>
/// Índice completo de una norma de seguridad, organizado jerárquicamente.
/// Nivel 1: secciones principales (1. Objetivo, 2. Campo de aplicación, …)
/// Nivel 2: subsecciones (5.1, 5.2, 7.1, 7.2, …)
/// Cada nodo incluye el texto completo y el conteo de tokens para LLM.
/// </summary>
public class IndiceNorma
{
    /// <summary>Nombre del archivo origen.</summary>
    public string ArchivoOrigen { get; set; } = string.Empty;

    /// <summary>Fecha y hora de la extracción del índice.</summary>
    public DateTime FechaExtraccion { get; set; } = DateTime.UtcNow;

    /// <summary>Modelo de tokenización usado (ej: cl100k_base para GPT-4).</summary>
    public string ModeloTokenizacion { get; set; } = "cl100k_base";

    /// <summary>Total de tokens en todo el documento.</summary>
    public int TotalTokensDocumento { get; set; }

    /// <summary>Total de secciones principales (nivel 1).</summary>
    public int TotalSecciones { get; set; }

    /// <summary>Total de subsecciones (nivel 2) en todo el documento.</summary>
    public int TotalSubsecciones { get; set; }

    /// <summary>Secciones principales del índice (1, 2, 3, … 16, Apéndice).</summary>
    public List<SeccionNorma> Secciones { get; set; } = [];
}

/// <summary>
/// Sección principal del índice (nivel 1).
/// Ejemplo: "1. Objetivo", "7. Condiciones de prevención y protección contra incendios".
/// </summary>
public class SeccionNorma
{
    /// <summary>Número de la sección: "1", "2", … "16", "Apendice".</summary>
    public string Numero { get; set; } = string.Empty;

    /// <summary>Nombre de la sección tal como aparece en el índice.</summary>
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Texto completo de la sección (incluye subsecciones).</summary>
    public string TextoCompleto { get; set; } = string.Empty;

    /// <summary>Cantidad de tokens (LLM) del texto completo de esta sección.</summary>
    public int Tokens { get; set; }

    /// <summary>Cantidad de caracteres del texto completo.</summary>
    public int Caracteres { get; set; }

    /// <summary>Páginas del PDF donde aparece esta sección (rango).</summary>
    public List<int> Paginas { get; set; } = [];

    /// <summary>Subsecciones (nivel 2): 5.1, 5.2, 7.1, etc.</summary>
    public List<SubseccionNorma> Subsecciones { get; set; } = [];
}

/// <summary>
/// Subsección del índice (nivel 2).
/// Ejemplo: "5.1", "7.2", "13.1".
/// </summary>
public class SubseccionNorma
{
    /// <summary>Número de la subsección: "5.1", "7.2", "13.1", etc.</summary>
    public string Numero { get; set; } = string.Empty;

    /// <summary>Nombre/título de la subsección.</summary>
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Texto completo de la subsección.</summary>
    public string TextoCompleto { get; set; } = string.Empty;

    /// <summary>Cantidad de tokens (LLM) del texto de esta subsección.</summary>
    public int Tokens { get; set; }

    /// <summary>Cantidad de caracteres del texto.</summary>
    public int Caracteres { get; set; }

    /// <summary>Páginas del PDF donde aparece esta subsección.</summary>
    public List<int> Paginas { get; set; } = [];
}
