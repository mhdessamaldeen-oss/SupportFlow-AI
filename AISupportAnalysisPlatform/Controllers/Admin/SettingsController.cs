using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Enums;
using AISupportAnalysisPlatform.Models;
using AISupportAnalysisPlatform.Services.AI.Providers;
using AISupportAnalysisPlatform.Services.Infrastructure;
using AISupportAnalysisPlatform.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Diagnostics;

using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI;
using AISupportAnalysisPlatform.Models.Common;
using AISupportAnalysisPlatform.Models.DTOs;
using AutoMapper;

namespace AISupportAnalysisPlatform.Controllers.Admin
{
    [Authorize(Roles = RoleNames.Admin)]
    public class SettingsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _config;
        private readonly IAiProviderFactory _providerFactory;
        private readonly AiProviderSettings _providerSettings;
        private readonly IDockerService _dockerService;
        private readonly IServiceProvider _serviceProvider;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IRuntimeDatabaseTargetService _runtimeDatabaseTargetService;
        private readonly IMapper _mapper;
        private const string SqlServerCandidatesTempDataKey = "SqlServerCandidates";
        private const string SqlServerProbeTempDataKey = "SqlServerProbe";
        private const string SqlServerLastServerNameTempDataKey = "SqlServerLastServerName";
        private const string SqlServerLastUseSqlAuthTempDataKey = "SqlServerLastUseSqlAuth";
        private const string SqlServerLastUserNameTempDataKey = "SqlServerLastUserName";

        private readonly CopilotToolRegistryService _toolRegistry;

        public SettingsController(
            ApplicationDbContext context,
            IConfiguration config,
            IAiProviderFactory providerFactory,
            IOptions<AiProviderSettings> providerSettings,
            IDockerService dockerService,
            IServiceProvider serviceProvider,
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager,
            IRuntimeDatabaseTargetService runtimeDatabaseTargetService,
            CopilotToolRegistryService toolRegistry,
            IMapper mapper)
        {
            _context = context;
            _config = config;
            _providerFactory = providerFactory;
            _providerSettings = providerSettings.Value;
            _dockerService = dockerService;
            _serviceProvider = serviceProvider;
            _userManager = userManager;
            _roleManager = roleManager;
            _runtimeDatabaseTargetService = runtimeDatabaseTargetService;
            _toolRegistry = toolRegistry;
            _mapper = mapper;
        }

        public async Task<IActionResult> Index(string tab = "themes")
        {
            var themes = await _context.CustomThemes.OrderBy(t => t.IsSystemTheme).ThenBy(t => t.Id).ToListAsync();
            var themeSetting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DefaultTheme);
            
            ViewBag.CurrentThemeId = themeSetting?.Value ?? "1";
            ViewBag.DockerBaseUrl = _providerSettings.DockerLocal.BaseUrl;
            ViewBag.ActiveTab = tab;

            // Copilot Tools
            ViewBag.CopilotTools = await _toolRegistry.GetAllToolsAsync();

            // Provider configuration for the AI tab
            ViewBag.ActiveProvider = _providerSettings.ActiveProvider;

            // Provider configuration for the AI tab
            ViewBag.ActiveProvider = _providerSettings.ActiveProvider;
            ViewBag.ActiveProviderType = _providerSettings.GetActiveProviderType();
            ViewBag.DockerSettings = _providerSettings.DockerLocal;
            ViewBag.OpenAiSettings = _providerSettings.OpenAI;
            ViewBag.CloudSettings = _providerSettings.Cloud;
            ViewBag.LocalAiSettings = _providerSettings.LocalAI;

