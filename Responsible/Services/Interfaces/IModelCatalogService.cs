using System.Collections.ObjectModel;
using Responsible.ViewModel;

namespace Responsible.Services;

public sealed record ProviderCatalogEntry(
    string Id,
    string DisplayName,
    string EnvKeyVar,
    string[] RegionVars,
    string[] DefaultModels);

public interface IModelCatalogService
{
    ObservableCollection<ProviderViewModel> Providers { get; }

    ObservableCollection<ModelOptionViewModel> ModelOptions { get; }

    IReadOnlyList<ProviderCatalogEntry> Catalog { get; }

    void AddOrUpdate(ProviderViewModel provider);

    void Remove(ProviderViewModel provider);

    void OpenConfigFile();

    // 用 DSH 运行时注册的 provider/model 目录刷新选择器；凭据仍由 DSH 自己解析
    void ApplyDshModels(IReadOnlyList<DshModelRow> models);
}