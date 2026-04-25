using AISupportAnalysisPlatform.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models;
using Serilog;
using AISupportAnalysisPlatform.Services.AI;
using AISupportAnalysisPlatform.Services.AI.Contracts;
using AISupportAnalysisPlatform.Services.AI.Pipeline;
using AISupportAnalysisPlatform.Services.AI.Pipeline.Abstractions;
using AISupportAnalysisPlatform.Services.AI.Pipeline.Analysis;
using AISupportAnalysisPlatform.Services.AI.Pipeline.Evaluation;
using AISupportAnalysisPlatform.Services.AI.Pipeline.Execution;
using AISupportAnalysisPlatform.Services.AI.Pipeline.Formatting;

using AISupportAnalysisPlatform.Services.AI.Providers;
using AISupportAnalysisPlatform.Services.Infrastructure;
using AISupportAnalysisPlatform.Services.Notifications;
using AISupportAnalysisPlatform.Models.AI;
using System.Text.Json;
using AISupportAnalysisPlatform.Mappings;

var builder = WebApplication.CreateBuilder(args);

// --- Configure Serilog for Daily Files ---
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("Logs/SupportFlow-AI-.log", rollingInterval: RollingInterval.Day, 
                  outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddSingleton<IRuntimeDatabaseTargetService>(_ => new RuntimeDatabaseTargetService(new RuntimeDatabaseTarget
{
    Provider = RuntimeDatabaseProvider.SqlServer,
    ConnectionString = connectionString
}));
builder.Services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
{
    var runtimeDatabaseTarget = serviceProvider.GetRequiredService<IRuntimeDatabaseTargetService>().GetCurrent();
    RuntimeDatabaseConfigurator.Configure(options, runtimeDatabaseTarget);
});
builder.Services.AddDbContextFactory<ApplicationDbContext>(lifetime: ServiceLifetime.Scoped);
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddDefaultIdentity<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = false)
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddControllersWithViews()
    .AddJsonOptions(options => {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddAutoMapper(config => { config.AddProfile<MappingProfile>(); });
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IDockerService, DockerService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ILocalizationService, LocalizationService>();
builder.Services.AddHttpClient();

// ── AI Provider Configuration ────────────────────────────────────────────────
builder.Services.Configure<AiProviderSettings>(builder.Configuration.GetSection(AiProviderSettings.SectionName));

// Register Copilot Text Configuration
builder.Configuration.AddJsonFile("copilot-text.json", optional: true, reloadOnChange: true);
builder.Services.Configure<CopilotTextSettings>(builder.Configuration);

  builder.Services.AddSingleton<CopilotHeuristicCatalog>();
  builder.Services.AddSingleton<CopilotTextCatalog>();
  builder.Services.AddScoped<CopilotMessageCatalog>();
  builder.Services.AddScoped<ICopilotKnowledgeEngine, CopilotKnowledgeEngine>();
  builder.Services.AddSingleton<IAiProviderFactory, AiProviderFactory>();

// ── AI Investigation Services ────────────────────────────────────────────────
builder.Services.AddScoped<TicketContextPreparationService>();
builder.Services.AddScoped<TicketAiPromptBuilder>();
builder.Services.AddScoped<IAiAnalysisService, AiAnalysisService>();
builder.Services.AddScoped<IAiReviewSignalService, AiReviewSignalService>();
builder.Services.AddScoped<IAiInsightsService, AiInsightsService>();
builder.Services.AddScoped<ISemanticSearchService, SemanticSearchService>();
builder.Services.AddScoped<BilingualRetrievalBenchmarkService>();
builder.Services.AddScoped<KnowledgeBaseRagService>();

builder.Services.AddScoped<CopilotLexicalMatcherService>();
builder.Services.AddSingleton<CopilotDataCatalogService>();
builder.Services.AddSingleton<CopilotDataCatalogValidatorService>();
builder.Services.AddScoped<CopilotDataIntentPlannerService>();
builder.Services.AddScoped<CopilotDataIntentPlanTranslatorService>();
builder.Services.AddScoped<CopilotDataQueryExecutorService>();

// W-5: Shared per-request metadata cache — eliminates 5-8 redundant DB hits per analytics query.
builder.Services.AddScoped<CopilotMetadataService>();
builder.Services.AddSingleton<CopilotTemporalService>();

// --- Core Analytics Pipeline (Primary Path for All Copilot Data Queries) ---
builder.Services.AddScoped<AnalyticsPipeline>();
builder.Services.AddScoped<IAnalyticsStep, PipelineAnalyzerService>();
builder.Services.AddScoped<IAnalyticsStep, PipelineEvaluatorService>();
builder.Services.AddScoped<IAnalyticsStep, PipelineExecutorService>();
builder.Services.AddScoped<IAnalyticsStep, PipelineFormatterService>();
builder.Services.AddScoped<CopilotAssessmentService>();
builder.Services.AddScoped<CopilotRecommendationEngine>();
builder.Services.AddScoped<CopilotEvaluationEngine>();
builder.Services.AddScoped<CopilotFollowUpPlanMergerService>();
builder.Services.AddScoped<CopilotRequestDecomposer>();
builder.Services.AddScoped<CopilotToolParameterResolverService>();
builder.Services.AddScoped<ICopilotConversationContextService, CopilotConversationContextService>();
builder.Services.AddScoped<ICopilotQuestionPreprocessor, CopilotQuestionPreprocessor>();
builder.Services.AddScoped<ICopilotToolIntentResolver, CopilotToolIntentResolver>();
builder.Services.AddScoped<ICopilotIntentClassifierService, CopilotIntentClassifierService>();
builder.Services.AddScoped<ICopilotPlanEngine, CopilotPlanEngine>();
builder.Services.AddScoped<CopilotTraceHistoryService>();
builder.Services.AddScoped<CopilotInvestigationExecutor>();
builder.Services.AddScoped<CopilotExternalToolExecutor>();
builder.Services.AddScoped<ICopilotIntelligenceEngine, CopilotIntelligenceEngine>();
builder.Services.AddScoped<ICopilotExecutionEngine, CopilotExecutionEngine>();
builder.Services.AddScoped<ICopilotVerificationEngine, CopilotVerificationEngine>();
builder.Services.AddScoped<ICopilotGroundingService, AISupportAnalysisPlatform.Services.AI.Validation.CopilotGroundingService>();
builder.Services.AddScoped<ICopilotChatEngine, CopilotChatEngine>();
builder.Services.AddScoped<CopilotToolRegistryService>();
builder.Services.AddSingleton<ITicketEmbeddingEngine, OllamaTicketEmbeddingEngine>();
builder.Services.AddSingleton<AiAnalysisQueueService>();
builder.Services.AddSingleton<EmbeddingQueueService>();

var app = builder.Build();

// Validate the Admin Copilot data catalog at startup so broken metadata cannot produce unsafe data plans.
{
    var validation = app.Services.GetRequiredService<CopilotDataCatalogValidatorService>()
        .ValidateAsync()
        .GetAwaiter()
        .GetResult();

    foreach (var warning in validation.Warnings)
    {
        Log.Warning("Copilot data catalog warning: {Warning}", warning);
    }

    if (!validation.IsValid)
    {
        foreach (var error in validation.Errors)
        {
            Log.Error("Copilot data catalog error: {Error}", error);
        }

        throw new InvalidOperationException("Copilot data catalog validation failed. Check startup logs for metadata errors.");
    }
}

// Log the active AI provider at startup
{
    var providerSettings = builder.Configuration.GetSection(AiProviderSettings.SectionName).Get<AiProviderSettings>() ?? new AiProviderSettings();
    Log.Information("AI Provider configured: {ActiveProvider} (Model: {Model})",
        providerSettings.ActiveProvider,
        providerSettings.GetActiveProviderType() switch
        {
            AiProviderType.DockerLocal => providerSettings.DockerLocal.Model,
            AiProviderType.OpenAI => providerSettings.OpenAI.Model,
            AiProviderType.Cloud => providerSettings.Cloud.Model,
            _ => "unknown"
        });
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// --- Localization Middleware ---
var supportedCultures = new[] { "en", "ar" };
var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture(supportedCultures[0])
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);

app.UseRequestLocalization(localizationOptions);

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapRazorPages()
   .WithStaticAssets();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
    await DbSeeder.InitializeCoreAsync(services, userManager, roleManager);

    var aiService = services.GetRequiredService<IAiAnalysisService>();
    await aiService.ResetInterruptedAnalysesAsync();
}

var runRetrievalBenchmark = args.Contains("--run-retrieval-benchmark", StringComparer.OrdinalIgnoreCase) ||
    string.Equals(Environment.GetEnvironmentVariable("RUN_RETRIEVAL_BENCHMARK"), "1", StringComparison.OrdinalIgnoreCase);

if (runRetrievalBenchmark)
{
    using var scope = app.Services.CreateScope();
    var benchmarkService = scope.ServiceProvider.GetRequiredService<BilingualRetrievalBenchmarkService>();
    var bucketIndex = Array.FindIndex(args, a => string.Equals(a, "--benchmark-bucket", StringComparison.OrdinalIgnoreCase));
    var bucket = bucketIndex >= 0 && bucketIndex + 1 < args.Length ? args[bucketIndex + 1] : null;
    var result = await benchmarkService.RunAsync(bucket: bucket);

    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        WriteIndented = true
    }));

    return;
}

app.Run();
