using System.Text.Json;
using Ditto.Interfaces;
using Ditto.Models;
using Microsoft.Extensions.Logging;

namespace Ditto.Configuration;

public class JsonConfigurationLoader : IConfigurationLoader
{
    private readonly ILogger<JsonConfigurationLoader> _logger;

    public JsonConfigurationLoader(ILogger<JsonConfigurationLoader> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<ServiceConfiguration>> LoadAsync(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("La ruta de configuración no puede estar vacía", nameof(configPath));

        if (!File.Exists(configPath))
        {
            _logger.LogWarning("El archivo de configuración no existe: {ConfigPath}", configPath);
            return new List<ServiceConfiguration>();
        }

        try
        {
            var jsonContent = await File.ReadAllTextAsync(configPath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };

            var services = JsonSerializer.Deserialize<List<ServiceConfiguration>>(jsonContent, options);
            
            if (services == null)
            {
                _logger.LogWarning("No se pudieron cargar servicios desde la configuración");
                return new List<ServiceConfiguration>();
            }

            _logger.LogInformation("Se cargaron {Count} servicios desde la configuración", services.Count);
            return services;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error al deserializar el archivo de configuración JSON");
            throw new InvalidOperationException("El archivo de configuración tiene un formato JSON inválido", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al cargar el archivo de configuración");
            throw;
        }
    }
}
