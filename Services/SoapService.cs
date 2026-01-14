using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Ditto.Interfaces;
using Ditto.Models;
using Microsoft.Extensions.Logging;

namespace Ditto.Services;

public class SoapService : IService
{
    private readonly ServiceConfiguration _configuration;
    private readonly ILogger<SoapService> _logger;
    private readonly ITemplateProcessor _templateProcessor;
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listenerTask;

    public bool IsRunning { get; private set; }
    public string Name => _configuration.Name;

    public SoapService(ServiceConfiguration configuration, ILogger<SoapService> logger, ITemplateProcessor templateProcessor)
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
        
        _logger.LogInformation("Servicio SOAP '{ServiceName}' iniciado en http://localhost:{Port}", 
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
        
        _logger.LogInformation("Servicio SOAP '{ServiceName}' detenido", Name);
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

            // Agregar headers (si no hay Content-Type, usar text/xml por defecto para SOAP)
            var hasContentType = false;
            foreach (var header in endpoint.Headers)
            {
                response.Headers.Add(header.Key, header.Value);
                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    hasContentType = true;
                }
            }

            // Si no se especificó Content-Type, usar text/xml por defecto para SOAP
            if (!hasContentType)
            {
                response.ContentType = "text/xml; charset=utf-8";
            }

            // Validar que responseBody y responseBodyFilePath sean mutuamente exclusivos
            if (endpoint.ResponseBody != null && !string.IsNullOrWhiteSpace(endpoint.ResponseBodyFilePath))
            {
                _logger.LogError("El endpoint {Path} tiene ambos 'responseBody' y 'responseBodyFilePath' configurados. Solo uno debe estar presente.", endpoint.Path);
                response.StatusCode = 500;
                response.Close();
                return;
            }

            // Escribir body de respuesta
            object? responseBodyToProcess = null;
            
            if (!string.IsNullOrWhiteSpace(endpoint.ResponseBodyFilePath))
            {
                // Leer el contenido del archivo
                try
                {
                    var fileContent = await LoadResponseBodyFromFileAsync(endpoint.ResponseBodyFilePath);
                    if (fileContent == null)
                    {
                        _logger.LogError("No se pudo leer el archivo de respuesta: {FilePath}", endpoint.ResponseBodyFilePath);
                        response.StatusCode = 500;
                        response.Close();
                        return;
                    }
                    
                    // Para SOAP, el contenido del archivo generalmente es XML
                    // Intentar parsear como JSON primero (por si el usuario quiere usar JSON en Handlebars)
                    try
                    {
                        responseBodyToProcess = JsonSerializer.Deserialize<object>(fileContent, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                    }
                    catch (JsonException)
                    {
                        // Si no es JSON válido, usar el contenido como string (XML o texto)
                        responseBodyToProcess = fileContent;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error al leer el archivo de respuesta: {FilePath}", endpoint.ResponseBodyFilePath);
                    response.StatusCode = 500;
                    response.Close();
                    return;
                }
            }
            else if (endpoint.ResponseBody != null)
            {
                responseBodyToProcess = endpoint.ResponseBody;
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
                
                // Serializar respuesta
                string responseText;
                if (processedBody is string stringResponse)
                {
                    // Si es un string, asumir que es XML y enviarlo directamente
                    responseText = stringResponse;
                }
                else
                {
                    // Si es un objeto, serializar como JSON (Handlebars puede generar objetos)
                    responseText = JsonSerializer.Serialize(processedBody, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                }
                
                var buffer = Encoding.UTF8.GetBytes(responseText);
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
            }

            response.Close();
            
            _logger.LogInformation("Respuesta SOAP enviada: {Method} {Path} -> {StatusCode}", 
                request.HttpMethod, request.Url?.PathAndQuery, endpoint.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al procesar la solicitud SOAP");
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

            // Intentar parsear como XML para SOAP
            try
            {
                var xmlDoc = XDocument.Parse(bodyText);
                return ConvertXmlToObject(xmlDoc.Root);
            }
            catch (Exception)
            {
                // Si no es XML válido, intentar como JSON
                try
                {
                    var jsonElement = JsonSerializer.Deserialize<JsonElement>(bodyText, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    return ConvertJsonElementToObject(jsonElement);
                }
                catch (JsonException)
                {
                    // Si tampoco es JSON válido, retornar como string
                    return bodyText;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al leer el body de la request SOAP");
            return null;
        }
    }

    private object ConvertXmlToObject(XElement? element)
    {
        if (element == null)
            return new Dictionary<string, object>();

        var dict = new Dictionary<string, object>();

        // Agregar atributos
        foreach (var attr in element.Attributes())
        {
            dict[$"@{attr.Name.LocalName}"] = attr.Value;
        }

        // Agregar elementos hijos
        var groupedElements = element.Elements().GroupBy(e => e.Name.LocalName);
        
        foreach (var group in groupedElements)
        {
            var elements = group.ToList();
            if (elements.Count == 1)
            {
                var child = elements[0];
                if (child.HasElements || child.HasAttributes)
                {
                    dict[group.Key] = ConvertXmlToObject(child);
                }
                else if (!child.IsEmpty)
                {
                    dict[group.Key] = child.Value;
                }
            }
            else
            {
                // Múltiples elementos con el mismo nombre -> array
                var list = elements.Select(e => 
                    e.HasElements || e.HasAttributes 
                        ? ConvertXmlToObject(e) 
                        : (object)e.Value).ToList();
                dict[group.Key] = list;
            }
        }

        // Si no tiene elementos pero tiene valor, retornar el valor
        if (!element.HasElements && !string.IsNullOrWhiteSpace(element.Value) && dict.Count == 0)
        {
            return element.Value;
        }

        return dict;
    }

    private object ConvertJsonElementToObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object>();
                foreach (var property in element.EnumerateObject())
                {
                    dict[property.Name] = ConvertJsonElementToObject(property.Value);
                }
                return dict;
            
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(ConvertJsonElementToObject).ToList();
            
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
            
            case JsonValueKind.Number:
                if (element.TryGetInt32(out var intValue))
                    return intValue;
                if (element.TryGetInt64(out var longValue))
                    return longValue;
                return element.GetDouble();
            
            case JsonValueKind.True:
                return true;
            
            case JsonValueKind.False:
                return false;
            
            case JsonValueKind.Null:
                return null!;
            
            default:
                return element.ToString();
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

    private async Task<string?> LoadResponseBodyFromFileAsync(string filePath)
    {
        try
        {
            // Si la ruta es relativa, buscar desde el directorio de trabajo actual
            var fullPath = Path.IsPathRooted(filePath) 
                ? filePath 
                : Path.Combine(Directory.GetCurrentDirectory(), filePath);

            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("El archivo de respuesta no existe: {FilePath}", fullPath);
                return null;
            }

            return await File.ReadAllTextAsync(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al leer el archivo de respuesta: {FilePath}", filePath);
            return null;
        }
    }
}
