using Ditto.Models;

namespace Ditto.Interfaces;

public interface IConfigurationLoader
{
    Task<List<ServiceConfiguration>> LoadAsync(string configPath);
}
