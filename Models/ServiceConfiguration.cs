namespace Ditto.Models;

public class ServiceConfiguration
{
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Port { get; set; }
    public List<EndpointConfiguration> Endpoints { get; set; } = new();
}

public class EndpointConfiguration
{
    // Campos comunes
    public object? ResponseBody { get; set; }
    public string? ResponseBodyFilePath { get; set; }
    public int? DelayMs { get; set; }
    
    // Campos específicos de REST
    public string Path { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public int StatusCode { get; set; } = 200;
    public Dictionary<string, string> Headers { get; set; } = new();
    
    // Campos específicos de TCP
    public string? Pattern { get; set; }  // Patrón para matchear mensajes TCP (regex opcional)
    
    // Campos específicos de COM - Respuestas múltiples secuenciales
    public List<SequentialResponse>? Responses { get; set; }  // Lista de respuestas para enviar secuencialmente (solo COM)
}

public class SequentialResponse
{
    public object? ResponseBody { get; set; }
    public string? ResponseBodyFilePath { get; set; }
    public int? DelayMs { get; set; }  // Delay antes de enviar esta respuesta (en milisegundos)
}
