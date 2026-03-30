using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SharpToken;
using TwinSeguridad.Models;

namespace TwinSeguridad.Services;

/// <summary>
/// Servicio que lee el JSON generado por PdfExtractionService y construye
/// el índice jerárquico de la norma con secciones (nivel 1) y subsecciones (nivel 1.X).
/// Cuenta tokens usando SharpToken (cl100k_base — compatible GPT-4/GPT-3.5).
///
/// Secciones del INDICE de la norma NOM-002-STPS-2010:
///   1. Objetivo                                    9. Brigadas contra incendio
///   2. Campo de aplicación                        10. Simulacros de emergencias de incendio
///   3. Referencias                                11. Capacitación
///   4. Definiciones                               12. Unidades de verificación
///   5. Obligaciones del patrón                    13. Procedimiento para la evaluación de la conformidad
///   6. Obligaciones de los trabajadores           14. Vigilancia
///   7. Condiciones de prevención y protección     15. Bibliografía
///   8. Plan de atención a emergencias de incendio 16. Concordancia con normas internacionales
///   + Apéndices
/// </summary>
public class IndiceExtractionService
{
    private readonly ILogger<IndiceExtractionService> _logger;
    private readonly GptEncoding _encoding;

    // Regex: línea que empieza con "N.N" — subsección (debe checarse ANTES que sección principal)
    private static readonly Regex SubseccionRegex = new(
        @"^\s*(\d{1,2}\.\d+[\.\d]*)\s+(.+)",
        RegexOptions.Compiled);

    // Regex: línea que empieza con "N. Texto" — sección principal
    private static readonly Regex SeccionPrincipalRegex = new(
        @"^\s*(\d{1,2})\.\s+(.+)",
        RegexOptions.Compiled);

    // Para limpiar espacios múltiples del PDF
    private static readonly Regex MultiSpaceRegex = new(
        @"\s{2,}", RegexOptions.Compiled);

