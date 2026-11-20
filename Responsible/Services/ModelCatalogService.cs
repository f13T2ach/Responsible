using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Responsible.ViewModel;

namespace Responsible.Services;

public sealed class ModelCatalogService : IModelCatalogService
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Responsible",
        "providers.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    public ObservableCollection<ModelOptionViewModel> ModelOptions { get; } = [];

    public IReadOnlyList<ProviderCatalogEntry> Catalog { get; } =
    [
        new("openai", "OpenAI", "OPENAI_API_KEY", [], ["GPT-5.2", "GPT-5.2-mini"]),
        new("anthropic", "Anthropic", "ANTHROPIC_API_KEY", [], ["Claude-4.6-Sonnet", "Claude-4.6-Opus"]),
        new("kimi", "Kimi", "MOONSHOT_API_KEY", [], ["Kimi-K3"]),
        new("google", "Google", "GEMINI_API_KEY", [], ["Gemini-3-Pro"]),
        new("amazon-bedrock", "Amazon Bedrock", "AWS_BEARER_TOKEN_BEDROCK", ["AWS_REGION", "AWS_DEFAULT_REGION"], []),
        new("azure", "Azure OpenAI", "AZURE_API_KEY", [], []),
        new("deepseek", "DeepSeek", "DEEPSEEK_API_KEY", [], ["DeepSeek-V4.1-Flash", "DeepSeek-V4-Pro"]),
    ];

    public ModelCatalogService()
    {
        var records = Load();
        foreach (var record in records)
            Providers.Add(FromRecord(record));

        if (Providers.Count == 0)
        {
            Seed();
            Save();
        }

        RebuildModelOptions();
    }

    public void AddOrUpdate(ProviderViewModel provider)
    {
        var existing = Providers.FirstOrDefault(p => p.Id == provider.Id);
        if (existing != null && !ReferenceEquals(existing, provider))
        {
            existing.Name = provider.Name;
            existing.Kind = provider.Kind;
            existing.IsConfigured = provider.IsConfigured;
            existing.ApiKey = provider.ApiKey;
            existing.BaseUrl = provider.BaseUrl;
            existing.Protocol = provider.Protocol;
            existing.Models.Clear();
            foreach (var model in provider.Models)
                existing.Models.Add(new ProviderModelEntryViewModel { Id = model.Id });
        }
        else if (existing == null)
        {
            Providers.Add(provider);
        }

        RebuildModelOptions();
        Save();
    }

    public void Remove(ProviderViewModel provider)
    {
        Providers.Remove(provider);
        RebuildModelOptions();
        Save();
    }

    public void OpenConfigFile()
    {
        Save();
        Process.Start(new ProcessStartInfo(ConfigPath) { UseShellExecute = true });
    }

    private void RebuildModelOptions()
    {
        var selectedName = ModelOptions.FirstOrDefault(o => o.IsSelected)?.DisplayName;

        ModelOptions.Clear();
        foreach (var provider in Providers)
            foreach (var model in provider.Models)
                if (!string.IsNullOrWhiteSpace(model.Id))
                    ModelOptions.Add(new ModelOptionViewModel
                    {
                        ProviderName = provider.Name,
                        DisplayName = model.Id,
                    });

        var selected = ModelOptions.FirstOrDefault(o => o.DisplayName == selectedName)
            ?? ModelOptions.FirstOrDefault();
        if (selected != null)
            selected.IsSelected = true;
    }

    private void Seed()
    {
        Providers.Add(new ProviderViewModel
        {
            Id = "test",
            Name = "Test",
            Kind = ProviderKind.Custom,
            IsConfigured = true,
            Models = { new ProviderModelEntryViewModel { Id = "Test" } },
        });
    }

    private static ProviderViewModel FromRecord(ProviderRecord record)
    {
        var provider = new ProviderViewModel
        {
            Id = record.Id,
            Name = record.Name,
            Kind = record.Kind,
            IsConfigured = record.IsConfigured,
            ApiKey = record.ApiKey,
            BaseUrl = record.BaseUrl,
            Protocol = record.Protocol,
        };
        foreach (var id in record.Models)
            provider.Models.Add(new ProviderModelEntryViewModel { Id = id });
        return provider;
    }

    private static ProviderRecord ToRecord(ProviderViewModel provider) => new()
    {
        Id = provider.Id,
        Name = provider.Name,
        Kind = provider.Kind,
        IsConfigured = provider.IsConfigured,
        ApiKey = provider.ApiKey,
        BaseUrl = provider.BaseUrl,
        Protocol = provider.Protocol,
        Models = provider.Models.Select(m => m.Id).ToArray(),
    };

    private static List<ProviderRecord> Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<List<ProviderRecord>>(File.ReadAllText(ConfigPath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return [];
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Providers.Select(ToRecord), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class ProviderRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public ProviderKind Kind { get; set; }
        public bool IsConfigured { get; set; }
        public string ApiKey { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public string Protocol { get; set; } = "OpenAI Chat Completions";
        public string[] Models { get; set; } = [];
    }
}