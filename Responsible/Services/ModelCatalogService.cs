using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Responsible.Messages;
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

    private IReadOnlyList<DshModelRow> _dshModels = [];
    private readonly IMessenger _messenger;

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

    public ModelCatalogService(IMessenger messenger)
    {
        _messenger = messenger;
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

    public void ApplyDshModels(IReadOnlyList<DshModelRow> models)
    {
        if (models.Count == 0) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyDshModels(models));
            return;
        }

        _dshModels = models;
        RebuildModelOptions();
    }

    private static string LocalRoute(string providerId) =>
        providerId == "deepseek" ? "deepseek-official" : providerId;

    // DSH 内置目录用 kebab 模型 id；本机目录存的是展示名，发请求前必须先换回真实 id
    private static string NormalizeModelId(string route, string id) => (route, id) switch
    {
        ("deepseek-official", "DeepSeek-V4.1-Flash") => "deepseek-flash",
        ("deepseek-official", "DeepSeek-V4-Pro") => "deepseek-v4-pro",
        _ => id,
    };

    private void RebuildModelOptions()
    {
        var previous = ModelOptions.FirstOrDefault(o => o.IsSelected);
        var selectedRoute = previous?.ProviderRoute;
        var selectedId = previous?.ModelId;
        var selectedName = previous?.DisplayName;

        ModelOptions.Clear();
        var seen = new HashSet<(string Provider, string Model)>();
        foreach (var provider in Providers)
            foreach (var model in provider.Models)
            {
                if (string.IsNullOrWhiteSpace(model.Id)) continue;
                var route = LocalRoute(provider.Id);
                // 同名同路由以 DSH 目录为准：本机条目只是本应用自己的旧目录
                if (_dshModels.Any(m => m.ProviderId == route
                        && (m.ModelId == model.Id || string.Equals(m.ModelName, model.Id, StringComparison.OrdinalIgnoreCase))))
                    continue;
                if (!seen.Add((route, model.Id))) continue;
                ModelOptions.Add(new ModelOptionViewModel
                {
                    ProviderName = provider.Name,
                    DisplayName = model.Id,
                    ProviderRoute = route,
                    ModelId = NormalizeModelId(route, model.Id),
                });
            }

        // DSH 运行时注册的模型（含 pi-ai 目录）与本机 provider 目录合并，选择器不会因刷新丢项
        foreach (var model in _dshModels)
        {
            if (!seen.Add((model.ProviderId, model.ModelId))) continue;
            ModelOptions.Add(new ModelOptionViewModel
            {
                ProviderName = model.ProviderName,
                DisplayName = model.ModelName,
                ProviderRoute = model.ProviderId,
                ModelId = model.ModelId,
            });
        }

        var selected = ModelOptions.FirstOrDefault(o => o.ProviderRoute == selectedRoute && o.ModelId == selectedId)
                       ?? ModelOptions.FirstOrDefault(o => o.DisplayName == selectedName)
                       ?? ModelOptions.FirstOrDefault();
        // 选择器里可能有 DSH 路由并不存在的本机条目；回落时保持 UI 与实际运行的模型一致
        var dshRoutes = _dshModels.Select(model => model.ProviderId).ToHashSet(StringComparer.Ordinal);
        if (selected is not null && dshRoutes.Count > 0 && !dshRoutes.Contains(selected.ProviderRoute))
            selected = ModelOptions.FirstOrDefault(o => dshRoutes.Contains(o.ProviderRoute)) ?? selected;
        if (selected != null)
            selected.IsSelected = true;
        _messenger.Send(new ModelOptionsChangedMessage());
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