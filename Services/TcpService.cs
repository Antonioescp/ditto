using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ditto.Helpers;
using Ditto.Interfaces;
using Ditto.Models;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace Ditto.Services;

public class TcpService : IService
{
    private readonly ServiceConfiguration _configuration;
    private readonly ILogger<TcpService> _logger;
    private readonly ITemplateProcessor _templateProcessor;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listenerTask;

    public bool IsRunning { get; private set; }
    public string Name => _configuration.Name;

    public TcpService(ServiceConfiguration configuration, ILogger<TcpService> logger, ITemplateProcessor templateProcessor)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _templateProcessor = templateProcessor ?? throw new ArgumentNullException(nameof(templateProcessor));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            _logger.LogWarning("El servicio {ServiceName} ya está en ejecución", Name);
            return;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, _configuration.Port);
        
        _listener.Start();
        IsRunning = true;
        
        _logger.LogInformation("Servicio TCP '{ServiceName}' iniciado en puerto {Port}", Name, _configuration.Port);

        _listenerTask = Task.Run(() => ListenAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return;
        }

        _cancellationTokenSource?.Cancel();
        _listener?.Stop();
        
        if (_listenerTask != null)
        {
            try
            {
                await _listenerTask;
            }
            catch (OperationCanceledException)
            {
                // Esperado cuando se cancela
            }
        }

        IsRunning = false;
        
        _logger.LogInformation("Servicio TCP '{ServiceName}' detenido", Name);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        if (_listener == null) return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("El listener fue detenido");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al aceptar cliente TCP");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        NetworkStream? stream = null;
        try
        {
            stream = client.GetStream();
            var buffer = new byte[4096];
            var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            
            if (bytesRead == 0)
            {
                client.Close();
                return;
            }

            var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var clientEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            
            _logger.LogDebug("Mensaje TCP recibido desde {ClientAddress}: {Message}", 
                clientEndPoint?.ToString(), message);

            // Encontrar el endpoint que coincide y capturar grupos regex
            var (endpoint, regexMatch) = FindMatchingEndpoint(message);
            if (endpoint == null)
            {
                // Si no hay endpoint específico, usar el primero disponible
                endpoint = _configuration.Endpoints.FirstOrDefault();
            }

            if (endpoint == null)
            {
                _logger.LogWarning("No hay endpoints configurados para el servicio TCP '{ServiceName}'", Name);
                client.Close();
                return;
            }

            // Validar que responseBody y responseBodyFilePath sean mutuamente exclusivos
            if (!EndpointValidator.ValidateResponseBodyExclusivity(endpoint, _logger, "endpoint TCP"))
            {
                client.Close();
                return;
            }

            // Aplicar delay si está configurado
            if (endpoint.DelayMs.HasValue && endpoint.DelayMs.Value > 0)
            {
                await Task.Delay(endpoint.DelayMs.Value, cancellationToken);
            }

            // Crear contexto de la request para Handlebars (incluye grupos de captura regex)
            var requestContext = CreateTcpRequestContext(message, clientEndPoint, regexMatch);
            
            // Obtener el body de respuesta
            var responseBodyToProcess = await ResponseFileLoader.GetResponseBodyAsync(endpoint, _logger);
            if (responseBodyToProcess == null && !string.IsNullOrWhiteSpace(endpoint.ResponseBodyFilePath))
            {
                _logger.LogError("No se pudo leer el archivo de respuesta: {FilePath}", endpoint.ResponseBodyFilePath);
                client.Close();
                return;
            }

            if (responseBodyToProcess == null)
            {
                client.Close();
                return;
            }

            // Procesar template con Handlebars
            object processedBody;
            try
            {
                processedBody = _templateProcessor.ProcessTemplate(responseBodyToProcess, requestContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al procesar template Handlebars");
                client.Close();
                return;
            }

            // Serializar y enviar respuesta
            var jsonResponse = JsonSerializer.Serialize(processedBody, new JsonSerializerOptions
            {
                WriteIndented = false
            });
            
            var responseBytes = Encoding.UTF8.GetBytes(jsonResponse);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken);
            
            _logger.LogInformation("Respuesta TCP enviada a {ClientAddress}", clientEndPoint?.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al manejar cliente TCP");
        }
        finally
        {
            stream?.Close();
            client.Close();
        }
    }

    private (EndpointConfiguration? endpoint, Match? match) FindMatchingEndpoint(string message)
    {
        foreach (var endpoint in _configuration.Endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint.Pattern))
            {
                continue;
            }

            try
            {
                var match = Regex.Match(message, endpoint.Pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return (endpoint, match);
                }
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Patrón regex inválido en endpoint: {Pattern}", endpoint.Pattern);
            }
        }

        return (null, null);
    }

    private Dictionary<string, object> CreateTcpRequestContext(string message, IPEndPoint? clientEndPoint, Match? regexMatch = null)
    {
        // Intentar parsear el mensaje como JSON si es posible
        object? parsedMessage = null;
        try
        {
            var jsonElement = JsonSerializer.Deserialize<JsonElement>(message, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            parsedMessage = JsonConverter.ConvertJsonElementToObject(jsonElement);
        }
        catch (JsonException)
        {
            // No es JSON, usar como string
            parsedMessage = message;
        }

        var requestDict = new Dictionary<string, object>
        {
            ["message"] = message,
            ["parsedMessage"] = parsedMessage ?? message,
            ["clientAddress"] = clientEndPoint?.Address?.ToString() ?? "unknown",
            ["clientPort"] = clientEndPoint?.Port ?? 0,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };

        // Extraer grupos de captura nombrados del regex si existen
        if (regexMatch != null && regexMatch.Success)
        {
            var capturesDict = RegexCaptureExtractor.ExtractNamedGroups(regexMatch);
            if (capturesDict.Count > 0)
            {
                requestDict["captures"] = capturesDict;
            }
        }

        return new Dictionary<string, object>
        {
            ["request"] = requestDict
        };
    }
}
