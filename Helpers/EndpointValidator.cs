using Ditto.Models;
using Microsoft.Extensions.Logging;

namespace Ditto.Helpers;

public static class EndpointValidator
{
    public static bool ValidateResponseBodyExclusivity(EndpointConfiguration endpoint, ILogger logger, string endpointIdentifier = "")
    {
        // Validar respuestas múltiples si existen (para COM)
        if (endpoint.Responses != null && endpoint.Responses.Count > 0)
        {
            // Si hay respuestas múltiples, no debe haber responseBody/responseBodyFilePath a nivel de endpoint
            if (endpoint.ResponseBody != null || !string.IsNullOrWhiteSpace(endpoint.ResponseBodyFilePath))
            {
                var identifier = string.IsNullOrWhiteSpace(endpointIdentifier) ? "endpoint" : endpointIdentifier;
                logger.LogError("El {Identifier} tiene 'responses' configurado junto con 'responseBody' o 'responseBodyFilePath'. Use solo 'responses' para respuestas múltiples.", identifier);
                return false;
            }

            // Validar cada respuesta secuencial
            for (int i = 0; i < endpoint.Responses.Count; i++)
            {
                var response = endpoint.Responses[i];
                if (response.ResponseBody != null && !string.IsNullOrWhiteSpace(response.ResponseBodyFilePath))
                {
                    var identifier = string.IsNullOrWhiteSpace(endpointIdentifier) ? "endpoint" : endpointIdentifier;
                    logger.LogError("El {Identifier} tiene la respuesta secuencial #{Index} con ambos 'responseBody' y 'responseBodyFilePath' configurados. Solo uno debe estar presente.", identifier, i + 1);
                    return false;
                }

                if (response.ResponseBody == null && string.IsNullOrWhiteSpace(response.ResponseBodyFilePath))
                {
                    var identifier = string.IsNullOrWhiteSpace(endpointIdentifier) ? "endpoint" : endpointIdentifier;
                    logger.LogError("El {Identifier} tiene la respuesta secuencial #{Index} sin 'responseBody' ni 'responseBodyFilePath' configurado.", identifier, i + 1);
                    return false;
                }
            }

            return true;
        }

        // Validación original para respuesta única
        if (endpoint.ResponseBody != null && !string.IsNullOrWhiteSpace(endpoint.ResponseBodyFilePath))
        {
            var identifier = string.IsNullOrWhiteSpace(endpointIdentifier) ? "endpoint" : endpointIdentifier;
            logger.LogError("El {Identifier} tiene ambos 'responseBody' y 'responseBodyFilePath' configurados. Solo uno debe estar presente.", identifier);
            return false;
        }
        return true;
    }
}
