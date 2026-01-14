using Ditto.Interfaces;
using Ditto.Models;
using Microsoft.Extensions.Logging;

namespace Ditto.Services;

public class RestServiceFactory : IServiceFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITemplateProcessor _templateProcessor;

    public RestServiceFactory(ILoggerFactory loggerFactory, ITemplateProcessor templateProcessor)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _templateProcessor = templateProcessor ?? throw new ArgumentNullException(nameof(templateProcessor));
    }

    public IService CreateService(ServiceConfiguration configuration)
    {
        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));

        return configuration.Type.ToUpperInvariant() switch
        {
            "REST" => new RestService(configuration, _loggerFactory.CreateLogger<RestService>(), _templateProcessor),
            "TCP" => new TcpService(configuration, _loggerFactory.CreateLogger<TcpService>(), _templateProcessor),
            "SOAP" => new SoapService(configuration, _loggerFactory.CreateLogger<SoapService>(), _templateProcessor),
            "COM" => new ComService(configuration, _loggerFactory.CreateLogger<ComService>(), _templateProcessor),
            _ => throw new NotSupportedException($"Tipo de servicio no soportado: {configuration.Type}")
        };
    }
}
