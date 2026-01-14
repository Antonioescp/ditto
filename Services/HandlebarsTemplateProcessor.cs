using System.Text.Json;
using Ditto.Interfaces;
using HandlebarsDotNet;
using System.Linq;

namespace Ditto.Services;

public class HandlebarsTemplateProcessor : ITemplateProcessor
{
    public object ProcessTemplate(object template, object context)
    {
        if (template == null)
            throw new ArgumentNullException(nameof(template));

        // Convertir el template a string JSON
        var templateJson = JsonSerializer.Serialize(template);
        
        // Procesar el template con Handlebars
        var handlebarsTemplate = Handlebars.Compile(templateJson);
        var resultJson = handlebarsTemplate(context);
        
        // Deserializar el resultado JSON de vuelta a objeto
        var result = JsonSerializer.Deserialize<object>(resultJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        // Procesar recursivamente para deserializar JSON strings anidados
        return ProcessRecursively(result, context) ?? new object();
    }

    private object ProcessRecursively(object? value, object context)
    {
        if (value == null)
            return null!;

        return value switch
        {
            JsonElement element => ProcessJsonElement(element, context),
            Dictionary<string, object> dict => ProcessDictionary(dict, context),
            List<object> list => ProcessList(list, context),
            string str => ProcessString(str, context),
            _ => value
        };
    }

    private object ProcessJsonElement(JsonElement element, object context)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ProcessDictionary(
                element.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value),
                context),
            JsonValueKind.Array => ProcessList(
                element.EnumerateArray().Select(e => (object)e).ToList(),
                context),
            JsonValueKind.String => ProcessString(element.GetString() ?? string.Empty, context),
            _ => JsonSerializer.Deserialize<object>(element.GetRawText()) ?? new object()
        };
    }

    private Dictionary<string, object> ProcessDictionary(Dictionary<string, object> dict, object context)
    {
        var result = new Dictionary<string, object>();
        foreach (var kvp in dict)
        {
            result[kvp.Key] = ProcessRecursively(kvp.Value, context);
        }
        return result;
    }

    private List<object> ProcessList(List<object> list, object context)
    {
        return list.Select(item => ProcessRecursively(item, context)).ToList();
    }

    private object ProcessString(string str, object context)
    {
        // Si el string parece ser un JSON válido (array u objeto), intentar deserializarlo
        if (!string.IsNullOrWhiteSpace(str))
        {
            var trimmed = str.TrimStart();
            if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
            {
                try
                {
                    var jsonResult = JsonSerializer.Deserialize<object>(str, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    
                    if (jsonResult != null)
                    {
                        // Procesar recursivamente el resultado deserializado
                        return ProcessRecursively(jsonResult, context);
                    }
                }
                catch (JsonException)
                {
                    // No es JSON válido, retornar como string
                }
            }
        }
        
        return str;
    }
}
