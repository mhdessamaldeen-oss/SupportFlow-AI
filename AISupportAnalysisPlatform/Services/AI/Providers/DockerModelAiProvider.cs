using AISupportAnalysisPlatform.Enums;
using System.Text;
using System.Text.Json;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace AISupportAnalysisPlatform.Services.AI.Providers
{
    /// <summary>
    /// AI provider implementation that uses 'docker model run' CLI.
    /// No longer requires a running local server.
    /// </summary>
    public class DockerModelAiProvider : IAiProvider
    {
        private readonly DockerLocalProviderOptions _options;
        private readonly ILogger<DockerModelAiProvider> _logger;
        private readonly IServiceProvider _serviceProvider;

        public AiProviderType ProviderType => AiProviderType.DockerLocal;

        public DockerModelAiProvider(
            IOptions<AiProviderSettings> settings,
            ILogger<DockerModelAiProvider> logger,
            IServiceProvider serviceProvider)
        {
            _options = settings.Value.DockerLocal;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        private string GetActiveModel()
        {
            try
            {
                var configuredModel = GetPersistedModelOverride() ?? _options.Model;
                var installedModels = GetInstalledDockerModels();

                if (!string.IsNullOrWhiteSpace(configuredModel) &&
                    installedModels.Any(model => string.Equals(model, configuredModel, StringComparison.OrdinalIgnoreCase)))
                {
                    return configuredModel;
                }

                if (installedModels.Count > 0)
                {
                    return installedModels[0];
                }

                return configuredModel;
            }
            catch
            {
                return _options.Model;
            }
        }

        private string? GetPersistedModelOverride()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetService<ApplicationDbContext>();
                if (dbContext == null)
                {
                    return null;
                }

                return dbContext.SystemSettings
                    .AsNoTracking()
                    .Where(setting => setting.Key == SettingKeys.DockerModel)
                    .Select(setting => setting.Value)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not load persisted Docker model override.");
                return null;
            }
        }

        public string ModelName => GetActiveModel();

        public async Task<AiProviderResult> GenerateAsync(string prompt)
        {
            var activeModel = GetActiveModel();
            _logger.LogInformation("Executing Direct Docker Model '{Model}' ({Length} chars)", activeModel, prompt.Length);

            try 
            {
                var processInfo = new ProcessStartInfo("docker")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                processInfo.ArgumentList.Add("model");
                processInfo.ArgumentList.Add("run");
                processInfo.ArgumentList.Add(activeModel);
                processInfo.ArgumentList.Add("--");

                using var process = Process.Start(processInfo);
                if (process == null) throw new Exception("Failed to start docker process");

                // CRITICAL: Start reading stdout/stderr BEFORE writing to stdin.
                // This prevents the classic .NET Process deadlock where the OS pipe
                // buffer fills up, blocking the child process, while we're blocked
                // waiting for exit.
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                // Write the prompt to stdin and close it to signal EOF
                await process.StandardInput.WriteAsync(prompt);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();

                // Wait for the process to exit with a timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
                try 
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Docker model execution timed out after {Timeout}s", _options.TimeoutSeconds);
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    return new AiProviderResult { Success = false, Error = $"Docker model execution timed out after {_options.TimeoutSeconds}s.", ProviderType = AiProviderType.DockerLocal };
                }

                // Now safely read the completed output
                var responseText = await outputTask;
                var errorText = await errorTask;

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning("Docker model exited with code {ExitCode}: {Error}", process.ExitCode, errorText);
                    return new AiProviderResult { Success = false, Error = $"Docker error (exit {process.ExitCode}): {errorText}", ProviderType = AiProviderType.DockerLocal };
                }

                if (string.IsNullOrWhiteSpace(responseText))
                {
                    _logger.LogWarning("Docker model returned empty response. Stderr: {Error}", errorText);
                    return new AiProviderResult { Success = false, Error = "Docker model returned an empty response.", ProviderType = AiProviderType.DockerLocal };
                }

                _logger.LogInformation("Docker model responded successfully ({Length} chars)", responseText.Length);
                return new AiProviderResult
                {
                    Success = true,
                    ResponseText = responseText.Trim(),
                    ProviderType = AiProviderType.DockerLocal
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing docker model");
                return new AiProviderResult { Success = false, Error = ex.Message, ProviderType = AiProviderType.DockerLocal };
            }
        }

        public Task<float[]> GetEmbeddingAsync(string text)
        {
            // Direct Docker CLI doesn't natively support embeddings as easily as a REST API
            _logger.LogWarning("GetEmbeddingAsync not implemented for DockerModelAiProvider.");
            return Task.FromResult(Array.Empty<float>());
        }

        public async Task<(bool IsValid, string? ErrorMessage)> ValidateConfigurationAsync()
        {
            try
            {
                var processInfo = new ProcessStartInfo("docker")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                processInfo.ArgumentList.Add("model");
                processInfo.ArgumentList.Add("ls");

                using var process = Process.Start(processInfo);
                if (process == null) return (false, "Could not start Docker process.");
                
                await process.WaitForExitAsync();
                if (process.ExitCode == 0) return (true, null);

                var error = await process.StandardError.ReadToEndAsync();
                return (false, $"Docker CLI error: {error}");
            }
            catch (Exception ex)
            {
                return (false, $"Docker model infrastructure check failed: {ex.Message}");
            }
        }

        private List<string> GetInstalledDockerModels()
        {
            var processInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            processInfo.ArgumentList.Add("model");
            processInfo.ArgumentList.Add("ls");

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                return new List<string>();
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return new List<string>();
            }

            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
    }
}
