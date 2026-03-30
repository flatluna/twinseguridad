using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SharpToken;
using TwinSeguridad.Models;

namespace TwinSeguridad.Services;

/// <summary>
/// Servicio que lee norma-seguridad-texto.txt (texto plano exportado) y extrae
/// cada sección buscando los encabezados conocidos del índice de la norma.
///
/// Estrategia (SIN REGEX):
///   1. Define el índice conocido: "1. Objetivo", "2. Campo de aplicación", etc.
///   2. En el texto plano, busca la ÚLTIMA aparición de cada encabezado
///      (la del cuerpo real, no la de la tabla de contenidos).
///   3. Corta el texto entre un encabezado y el siguiente.
///   4. Dentro de cada sección, busca subíndices (ej: "4.1 ", "4.2 ") de la misma forma.
///   5. Genera tokens con SharpToken y produce norma-seguridad-estructurada.json.
///
/// Endpoint: GET /api/seguridad/estructurar-texto
/// </summary>
public class NormaTextoEstructuradoService
{
    private readonly ILogger<NormaTextoEstructuradoService> _logger;
    private readonly GptEncoding _encoding;

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Índice conocido de la NOM-002-STPS-2010.
    /// Cada entrada: (número, título exacto como aparece en el cuerpo del texto).
    /// </summary>
    private static readonly List<(int Numero, string Encabezado)> IndiceConocido =
    [
        (1,  "1. Objetivo"),
        (2,  "2. Campo de aplicación"),
        (3,  "3. Referencias"),
        (4,  "4. Definiciones"),
        (5,  "5. Obligaciones del patrón"),
        (6,  "6. Obligaciones de los trabajadores"),
        (7,  "7. Condiciones de prevención y protección contra incendios"),
        (8,  "8. Plan de atención a emergencias de incendio"),
        (9,  "9. Brigadas contra incendio"),
        (10, "10. Simulacros de emergencias de incendio"),
        (11, "11. Capacitación"),
        (12, "12. Unidades de verificación"),
        (13, "13. Procedimiento para la evaluación de la conformidad"),
        (14, "14. Vigilancia"),
        (15, "15. Bibliografía"),
        (16, "16. Concordancia con normas internacionales"),
    ];

    /// <summary>
    /// Secciones adicionales después de la 16 (Apéndice y Guías).
    /// </summary>
    private static readonly List<(int Numero, string Encabezado)> SeccionesAdicionales =
    [
        (17, "Apéndice A"),
        (18, "Guía de Referencia I"),
        (19, "Guía de Referencia II"),
        (20, "Guía de Referencia III"),
        (21, "Guía de Referencia IV"),
        (22, "Guía de Referencia V"),
        (23, "Guía de Referencia VI"),
        (24, "Guía de Referencia VII"),
        (25, "Guía de Referencia VIII"),
        (26, "Guía de Referencia IX"),
    ];

    public NormaTextoEstructuradoService(ILogger<NormaTextoEstructuradoService> logger)
    {
        _logger = logger;
        _encoding = GptEncoding.GetEncoding("cl100k_base");
    }

