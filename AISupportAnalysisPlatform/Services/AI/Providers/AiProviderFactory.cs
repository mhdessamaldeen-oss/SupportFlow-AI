using AISupportAnalysisPlatform.Enums;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AISupportAnalysisPlatform.Services.AI.Providers
{
    /// <summary>
    /// Resolves the correct AI provider based on DB settings (from UI) with fallback to appsettings.json.
    /// </summary>
    public class AiProviderFactory : IAiProviderFactory
    {
        private readonly AiProviderSettings _settings;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AiProviderFactory> _logger;

        private readonly Lazy<DockerModelAiProvider> _dockerProvider;
        private readonly Lazy<OpenAiProvider> _openAiProvider;
        private readonly Lazy<CloudAiProvider> _cloudProvider;
        private readonly Lazy<LocalAiProvider> _localAiProvider;
        private readonly Lazy<GeminiAiProvider> _geminiProvider;

        public AiProviderFactory(
            IOptions<AiProviderSettings> settings,
            IServiceProvider serviceProvider,
            ILogger<AiProviderFactory> logger)
        {
            _settings = settings.Value;
            _serviceProvider = serviceProvider;
            _logger = logger;

            _dockerProvider = new Lazy<DockerModelAiProvider>(() =>
                ActivatorUtilities.CreateInstance<DockerModelAiProvider>(_serviceProvider));

            _openAiProvider = new Lazy<OpenAiProvider>(() =>
                ActivatorUtilities.CreateInstance<OpenAiProvider>(_serviceProvider));

            _cloudProvider = new Lazy<CloudAiProvider>(() =>
                ActivatorUtilities.CreateInstance<CloudAiProvider>(_serviceProvider));

            _localAiProvider = new Lazy<LocalAiProvider>(() =>
                ActivatorUtilities.CreateInstance<LocalAiProvider>(_serviceProvider));

            _geminiProvider = new Lazy<GeminiAiProvider>(() =>
                ActivatorUtilities.CreateInstance<GeminiAiProvider>(_serviceProvider));
        }

        public AiProviderType ActiveProviderType => ResolveActiveProviderType();

        public IAiProvider GetActiveProvider()
        {
            var providerType = ResolveActiveProviderType();
            return GetProvider(providerType);
        }

        public IAiProvider GetProvider(AiProviderType providerType)
        {
            return providerType switch
            {
                AiProviderType.DockerLocal => _dockerProvider.Value,
                AiProviderType.OpenAI => _openAiProvider.Value,
                AiProviderType.Cloud => _cloudProvider.Value,
                AiProviderType.LocalAI => _localAiProvider.Value,
                AiProviderType.Gemini => _geminiProvider.Value,
                _ => throw new ArgumentException($"Unsupported AI provider type: {providerType}")
            };
        }

        public async Task<(bool IsValid, string? ErrorMessage)> ValidateActiveProviderAsync()
        {
            try
            {
                var provider = GetActiveProvider();
                return await provider.ValidateConfigurationAsync();
            }
            catch (Exception ex)
            {
                return (false, $"Failed to initialize {ActiveProviderType} provider: {ex.Message}");
            }
        }

        private AiProviderType ResolveActiveProviderType()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var dbSetting = db.SystemSettings.FirstOrDefault(s => s.Key == SettingKeys.AiActiveProvider);
                if (dbSetting != null && !string.IsNullOrWhiteSpace(dbSetting.Value))
                {
                    return dbSetting.Value.Trim() switch
                    {
                        AiProviderNames.DockerLocal or AiProviderNames.LegacyDockerLocalAlias => AiProviderType.DockerLocal,
                        "OpenAI" or "GPT" => AiProviderType.OpenAI,
                        "Cloud" => AiProviderType.Cloud,
                        "LocalAI" => AiProviderType.LocalAI,
                        "Gemini" => AiProviderType.Gemini,
                        _ => _settings.GetActiveProviderType()
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read AiActiveProvider from DB.");
            }

            return _settings.GetActiveProviderType();
        }
    }
}

