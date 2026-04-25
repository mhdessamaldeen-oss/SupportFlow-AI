using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using System.Globalization;
using System.Collections.Concurrent;
using Microsoft.Extensions.Localization;

namespace AISupportAnalysisPlatform.Services.Infrastructure
{
    public interface ILocalizationService
    {
        string Get(string key);
        string Get(string key, string language);
        string CurrentLanguage { get; }
        string Direction { get; }
        bool IsRtl { get; }
    }

    public class LocalizationService : ILocalizationService
    {
        private readonly string _contentRootPath;
        private readonly IStringLocalizer<SharedResource> _localizer;
        private static ConcurrentDictionary<string, Dictionary<string, string>> _cache = new();

        public LocalizationService(IWebHostEnvironment env, IStringLocalizer<SharedResource> localizer)
        {
            _contentRootPath = env.ContentRootPath;
            _localizer = localizer;
        }

        public string CurrentLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        public string Direction => IsRtl ? "rtl" : "ltr";
        public bool IsRtl => CurrentLanguage == "ar";

        public string Get(string key) => Get(key, CurrentLanguage);

        public string Get(string key, string language)
        {
            using var _ = new CultureScope(ToCultureInfo(language));
            var packageValue = _localizer[key];
            if (!packageValue.ResourceNotFound && !string.Equals(packageValue.Value, key, StringComparison.Ordinal))
            {
                return packageValue.Value;
            }

            var dict = GetDictionary(language);
            if (dict.TryGetValue(key, out var value))
            {
                return value;
            }
            return key;
        }

        private Dictionary<string, string> GetDictionary(string language)
        {
            var lang = language.ToLower() switch {
                "arabic" or "ar" => "ar",
                _ => "en"
            };

            if (_cache.TryGetValue(lang, out var cachedDict))
            {
                return cachedDict;
            }

            var path = Path.Combine(_contentRootPath, "Resources", $"{lang}.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                _cache[lang] = dict;
                return dict;
            }

            // Fallback to English if file not found
            if (lang != "en")
            {
                var enPath = Path.Combine(_contentRootPath, "Resources", "en.json");
                if (File.Exists(enPath))
                {
                    var enJson = File.ReadAllText(enPath);
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(enJson) ?? new();
                }
            }

            return new Dictionary<string, string>();
        }

        private static CultureInfo ToCultureInfo(string language)
        {
            var lang = language.ToLowerInvariant() switch
            {
                "arabic" or "ar" => "ar",
                _ => "en"
            };

            return CultureInfo.GetCultureInfo(lang);
        }

        private sealed class CultureScope : IDisposable
        {
            private readonly CultureInfo _originalCulture;
            private readonly CultureInfo _originalUiCulture;

            public CultureScope(CultureInfo culture)
            {
                _originalCulture = CultureInfo.CurrentCulture;
                _originalUiCulture = CultureInfo.CurrentUICulture;
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
            }

            public void Dispose()
            {
                CultureInfo.CurrentCulture = _originalCulture;
                CultureInfo.CurrentUICulture = _originalUiCulture;
            }
        }
    }
}