    /// <summary>
    /// Lee el TXT plano y genera el JSON estructurado con secciones, subíndices y tokens.
    /// </summary>
    public async Task<(NormaEstructurada Norma, string OutputPath)> GenerarDesdeTextoAsync(string txtFilePath)
    {
        if (!File.Exists(txtFilePath))
            throw new FileNotFoundException($"No se encontró: {txtFilePath}");

        _logger.LogInformation("?? Leyendo texto plano: {Path}", txtFilePath);
        var textoCompleto = await File.ReadAllTextAsync(txtFilePath, Encoding.UTF8);

        // Combinar todas las secciones conocidas
        var todasLasSecciones = new List<(int Numero, string Encabezado)>();
        todasLasSecciones.AddRange(IndiceConocido);
        todasLasSecciones.AddRange(SeccionesAdicionales);

        // 1) Encontrar la posición de cada sección en el texto (ÚLTIMA aparición del cuerpo real)
        var posiciones = FindSectionPositions(textoCompleto, todasLasSecciones);

        _logger.LogInformation("?? Secciones encontradas: {Count}/{Total}",
            posiciones.Count, todasLasSecciones.Count);

        // 2) Extraer el texto de cada sección (cortar de una a otra)
        var indices = ExtractSections(textoCompleto, posiciones, todasLasSecciones);

        // 3) Para cada sección, buscar subíndices
        foreach (var indice in indices)
        {
            if (indice.Indice <= 16) // Solo secciones numeradas tienen subíndices N.X
            {
                indice.ListaSubindices = ExtractSubindices(indice);
            }

            // Calcular tokens del índice completo
            var textoTodo = string.Join("\n",
                indice.ListaSubindices.Select(s => s.Texto).Where(t => !string.IsNullOrWhiteSpace(t)));
            if (string.IsNullOrWhiteSpace(textoTodo))
                textoTodo = indice.SumarioEjecutivo;
            indice.TotalTokensIndice = CountTokens(textoTodo);
        }

        var norma = new NormaEstructurada
        {
            ArchivoOrigen = Path.GetFileName(txtFilePath),
            FechaGeneracion = DateTime.UtcNow,
            ModeloTokenizacion = "cl100k_base",
            TotalIndices = indices.Count,
            TotalSubindices = indices.Sum(i => i.ListaSubindices.Count),
            TotalTokensDocumento = indices.Sum(i => i.TotalTokensIndice),
            Indices = indices
        };

        // Guardar
        var directory = Path.GetDirectoryName(txtFilePath)!;
        var outputPath = Path.Combine(directory, "norma-seguridad-estructurada.json");
        var outputJson = JsonSerializer.Serialize(norma, JsonWriteOptions);
        await File.WriteAllTextAsync(outputPath, outputJson, Encoding.UTF8);

        _logger.LogInformation(
            "? Estructura generada: {Indices} índices, {Sub} subíndices, {Tokens} tokens ? {Path}",
            norma.TotalIndices, norma.TotalSubindices, norma.TotalTokensDocumento, outputPath);

        return (norma, outputPath);
    }

    /// <summary>
    /// Encuentra la posición de cada sección en el texto.
    /// Busca la ÚLTIMA aparición que esté seguida de texto real (no de otro encabezado inmediato).
    /// Para las secciones 1-16 busca la última; para Apéndice/Guías busca la última también.
    /// </summary>
    private Dictionary<int, int> FindSectionPositions(
        string texto, List<(int Numero, string Encabezado)> secciones)
    {
        var positions = new Dictionary<int, int>();

        foreach (var (numero, encabezado) in secciones)
        {
            // Buscar TODAS las apariciones y quedarse con la última que tenga contenido después
            var lastValidPos = -1;
            var searchFrom = 0;

            while (true)
            {
                var pos = texto.IndexOf(encabezado, searchFrom, StringComparison.Ordinal);
                if (pos < 0) break;

                // Verificar que después del encabezado hay un salto de línea 
                // (no es parte de una oración más larga)
                var afterEnd = pos + encabezado.Length;
                if (afterEnd < texto.Length)
                {
                    var charAfter = texto[afterEnd];
                    // Debe seguir un newline, espacio o fin de texto
                    if (charAfter == '\n' || charAfter == '\r' || charAfter == ' ')
                    {
                        // Para secciones 1-16: verificar que después hay texto real (no otro encabezado)
                        var nextLineStart = texto.IndexOf('\n', afterEnd);
                        if (nextLineStart > 0 && nextLineStart < texto.Length - 5)
                        {
                            var nextLine = GetNextNonEmptyLine(texto, nextLineStart);
                            // Si la siguiente línea NO empieza con un número de sección diferente, es contenido real
                            var isContentLine = !string.IsNullOrWhiteSpace(nextLine) &&
                                                !IsTableOrPageMarker(nextLine);

                            if (isContentLine)
                                lastValidPos = pos;
                        }
                    }
                }

                searchFrom = pos + 1;
            }

            if (lastValidPos >= 0)
            {
                positions[numero] = lastValidPos;
                _logger.LogDebug("?? Sección {Num}: \"{Enc}\" ? pos {Pos}", numero, encabezado, lastValidPos);
            }
            else
            {
                _logger.LogWarning("?? Sección {Num}: \"{Enc}\" no encontrada", numero, encabezado);
            }
        }

        return positions;
    }

