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

            // Aplicar delay si está configurado
            if (endpoint.DelayMs.HasValue && endpoint.DelayMs.Value > 0)
            {
                await Task.Delay(endpoint.DelayMs.Value, cancellationToken);
            }

            // Crear contexto de la request para Handlebars (incluye grupos de captura regex)
            var requestContext = CreateComRequestContext(message, regexMatch);
            
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
                processedBody = _templateProcessor.ProcessTemplate(responseBodyToProcess, requestContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al procesar template Handlebars");
                return;
            }

            // Formatear respuesta
            string responseText;
            if (processedBody is string stringResponse)
            {
                // Si es un string, enviarlo directamente (texto plano)
                responseText = stringResponse;
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
}
