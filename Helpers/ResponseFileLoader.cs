using Ditto.Models;
using Microsoft.Extensions.Logging;

namespace Ditto.Helpers;

public static class ResponseFileLoader
{
    public static async Task<string?> LoadResponseFileAsync(string filePath, ILogger logger)
    {
        try
        {
            var fullPath = Path.IsPathRooted(filePath)
                ? filePath
                : Path.Combine(Directory.GetCurrentDirectory(), filePath);

            if (!File.Exists(fullPath))
            {
                logger.LogWarning("El archivo de respuesta no existe: {FilePath}", fullPath);
                return null;
            }

            return await File.ReadAllTextAsync(fullPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error al leer el archivo de respuesta: {FilePath}", filePath);
            return null;
        }
    }

    public static object? ParseResponseFileContent(string fileContent)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<object>(fileContent, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (System.Text.Json.JsonException)
        {
            // No es JSON válido, retornar como string
            return fileContent;
        }
    }

    public static async Task<object?> GetResponseBodyAsync(EndpointConfiguration endpoint, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.ResponseBodyFilePath))
        {
            var fileContent = await LoadResponseFileAsync(endpoint.ResponseBodyFilePath, logger);
            if (fileContent == null)
            {
                logger.LogError("No se pudo leer el archivo de respuesta: {FilePath}", endpoint.ResponseBodyFilePath);
                return null;
            }
            return ParseResponseFileContent(fileContent);
        }
        
        return endpoint.ResponseBody;
    }
}
