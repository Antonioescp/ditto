using System.Net;
using System.Text;
using System.Text.Json;
using Ditto.Helpers;
using Ditto.Interfaces;
using Ditto.Models;
using Microsoft.Extensions.Logging;

namespace Ditto.Services;

public class RestService : IService
{
    private readonly ServiceConfiguration _configuration;
    private readonly ILogger<RestService> _logger;
    private readonly ITemplateProcessor _templateProcessor;
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listenerTask;

    public bool IsRunning { get; private set; }
    public string Name => _configuration.Name;

    public RestService(ServiceConfiguration configuration, ILogger<RestService> logger, ITemplateProcessor templateProcessor)
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
        _listener = new HttpListener();
        
        var prefix = $"http://localhost:{_configuration.Port}/";
        _listener.Prefixes.Add(prefix);
        
        _listener.Start();
        IsRunning = true;
        
        _logger.LogInformation("Servicio REST '{ServiceName}' iniciado en http://localhost:{Port}", 
            Name, _configuration.Port);

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

        _listener?.Close();
        IsRunning = false;
        
        _logger.LogInformation("Servicio REST '{ServiceName}' detenido", Name);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        if (_listener == null) return;

        while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => ProcessRequestAsync(context, cancellationToken), cancellationToken);
            }
            catch (HttpListenerException ex) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("El listener fue detenido: {Message}", ex.Message);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al obtener contexto del listener");
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            var endpoint = FindMatchingEndpoint(request);
            if (endpoint == null)
            {
                response.StatusCode = 404;
                response.Close();
                _logger.LogWarning("Endpoint no encontrado: {Method} {Path}", request.HttpMethod, request.Url?.PathAndQuery);
                return;
            }

            // Aplicar delay si está configurado
            if (endpoint.DelayMs.HasValue && endpoint.DelayMs.Value > 0)
            {
                await Task.Delay(endpoint.DelayMs.Value, cancellationToken);
            }

            // Configurar respuesta
            response.StatusCode = endpoint.StatusCode;

            // Agregar headers
            foreach (var header in endpoint.Headers)
            {
                response.Headers.Add(header.Key, header.Value);
            }

            // Validar que responseBody y responseBodyFilePath sean mutuamente exclusivos
            if (!EndpointValidator.ValidateResponseBodyExclusivity(endpoint, _logger, $"endpoint {endpoint.Path}"))
            {
                response.StatusCode = 500;
                response.Close();
                return;
            }

            // Obtener body de respuesta
            var responseBodyToProcess = await ResponseFileLoader.GetResponseBodyAsync(endpoint, _logger);
            if (responseBodyToProcess == null && !string.IsNullOrWhiteSpace(endpoint.ResponseBodyFilePath))
            {
                response.StatusCode = 500;
                response.Close();
                return;
            }

            if (responseBodyToProcess != null)
            {
                // Crear contexto de la request para Handlebars
                var requestContext = await CreateRequestContext(request);
                
                // Procesar template con Handlebars
                object processedBody;
                try
                {
                    processedBody = _templateProcessor.ProcessTemplate(responseBodyToProcess, requestContext);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error al procesar template Handlebars");
                    response.StatusCode = 500;
                    response.Close();
                    return;
                }
                
                var jsonResponse = JsonSerializer.Serialize(processedBody, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                var buffer = Encoding.UTF8.GetBytes(jsonResponse);
                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
            }

            response.Close();
            
            _logger.LogInformation("Respuesta enviada: {Method} {Path} -> {StatusCode}", 
                request.HttpMethod, request.Url?.PathAndQuery, endpoint.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al procesar la solicitud");
            response.StatusCode = 500;
            response.Close();
        }
    }

    private EndpointConfiguration? FindMatchingEndpoint(HttpListenerRequest request)
    {
        var requestPath = request.Url?.AbsolutePath ?? string.Empty;
        var requestMethod = request.HttpMethod;

        return _configuration.Endpoints.FirstOrDefault(e =>
            e.Path.Equals(requestPath, StringComparison.OrdinalIgnoreCase) &&
            e.Method.Equals(requestMethod, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<object?> ReadRequestBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
            return null;

        try
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            var bodyText = await reader.ReadToEndAsync();
            
            if (string.IsNullOrWhiteSpace(bodyText))
                return null;

            // Intentar deserializar como JSON
            try
            {
                var jsonElement = JsonSerializer.Deserialize<JsonElement>(bodyText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                
                // Convertir JsonElement a diccionario para que Handlebars pueda acceder a las propiedades
                return JsonConverter.ConvertJsonElementToObject(jsonElement);
            }
            catch (JsonException)
            {
                // Si no es JSON válido, retornar como string
                return bodyText;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al leer el body de la request");
            return null;
        }
    }

    private Dictionary<string, string> ExtractQueryParameters(Uri? url)
    {
        var queryParams = new Dictionary<string, string>();
        
        if (url?.Query == null || url.Query.Length <= 1)
            return queryParams;

        var queryString = url.Query.TrimStart('?');
        var pairs = queryString.Split('&');

        foreach (var pair in pairs)
        {
            var keyValue = pair.Split('=', 2);
            if (keyValue.Length == 2)
            {
                var key = Uri.UnescapeDataString(keyValue[0]);
                var value = Uri.UnescapeDataString(keyValue[1]);
                queryParams[key] = value;
            }
        }

        return queryParams;
    }

    private Dictionary<string, string> ExtractHeaders(HttpListenerRequest request)
    {
        var headers = new Dictionary<string, string>();
        
        foreach (string? key in request.Headers.AllKeys)
        {
            if (key != null)
            {
                headers[key] = request.Headers[key] ?? string.Empty;
            }
        }

        return headers;
    }

    private async Task<Dictionary<string, object>> CreateRequestContext(HttpListenerRequest request)
    {
        var body = await ReadRequestBodyAsync(request);
        var queryParams = ExtractQueryParameters(request.Url);
        var headers = ExtractHeaders(request);

        // Estructura anidada para que Handlebars pueda acceder como request.method, request.query.param, etc.
        return new Dictionary<string, object>
        {
            ["request"] = new Dictionary<string, object>
            {
                ["method"] = request.HttpMethod,
                ["path"] = request.Url?.AbsolutePath ?? string.Empty,
                ["query"] = queryParams,
                ["headers"] = headers,
                ["body"] = body ?? new object()
            }
        };
    }
}
