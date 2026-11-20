namespace Responsible.Services;

public interface INavigationService
{
    void NavigateTo<TViewModel>() where TViewModel : class;
}