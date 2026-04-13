using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nevolution.Core.Models;

public sealed class EmailMessage : INotifyPropertyChanged
{
    private bool _hasBody;
    private bool _bodyUnavailable;
    private bool _deletedOnServer;
    private bool _isRead;
    private string _textBody = string.Empty;
    private string _htmlBody = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string AccountId { get; set; } = string.Empty;

    public string Folder { get; set; } = string.Empty;

    public uint ImapUid { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    public bool IsRead
    {
        get => _isRead;
        set
        {
            if (_isRead == value)
            {
                return;
            }

            _isRead = value;
            OnPropertyChanged();
        }
    }

    public bool HasBody
    {
        get => _hasBody;
        set
        {
            if (_hasBody == value)
            {
                return;
            }

            _hasBody = value;
            OnPropertyChanged();
        }
    }

    public bool BodyUnavailable
    {
        get => _bodyUnavailable;
        set
        {
            if (_bodyUnavailable == value)
            {
                return;
            }

            _bodyUnavailable = value;
            OnPropertyChanged();
        }
    }

    public bool DeletedOnServer
    {
        get => _deletedOnServer;
        set
        {
            if (_deletedOnServer == value)
            {
                return;
            }

            _deletedOnServer = value;
            OnPropertyChanged();
        }
    }

    public string TextBody
    {
        get => _textBody;
        set
        {
            if (string.Equals(_textBody, value, StringComparison.Ordinal))
            {
                return;
            }

            _textBody = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Body));
        }
    }

    public string HtmlBody
    {
        get => _htmlBody;
        set
        {
            if (string.Equals(_htmlBody, value, StringComparison.Ordinal))
            {
                return;
            }

            _htmlBody = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Body));
        }
    }

    public string Body
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(TextBody))
            {
                return TextBody;
            }

            return HtmlBody;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
