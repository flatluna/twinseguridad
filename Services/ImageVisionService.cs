using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TwinSeguridad.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace TwinSeguridad.Services;

/// <summary>
/// Servicio que extrae imágenes del PDF, las guarda en disco como PNG,
/// y llama a Azure OpenAI GPT-4 mini con visión para describir cada imagen.
/// La descripción se inserta en el texto de la página original.
///
/// Configuración (local.settings.json):
///   AZURE_OPENAI_ENDPOINT   = https://flatbitai.openai.azure.com
///   AZURE_OPENAI_API_KEY    = ...
///   AZURE_OPENAI_CHAT_DEPLOYMENT_NAME = gpt4mini
/// </summary>
public class ImageVisionService
{
    private readonly ILogger<ImageVisionService> _logger;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _deploymentName;
    private readonly HttpClient _httpClient;

    public ImageVisionService(ILogger<ImageVisionService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _endpoint = configuration["AZURE_OPENAI_ENDPOINT"]
                    ?? configuration["Values:AZURE_OPENAI_ENDPOINT"]
                    ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT no configurado.");
        _apiKey = configuration["AZURE_OPENAI_API_KEY"]
                  ?? configuration["Values:AZURE_OPENAI_API_KEY"]
                  ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY no configurado.");
        _deploymentName = configuration["AZURE_OPENAI_CHAT_DEPLOYMENT_NAME"]
                          ?? configuration["Values:AZURE_OPENAI_CHAT_DEPLOYMENT_NAME"]
                          ?? "gpt4mini";
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Extrae todas las imágenes de un PDF, las guarda en disco, y llama GPT-4 mini
    /// con visión para describir cada una. Devuelve las imágenes organizadas por página.
    /// </summary>
    public async Task<Dictionary<int, List<ImagenExtraida>>> ExtractAndDescribeImagesAsync(
        string pdfFilePath, string outputDirectory)
    {
        var imagesDir = Path.Combine(outputDirectory, "images");
        Directory.CreateDirectory(imagesDir);

        var result = new Dictionary<int, List<ImagenExtraida>>();

        using var pdfDocument = PdfDocument.Open(pdfFilePath);

        for (var pageNum = 1; pageNum <= pdfDocument.NumberOfPages; pageNum++)
        {
            try
            {
                var page = pdfDocument.GetPage(pageNum);
                var images = page.GetImages().ToList();

                if (images.Count == 0) continue;

                var pageImages = new List<ImagenExtraida>();

                for (var imgIdx = 0; imgIdx < images.Count; imgIdx++)
                {
                    var pdfImage = images[imgIdx];

                    try
                    {
                        var imagen = await ProcessImageAsync(pdfImage, pageNum, imgIdx, imagesDir);
                        if (imagen != null)
                        {
                            pageImages.Add(imagen);
                            _logger.LogInformation(
                                "??? Página {Page} imagen {Idx}: {File} ({W}x{H}) — {Desc}",
                                pageNum, imgIdx, imagen.NombreArchivo,
                                imagen.Ancho, imagen.Alto,
                                (imagen.DescripcionIA ?? "sin descripción").Length > 80
                                    ? (imagen.DescripcionIA ?? "sin descripción")[..80] + "..."
                                    : imagen.DescripcionIA ?? "sin descripción");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "?? Error procesando imagen {Idx} de página {Page}", imgIdx, pageNum);
                    }
                }

                if (pageImages.Count > 0)
                    result[pageNum] = pageImages;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "?? Error extrayendo imágenes de página {Page}", pageNum);
            }
        }

        _logger.LogInformation("??? Total imágenes extraídas: {Count} en {Pages} páginas",
            result.Values.Sum(v => v.Count), result.Count);

        return result;
    }