    /// <summary>
    /// Extrae el texto de cada sección cortando entre posiciones consecutivas.
    /// </summary>
    private List<IndiceEstructurado> ExtractSections(
        string texto,
        Dictionary<int, int> posiciones,
        List<(int Numero, string Encabezado)> secciones)
    {
        var result = new List<IndiceEstructurado>();

        // Ordenar por posición en el texto
        var ordered = posiciones.OrderBy(p => p.Value).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var (numero, startPos) = ordered[i];
            var seccion = secciones.FirstOrDefault(s => s.Numero == numero);
            if (seccion == default) continue;

            // El texto va desde esta posición hasta la siguiente sección (o fin del texto)
            var endPos = (i + 1 < ordered.Count) ? ordered[i + 1].Value : texto.Length;

            var seccionTexto = texto[startPos..endPos].Trim();

            // Quitar el encabezado de la primera línea para el sumario
            var primeraLinea = seccion.Encabezado;
            var textoSinEncabezado = seccionTexto;
            if (textoSinEncabezado.StartsWith(primeraLinea))
                textoSinEncabezado = textoSinEncabezado[primeraLinea.Length..].Trim();

            // Limpiar marcadores de tabla y página
            textoSinEncabezado = CleanSectionText(textoSinEncabezado);

            // Generar sumario (primeros ~500 chars)
            var sumario = GenerarSumario(textoSinEncabezado);

            // Detectar en qué página está (buscar "PÁGINA N" antes de la posición)
            var pagina = DetectPageNumber(texto, startPos);

            result.Add(new IndiceEstructurado
            {
                Indice = numero,
                TituloIndice = seccion.Encabezado,
                SumarioEjecutivo = sumario,
                Pagina = pagina,
                ListaSubindices =
                [
                    new SubindiceEstructurado
                    {
                        TituloSubindice = seccion.Encabezado,
                        Texto = textoSinEncabezado,
                        Pagina = pagina,
                        TotalTokensSubindice = CountTokens(textoSinEncabezado)
                    }
                ],
                Imagenes = []
            });
        }

