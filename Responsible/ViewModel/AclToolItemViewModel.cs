using CommunityToolkit.Mvvm.ComponentModel;
using Responsible.Localization;
using Responsible.Services;

namespace Responsible.ViewModel;

public partial class AclToolItemViewModel : ObservableObject
{
    public AclToolItemViewModel(AclToolGrant grant)
    {
        Grant = grant;
        State = grant.State;
        IsValid = grant.IsValid;
        StatusText = !grant.IsValid
            ? Loc.T("L.Acl.InvalidStartup")
            : grant.State == AclGrantState.Authorized
                ? Loc.T("L.Acl.Granted")
                : grant.State == AclGrantState.Pending
                    ? Loc.T("L.Acl.Pending")
                    : Loc.T("L.Acl.Regrant");
        Error = grant.LastError ?? string.Empty;
    }

    public AclToolGrant Grant { get; private set; }

    public string Name => Grant.Name;

    public string Path => Grant.Path;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool IsProgressVisible => IsBusy;

    public bool IsProgressIndeterminate { get; private set; }

    public bool CanRetry => !IsBusy && (State == AclGrantState.Error || !IsValid);

    public bool CanRemove => !IsBusy;

    public bool LastOperationWasRevoke { get; private set; }

    public string RetryTip => LastOperationWasRevoke ? Loc.T("L.Acl.RetryRevoke") : Loc.T("L.Acl.RetryGrant");

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    private bool _isValid;

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
        LastOperationWasRevoke = false;
        OnPropertyChanged(nameof(RetryTip));
        Begin(Loc.T("L.Acl.Writing"));
    }

    public void BeginRevoke()
    {
        LastOperationWasRevoke = true;
        OnPropertyChanged(nameof(RetryTip));
        Begin(Loc.T("L.Acl.Cleaning"));
    }

    public void ApplyProgress(AclProgress progress)
    {
        Processed = progress.Processed;
        Total = progress.Total;
        IsProgressIndeterminate = Total == 0;
        ProgressValue = Total == 0 ? 0 : Math.Clamp(Processed * 100.0 / Total, 0, 100);
        StatusText = IsProgressIndeterminate
            ? Loc.T("L.Acl.Counting")
            : Loc.F("L.Acl.InProgress",
                Loc.T(LastOperationWasRevoke ? "L.Acl.RevokeOperation" : "L.Acl.GrantOperation"),
                Processed, Total);
    }

    public void Complete(AclGrantState state, string message)
    {
        IsBusy = false;
        IsProgressIndeterminate = false;
        State = state;
        if (state == AclGrantState.Authorized)
        {
            IsValid = true;
            Grant = Grant with { IsValid = true };
            Error = string.Empty;
            ProgressValue = 100;
            StatusText = Loc.T("L.Acl.Granted");
        }
        else
        {
            Error = message;
            StatusText = Loc.T(LastOperationWasRevoke ? "L.Acl.RevokeFailed" : "L.Acl.GrantFailed");
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