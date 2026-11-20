using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Responsible.Services;

namespace Responsible.ViewModel;

public partial class ProviderEditorViewModel : ObservableObject
{
    private ProviderViewModel? _editing;

    public ProviderEditorViewModel(IModelCatalogService catalog)
    {
        Catalog = catalog.Catalog;
        _selectedCatalogEntry = Catalog.FirstOrDefault(e => e.Id == "amazon-bedrock") ?? Catalog[0];
    }

    public IReadOnlyList<ProviderCatalogEntry> Catalog { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomMode))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanFetch))]
    [NotifyCanExecuteChangedFor(nameof(FetchModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private int _modeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnvKeyPlaceholder))]
    [NotifyPropertyChangedFor(nameof(EnvWarningText))]
    [NotifyPropertyChangedFor(nameof(RegionsHint))]
    [NotifyPropertyChangedFor(nameof(ShowEnvWarning))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(FetchModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private ProviderCatalogEntry? _selectedCatalogEntry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEnvWarning))]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanFetch))]
    [NotifyCanExecuteChangedFor(nameof(FetchModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _providerId = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _protocol = "OpenAI Chat Completions";

    [ObservableProperty]
    private string _modelsSubtitle = "正在使用适配器默认模型";

    public ObservableCollection<ProviderModelEntryViewModel> Models { get; } = [];

    public bool IsCustomMode => ModeIndex == 1;

    public string EnvKeyPlaceholder => $"留空使用环境认证（{SelectedCatalogEntry?.EnvKeyVar}）";

    public string EnvWarningText => $"未检测到环境变量。请填写 API 密钥，或设置环境变量 {SelectedCatalogEntry?.EnvKeyVar}。";

    public string RegionsHint => "提供方地址配置：" + string.Join(", ", SelectedCatalogEntry?.RegionVars ?? []);

    public bool ShowEnvWarning => !IsCustomMode
        && SelectedCatalogEntry != null
        && Environment.GetEnvironmentVariable(SelectedCatalogEntry.EnvKeyVar) == null
        && string.IsNullOrEmpty(ApiKey);

    public bool CanSave => IsCustomMode
        ? Regex.IsMatch(ProviderId, "^[a-z][a-z0-9-]*$") && !string.IsNullOrWhiteSpace(BaseUrl)
        : SelectedCatalogEntry != null;

    public bool CanFetch => IsCustomMode ? !string.IsNullOrWhiteSpace(BaseUrl) : SelectedCatalogEntry != null;

    public event EventHandler? SaveRequested;

    public event EventHandler? CancelRequested;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => SaveRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void AddModel() => Models.Add(new ProviderModelEntryViewModel());

    [RelayCommand]
    private void RemoveModel(ProviderModelEntryViewModel? entry)
    {
        if (entry != null)
            Models.Remove(entry);
    }

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private void FetchModels()
    {
        string[] defaults = IsCustomMode ? [] : SelectedCatalogEntry?.DefaultModels ?? [];
        if (defaults.Length > 0)
        {
            Models.Clear();
            foreach (var id in defaults)
                Models.Add(new ProviderModelEntryViewModel { Id = id });
            ModelsSubtitle = "正在使用适配器默认模型";
        }
        else
        {
            ModelsSubtitle = "未获取到模型；可手动添加。";
        }
    }

    public void Load(ProviderViewModel? provider)
    {
        _editing = provider;
        if (provider == null)
            return;

        ApiKey = provider.ApiKey;
        BaseUrl = provider.BaseUrl;
        Protocol = provider.Protocol;
        foreach (var model in provider.Models)
            Models.Add(new ProviderModelEntryViewModel { Id = model.Id });

        if (provider.Kind == ProviderKind.Custom)
        {
            ModeIndex = 1;
            ProviderId = provider.Id;
            DisplayName = provider.Name;
        }
        else
        {
            ModeIndex = 0;
            SelectedCatalogEntry = Catalog.FirstOrDefault(e => e.Id == provider.Id) ?? SelectedCatalogEntry;
        }
    }

    public ProviderViewModel BuildProvider()
    {
        var target = _editing ?? new ProviderViewModel { Id = IsCustomMode ? ProviderId : SelectedCatalogEntry!.Id };
        if (IsCustomMode)
        {
            target.Kind = ProviderKind.Custom;
            target.Name = string.IsNullOrWhiteSpace(DisplayName) ? ProviderId : DisplayName;
            target.IsConfigured = !string.IsNullOrEmpty(ApiKey);
        }
        else
        {
            target.Kind = ProviderKind.Catalog;
            target.Name = SelectedCatalogEntry!.DisplayName;
            target.IsConfigured = !string.IsNullOrEmpty(ApiKey)
                || Environment.GetEnvironmentVariable(SelectedCatalogEntry.EnvKeyVar) != null;
        }

        target.ApiKey = ApiKey;
        target.BaseUrl = BaseUrl;
        target.Protocol = Protocol;
        target.Models.Clear();
        foreach (var model in Models.Where(m => !string.IsNullOrWhiteSpace(m.Id)))
            target.Models.Add(new ProviderModelEntryViewModel { Id = model.Id });
        return target;
    }
}