        return result;
    }

    /// <summary>
    /// Extrae subíndices de una sección. Para sección N, busca "N.1 ", "N.2 ", etc.
    /// </summary>
    private List<SubindiceEstructurado> ExtractSubindices(IndiceEstructurado indice)
    {
        if (indice.ListaSubindices.Count == 0) return [];

        var textoSeccion = indice.ListaSubindices[0].Texto;
        if (string.IsNullOrWhiteSpace(textoSeccion)) return indice.ListaSubindices;

        var secNum = indice.Indice;

        // Buscar subíndices: "N.1 ", "N.2 ", ... "N.99 "
        var subPositions = new List<(string Titulo, int Pos)>();

        for (var sub = 1; sub <= 99; sub++)
        {
            var marker = $"{secNum}.{sub} ";
            var pos = textoSeccion.IndexOf(marker, StringComparison.Ordinal);
            if (pos >= 0)
            {
                subPositions.Add((marker.Trim(), pos));
            }

            // También buscar sub-subíndices: N.X.Y
            for (var subsub = 1; subsub <= 20; subsub++)
            {
                var subSubMarker = $"{secNum}.{sub}.{subsub} ";
                var subPos = textoSeccion.IndexOf(subSubMarker, StringComparison.Ordinal);
                if (subPos >= 0)
                {
                    subPositions.Add((subSubMarker.Trim(), subPos));
                }
            }
        }

        if (subPositions.Count == 0) return indice.ListaSubindices;

        // Ordenar por posición
        subPositions = subPositions.OrderBy(p => p.Pos).ToList();

        var subs = new List<SubindiceEstructurado>();

        // Si hay texto antes del primer subíndice, incluirlo como intro
        if (subPositions[0].Pos > 10)
        {
            var introText = CleanSectionText(textoSeccion[..subPositions[0].Pos].Trim());
            if (!string.IsNullOrWhiteSpace(introText) && introText.Length > 20)
            {
                subs.Add(new SubindiceEstructurado
                {
                    TituloSubindice = indice.TituloIndice,
                    Texto = introText,
                    Pagina = indice.Pagina,
                    TotalTokensSubindice = CountTokens(introText)
                });
            }
        }

        for (var i = 0; i < subPositions.Count; i++)
        {
            var (titulo, startPos) = subPositions[i];
            var endPos = (i + 1 < subPositions.Count) ? subPositions[i + 1].Pos : textoSeccion.Length;

            var subTexto = textoSeccion[startPos..endPos].Trim();

            // Extraer el título completo (primera línea)
            var firstNewline = subTexto.IndexOf('\n');
            var tituloCompleto = firstNewline > 0 ? subTexto[..firstNewline].Trim() : titulo;
            var textoSinTitulo = firstNewline > 0 ? subTexto[(firstNewline + 1)..].Trim() : "";

            textoSinTitulo = CleanSectionText(textoSinTitulo);

            subs.Add(new SubindiceEstructurado
            {
                TituloSubindice = tituloCompleto,
                Texto = string.IsNullOrWhiteSpace(textoSinTitulo) ? subTexto : textoSinTitulo,
                Pagina = indice.Pagina,
                TotalTokensSubindice = CountTokens(string.IsNullOrWhiteSpace(textoSinTitulo) ? subTexto : textoSinTitulo)
            });
        }

        return subs.Count > 0 ? subs : indice.ListaSubindices;
    }

    /// <summary>
    /// Limpia el texto de una sección: quita marcadores de tabla y página.
    /// </summary>
    private static string CleanSectionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder();
        var lines = text.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Saltar marcadores de tabla y página
            if (trimmed.StartsWith("[TABLA ") || trimmed.StartsWith("[FIN TABLA"))
                continue;
            if (trimmed.StartsWith("[IMAGEN ") || trimmed.StartsWith("[FIN IMAGEN"))
                continue;
            if (trimmed.Contains("??? PÁGINA") || trimmed.Contains("??? PÁGINA"))
                continue;
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            sb.AppendLine(trimmed);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Genera un sumario ejecutivo (primeros ~500 chars del texto limpio).
    /// </summary>
    private static string GenerarSumario(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return string.Empty;

        var limpio = CleanSectionText(texto);
        if (limpio.Length <= 500) return limpio;
        return limpio[..497] + "...";
    }

    /// <summary>
    /// Detecta el número de página buscando "PÁGINA N" antes de la posición dada.
    /// </summary>
    private static int DetectPageNumber(string texto, int position)
    {
        // Buscar hacia atrás el último "PÁGINA N"
        var searchArea = texto[..position];
        var lastPageMarker = searchArea.LastIndexOf("PÁGINA ", StringComparison.Ordinal);
        if (lastPageMarker < 0) return 1;

        // Extraer el número
        var afterMarker = searchArea[(lastPageMarker + 8)..];
        var numStr = new string(afterMarker.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(numStr, out var pageNum) ? pageNum : 1;
    }

    /// <summary>
    /// Obtiene la siguiente línea no vacía desde una posición.
    /// </summary>
    private static string GetNextNonEmptyLine(string texto, int fromPos)
    {
        var lines = texto[fromPos..Math.Min(fromPos + 500, texto.Length)].Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                return trimmed;
        }
        return string.Empty;
    }

    /// <summary>
    /// Verifica si una línea es un marcador de tabla o página (no contenido real).
    /// </summary>
    private static bool IsTableOrPageMarker(string line)
    {
        var t = line.Trim();
        return t.StartsWith("[TABLA ") || t.StartsWith("[FIN TABLA") ||
               t.Contains("??? PÁGINA") || t.Contains("??? PÁGINA") ||
               string.IsNullOrWhiteSpace(t);
    }

    private int CountTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return _encoding.Encode(text).Count;
    }
}
