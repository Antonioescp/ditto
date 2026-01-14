using Ditto.Interfaces;
using Microsoft.Extensions.Logging;

namespace Ditto.Services;

public class ServiceManager
{
    private readonly List<IService> _services;
    private readonly ILogger<ServiceManager> _logger;

    public ServiceManager(ILogger<ServiceManager> logger)
    {
        _services = new List<IService>();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RegisterService(IService service)
    {
        if (service == null)
            throw new ArgumentNullException(nameof(service));

        _services.Add(service);
        _logger.LogInformation("Servicio '{ServiceName}' registrado", service.Name);
    }

    public async Task StartAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Iniciando {Count} servicios...", _services.Count);

        var tasks = _services.Select(service => service.StartAsync(cancellationToken));
        await Task.WhenAll(tasks);

        _logger.LogInformation("Todos los servicios han sido iniciados");
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deteniendo {Count} servicios...", _services.Count);

        var tasks = _services.Select(service => service.StopAsync(cancellationToken));
        await Task.WhenAll(tasks);

        _logger.LogInformation("Todos los servicios han sido detenidos");
    }
}