            // Read DB-stored overrides
            var dbProvider = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AiActiveProvider);
            var dbDockerBaseUrl = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerBaseUrl);
            var dbDockerModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerModel);
            var dbDockerTimeoutSeconds = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerTimeoutSeconds);
            var dbDockerMaxPromptChars = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerMaxPromptChars);
            var dbDockerMaxPromptTokens = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerMaxPromptTokens);
            var dbDockerTemperature = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerTemperature);
            var dbDockerNumCtx = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerNumCtx);
            var dbDockerNumPredict = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerNumPredict);

            var dbOpenAiKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiApiKey);
            var dbOpenAiModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiModel);
            var dbOpenAiBaseUrl = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiBaseUrl);
            var dbOpenAiTimeoutSeconds = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiTimeoutSeconds);
            var dbOpenAiMaxPromptChars = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiMaxPromptChars);
            var dbOpenAiTemperature = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiTemperature);
            var dbOpenAiMaxTokens = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiMaxTokens);
            var dbOpenAiOrganizationId = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiOrganizationId);
            var dbOpenAiProjectId = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiProjectId);
            var dbCloudEndpoint = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudEndpoint);
            var dbCloudApiKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudApiKey);
            var dbCloudModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudModel);
            var dbCloudDeployment = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudDeploymentName);
            var dbCloudTimeoutSeconds = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudTimeoutSeconds);
            var dbCloudMaxPromptChars = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudMaxPromptChars);
            var dbCloudTemperature = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudTemperature);
            var dbCloudMaxTokens = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudMaxTokens);
            var dbCloudApiVersion = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudApiVersion);
            var dbCloudAuthHeaderName = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudAuthHeaderName);
            var dbCloudUseBearerToken = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudUseBearerToken);
            var dbLocalAiBaseUrl = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiBaseUrl);
            var dbLocalAiModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiModel);
            var dbLocalAiApiKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiApiKey);
            var dbLocalAiTimeoutSeconds = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiTimeoutSeconds);
            var dbLocalAiMaxPromptChars = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiMaxPromptChars);
            var dbLocalAiTemperature = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiTemperature);
            var dbGeminiKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiApiKey);
            var dbGeminiModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiModel);
            var dbGeminiTemperature = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiTemperature);
            var dbGeminiMaxTokens = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiMaxTokens);

            ViewBag.DbActiveProvider = _providerFactory.ActiveProviderType;
            ViewBag.DbDockerBaseUrl = dbDockerBaseUrl?.Value ?? _providerSettings.DockerLocal.BaseUrl;
            ViewBag.DbDockerModel = dbDockerModel?.Value ?? _providerSettings.DockerLocal.Model;
            ViewBag.DbDockerTimeoutSeconds = dbDockerTimeoutSeconds?.Value ?? _providerSettings.DockerLocal.TimeoutSeconds.ToString();
            ViewBag.DbDockerMaxPromptChars = dbDockerMaxPromptChars?.Value ?? _providerSettings.DockerLocal.MaxPromptChars.ToString();
            ViewBag.DbDockerMaxPromptTokens = dbDockerMaxPromptTokens?.Value ?? _providerSettings.DockerLocal.MaxPromptTokens.ToString();
            ViewBag.DbDockerTemperature = dbDockerTemperature?.Value ?? _providerSettings.DockerLocal.Temperature.ToString("0.##");
            ViewBag.DbDockerNumCtx = dbDockerNumCtx?.Value ?? _providerSettings.DockerLocal.NumCtx.ToString();
            ViewBag.DbDockerNumPredict = dbDockerNumPredict?.Value ?? _providerSettings.DockerLocal.NumPredict.ToString();

            ViewBag.DbOpenAiKey = dbOpenAiKey?.Value ?? "";
            ViewBag.DbOpenAiModel = dbOpenAiModel?.Value ?? _providerSettings.OpenAI.Model;
            ViewBag.DbOpenAiBaseUrl = dbOpenAiBaseUrl?.Value ?? _providerSettings.OpenAI.BaseUrl;
            ViewBag.DbOpenAiTimeoutSeconds = dbOpenAiTimeoutSeconds?.Value ?? _providerSettings.OpenAI.TimeoutSeconds.ToString();
            ViewBag.DbOpenAiMaxPromptChars = dbOpenAiMaxPromptChars?.Value ?? _providerSettings.OpenAI.MaxPromptChars.ToString();
            ViewBag.DbOpenAiTemperature = dbOpenAiTemperature?.Value ?? _providerSettings.OpenAI.Temperature.ToString("0.##");
            ViewBag.DbOpenAiMaxTokens = dbOpenAiMaxTokens?.Value ?? _providerSettings.OpenAI.MaxTokens.ToString();
            ViewBag.DbOpenAiOrganizationId = dbOpenAiOrganizationId?.Value ?? _providerSettings.OpenAI.OrganizationId ?? "";
            ViewBag.DbOpenAiProjectId = dbOpenAiProjectId?.Value ?? _providerSettings.OpenAI.ProjectId ?? "";
            ViewBag.DbCloudEndpoint = dbCloudEndpoint?.Value ?? _providerSettings.Cloud.Endpoint;
            ViewBag.DbCloudApiKey = dbCloudApiKey?.Value ?? "";
            ViewBag.DbCloudModel = dbCloudModel?.Value ?? _providerSettings.Cloud.Model;
            ViewBag.DbCloudDeployment = dbCloudDeployment?.Value ?? _providerSettings.Cloud.DeploymentName ?? "";
            ViewBag.DbCloudTimeoutSeconds = dbCloudTimeoutSeconds?.Value ?? _providerSettings.Cloud.TimeoutSeconds.ToString();
            ViewBag.DbCloudMaxPromptChars = dbCloudMaxPromptChars?.Value ?? _providerSettings.Cloud.MaxPromptChars.ToString();
            ViewBag.DbCloudTemperature = dbCloudTemperature?.Value ?? _providerSettings.Cloud.Temperature.ToString("0.##");
            ViewBag.DbCloudMaxTokens = dbCloudMaxTokens?.Value ?? _providerSettings.Cloud.MaxTokens.ToString();
            ViewBag.DbCloudApiVersion = dbCloudApiVersion?.Value ?? _providerSettings.Cloud.ApiVersion ?? "";
            ViewBag.DbCloudAuthHeaderName = dbCloudAuthHeaderName?.Value ?? _providerSettings.Cloud.AuthHeaderName;
            ViewBag.DbCloudUseBearerToken = bool.TryParse(dbCloudUseBearerToken?.Value, out var dbUseBearer)
                ? dbUseBearer
                : _providerSettings.Cloud.UseBearerToken;
            ViewBag.DbLocalAiBaseUrl = dbLocalAiBaseUrl?.Value ?? _providerSettings.LocalAI.BaseUrl;
            ViewBag.DbLocalAiModel = dbLocalAiModel?.Value ?? _providerSettings.LocalAI.Model;
            ViewBag.DbLocalAiApiKey = dbLocalAiApiKey?.Value ?? _providerSettings.LocalAI.ApiKey;
            ViewBag.DbLocalAiTimeoutSeconds = dbLocalAiTimeoutSeconds?.Value ?? _providerSettings.LocalAI.TimeoutSeconds.ToString();
            ViewBag.DbLocalAiMaxPromptChars = dbLocalAiMaxPromptChars?.Value ?? _providerSettings.LocalAI.MaxPromptChars.ToString();
            ViewBag.DbLocalAiTemperature = dbLocalAiTemperature?.Value ?? _providerSettings.LocalAI.Temperature.ToString("0.##");
            ViewBag.DbGeminiKey = dbGeminiKey?.Value ?? "";
            ViewBag.DbGeminiModel = dbGeminiModel?.Value ?? "gemini-1.5-flash";
            ViewBag.DbGeminiTemperature = dbGeminiTemperature?.Value ?? _providerSettings.Cloud.Temperature.ToString("0.##");
            ViewBag.DbGeminiMaxTokens = dbGeminiMaxTokens?.Value ?? _providerSettings.Cloud.MaxTokens.ToString();
            ViewBag.TicketCount = await _context.Tickets.CountAsync();
            ViewBag.TicketCommentCount = await _context.TicketComments.CountAsync();
            ViewBag.TicketAttachmentCount = await _context.TicketAttachments.CountAsync();
            ViewBag.TicketHistoryCount = await _context.TicketHistories.CountAsync();
            ViewBag.NotificationCount = await _context.Notifications.CountAsync();
            ViewBag.AiAnalysisCount = await _context.TicketAiAnalyses.CountAsync();
            ViewBag.EmbeddingCount = await _context.TicketSemanticEmbeddings.CountAsync();
            var currentRuntimeTarget = _runtimeDatabaseTargetService.GetCurrent();
            ViewBag.CurrentConnectionString = currentRuntimeTarget.ConnectionString;
            ViewBag.CurrentRuntimeDatabaseProvider = currentRuntimeTarget.Provider.ToString();

            var currentConnectionBuilder = BuildConnectionStringBuilder(ViewBag.CurrentConnectionString);
            ViewBag.DbServerName = TempData.Peek(SqlServerLastServerNameTempDataKey) as string ?? currentConnectionBuilder.DataSource;
            ViewBag.DbDatabaseName = currentConnectionBuilder.InitialCatalog;
            ViewBag.DbUseSqlAuthentication = bool.TryParse(TempData.Peek(SqlServerLastUseSqlAuthTempDataKey) as string, out var useSqlAuth)
                ? useSqlAuth
                : !currentConnectionBuilder.IntegratedSecurity;
            ViewBag.DbUserName = TempData.Peek(SqlServerLastUserNameTempDataKey) as string ?? (!currentConnectionBuilder.IntegratedSecurity ? currentConnectionBuilder.UserID : "");
            ViewBag.DbTrustServerCertificate = currentConnectionBuilder.TrustServerCertificate;
            ViewBag.DbEncryptConnection = currentConnectionBuilder.Encrypt;
            ViewBag.SqlServerCandidates = DeserializeTempData<List<SqlServerCandidateInfo>>(SqlServerCandidatesTempDataKey) ?? new List<SqlServerCandidateInfo>();
            ViewBag.SqlServerProbe = DeserializeTempData<SqlServerProbeResult>(SqlServerProbeTempDataKey);

            var externalApis = await _context.ExternalApiSettings.OrderBy(a => a.Title).ToListAsync();
            ViewBag.ExternalApis = externalApis;

            var copilotTools = await _toolRegistry.GetAllToolsAsync();
            ViewBag.CopilotTools = copilotTools;

            return View(themes);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SeedData()
        {
            try
            {
                await DbSeeder.SeedOperationalDataAsync(_serviceProvider, _userManager, _roleManager);
                TempData["Success"] = "Operational seed data loaded successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Data seed failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> FlushData()
        {
            try
            {
                await DbSeeder.PurgeOperationalDataAsync(_serviceProvider);
                TempData["Success"] = "Operational data flushed successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Data flush failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> FlushAndSeedData()
        {
            try
            {
                await DbSeeder.PurgeOperationalDataAsync(_serviceProvider);
                await DbSeeder.SeedOperationalDataAsync(_serviceProvider, _userManager, _roleManager);
                TempData["Success"] = "Operational data flushed and reseeded successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Flush and seed failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActivateRuntimeDatabaseTarget(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                TempData["Error"] = "Database connection string is required.";
                return RedirectToAction(nameof(Index), new { tab = "data" });
            }

            try
            {
                var target = BuildSqlServerTarget(connectionString);
                await ActivateRuntimeTargetAsync(target, runMigrations: false);
                TempData["Success"] = "Runtime database target switched successfully. It is not persisted and will reset after app restart.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Runtime target switch failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DiscoverSqlServers()
        {
            try
            {
                var candidates = await DiscoverSqlServerCandidatesAsync();
                StoreTempData(SqlServerCandidatesTempDataKey, candidates);
                TempData["Success"] = candidates.Count > 0
                    ? $"Detected {candidates.Count} reachable SQL Server instance(s)."
                    : "No reachable local SQL Server instances were detected. You can still type a server name manually.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"SQL Server discovery failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> InspectSqlServer(string serverName, bool useSqlAuthentication = false, string userName = "", string password = "")
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                TempData["Error"] = "Server name is required.";
                return RedirectToAction(nameof(Index), new { tab = "data" });
            }

            TempData[SqlServerLastServerNameTempDataKey] = serverName.Trim();
            TempData[SqlServerLastUseSqlAuthTempDataKey] = useSqlAuthentication.ToString();
            TempData[SqlServerLastUserNameTempDataKey] = userName?.Trim() ?? "";

            try
            {
                var probe = await ProbeSqlServerAsync(serverName.Trim(), useSqlAuthentication, userName ?? "", password ?? "");
                StoreTempData(SqlServerProbeTempDataKey, probe);
                TempData["Success"] = $"Connected to SQL Server '{probe.ServerName}'.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Server inspection failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MigrateDatabase(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                TempData["Error"] = "Database connection string is required for migration.";
                return RedirectToAction(nameof(Index), new { tab = "data" });
            }

            try
            {
                var target = BuildSqlServerTarget(connectionString);
                await ActivateRuntimeTargetAsync(target, runMigrations: true);
                TempData["Success"] = "Database migrated and activated as the current runtime target. Nothing was written to appsettings.json.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Database migration failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        // ─── AI Provider Management ───────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetActiveProvider(string providerType)
        {
            if (string.IsNullOrWhiteSpace(providerType))
                return BadRequest("Provider type is required");

            // Validate provider type
            var validTypes = new[] { AiProviderNames.DockerLocal, AiProviderNames.LegacyDockerLocalAlias, AiProviderNames.OpenAI, AiProviderNames.Cloud, AiProviderNames.LocalAI, AiProviderNames.Gemini };
            if (!validTypes.Contains(providerType))
                return BadRequest($"Invalid provider type: {providerType}");

            // Map old type to new if necessary
            if (providerType == AiProviderNames.LegacyDockerLocalAlias) providerType = AiProviderNames.DockerLocal;

            await UpsertSystemSettingAsync(SettingKeys.AiActiveProvider, providerType);

            TempData["Success"] = $"AI Provider switched to: {providerType}";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveOpenAiConfig(string apiKey, string model, string baseUrl, int? timeoutSeconds, int? maxPromptChars, double? temperature, int? maxTokens, string organizationId, string projectId)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                await UpsertSystemSettingAsync(SettingKeys.OpenAiApiKey, apiKey);
            if (!string.IsNullOrWhiteSpace(model))
                await UpsertSystemSettingAsync(SettingKeys.OpenAiModel, model);
            if (!string.IsNullOrWhiteSpace(baseUrl))
                await UpsertSystemSettingAsync(SettingKeys.OpenAiBaseUrl, baseUrl);
            if (timeoutSeconds.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.OpenAiTimeoutSeconds, timeoutSeconds.Value.ToString());
            if (maxPromptChars.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.OpenAiMaxPromptChars, maxPromptChars.Value.ToString());
            if (temperature.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.OpenAiTemperature, temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (maxTokens.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.OpenAiMaxTokens, maxTokens.Value.ToString());
            await UpsertSystemSettingAsync(SettingKeys.OpenAiOrganizationId, organizationId ?? "");
            await UpsertSystemSettingAsync(SettingKeys.OpenAiProjectId, projectId ?? "");

            TempData["Success"] = "OpenAI / ChatGPT settings saved to the database.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCloudConfig(string endpoint, string apiKey, string model, string deploymentName, int? timeoutSeconds, int? maxPromptChars, double? temperature, int? maxTokens, string apiVersion, string authHeaderName, bool useBearerToken)
        {
            if (!string.IsNullOrWhiteSpace(endpoint))
                await UpsertSystemSettingAsync(SettingKeys.CloudEndpoint, endpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
                await UpsertSystemSettingAsync(SettingKeys.CloudApiKey, apiKey);
            if (!string.IsNullOrWhiteSpace(model))
                await UpsertSystemSettingAsync(SettingKeys.CloudModel, model);
            if (timeoutSeconds.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.CloudTimeoutSeconds, timeoutSeconds.Value.ToString());
            if (maxPromptChars.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.CloudMaxPromptChars, maxPromptChars.Value.ToString());
            if (temperature.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.CloudTemperature, temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (maxTokens.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.CloudMaxTokens, maxTokens.Value.ToString());

            await UpsertSystemSettingAsync(SettingKeys.CloudDeploymentName, deploymentName ?? "");
            await UpsertSystemSettingAsync(SettingKeys.CloudApiVersion, apiVersion ?? "");
            await UpsertSystemSettingAsync(SettingKeys.CloudAuthHeaderName, authHeaderName ?? "api-key");
            await UpsertSystemSettingAsync(SettingKeys.CloudUseBearerToken, useBearerToken.ToString());

            TempData["Success"] = "Cloud AI settings saved to the database.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveLocalAiConfig(string baseUrl, string model, string apiKey, int? timeoutSeconds, int? maxPromptChars, double? temperature)
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
                await UpsertSystemSettingAsync(SettingKeys.LocalAiBaseUrl, baseUrl);
            if (!string.IsNullOrWhiteSpace(model))
                await UpsertSystemSettingAsync(SettingKeys.LocalAiModel, model);
            await UpsertSystemSettingAsync(SettingKeys.LocalAiApiKey, apiKey ?? "");
            if (timeoutSeconds.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.LocalAiTimeoutSeconds, timeoutSeconds.Value.ToString());
            if (maxPromptChars.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.LocalAiMaxPromptChars, maxPromptChars.Value.ToString());
            if (temperature.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.LocalAiTemperature, temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            TempData["Success"] = "LocalAI settings saved to the database.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveGeminiConfig(string apiKey, string model, double? temperature, int? maxTokens)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                await UpsertSystemSettingAsync(SettingKeys.GeminiApiKey, apiKey);
            if (!string.IsNullOrWhiteSpace(model))
                await UpsertSystemSettingAsync(SettingKeys.GeminiModel, model);
            if (temperature.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.GeminiTemperature, temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (maxTokens.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.GeminiMaxTokens, maxTokens.Value.ToString());

            TempData["Success"] = "Gemini settings saved to the database.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDockerLocalConfig(string baseUrl, int? timeoutSeconds, int? maxPromptChars, int? maxPromptTokens, double? temperature, int? numCtx, int? numPredict)
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
                await UpsertSystemSettingAsync(SettingKeys.DockerBaseUrl, baseUrl);
            if (timeoutSeconds.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.DockerTimeoutSeconds, timeoutSeconds.Value.ToString());
            if (maxPromptChars.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.DockerMaxPromptChars, maxPromptChars.Value.ToString());
            if (maxPromptTokens.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.DockerMaxPromptTokens, maxPromptTokens.Value.ToString());
            if (temperature.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.DockerTemperature, temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (numCtx.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.DockerNumCtx, numCtx.Value.ToString());
            if (numPredict.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.DockerNumPredict, numPredict.Value.ToString());

            TempData["Success"] = "Docker engine settings saved. Default model is managed from the installed model list.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpGet]
        public async Task<IActionResult> ValidateProvider(string providerType)
        {
            try
            {
                if (providerType == AiProviderNames.DockerLocal || providerType == AiProviderNames.LegacyDockerLocalAlias)
                {
                    var result = await _dockerService.IsDockerRunningAsync();
                    if (result)
                        return Json(ApiResponse.Ok("Docker Engine is active and responding."));
                    return Json(ApiResponse.Fail("Docker Engine is not running."));
                }
                else if (providerType == AiProviderNames.OpenAI)
                {
                    var dbKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiApiKey);
                    var apiKey = dbKey?.Value ?? _providerSettings.OpenAI.ApiKey;
                    if (string.IsNullOrWhiteSpace(apiKey))
                        return Json(ApiResponse.Fail("API Key is not configured"));

                    var dbBaseUrl = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiBaseUrl);
                    var baseUrl = dbBaseUrl?.Value ?? _providerSettings.OpenAI.BaseUrl;

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                    var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/models");
                    if (response.IsSuccessStatusCode)
                        return Json(ApiResponse.Ok("OpenAI API connection verified"));
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        return Json(ApiResponse.Fail("API Key is invalid or expired"));
                    return Json(ApiResponse.Fail($"OpenAI returned HTTP {(int)response.StatusCode}"));
                }
                else if (providerType == AiProviderNames.Cloud)
                {
                    var dbEndpoint = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudEndpoint);
                    var endpoint = dbEndpoint?.Value ?? _providerSettings.Cloud.Endpoint;
                    if (string.IsNullOrWhiteSpace(endpoint))
                        return Json(ApiResponse.Fail("Cloud endpoint is not configured"));

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var response = await http.GetAsync(endpoint);
                    return Json(ApiResponse.Ok($"Cloud endpoint reachable at {endpoint}"));
                }
                else if (providerType == AiProviderNames.LocalAI)
                {
                    var dbBaseUrl = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiBaseUrl);
                    var baseUrl = dbBaseUrl?.Value ?? _providerSettings.LocalAI.BaseUrl;

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/models");
                    if (response.IsSuccessStatusCode)
                        return Json(ApiResponse.Ok($"Connected to LocalAI at {baseUrl}"));
                    return Json(ApiResponse.Fail($"LocalAI returned HTTP {(int)response.StatusCode}"));
                }
                else if (providerType == AiProviderNames.Gemini)
                {
                    var dbKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiApiKey);
                    var apiKey = dbKey?.Value ?? "";
                    if (string.IsNullOrWhiteSpace(apiKey))
                        return Json(ApiResponse.Fail("Gemini API Key is not configured"));

                    var dbModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiModel);
                    var model = dbModel?.Value ?? "gemini-1.5-flash";

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}?key={apiKey}";
                    var response = await http.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                        return Json(ApiResponse.Ok($"Gemini model '{model}' verified"));
                    
                    var error = await response.Content.ReadAsStringAsync();
                    return Json(ApiResponse.Fail($"Gemini returned HTTP {(int)response.StatusCode}: {error}"));
                }

                return Json(ApiResponse.Fail("Unknown provider type"));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail($"Connection failed: {ex.Message}"));
            }
        }

        // ─── AI Model Management (Docker) ──────────────────────────

        [HttpGet]
        public async Task<IActionResult> GetDockerModels()
        {
            try
            {
                var result = await _dockerService.IsDockerInstalledAsync();
                if (!result) return Json(ApiResponse.Fail("Docker is not installed or accessible."));

                var models = new List<DockerModelDto>();

                // Use Docker Models as the source of truth for selectable runtime models.
                var modelInfo = new ProcessStartInfo("docker")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                modelInfo.ArgumentList.Add("model");
                modelInfo.ArgumentList.Add("ls");

                using (var process = Process.Start(modelInfo))
                {
                    if (process != null)
                    {
                        string output = await process.StandardOutput.ReadToEndAsync();
                        await process.WaitForExitAsync();
                        
                        // Skip header and parse lines
                        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 1)
                        {
                            // Headers: MODEL NAME  PARAMETERS  QUANTIZATION    ARCHITECTURE  MODEL ID      CREATED        CONTEXT  SIZE
                            foreach (var line in lines.Skip(1))
                            {
                                // Split by multiple spaces or tabs
                                var parts = System.Text.RegularExpressions.Regex.Split(line.Trim(), @"\s{2,}");
                                if (parts.Length >= 5)
                                {
                                    models.Add(new DockerModelDto
                                    {
                                        Name = parts[0],
                                        Size = parts[parts.Length - 1], // Usually SIZE is last
                                        ParameterSize = parts[1],
                                        Family = "Docker Model",
                                        Quantization = parts[2]
                                    });
                                }
                            }
                        }
                    }
                }

                var activeModel = _providerFactory.GetProvider(AiProviderType.DockerLocal).ModelName;

                return Json(ApiResponse<DockerModelsResponseDto>.Ok(new DockerModelsResponseDto
                {
                    Models = models,
                    ActiveModel = activeModel
                }));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail($"Cannot execute docker logic: {ex.Message}"));
            }
        }

        [HttpPost]
        public async Task PullDockerModel([FromBody] PullModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ModelName))
            {
                Response.StatusCode = 400;
                await Response.WriteAsync(JsonSerializer.Serialize(new { error = "Model name is required" }));
                return;
            }

            try
            {
                Response.StatusCode = 200;
                Response.ContentType = "application/x-ndjson";
                var requestedName = request.ModelName.Trim();

                // Model tags (e.g. llama3.2, phi3:mini) use 'docker model pull'
                // Container images (e.g. vllm/vllm-openai:latest) use 'docker pull'
                bool isModelTag = !requestedName.Contains('/');
                string dockerArgs = isModelTag
                    ? $"model pull {requestedName}"
                    : $"pull {requestedName}";

                var statusMsg = JsonSerializer.Serialize(new { status = $"[CMD] docker {dockerArgs}" });
                await Response.WriteAsync(statusMsg + "\n");
                await Response.Body.FlushAsync();

                var processInfo = new ProcessStartInfo("docker", dockerArgs)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    var errorLines = new List<string>();
                    var outTask = Task.Run(async () =>
                    {
                        while (true)
                        {
                            var line = await process.StandardOutput.ReadLineAsync();
                            if (line == null) break;
                            
                            var json = JsonSerializer.Serialize(new { status = line });
                            await Response.WriteAsync(json + "\n");
                            await Response.Body.FlushAsync();
                        }
                    });

                    var errTask = Task.Run(async () =>
                    {
                        while (true)
                        {
                            var line = await process.StandardError.ReadLineAsync();
                            if (line == null) break;

                            errorLines.Add(line);
                            var json = JsonSerializer.Serialize(new { status = line });
                            await Response.WriteAsync(json + "\n");
                            await Response.Body.FlushAsync();
                        }
                    });

                    await Task.WhenAll(outTask, errTask);
                    await process.WaitForExitAsync();
                    
                    if (process.ExitCode != 0)
                    {
                        var json = JsonSerializer.Serialize(new { isError = true, status = $"Process exited with code {process.ExitCode}." });
                        await Response.WriteAsync(json + "\n");
                        await Response.Body.FlushAsync();

                        if (isModelTag && errorLines.Any(line =>
                                line.Contains("pull access denied", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("repository does not exist", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("authorization failed", StringComparison.OrdinalIgnoreCase)))
                        {
                            var hintJson = JsonSerializer.Serialize(new
                            {
                                status = $"[HINT] '{requestedName}' is not a valid published Docker model name. Search first with 'docker model search {requestedName.Split(':')[0]}' and use the full model name returned by Docker, for example 'hf.co/microsoft/Phi-3-mini-4k-instruct' or 'ai/smollm2:360M-Q4_K_M'."
                            });
                            await Response.WriteAsync(hintJson + "\n");
                            await Response.Body.FlushAsync();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Response.StatusCode = 500;
                await Response.WriteAsync(JsonSerializer.Serialize(new { isError = true, error = $"Error: {ex.Message}" }));
            }
        }

        public class PullModelRequest
        {
            public string ModelName { get; set; } = "";
        }

        [HttpPost]
        public async Task<IActionResult> SetDockerDefaultModel([FromBody] PullModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ModelName))
                return BadRequest(ApiResponse.Fail("Model name is required"));

            try
            {
                var requestedName = request.ModelName.Trim();
                var installedModelNames = await GetInstalledDockerModelNamesAsync();

                if (!installedModelNames.Contains(requestedName))
                {
                    return Json(ApiResponse.Fail($"'{requestedName}' is not installed as a Docker model on this host."));
                }

                await UpsertSystemSettingAsync(SettingKeys.DockerModel, requestedName);

                return Json(ApiResponse.Ok($"Runtime default switched to '{requestedName}'."));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail($"Error: {ex.Message}"));
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteDockerModel([FromBody] PullModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ModelName))
                return BadRequest(ApiResponse.Fail("Model name is required"));

            try
            {
                var requestedName = request.ModelName.Trim();
                var installedModelNames = await GetInstalledDockerModelNamesAsync();
                var isDockerModel = installedModelNames.Contains(requestedName);
                var processInfo = new ProcessStartInfo("docker")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (isDockerModel)
                {
                    processInfo.ArgumentList.Add("model");
                    processInfo.ArgumentList.Add("rm");
                    processInfo.ArgumentList.Add(requestedName);
                }
                else
                {
                    processInfo.ArgumentList.Add("rmi");
                    processInfo.ArgumentList.Add(requestedName);
                }

                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode == 0)
                    {
                        var defaultSetting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerModel);
                        var clearedDefault = false;
                        if (defaultSetting != null &&
                            string.Equals(defaultSetting.Value, requestedName, StringComparison.OrdinalIgnoreCase))
                        {
                            _context.SystemSettings.Remove(defaultSetting);
                            await _context.SaveChangesAsync();
                            clearedDefault = true;
                        }

                        var targetLabel = isDockerModel ? "Docker model" : "Docker image";
                        var suffix = clearedDefault ? " The saved runtime default was cleared." : "";
                        return Json(ApiResponse.Ok($"{targetLabel} '{requestedName}' was removed from Docker.{suffix}"));
                    }
                    else
                    {
                        var error = await process.StandardError.ReadToEndAsync();
                        return Json(ApiResponse.Fail($"Delete failed: {error}"));
                    }
                }
                return Json(ApiResponse.Fail("Failed to launch docker process."));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail($"Error: {ex.Message}"));
            }
        }

        [HttpGet]
        public async Task<IActionResult> CheckDockerStatus()
        {
            var isInstalled = await _dockerService.IsDockerInstalledAsync();
            if (!isInstalled) return Json(ApiResponse<DockerStatusDto>.Ok(new DockerStatusDto { Status = "missing", Message = "Docker CLI not found on host system." }));

            var isRunning = await _dockerService.IsDockerRunningAsync();
            if (!isRunning) return Json(ApiResponse<DockerStatusDto>.Ok(new DockerStatusDto { Status = "stopped", Message = "Docker is installed but currently not running." }));

            return Json(ApiResponse<DockerStatusDto>.Ok(new DockerStatusDto { Status = "running", Message = "Docker is active and healthy." }));
        }

        [HttpPost]
        public async Task<IActionResult> ProvisionEngine(string engineType)
        {
            if (string.IsNullOrWhiteSpace(engineType)) return BadRequest(ApiResponse.Fail("Engine type is required"));

            var result = await _dockerService.LaunchEngineAsync(engineType);
            if (result.Success)
            {
                return Json(ApiResponse.Ok(result.Message));
            }
            return Json(ApiResponse.Fail(result.Message));
        }

        // ─── Theme Management ──────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTheme(int themeId)
        {
            var theme = await _context.CustomThemes.FindAsync(themeId);
            if (theme == null) return NotFound();

            await UpsertSystemSettingAsync("DefaultTheme", themeId.ToString());

            TempData["Success"] = $"Platform Architecture Synchronized to: {theme.Name.ToUpper()}";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveTheme(CustomTheme model)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    if (model.Id > 0)
                    {
                        var existing = await _context.CustomThemes.FirstOrDefaultAsync(t => t.Id == model.Id);
                        if (existing == null) return NotFound();

                        existing.Name = model.Name;
                        existing.PrimaryColor = model.PrimaryColor;
                        existing.BgMain = model.BgMain;
                        existing.BgCard = model.BgCard;
                        existing.BgSidebar = model.BgSidebar;
                        existing.BgHeader = model.BgHeader;
                        existing.TextMain = model.TextMain;
                        existing.TextMuted = model.TextMuted;
                        existing.BorderColor = model.BorderColor;
                        existing.IsSystemTheme = model.IsSystemTheme;
                        existing.SystemIdentifier = model.SystemIdentifier;
                        
                        _context.CustomThemes.Update(existing);
                        TempData["Success"] = $"Architecture Token '{model.Name}' refined successfully.";
                    }
                    else
                    {
                        _context.CustomThemes.Add(model);
                        TempData["Success"] = $"New Architecture Token '{model.Name}' established.";
                    }
                    await _context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    TempData["Error"] = $"Architectural Persistence Error: {ex.Message}";
                }
            }
            else
            {
                var errors = string.Join(" | ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
                TempData["Error"] = $"Validation Collision: {errors}";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetTheme(int id)
        {
            var theme = await _context.CustomThemes.FindAsync(id);
            if (theme == null || !theme.IsSystemTheme) return NotFound();

            switch (theme.SystemIdentifier)
            {
                case "emerald-obsidian": theme.PrimaryColor = "#10b981"; theme.BgMain = "#000000"; theme.BgCard = "#0a0a0a"; theme.BgSidebar = "#000000"; theme.BgHeader = "#000000"; theme.TextMain = "#ffffff"; theme.TextMuted = "#9ca3af"; theme.BorderColor = "#1f1f23"; break;
                case "solar-ember": theme.PrimaryColor = "#f59e0b"; theme.BgMain = "#111827"; theme.BgCard = "#1f2937"; theme.BgSidebar = "#111827"; theme.BgHeader = "#111827"; theme.TextMain = "#f9fafb"; theme.TextMuted = "#9ca3af"; theme.BorderColor = "#374151"; break;
                case "indigo-horizon": theme.PrimaryColor = "#6366f1"; theme.BgMain = "#f1f5f9"; theme.BgCard = "#ffffff"; theme.BgSidebar = "#ffffff"; theme.BgHeader = "#ffffff"; theme.TextMain = "#1e293b"; theme.TextMuted = "#64748b"; theme.BorderColor = "#e2e8f0"; break;
                case "midnight-azure": theme.PrimaryColor = "#3b82f6"; theme.BgMain = "#020617"; theme.BgCard = "#0f172a"; theme.BgSidebar = "#020617"; theme.BgHeader = "#020617"; theme.TextMain = "#f8fafc"; theme.TextMuted = "#94a3b8"; theme.BorderColor = "#1e293b"; break;
                case "slate-alpine": theme.PrimaryColor = "#64748b"; theme.BgMain = "#f1f5f9"; theme.BgCard = "#ffffff"; theme.BgSidebar = "#ffffff"; theme.BgHeader = "#f8fafc"; theme.TextMain = "#0f172a"; theme.TextMuted = "#475569"; theme.BorderColor = "#cbd5e1"; break;
                case "cyber-neon": theme.PrimaryColor = "#f97316"; theme.BgMain = "#0f0f0f"; theme.BgCard = "#1a1a1a"; theme.BgSidebar = "#000000"; theme.BgHeader = "#000000"; theme.TextMain = "#ffffff"; theme.TextMuted = "#a1a1aa"; theme.BorderColor = "#27272a"; break;
                case "rose-quartz": theme.PrimaryColor = "#ec4899"; theme.BgMain = "#fff1f2"; theme.BgCard = "#ffffff"; theme.BgSidebar = "#ffffff"; theme.BgHeader = "#ffffff"; theme.TextMain = "#881337"; theme.TextMuted = "#be123c"; theme.BorderColor = "#fecdd3"; break;
                case "deep-forest": theme.PrimaryColor = "#059669"; theme.BgMain = "#022c22"; theme.BgCard = "#064e3b"; theme.BgSidebar = "#022c22"; theme.BgHeader = "#022c22"; theme.TextMain = "#ecfdf5"; theme.TextMuted = "#6ee7b7"; theme.BorderColor = "#065f46"; break;
                case "emerald-light": theme.PrimaryColor = "#10b981"; theme.BgMain = "#f1f5f9"; theme.BgCard = "#ffffff"; theme.BgSidebar = "#ffffff"; theme.BgHeader = "#ffffff"; theme.TextMain = "#0f172a"; theme.TextMuted = "#4b5563"; theme.BorderColor = "#d1d5db"; break;
            }

            _context.CustomThemes.Update(theme);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Architecture Token '{theme.Name}' reset to factory directive.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteTheme(int id)
        {
            var theme = await _context.CustomThemes.FindAsync(id);
            if (theme == null || theme.IsSystemTheme) return BadRequest();

            var currentThemeSetting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == "DefaultTheme");
            if (currentThemeSetting != null && currentThemeSetting.Value == id.ToString())
            {
                TempData["Error"] = $"Conflict Resolution Failure: '{theme.Name}' is the currently active architecture. Transition before purging.";
                return RedirectToAction(nameof(Index));
            }

            _context.CustomThemes.Remove(theme);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Custom Architecture '{theme.Name}' successfully purged.";
            return RedirectToAction(nameof(Index));
        }

        // ─── Helper ───────────────────────────────────────────────

        private async Task UpsertSystemSettingAsync(string key, string value)
        {
            var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (setting == null)
            {
                setting = new SystemSetting { Key = key, Value = value };
                _context.SystemSettings.Add(setting);
            }
            else
            {
                setting.Value = value;
                _context.SystemSettings.Update(setting);
            }
            await _context.SaveChangesAsync();
        }

        private async Task<HashSet<string>> GetInstalledDockerModelNamesAsync()
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
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        }

        private static SqlConnectionStringBuilder BuildConnectionStringBuilder(string connectionString)
        {
            try
            {
                return new SqlConnectionStringBuilder(connectionString);
            }
            catch
            {
                return new SqlConnectionStringBuilder
                {
                    DataSource = ".",
                    InitialCatalog = "AISupportAnalysisPlatform",
                    IntegratedSecurity = true,
                    TrustServerCertificate = true,
                    Encrypt = false
                };
            }
        }

        private static RuntimeDatabaseTarget BuildSqlServerTarget(string connectionString) => new()
        {
            Provider = RuntimeDatabaseProvider.SqlServer,
            ConnectionString = connectionString.Trim()
        };

        private async Task<List<SqlServerCandidateInfo>> DiscoverSqlServerCandidatesAsync()
        {
            var machineName = Environment.MachineName;
            var candidateNames = new[]
            {
                ".",
                "localhost",
                machineName,
                @".\\SQLEXPRESS",
                @"localhost\\SQLEXPRESS",
                $@"{machineName}\\SQLEXPRESS",
                @"(localdb)\\MSSQLLocalDB"
            }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            var candidates = new List<SqlServerCandidateInfo>();

            foreach (var candidateName in candidateNames)
            {
                try
                {
                    var probe = await ProbeSqlServerAsync(candidateName, useSqlAuthentication: false, userName: "", password: "");
                    candidates.Add(new SqlServerCandidateInfo
                    {
                        ServerName = candidateName,
                        Edition = probe.Edition,
                        ProductVersion = probe.ProductVersion,
                        EngineEdition = probe.EngineEdition
                    });
                }
                catch
                {
                    // Ignore unreachable candidates and only surface working SQL Server instances.
                }
            }

            return candidates;
        }

        private static string BuildSqlConnectionString(
            string serverName,
            string databaseName,
            bool useSqlAuthentication,
            string userName,
            string password,
            bool trustServerCertificate = true,
            bool encrypt = false,
            int timeoutSeconds = 5)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = serverName.Trim(),
                InitialCatalog = string.IsNullOrWhiteSpace(databaseName) ? "master" : databaseName.Trim(),
                IntegratedSecurity = !useSqlAuthentication,
                TrustServerCertificate = trustServerCertificate,
                Encrypt = encrypt,
                ConnectTimeout = timeoutSeconds,
                MultipleActiveResultSets = false
            };

            if (useSqlAuthentication)
            {
                builder.UserID = userName?.Trim() ?? "";
                builder.Password = password ?? "";
            }

            return builder.ConnectionString;
        }

        private async Task<SqlServerProbeResult> ProbeSqlServerAsync(string serverName, bool useSqlAuthentication, string userName, string password)
        {
            var connectionString = BuildSqlConnectionString(serverName, "master", useSqlAuthentication, userName, password, timeoutSeconds: 3);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var probe = new SqlServerProbeResult
            {
                ServerName = serverName
            };

            const string sql = @"
SELECT
    CAST(SERVERPROPERTY('Edition') AS nvarchar(256)) AS Edition,
    CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS ProductVersion,
    CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128)) AS ProductLevel,
    CAST(SERVERPROPERTY('EngineEdition') AS int) AS EngineEdition;

