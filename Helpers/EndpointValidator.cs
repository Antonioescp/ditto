using Ditto.Models;
using Microsoft.Extensions.Logging;

namespace Ditto.Helpers;

public static class EndpointValidator
{
    public static bool ValidateResponseBodyExclusivity(EndpointConfiguration endpoint, ILogger logger, string endpointIdentifier = "")
    {
        if (endpoint.ResponseBody != null && !string.IsNullOrWhiteSpace(endpoint.ResponseBodyFilePath))
        {
            var identifier = string.IsNullOrWhiteSpace(endpointIdentifier) ? "endpoint" : endpointIdentifier;
            logger.LogError("El {Identifier} tiene ambos 'responseBody' y 'responseBodyFilePath' configurados. Solo uno debe estar presente.", identifier);
            return false;
        }
        return true;
    }
}
