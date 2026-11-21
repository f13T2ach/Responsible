using CommunityToolkit.Mvvm.ComponentModel;
using Responsible.Services;

namespace Responsible.ViewModel;

public partial class AclToolItemViewModel : ObservableObject
{
    private string _operation = "授权";

    public AclToolItemViewModel(AclToolGrant grant)
    {
        Grant = grant;
        State = grant.State;
        StatusText = grant.State == AclGrantState.Authorized
            ? "已授权"
            : grant.State == AclGrantState.Pending
                ? "待授权"
                : "需要重新授权";
        Error = grant.LastError ?? string.Empty;
    }

    public AclToolGrant Grant { get; private set; }

    public string Name => Grant.Name;

    public string Path => Grant.Path;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool IsProgressVisible => IsBusy;

    public bool IsProgressIndeterminate { get; private set; }

    public bool CanRetry => !IsBusy && State == AclGrantState.Error;

    public bool CanRemove => !IsBusy;

    public bool LastOperationWasRevoke { get; private set; }

    public string RetryTip => LastOperationWasRevoke ? "继续撤销" : "重新授权";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private AclGrantState _state;

    private int Processed { get; set; }

    private int Total { get; set; }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProgressVisible));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemove));
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnStateChanged(AclGrantState value)
    {
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemove));
    }

    public void BeginGrant()
    {
        _operation = "授权";
        LastOperationWasRevoke = false;
        OnPropertyChanged(nameof(RetryTip));
        Begin("正在统计对象…");
    }

    public void BeginRevoke()
    {
        _operation = "撤销";
        LastOperationWasRevoke = true;
        OnPropertyChanged(nameof(RetryTip));
        Begin("正在统计对象…");
    }

    public void ApplyProgress(AclProgress progress)
    {
        Processed = progress.Processed;
        Total = progress.Total;
        IsProgressIndeterminate = Total == 0;
        ProgressValue = Total == 0 ? 0 : Math.Clamp(Processed * 100.0 / Total, 0, 100);
        StatusText = IsProgressIndeterminate
            ? "正在统计对象…"
            : $"{_operation}中 {Processed}/{Total}";
    }

    public void Complete(AclGrantState state, string message)
    {
        IsBusy = false;
        IsProgressIndeterminate = false;
        State = state;
        if (state == AclGrantState.Authorized)
        {
            Error = string.Empty;
            ProgressValue = 100;
            StatusText = "已授权";
        }
        else
        {
            Error = message;
            StatusText = _operation == "撤销" ? "撤销失败" : "授权失败";
            ProgressValue = 0;
        }
    }

    private void Begin(string status)
    {
        IsBusy = true;
        State = AclGrantState.Pending;
        Error = string.Empty;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        Processed = 0;
        Total = 0;
        StatusText = status;
    }
}