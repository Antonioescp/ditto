using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ditto.Helpers;
using Ditto.Interfaces;
using Ditto.Models;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace Ditto.Services;

public class ComService : IService
{
    private readonly ServiceConfiguration _configuration;
    private readonly ILogger<ComService> _logger;
    private readonly ITemplateProcessor _templateProcessor;
    private SerialPort? _serialPort;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly object _lockObject = new object();
    private string _receivedDataBuffer = string.Empty;

    public bool IsRunning { get; private set; }
    public string Name => _configuration.Name;

    public ComService(ServiceConfiguration configuration, ILogger<ComService> logger, ITemplateProcessor templateProcessor)
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
        
        // El port ahora es el nombre del puerto serial (COM1, COM9, etc.)
        var portName = GetPortName();
        
        try
        {
            _serialPort = new SerialPort(portName)
            {
                BaudRate = 9600,
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                ReadTimeout = 500,
                WriteTimeout = 500
            };

            _serialPort.DataReceived += OnDataReceived;
            _serialPort.Open();
            
            IsRunning = true;
            
            _logger.LogInformation("Servicio COM '{ServiceName}' iniciado en puerto serial '{PortName}'", 
                Name, portName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al abrir el puerto serial '{PortName}'", portName);
            _serialPort?.Dispose();
            _serialPort = null;
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return;
        }

        _cancellationTokenSource?.Cancel();

        lock (_lockObject)
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.DataReceived -= OnDataReceived;
                _serialPort.Close();
                _serialPort.Dispose();
                _serialPort = null;
            }
        }

        IsRunning = false;
        
        _logger.LogInformation("Servicio COM '{ServiceName}' detenido", Name);
    }

    private string GetPortName()
    {
        // El port puede ser un número (interpretado como COM{port}) o un string (COM1, COM9, etc.)
        if (int.TryParse(_configuration.Port.ToString(), out var portNumber))
        {
            return $"COM{portNumber}";
        }
        return _configuration.Port.ToString();
    }

    private async void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort == null || !_serialPort.IsOpen || _cancellationTokenSource?.Token.IsCancellationRequested == true)
            return;

        try
        {
            lock (_lockObject)
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                    return;

                _receivedDataBuffer += _serialPort.ReadExisting();
            }

            // Procesar cuando recibimos un salto de línea (común en comunicación serial)
            // o cuando el buffer tiene suficiente datos
            if (_receivedDataBuffer.Contains('\n') || _receivedDataBuffer.Contains('\r'))
            {
                var message = _receivedDataBuffer.TrimEnd('\r', '\n');
                _receivedDataBuffer = string.Empty; // Limpiar buffer

                if (!string.IsNullOrWhiteSpace(message))
                {
                    _ = Task.Run(() => ProcessMessageAsync(message, _cancellationTokenSource?.Token ?? CancellationToken.None));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al leer datos del puerto serial");
        }
    }

    private async Task ProcessMessageAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Mensaje COM recibido: {Message}", message);

            // Encontrar el endpoint que coincide y capturar grupos regex
            var (endpoint, regexMatch) = FindMatchingEndpoint(message);
            if (endpoint == null)
            {
                // Si no hay endpoint específico, usar el primero disponible
                endpoint = _configuration.Endpoints.FirstOrDefault();
            }

            if (endpoint == null)
            {
                _logger.LogWarning("No hay endpoints configurados para el servicio COM '{ServiceName}'", Name);
                return;
            }

            // Validar que responseBody y responseBodyFilePath sean mutuamente exclusivos
            if (!EndpointValidator.ValidateResponseBodyExclusivity(endpoint, _logger, "endpoint COM"))
            {
                return;
            }

            // Crear contexto de la request para Handlebars (incluye grupos de captura regex)
            var requestContext = CreateComRequestContext(message, regexMatch);

            // Aplicar delay inicial si está configurado (solo para respuesta única)
            if (endpoint.DelayMs.HasValue && endpoint.DelayMs.Value > 0 && (endpoint.Responses == null || endpoint.Responses.Count == 0))
            {
                await Task.Delay(endpoint.DelayMs.Value, cancellationToken);
            }

            // Manejar respuestas múltiples secuenciales (solo para COM)
            if (endpoint.Responses != null && endpoint.Responses.Count > 0)
            {
                await SendMultipleResponsesAsync(endpoint.Responses, requestContext, cancellationToken);
            }
            else
            {
                // Comportamiento original: respuesta única
                await SendSingleResponseAsync(endpoint, requestContext, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al procesar mensaje COM");
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

    private Dictionary<string, object> CreateComRequestContext(string message, Match? regexMatch = null)
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

    private async Task SendSingleResponseAsync(EndpointConfiguration endpoint, Dictionary<string, object> requestContext, CancellationToken cancellationToken)
    {
        // Obtener el body de respuesta
        var responseBodyToProcess = await ResponseFileLoader.GetResponseBodyAsync(endpoint, _logger);
        if (responseBodyToProcess == null && !string.IsNullOrWhiteSpace(endpoint.ResponseBodyFilePath))
        {
            _logger.LogError("No se pudo leer el archivo de respuesta: {FilePath}", endpoint.ResponseBodyFilePath);
            return;
        }

        if (responseBodyToProcess == null)
        {
            return;
        }

        // Procesar template con Handlebars
        object processedBody;
        try
        {
            processedBody = ProcessComResponse(responseBodyToProcess, requestContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al procesar template Handlebars");
            return;
        }

        // Formatear y enviar respuesta
        await SendResponseAsync(processedBody, cancellationToken);
    }

    private async Task SendMultipleResponsesAsync(List<SequentialResponse> responses, Dictionary<string, object> requestContext, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Enviando {Count} respuestas secuenciales COM", responses.Count);

        for (int i = 0; i < responses.Count; i++)
        {
            var sequentialResponse = responses[i];

            // Aplicar delay antes de esta respuesta si está configurado
            if (sequentialResponse.DelayMs.HasValue && sequentialResponse.DelayMs.Value > 0)
            {
                await Task.Delay(sequentialResponse.DelayMs.Value, cancellationToken);
            }

            // Obtener el body de respuesta
            var responseBodyToProcess = await ResponseFileLoader.GetSequentialResponseBodyAsync(sequentialResponse, _logger);
            if (responseBodyToProcess == null)
            {
                _logger.LogWarning("La respuesta secuencial #{Index} no tiene contenido", i + 1);
                continue;
            }

            // Procesar template con Handlebars
            object processedBody;
            try
            {
                processedBody = ProcessComResponse(responseBodyToProcess, requestContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al procesar template Handlebars para respuesta secuencial #{Index}", i + 1);
                continue;
            }

            // Formatear y enviar respuesta
            await SendResponseAsync(processedBody, cancellationToken);
        }

        _logger.LogInformation("Todas las respuestas secuenciales COM han sido enviadas");
    }

    /// <summary>
    /// Procesa la respuesta COM con Handlebars, manejando correctamente strings y objetos.
    /// </summary>
    private object ProcessComResponse(object responseBody, Dictionary<string, object> requestContext)
    {
        // Si el responseBody es un string directo, procesarlo como template de texto plano
        if (responseBody is string stringBody)
        {
            try
            {
                // Procesar el string con Handlebars directamente
                var template = HandlebarsDotNet.Handlebars.Compile(stringBody);
                var result = template(requestContext);
                return result;
            }
            catch
            {
                // Si falla el procesamiento, retornar el string original
                return stringBody;
            }
        }

        // Para objetos, usar el procesador de templates normal
        return _templateProcessor.ProcessTemplate(responseBody, requestContext);
    }

    private async Task SendResponseAsync(object processedBody, CancellationToken cancellationToken)
    {
        // Formatear respuesta
        string responseText;
        if (processedBody is string stringResponse)
        {
            // Si es un string, enviarlo directamente (texto plano)
            // Remover comillas escapadas si Handlebars las agregó
            responseText = UnescapeString(stringResponse);
        }
        else
        {
            // Si es un objeto, serializar como JSON
            responseText = JsonSerializer.Serialize(processedBody, new JsonSerializerOptions
            {
                WriteIndented = false
            });
        }

        // Enviar respuesta al puerto serial
        lock (_lockObject)
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Write(responseText + Environment.NewLine);
                _logger.LogInformation("Respuesta COM enviada: {Response}", responseText);
            }
        }
    }

    /// <summary>
    /// Remueve comillas JSON escapadas de un string si las tiene.
    /// Si el string está envuelto en comillas JSON (ej: "\"texto\""), retorna solo el contenido.
    /// </summary>
    private string UnescapeString(string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;

        // Si el string comienza y termina con comillas escapadas, removerlas
        if (str.Length >= 2 && str.StartsWith("\"") && str.EndsWith("\""))
        {
            try
            {
                // Intentar deserializar como JSON string para obtener el valor sin comillas
                var unescaped = JsonSerializer.Deserialize<string>(str);
                return unescaped ?? str;
            }
            catch
            {
                // Si falla, retornar el string original
            }
        }

        return str;
    }
}
