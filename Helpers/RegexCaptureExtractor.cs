using System.Text.RegularExpressions;

namespace Ditto.Helpers;

public static class RegexCaptureExtractor
{
    public static Dictionary<string, object> ExtractNamedGroups(Match match)
    {
        var capturesDict = new Dictionary<string, object>();

        // Extraer grupos nombrados
        foreach (var groupName in match.Groups.Keys)
        {
            // Ignorar grupos numéricos, solo tomar grupos nombrados
            if (int.TryParse(groupName, out _))
                continue;

            var group = match.Groups[groupName];
            if (group.Success)
            {
                capturesDict[groupName] = group.Value;
            }
        }

        // Si no hay grupos nombrados, agregar grupos numéricos
        if (capturesDict.Count == 0 && match.Groups.Count > 1)
        {
            for (int i = 1; i < match.Groups.Count; i++)
            {
                var group = match.Groups[i];
                if (group.Success)
                {
                    capturesDict[$"group{i}"] = group.Value;
                }
            }
        }

        return capturesDict;
    }
}
