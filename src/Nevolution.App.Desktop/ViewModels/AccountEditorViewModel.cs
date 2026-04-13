using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nevolution.Core.Models;
using Nevolution.Core.Resources;

namespace Nevolution.App.Desktop.ViewModels;

public sealed class AccountEditorViewModel : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private string _email = string.Empty;
    private string _imapHost = string.Empty;
    private string _imapPort = "993";
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _useAsActive = true;
    private bool _isBusy;
    private string _validationMessage = Strings.AccountDialog_ErrorDisplayNameRequired;
    private string _saveErrorMessage = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (string.Equals(_displayName, value, StringComparison.Ordinal))
            {
                return;
            }

            _displayName = value;
            Validate();
            OnPropertyChanged();
        }
    }

    public string Email
    {
        get => _email;
        set
        {
            if (string.Equals(_email, value, StringComparison.Ordinal))
            {
                return;
            }

            var previousEmail = _email;
            _email = value;

            if (string.IsNullOrWhiteSpace(_username) || string.Equals(_username, previousEmail, StringComparison.OrdinalIgnoreCase))
            {
                _username = value;
                OnPropertyChanged(nameof(Username));
            }

            Validate();
            OnPropertyChanged();
        }
    }

    public string ImapHost
    {
        get => _imapHost;
        set
        {
            if (string.Equals(_imapHost, value, StringComparison.Ordinal))
            {
                return;
            }

            _imapHost = value;
            Validate();
            OnPropertyChanged();
        }
    }

    public string ImapPort
    {
        get => _imapPort;
        set
        {
            if (string.Equals(_imapPort, value, StringComparison.Ordinal))
            {
                return;
            }

            _imapPort = value;
            Validate();
            OnPropertyChanged();
        }
    }

    public string Username
    {
        get => _username;
        set
        {
            if (string.Equals(_username, value, StringComparison.Ordinal))
            {
                return;
            }

            _username = value;
            Validate();
            OnPropertyChanged();
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (string.Equals(_password, value, StringComparison.Ordinal))
            {
                return;
            }

            _password = value;
            Validate();
            OnPropertyChanged();
        }
    }

    public bool UseAsActive
    {
        get => _useAsActive;
        set
        {
            if (_useAsActive == value)
            {
                return;
            }

            _useAsActive = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSubmit));
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (string.Equals(_validationMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            _validationMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasValidationMessage));
        }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public string SaveErrorMessage
    {
        get => _saveErrorMessage;
        private set
        {
            if (string.Equals(_saveErrorMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            _saveErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSaveErrorMessage));
        }
    }

    public bool HasSaveErrorMessage => !string.IsNullOrWhiteSpace(SaveErrorMessage);

    public bool CanSubmit => !IsBusy && string.IsNullOrWhiteSpace(ValidationMessage);

    public MailAccount CreateAccount()
    {
        Validate();

        if (!CanSubmit)
        {
            throw new InvalidOperationException(ValidationMessage);
        }

        return new MailAccount
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = DisplayName.Trim(),
            Email = Email.Trim(),
            ImapHost = ImapHost.Trim(),
            ImapPort = int.Parse(ImapPort.Trim()),
            Username = Username.Trim(),
            Password = Password,
            IsActive = UseAsActive
        };
    }

    public void SetSaveError(string? message)
    {
        SaveErrorMessage = message ?? string.Empty;
    }

    private void Validate()
    {
        SaveErrorMessage = string.Empty;

        ValidationMessage = string.IsNullOrWhiteSpace(DisplayName)
            ? Strings.AccountDialog_ErrorDisplayNameRequired
            : string.IsNullOrWhiteSpace(Email)
                ? Strings.AccountDialog_ErrorEmailRequired
                : string.IsNullOrWhiteSpace(ImapHost)
                    ? Strings.AccountDialog_ErrorImapHostRequired
                    : !int.TryParse(ImapPort, out var parsedPort) || parsedPort <= 0 || parsedPort > 65535
                        ? Strings.AccountDialog_ErrorImapPortInvalid
                        : string.IsNullOrWhiteSpace(Username)
                            ? Strings.AccountDialog_ErrorUsernameRequired
                            : string.IsNullOrWhiteSpace(Password)
                                ? Strings.AccountDialog_ErrorPasswordRequired
                                : string.Empty;

        OnPropertyChanged(nameof(CanSubmit));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
