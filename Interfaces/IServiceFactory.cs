using Ditto.Models;

namespace Ditto.Interfaces;

public interface IServiceFactory
{
    IService CreateService(ServiceConfiguration configuration);
}