    // Nombres conocidos del INDICE para validar secciones reales vs. texto suelto
    private static readonly Dictionary<int, string> SeccionesConocidas = new()
    {
        [1]  = "Objetivo",
        [2]  = "Campo de aplicación",
        [3]  = "Referencias",
        [4]  = "Definiciones",
        [5]  = "Obligaciones del patrón",
        [6]  = "Obligaciones de los trabajadores",
        [7]  = "Condiciones de prevención y protección contra incendios",
        [8]  = "Plan de atención a emergencias de incendio",
        [9]  = "Brigadas contra incendio",
        [10] = "Simulacros de emergencias de incendio",
        [11] = "Capacitación",
        [12] = "Unidades de verificación",
        [13] = "Procedimiento para la evaluación de la conformidad",
        [14] = "Vigilancia",
        [15] = "Bibliografía",
        [16] = "Concordancia con normas internacionales"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public IndiceExtractionService(ILogger<IndiceExtractionService> logger)
    {
        _logger = logger;
        // cl100k_base = tokenizer de GPT-4 / GPT-4o / GPT-3.5-turbo
        _encoding = GptEncoding.GetEncoding("cl100k_base");
    }

    /// <summary>
    /// Lee el JSON de extracción del PDF y genera el índice jerárquico con tokens.
    /// Guarda el resultado como norma-seguridad-indice.json.
    /// </summary>
    public async Task<(IndiceNorma Indice, string JsonPath)> ExtractIndiceAsync(string jsonFilePath)
    {
        if (!File.Exists(jsonFilePath))
            throw new FileNotFoundException($"No se encontró el JSON: {jsonFilePath}");

        _logger.LogInformation("?? Leyendo JSON de extracción: {Path}", jsonFilePath);

        var jsonContent = await File.ReadAllTextAsync(jsonFilePath, Encoding.UTF8);
        var documento = JsonSerializer.Deserialize<DocumentoLey>(jsonContent, JsonOptions)
            ?? throw new InvalidOperationException("No se pudo deserializar el JSON del documento.");

        var indice = BuildIndice(documento);

        // Guardar en la misma carpeta
        var directory = Path.GetDirectoryName(jsonFilePath)!;
        var outputPath = Path.Combine(directory,
            Path.GetFileNameWithoutExtension(jsonFilePath) + "-indice.json");

        var outputJson = JsonSerializer.Serialize(indice, JsonOptions);
        await File.WriteAllTextAsync(outputPath, outputJson, Encoding.UTF8);

        _logger.LogInformation(
            "? Índice generado: {Secciones} secciones, {Sub} subsecciones, {Tokens} tokens totales ? {Path}",
            indice.TotalSecciones, indice.TotalSubsecciones, indice.TotalTokensDocumento, outputPath);

        return (indice, outputPath);
    }

    /// <summary>
    /// Construye el índice jerárquico a partir del documento extraído.
    /// Estrategia:
    ///   1) Concatenar todas las líneas limpias de todas las páginas.
    ///   2) Saltar la zona del "INDICE" (tabla de contenidos) para no duplicar.
    ///   3) Encontrar la ÚLTIMA aparición de cada sección "N. Nombre" (la del cuerpo real).
    ///   4) Dentro de cada sección, buscar subsecciones "N.X".
    /// </summary>
    private IndiceNorma BuildIndice(DocumentoLey documento)
    {
        // 1) Concatenar líneas limpias con su número de página
        var allLines = new List<(string TextoLimpio, int Pagina)>();

        foreach (var pagina in documento.Paginas)
        {
            foreach (var linea in pagina.Lineas)
            {
                var limpio = CleanText(linea.Texto);
                if (!string.IsNullOrWhiteSpace(limpio))
                    allLines.Add((limpio, pagina.NumeroPagina));
            }

            if (pagina.Lineas.Count == 0 && !string.IsNullOrWhiteSpace(pagina.TextoCompleto))
            {
                foreach (var raw in pagina.TextoCompleto.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var limpio = CleanText(raw);
                    if (!string.IsNullOrWhiteSpace(limpio))
                        allLines.Add((limpio, pagina.NumeroPagina));
                }
            }
        }

        _logger.LogInformation("?? Total líneas limpias: {Count}", allLines.Count);

        // 2) Encontrar donde termina la zona del INDICE (tabla de contenidos)
        //    Buscamos la línea "INDICE" y luego saltamos hasta encontrar "1. Objetivo" después del bloque del índice.
        int indiceStart = -1;
        int bodyStart = 0;

        for (int i = 0; i < allLines.Count; i++)
        {
            if (allLines[i].TextoLimpio.Equals("INDICE", StringComparison.OrdinalIgnoreCase) ||
                allLines[i].TextoLimpio.StartsWith("INDICE", StringComparison.OrdinalIgnoreCase))
            {
                indiceStart = i;
                break;
            }
        }

        if (indiceStart >= 0)
        {
            // Buscar el fin del bloque INDICE: la primera línea DESPUÉS del índice
            // que sea "1. Objetivo" (inicio real del cuerpo de la norma)
            for (int i = indiceStart + 1; i < allLines.Count; i++)
            {
                var line = allLines[i].TextoLimpio;
                var match = SeccionPrincipalRegex.Match(line);
                if (match.Success && match.Groups[1].Value == "1" && !SubseccionRegex.IsMatch(line))
                {
                    // Verificar que NO es una línea corta del índice (ej: "1. Objetivo" con texto corto)
                    // sino el encabezado real del cuerpo. El cuerpo vendrá en una página posterior.
                    if (allLines[i].Pagina > allLines[indiceStart].Pagina)
                    {
                        bodyStart = i;
                        _logger.LogInformation("?? Cuerpo de la norma inicia en línea {Line}, página {Page}",
                            i, allLines[i].Pagina);
                        break;
                    }
                }
            }
        }

        // 3) Solo buscar secciones principales en el cuerpo (después del INDICE)
        //    Usar la ÚLTIMA ocurrencia de cada número de sección para evitar duplicados
        var sectionCandidates = new Dictionary<string, (int LineIndex, string Numero, string Nombre)>();
        var apendiceIndex = -1;

        for (int i = bodyStart; i < allLines.Count; i++)
        {
            var line = allLines[i].TextoLimpio;

            // Saltar subsecciones
            if (SubseccionRegex.IsMatch(line))
                continue;

            // Sección principal: "N. Nombre"
            var match = SeccionPrincipalRegex.Match(line);
            if (match.Success)
            {
                var num = match.Groups[1].Value;
                var nombre = match.Groups[2].Value.Trim();

                if (int.TryParse(num, out var n) && n >= 1 && n <= 16)
                {
                    // Solo guardar la PRIMERA ocurrencia en el cuerpo (no la del índice)
                    if (!sectionCandidates.ContainsKey(num))
                    {
                        sectionCandidates[num] = (i, num, nombre);
                        _logger.LogDebug("  Sección {Num}. {Nombre} ? línea {Line}, página {Page}",
                            num, nombre, i, allLines[i].Pagina);
                    }
                }
            }

            // Detectar Apéndice
            if (apendiceIndex < 0 &&
                Regex.IsMatch(line, @"^Ap[eé]ndice", RegexOptions.IgnoreCase))
            {
                apendiceIndex = i;
            }
        }

        // Construir lista ordenada de secciones
        var sectionStarts = sectionCandidates.Values
            .OrderBy(s => s.LineIndex)
            .ToList();

        // Agregar Apéndice al final si se encontró
        if (apendiceIndex >= 0)
        {
            sectionStarts.Add((apendiceIndex, "Apendice", allLines[apendiceIndex].TextoLimpio));
        }

        _logger.LogInformation("?? Secciones principales detectadas: {Count}", sectionStarts.Count);
        foreach (var s in sectionStarts)
        {
            _logger.LogInformation("   {Num}. {Nombre} (línea {Line})", s.Numero, s.Nombre, s.LineIndex);
        }

        // 4) Para cada sección, extraer texto completo y buscar subsecciones dentro
        var secciones = new List<SeccionNorma>();

        for (int s = 0; s < sectionStarts.Count; s++)
        {
            var start = sectionStarts[s].LineIndex;
            var end = (s + 1 < sectionStarts.Count) ? sectionStarts[s + 1].LineIndex : allLines.Count;

            var sectionLines = allLines.GetRange(start, end - start);
            var seccion = BuildSeccion(sectionStarts[s].Numero, sectionStarts[s].Nombre, sectionLines);
            secciones.Add(seccion);
        }

        var indice = new IndiceNorma
        {
            ArchivoOrigen = documento.ArchivoOrigen,
            FechaExtraccion = DateTime.UtcNow,
            ModeloTokenizacion = "cl100k_base",
            Secciones = secciones,
            TotalSecciones = secciones.Count,
            TotalSubsecciones = secciones.Sum(s => s.Subsecciones.Count),
            TotalTokensDocumento = secciones.Sum(s => s.Tokens)
        };

        return indice;
    }

    /// <summary>
    /// Construye una SeccionNorma a partir de sus líneas, detectando subsecciones internas.
    /// </summary>
    private SeccionNorma BuildSeccion(string numero, string nombre,
        List<(string TextoLimpio, int Pagina)> lines)
    {
        var textoCompleto = string.Join("\n", lines.Select(l => l.TextoLimpio));
        var paginas = lines.Select(l => l.Pagina).Distinct().OrderBy(p => p).ToList();

        // Buscar subsecciones (N.X) dentro de las líneas de esta sección
        var subStarts = new List<(int LineIndex, string SubNumero, string SubNombre)>();

        for (int i = 0; i < lines.Count; i++)
        {
            var match = SubseccionRegex.Match(lines[i].TextoLimpio);
            if (match.Success)
            {
                var subNum = match.Groups[1].Value;
                var subNombre = match.Groups[2].Value.Trim();

                // Verificar que la subsección pertenece a esta sección principal
                if (subNum.StartsWith(numero + "."))
                {
                    subStarts.Add((i, subNum, subNombre));
                }
            }
        }

        // Construir subsecciones
        var subsecciones = new List<SubseccionNorma>();

        for (int ss = 0; ss < subStarts.Count; ss++)
        {
            var subStart = subStarts[ss].LineIndex;
            var subEnd = (ss + 1 < subStarts.Count) ? subStarts[ss + 1].LineIndex : lines.Count;

            var subLines = lines.GetRange(subStart, subEnd - subStart);
            var subTexto = string.Join("\n", subLines.Select(l => l.TextoLimpio));
            var subPaginas = subLines.Select(l => l.Pagina).Distinct().OrderBy(p => p).ToList();

            subsecciones.Add(new SubseccionNorma
            {
                Numero = subStarts[ss].SubNumero,
                Nombre = subStarts[ss].SubNombre,
                TextoCompleto = subTexto,
                Tokens = CountTokens(subTexto),
                Caracteres = subTexto.Length,
                Paginas = subPaginas
            });
        }

        var seccion = new SeccionNorma
        {
            Numero = numero,
            Nombre = nombre,
            TextoCompleto = textoCompleto,
            Tokens = CountTokens(textoCompleto),
            Caracteres = textoCompleto.Length,
            Paginas = paginas,
            Subsecciones = subsecciones
        };

        return seccion;
    }

    /// <summary>
    /// Cuenta tokens usando SharpToken (cl100k_base — GPT-4/GPT-4o).
    /// </summary>
    private int CountTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return _encoding.Encode(text).Count;
    }

    /// <summary>
    /// Limpia el texto extraído del PDF: normaliza espacios múltiples, trim.
    /// </summary>
    private static string CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return MultiSpaceRegex.Replace(text, " ").Trim();
    }
}