SELECT name
FROM sys.databases
WHERE state_desc = 'ONLINE'
ORDER BY name;";

            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                probe.Edition = reader["Edition"]?.ToString() ?? "Unknown";
                probe.ProductVersion = reader["ProductVersion"]?.ToString() ?? "Unknown";
                probe.ProductLevel = reader["ProductLevel"]?.ToString() ?? "";
                probe.EngineEdition = MapEngineEdition(reader["EngineEdition"] as int? ?? 0);
            }

            if (await reader.NextResultAsync())
            {
                while (await reader.ReadAsync())
                {
                    var databaseName = reader["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(databaseName))
                    {
                        probe.Databases.Add(databaseName);
                    }
                }
            }

            return probe;
        }

        private static string MapEngineEdition(int engineEdition) => engineEdition switch
        {
            1 => "Personal or Desktop",
            2 => "Standard",
            3 => "Enterprise",
            4 => "Express",
            5 => "Azure SQL Database",
            6 => "Azure Synapse",
            8 => "Azure SQL Managed Instance",
            9 => "Azure SQL Edge",
            11 => "Serverless SQL Pool",
            12 => "Microsoft Fabric SQL Database",
            _ => $"Unknown ({engineEdition})"
        };

        private void StoreTempData<T>(string key, T value)
        {
            TempData[key] = JsonSerializer.Serialize(value);
        }

        private T? DeserializeTempData<T>(string key)
        {
            var json = TempData.Peek(key) as string;
            return string.IsNullOrWhiteSpace(json)
                ? default
                : JsonSerializer.Deserialize<T>(json);
        }

        private async Task ActivateRuntimeTargetAsync(RuntimeDatabaseTarget target, bool runMigrations)
        {
            var previousTarget = _runtimeDatabaseTargetService.GetCurrent();

            try
            {
                _runtimeDatabaseTargetService.SetCurrent(target);

                using var scope = _serviceProvider.CreateScope();
                var scopedServices = scope.ServiceProvider;
                var dbContext = scopedServices.GetRequiredService<ApplicationDbContext>();

                if (runMigrations)
                {
                    await dbContext.Database.MigrateAsync();
                    var scopedUserManager = scopedServices.GetRequiredService<UserManager<ApplicationUser>>();
                    var scopedRoleManager = scopedServices.GetRequiredService<RoleManager<IdentityRole>>();
                    await DbSeeder.InitializeCoreAsync(scopedServices, scopedUserManager, scopedRoleManager);
                    return;
                }

                if (!await dbContext.Database.CanConnectAsync())
                {
                    throw new InvalidOperationException("The selected database target is not reachable.");
                }

                var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
                if (pendingMigrations.Any())
                {
                    throw new InvalidOperationException("The selected database target is not initialized yet. Run migration first, then activate it.");
                }
            }
            catch
            {
                _runtimeDatabaseTargetService.SetCurrent(previousTarget);
                throw;
            }
        }

        public sealed class SqlServerCandidateInfo
        {
            public string ServerName { get; set; } = "";
            public string Edition { get; set; } = "";
            public string ProductVersion { get; set; } = "";
            public string EngineEdition { get; set; } = "";
        }

        public sealed class SqlServerProbeResult
        {
            public string ServerName { get; set; } = "";
            public string Edition { get; set; } = "";
            public string ProductVersion { get; set; } = "";
            public string ProductLevel { get; set; } = "";
            public string EngineEdition { get; set; } = "";
            public List<string> Databases { get; set; } = new();
        }
        // ─── External API Management ───────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveExternalApi(ExternalApiSetting model)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    if (model.Id > 0)
                    {
                        var existing = await _context.ExternalApiSettings.FindAsync(model.Id);
                        if (existing == null) return NotFound();

                        existing.Title = model.Title;
                        existing.Endpoint = model.Endpoint;
                        existing.Description = model.Description;
                        existing.IsActive = model.IsActive;
                        _context.ExternalApiSettings.Update(existing);
                        TempData["Success"] = $"API Hub '{model.Title}' updated successfully.";
                    }
                    else
                    {
                        _context.ExternalApiSettings.Add(model);
                        TempData["Success"] = $"New API Hub '{model.Title}' integrated into the chatbot fabric.";
                    }
                    await _context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    TempData["Error"] = $"API Integration Error: {ex.Message}";
                }
            }
            else
            {
                var errors = string.Join(" | ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
                TempData["Error"] = $"Validation Collision: {errors}";
            }

            return RedirectToAction(nameof(Index), new { tab = "chatbot" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteExternalApi(int id)
        {
            var api = await _context.ExternalApiSettings.FindAsync(id);
            if (api == null) return NotFound();

            _context.ExternalApiSettings.Remove(api);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"API Hub '{api.Title}' has been disconnected.";
            return RedirectToAction(nameof(Index), new { tab = "chatbot" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleExternalApi(int id)
        {
            var api = await _context.ExternalApiSettings.FindAsync(id);
            if (api == null) return NotFound();

            api.IsActive = !api.IsActive;
            await _context.SaveChangesAsync();

            var status = api.IsActive ? "Activated" : "Deactivated";
            TempData["Success"] = $"API Hub '{api.Title}' {status}.";
            return RedirectToAction(nameof(Index), new { tab = "chatbot" });
        }

        // ─── Copilot Tool Management ─────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCopilotTool(CopilotToolDefinition tool)
        {
            // Basic validation
            if (string.IsNullOrWhiteSpace(tool.ToolKey))
            {
                ModelState.AddModelError(nameof(tool.ToolKey), "Routing Key is required.");
            }
            else if (!System.Text.RegularExpressions.Regex.IsMatch(tool.ToolKey, @"^[a-zA-Z0-9_\-]+$"))
            {
                ModelState.AddModelError(nameof(tool.ToolKey), "Routing Key must be alphanumeric (underscores/dashes allowed).");
            }

            if (tool.ToolType == "External")
            {
                if (string.IsNullOrWhiteSpace(tool.EndpointUrl))
                {
                    ModelState.AddModelError(nameof(tool.EndpointUrl), "Endpoint URL is required for external tools.");
                }
                else if (!Uri.TryCreate(tool.EndpointUrl, UriKind.Absolute, out _))
                {
                    ModelState.AddModelError(nameof(tool.EndpointUrl), "Endpoint URL must be a valid absolute URI.");
                }
            }

            // Auto-generate Test Prompt if missing
            if (string.IsNullOrWhiteSpace(tool.TestPrompt))
            {
                tool.TestPrompt = BuildGeneratedToolPrompt(tool);
            }

            if (ModelState.IsValid)
            {
                try
                {
                    await _toolRegistry.SaveAsync(tool);
                    TempData["Success"] = $"Tool '{tool.Title}' saved successfully.";
                }
                catch (Exception ex)
                {
                    TempData["Error"] = $"Failed to save tool: {ex.Message}";
                }
            }
            else
            {
                var firstError = ModelState.Values.SelectMany(v => v.Errors).FirstOrDefault()?.ErrorMessage;
                TempData["Error"] = $"Validation failed: {firstError ?? "Invalid configuration."}";
            }
            return RedirectToAction(nameof(Index), new { tab = "chatbot" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleCopilotTool(int id, bool isEnabled)
        {
            await _toolRegistry.ToggleAsync(id, isEnabled);
            TempData["Success"] = "Tool visibility updated.";
            return RedirectToAction(nameof(Index), new { tab = "chatbot" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCopilotTool(int id)
        {
            await _toolRegistry.DeleteAsync(id);
            TempData["Success"] = "Tool removed from registry.";
            return RedirectToAction(nameof(Index), new { tab = "chatbot" });
        }

        private static string BuildGeneratedToolPrompt(CopilotToolDefinition tool)
        {
            if (!string.IsNullOrWhiteSpace(tool.QueryExtractionHint))
            {
                return $"{tool.Title}: {tool.QueryExtractionHint.Trim()}";
            }

            if (!string.IsNullOrWhiteSpace(tool.Description))
            {
                return $"{tool.Title}: {tool.Description.Trim()}";
            }

            return tool.Title;
        }
    }
}