    /// <summary>
    /// Procesa una imagen individual: la guarda en disco y llama a GPT-4 mini visión.
    /// </summary>
    private async Task<ImagenExtraida?> ProcessImageAsync(
        IPdfImage pdfImage, int pageNum, int imgIdx, string imagesDir)
    {
        // Intentar obtener los bytes de la imagen
        byte[]? imageBytes = null;
        string extension = "png";

        if (pdfImage.TryGetPng(out var pngBytes))
        {
            imageBytes = pngBytes;
            extension = "png";
        }
        else
        {
            // Fallback: usar RawBytes
            imageBytes = pdfImage.RawBytes.ToArray();
            // Detectar formato por los magic bytes
            if (imageBytes.Length > 2 && imageBytes[0] == 0xFF && imageBytes[1] == 0xD8)
                extension = "jpg";
            else
                extension = "png";
        }

        if (imageBytes == null || imageBytes.Length < 100)
        {
            _logger.LogDebug("Imagen muy pequeña o vacía en página {Page} idx {Idx}, saltando", pageNum, imgIdx);
            return null;
        }

        // Guardar en disco
        var fileName = $"pagina-{pageNum:D3}-img-{imgIdx:D2}.{extension}";
        var filePath = Path.Combine(imagesDir, fileName);
        await File.WriteAllBytesAsync(filePath, imageBytes);

        // Obtener dimensiones de la imagen
        var bounds = pdfImage.Bounds;

        var imagen = new ImagenExtraida
        {
            IndiceImagen = imgIdx,
            RutaArchivo = filePath,
            NombreArchivo = fileName,
            Ancho = (int)bounds.Width,
            Alto = (int)bounds.Height,
            PosicionY = bounds.Bottom,
            PosicionX = bounds.Left,
            Formato = extension,
            TamanoBytes = imageBytes.Length
        };

        // Llamar GPT-4 mini con visión para describir la imagen
        try
        {
            imagen.DescripcionIA = await DescribeImageWithVisionAsync(imageBytes, extension, pageNum, imgIdx);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "?? No se pudo describir imagen {File} con GPT-4 mini", fileName);
            imagen.DescripcionIA = $"[Error al describir imagen: {ex.Message}]";
        }

        return imagen;
    }

    /// <summary>
    /// Llama a Azure OpenAI GPT-4 mini con la imagen en base64 para obtener
    /// una descripción detallada de lo que contiene.
    /// Usa la REST API directamente (compatible con cualquier versión).
    /// </summary>
    private async Task<string> DescribeImageWithVisionAsync(
        byte[] imageBytes, string extension, int pageNum, int imgIdx)
    {
        var base64Image = Convert.ToBase64String(imageBytes);
        var mimeType = extension == "jpg" ? "image/jpeg" : "image/png";

        // Azure OpenAI REST API — Chat Completions con visión
        var apiVersion = "2024-08-01-preview";
        var url = $"{_endpoint.TrimEnd('/')}/openai/deployments/{_deploymentName}/chat/completions?api-version={apiVersion}";

        var requestBody = new
        {
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "Eres un experto en analizar documentos de normas de seguridad mexicanas (NOM). " +
                              "Describe detalladamente el contenido de la imagen: diagramas, tablas, figuras, " +
                              "símbolos de seguridad, flujos de proceso, etc. " +
                              "Si contiene texto, transcríbelo. Si es una tabla, extráela en formato estructurado. " +
                              "Si es un diagrama de flujo, describe cada paso. " +
                              "Responde en español."
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = $"Esta imagen fue extraída de la página {pageNum} de una norma de seguridad (NOM-002-STPS-2010). " +
                                   "Describe detalladamente qué contiene esta imagen."
                        },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:{mimeType};base64,{base64Image}",
                                detail = "high"
                            }
                        }
                    }
                }
            },
            max_tokens = 2000,
            temperature = 0.1
        };

        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("api-key", _apiKey);

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("? GPT-4 mini visión error {Status}: {Body}",
                response.StatusCode, responseBody[..Math.Min(500, responseBody.Length)]);
            return $"[Error GPT-4 mini: {response.StatusCode}]";
        }

        // Parsear la respuesta
        using var doc = JsonDocument.Parse(responseBody);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() > 0)
        {
            var content = choices[0].GetProperty("message").GetProperty("content").GetString();
            return content ?? "[Sin respuesta]";
        }

        return "[Sin respuesta del modelo]";
    }
}
