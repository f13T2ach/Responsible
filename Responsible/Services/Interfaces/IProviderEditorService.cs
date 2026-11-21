using Responsible.ViewModel;

namespace Responsible.Services;

public interface IProviderEditorService
{
    Task EditAsync(ProviderViewModel? existing);
}
