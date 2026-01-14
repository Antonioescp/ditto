using Ditto.Configuration;
using Ditto.Interfaces;
using Ditto.Services;
using HandlebarsTemplateProcessor = Ditto.Services.HandlebarsTemplateProcessor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Configurar logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Registrar servicios (DI)
builder.Services.AddSingleton<IConfigurationLoader, JsonConfigurationLoader>();
builder.Services.AddSingleton<Ditto.Interfaces.ITemplateProcessor, HandlebarsTemplateProcessor>();
builder.Services.AddSingleton<IServiceFactory, RestServiceFactory>();
builder.Services.AddSingleton<ServiceManager>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var configLoader = host.Services.GetRequiredService<IConfigurationLoader>();
var serviceFactory = host.Services.GetRequiredService<IServiceFactory>();
var serviceManager = host.Services.GetRequiredService<ServiceManager>();

try
{
    // Obtener ruta de configuración desde argumentos o usar valor por defecto
    var configPath = args.Length > 0 ? args[0] : "services.json";
    
    logger.LogInformation("Cargando configuración desde: {ConfigPath}", configPath);

    // Cargar configuración
    var configurations = await configLoader.LoadAsync(configPath);
    
    if (configurations.Count == 0)
    {
        logger.LogWarning("No se encontraron servicios para iniciar. Verifique el archivo de configuración.");
        return;
    }

    // Crear y registrar servicios
    foreach (var config in configurations)
    {
        try
        {
            var service = serviceFactory.CreateService(config);
            serviceManager.RegisterService(service);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error al crear el servicio '{ServiceName}' de tipo '{ServiceType}'", 
                config.Name, config.Type);
        }
    }

    // Iniciar todos los servicios
    using var cts = new CancellationTokenSource();
    
    Console.CancelKeyPress += (sender, e) =>
    {
        e.Cancel = true;
        logger.LogInformation("Se recibió señal de interrupción. Deteniendo servicios...");
        cts.Cancel();
    };

    await serviceManager.StartAllAsync(cts.Token);

    logger.LogInformation("Servicios en ejecución. Presione Ctrl+C para detener.");

    // Esperar hasta que se reciba la señal de cancelación
    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // Esperado cuando se cancela
    }

    // Detener todos los servicios
    await serviceManager.StopAllAsync(cts.Token);
}
catch (Exception ex)
{
    logger.LogError(ex, "Error fatal en la aplicación");
    Environment.ExitCode = 1;
}
