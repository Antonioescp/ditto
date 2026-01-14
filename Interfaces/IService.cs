namespace Ditto.Interfaces;

public interface IService
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    bool IsRunning { get; }
    string Name { get; }
}
