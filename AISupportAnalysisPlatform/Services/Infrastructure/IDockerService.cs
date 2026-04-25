namespace AISupportAnalysisPlatform.Services.Infrastructure;

public interface IDockerService
{
    Task<bool> IsDockerInstalledAsync();
    Task<bool> IsDockerRunningAsync();
    Task<(bool Success, string Message)> LaunchEngineAsync(string engineType);
}
