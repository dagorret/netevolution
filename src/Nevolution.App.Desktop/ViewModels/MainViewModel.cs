using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using Nevolution.Core;
using Nevolution.Core.Abstractions;
using Nevolution.Core.Localization;
using Nevolution.Core.Models;
using Nevolution.Core.Resources;

namespace Nevolution.App.Desktop.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const int PageSize = 100;

    private readonly IEmailRepository _repository;
    private readonly IMailClient _folderMailClient;
    private readonly SyncService _syncService;
    private readonly BackgroundBodySyncService _bodySyncService;
    private readonly Dictionary<string, MailAccount> _accountLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _displayBodyCache = new(StringComparer.Ordinal);
    private readonly List<EmailMessage> _allEmails = [];
    private readonly string _dataDirectory;
    private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
    private EmailMessage? _selectedEmail;
    private MailAccount? _selectedAccount;
    private MailFolderInfo? _selectedFolder;
    private LanguageOption? _selectedLanguage;
    private string _filterText = string.Empty;
    private bool _showUnreadOnly;
    private bool _selectedEmailUsesHtmlFallback;
    private bool _isLoadingMore;
    private bool _hasMoreEmails;
    private bool _selectedEmailIsLoadingBody;
    private string _accountStatusMessage = string.Empty;
    private string _listStatusMessage = string.Empty;
    private string _lastAccountErrorSignature = string.Empty;
    private Func<string>? _accountStatusFactory;
    private Func<string>? _listStatusFactory;
    private CancellationTokenSource? _selectedEmailLoadCts;
    private CancellationTokenSource? _folderChangeCts;
    private CancellationTokenSource? _backgroundDownloadCts;
    private int _contextVersion;
    private bool _hasCompletedInitialFolderLoad;
    private bool _hasLoggedStartupEmailsVisible;
    private bool _isInitialImapSyncInProgress;
    private long _windowOpenedOnViewModelElapsedMs = -1;

    public MainViewModel(
        IEmailRepository repository,
        IMailClient folderMailClient,
        SyncService syncService,
        BackgroundBodySyncService bodySyncService,
        string dataDirectory)
    {
        _repository = repository;
        _folderMailClient = folderMailClient;
        _syncService = syncService;
        _bodySyncService = bodySyncService;
        _dataDirectory = dataDirectory;
        Console.WriteLine("Desktop startup: MainViewModel ctor");
        _bodySyncService.BodyDownloaded += OnBodyDownloaded;
        _bodySyncService.DownloadFailed += OnBackgroundDownloadFailed;
        AppCulture.CultureChanged += OnCultureChanged;
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, () => HasMoreEmails && !IsLoadingMore && SelectedAccount is not null && SelectedFolder is not null);
        RebuildLanguageOptions();
        Console.WriteLine("Desktop startup: MainViewModel ctor completed");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MailAccount> Accounts { get; } = [];

    public ObservableCollection<MailFolderInfo> Folders { get; } = [];

    public ObservableCollection<EmailMessage> Emails { get; } = [];

    public ObservableCollection<LanguageOption> AvailableLanguages { get; } = [];

    public ICommand LoadMoreCommand { get; }

    public LanguageOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || ReferenceEquals(_selectedLanguage, value))
            {
                return;
            }

            _selectedLanguage = value;
            OnPropertyChanged();
            ApplySelectedLanguage(value);
        }
    }

    public MailAccount? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (ReferenceEquals(_selectedAccount, value))
            {
                return;
            }

            _selectedAccount = value;
            OnPropertyChanged();
            _ = HandleSelectedAccountChangedAsync(value);
        }
    }

    public MailFolderInfo? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (ReferenceEquals(_selectedFolder, value))
            {
                return;
            }

            _selectedFolder = value;
            OnPropertyChanged();
            _ = HandleSelectedFolderChangedAsync(value);
        }
    }

    public EmailMessage? SelectedEmail
    {
        get => _selectedEmail;
        set
        {
            if (ReferenceEquals(_selectedEmail, value))
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var previousEmailId = _selectedEmail?.Id ?? "-";
            CancelSelectedEmailLoad();
            DetachSelectedEmail();
            _selectedEmail = value;
            AttachSelectedEmail(value);
            UpdateSelectedEmailLoadingState(value);
            OnPropertyChanged();
            NotifySelectedEmailContentChanged();
            Console.WriteLine(
                $"Email selection UI: previous={previousEmailId}, next={value?.Id ?? "-"}, elapsedMs={stopwatch.ElapsedMilliseconds}, hasLocalBody={HasUsableBody(value)}");

            if (value is not null)
            {
                _ = HandleSelectedEmailChangedAsync(value);
            }
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (string.Equals(_filterText, value, StringComparison.Ordinal))
            {
                return;
            }

            _filterText = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public bool ShowUnreadOnly
    {
        get => _showUnreadOnly;
        set
        {
            if (_showUnreadOnly == value)
            {
                return;
            }

            _showUnreadOnly = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public string SelectedEmailBody
    {
        get
        {
            if (SelectedEmail is null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(SelectedEmail.TextBody))
            {
                return SelectedEmail.TextBody;
            }

            if (!string.IsNullOrWhiteSpace(SelectedEmail.HtmlBody))
            {
                if (!_displayBodyCache.TryGetValue(SelectedEmail.Id, out var displayBody))
                {
                    displayBody = HtmlBodyConverter.ToDisplayText(SelectedEmail.HtmlBody);
                    _displayBodyCache[SelectedEmail.Id] = displayBody;
                }

                return displayBody;
            }

            return SelectedEmailIsLoadingBody ? Strings.MailBody_Loading : string.Empty;
        }
    }

    public bool SelectedEmailUsesHtmlFallback
    {
        get => _selectedEmailUsesHtmlFallback;
        private set
        {
            if (_selectedEmailUsesHtmlFallback == value)
            {
                return;
            }

            _selectedEmailUsesHtmlFallback = value;
            OnPropertyChanged();
        }
    }

    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        private set
        {
            if (_isLoadingMore == value)
            {
                return;
            }

            _isLoadingMore = value;
            OnPropertyChanged();
            RaiseLoadMoreCanExecuteChanged();
        }
    }

    public bool HasMoreEmails
    {
        get => _hasMoreEmails;
        private set
        {
            if (_hasMoreEmails == value)
            {
                return;
            }

            _hasMoreEmails = value;
            OnPropertyChanged();
            RaiseLoadMoreCanExecuteChanged();
        }
    }

    public bool SelectedEmailIsLoadingBody
    {
        get => _selectedEmailIsLoadingBody;
        private set
        {
            if (_selectedEmailIsLoadingBody == value)
            {
                return;
            }

            _selectedEmailIsLoadingBody = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedEmailBody));
        }
    }

    public string AccountStatusMessage
    {
        get => _accountStatusMessage;
        private set
        {
            if (string.Equals(_accountStatusMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            _accountStatusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAccountStatusMessage));
        }
    }

    public bool HasAccountStatusMessage => !string.IsNullOrWhiteSpace(AccountStatusMessage);

    public string ListStatusMessage
    {
        get => _listStatusMessage;
        private set
        {
            if (string.Equals(_listStatusMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            _listStatusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasListStatusMessage));
        }
    }

    public bool HasListStatusMessage => !string.IsNullOrWhiteSpace(ListStatusMessage);

    public void NotifyWindowOpened(long elapsedMs)
    {
        _windowOpenedOnViewModelElapsedMs = _startupStopwatch.ElapsedMilliseconds;
        Console.WriteLine($"Desktop startup: window visible elapsedMs={elapsedMs}, viewModelElapsedMs={_windowOpenedOnViewModelElapsedMs}");
    }

    public async Task InitializeAsync()
    {
        var startupStopwatch = Stopwatch.StartNew();
        Console.WriteLine("Desktop startup: MainViewModel.InitializeAsync started");

        try
        {
            await LoadAccountsAsync();
            Console.WriteLine($"Desktop startup: accounts loaded elapsedMs={startupStopwatch.ElapsedMilliseconds}, count={Accounts.Count}");

            var activeAccount = Accounts.FirstOrDefault(account => account.IsActive) ?? Accounts.FirstOrDefault();
            Console.WriteLine($"Desktop startup: active account resolved accountId={activeAccount?.Id ?? "-"}");
            SelectedAccount = activeAccount;

            if (activeAccount is null)
            {
                await ResetFolderAndEmailsAsync();
                SetAccountStatusResource(nameof(Strings.Status_NoActiveAccount));
                SetListStatusResource(nameof(Strings.Status_NoLocalEmailsYet));
            }

            Console.WriteLine($"Desktop startup: MainViewModel.InitializeAsync completed elapsedMs={startupStopwatch.ElapsedMilliseconds}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Desktop startup: MainViewModel.InitializeAsync failed: {exception}");
            SetAccountStatusResource(nameof(Strings.Status_InitialLoadFailed));
            throw;
        }
    }

    private async Task LoadAccountsAsync()
    {
        Console.WriteLine("Desktop startup: LoadAccountsAsync started");
        var accounts = await _repository.GetAccountsAsync();
        var selectedAccountId = SelectedAccount?.Id;

        Accounts.Clear();
        _accountLookup.Clear();

        foreach (var account in accounts)
        {
            Accounts.Add(account);
            _accountLookup[account.Id] = account;
        }

        if (selectedAccountId is null)
        {
            Console.WriteLine($"Desktop startup: LoadAccountsAsync completed with {Accounts.Count} account(s)");
            return;
        }

        var refreshedSelectedAccount = Accounts.FirstOrDefault(account => account.Id == selectedAccountId);

        if (refreshedSelectedAccount is not null && !ReferenceEquals(refreshedSelectedAccount, _selectedAccount))
        {
            _selectedAccount = refreshedSelectedAccount;
            OnPropertyChanged(nameof(SelectedAccount));
        }

        Console.WriteLine($"Desktop startup: LoadAccountsAsync completed with {Accounts.Count} account(s)");
    }

    private async Task HandleSelectedAccountChangedAsync(MailAccount? account)
    {
        var version = Interlocked.Increment(ref _contextVersion);
        CancelFolderChangeOperations();
        CancelBackgroundDownload();

        if (account is null)
        {
            await ResetFolderAndEmailsAsync();
            SetAccountStatusResource(nameof(Strings.Status_NoActiveAccount));
            return;
        }

        ClearAccountStatus();

        try
        {
            await _repository.SetActiveAccountAsync(account.Id);
            SetActiveAccountLocally(account.Id);
            LoadFoldersLocally(account, version);
            _ = RefreshFoldersFromServerAsync(account, version);
        }
        catch (ImapConnectionException exception)
        {
            HandleImapException(exception, logPrefix: "SelectedAccount");
            await ResetFolderAndEmailsAsync();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"SelectedAccount error for {account.Id}: {exception}");
            SetAccountStatusResource(nameof(Strings.Status_CannotLoadActiveAccount));
            await ResetFolderAndEmailsAsync();
        }
    }

    private void LoadFoldersLocally(MailAccount account, int version)
    {
        Console.WriteLine($"Desktop startup: applying local folders accountId={account.Id}, source=defaults");
        ApplyFolders(MailFolderCatalog.Defaults, version, source: "local-defaults");
    }

    private async Task RefreshFoldersFromServerAsync(MailAccount account, int version)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var folders = await _folderMailClient.GetKnownFoldersAsync(account);
            ApplyFolders(folders, version, source: "imap");
            Console.WriteLine($"Folder list IMAP refresh completed: accountId={account.Id}, elapsedMs={stopwatch.ElapsedMilliseconds}, count={folders.Count}");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Folder list IMAP refresh cancelled: accountId={account.Id}");
        }
        catch (ImapConnectionException exception)
        {
            Console.WriteLine($"Folder list IMAP refresh failed: accountId={account.Id}, failure={exception.FailureKind}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"LoadFolders error for {account.Id}: {exception}");
        }
    }

    private void ApplyFolders(IReadOnlyList<MailFolderInfo> folders, int version, string source)
    {
        if (version != Volatile.Read(ref _contextVersion))
        {
            return;
        }

        var previousKind = SelectedFolder?.Kind ?? MailFolderKind.Inbox;

        Folders.Clear();

        foreach (var folder in folders)
        {
            Folders.Add(folder);
        }

        var selectedFolder = Folders.FirstOrDefault(folder => folder.Kind == previousKind)
            ?? Folders.FirstOrDefault(folder => folder.Kind == MailFolderKind.Inbox)
            ?? Folders.FirstOrDefault();

        Console.WriteLine($"Desktop startup: initial folder resolved accountId={SelectedAccount?.Id ?? "-"}, folder={selectedFolder?.ImapFolderName ?? "-"}, source={source}");

        if (selectedFolder is null)
        {
            SelectedFolder = null;
            return;
        }

        if (_selectedFolder is not null
            && _selectedFolder.Kind == selectedFolder.Kind
            && string.Equals(_selectedFolder.ImapFolderName, selectedFolder.ImapFolderName, StringComparison.OrdinalIgnoreCase))
        {
            _selectedFolder = selectedFolder;
            OnPropertyChanged(nameof(SelectedFolder));
            return;
        }

        SelectedFolder = selectedFolder;
    }

    private async Task HandleSelectedFolderChangedAsync(MailFolderInfo? folder)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var isInitialFolderLoad = !_hasCompletedInitialFolderLoad;
        var version = Interlocked.Increment(ref _contextVersion);
        CancelFolderChangeOperations();
        CancelBackgroundDownload();

        if (SelectedAccount is null || folder is null)
        {
            await ResetEmailsAsync();
            return;
        }

        _folderChangeCts = new CancellationTokenSource();
        var cancellationToken = _folderChangeCts.Token;

        ClearAccountStatus();
        ClearListStatus();
        var preferredSelectedEmailId = SelectedEmail?.Id;

        var sqliteStopwatch = Stopwatch.StartNew();
        await ReloadEmailsAsync(
            reset: true,
            contextVersion: version,
            preferredSelectedEmailId: preferredSelectedEmailId,
            cancellationToken: cancellationToken);
        Console.WriteLine(
            $"Folder change SQLite load: accountId={SelectedAccount.Id}, folder={folder.ImapFolderName}, elapsedMs={sqliteStopwatch.ElapsedMilliseconds}, totalUiElapsedMs={totalStopwatch.ElapsedMilliseconds}, initial={isInitialFolderLoad}, visibleEmails={Emails.Count}");

        if (cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _contextVersion))
        {
            Console.WriteLine($"Folder change discarded after SQLite load: accountId={SelectedAccount.Id}, folder={folder.ImapFolderName}, reason=obsolete");
            return;
        }

        _hasCompletedInitialFolderLoad = true;
        UpdateListStatusAfterEmailChange(isSyncInProgress: true);
        LogStartupEmailsVisibleIfNeeded(source: "sqlite-initial");

        StartBackgroundDownload(SelectedAccount, folder);
        _ = SyncFolderInBackgroundAsync(SelectedAccount, folder, version, preferredSelectedEmailId, totalStopwatch, cancellationToken);
    }

    private async Task ReloadEmailsAsync(
        bool reset,
        int? contextVersion = null,
        string? preferredSelectedEmailId = null,
        CancellationToken cancellationToken = default)
    {
        if (SelectedAccount is null || SelectedFolder is null)
        {
            await ResetEmailsAsync();
            return;
        }

        var selectedEmailIdToRestore = preferredSelectedEmailId ?? SelectedEmail?.Id;

        if (IsLoadingMore)
        {
            return;
        }

        IsLoadingMore = true;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loadedCount = reset ? 0 : _allEmails.Count;
            var emails = await _repository.GetEmailsAsync(
                SelectedAccount.Id,
                SelectedFolder.ImapFolderName,
                PageSize,
                loadedCount);

            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine(
                    $"Folder load cancelled before apply: accountId={SelectedAccount.Id}, folder={SelectedFolder.ImapFolderName}, reset={reset}");
                return;
            }

            if (contextVersion.HasValue && contextVersion.Value != Volatile.Read(ref _contextVersion))
            {
                Console.WriteLine(
                    $"Folder load discarded as obsolete: accountId={SelectedAccount.Id}, folder={SelectedFolder.ImapFolderName}, reset={reset}");
                return;
            }

            if (reset)
            {
                _allEmails.Clear();
                Emails.Clear();
                SelectedEmail = null;
                HasMoreEmails = false;
            }

            foreach (var email in emails)
            {
                _allEmails.Add(email);
            }

            HasMoreEmails = emails.Count == PageSize;
            ApplyFilter();
            UpdateListStatusAfterEmailChange(isSyncInProgress: _isInitialImapSyncInProgress);

            if (reset)
            {
                SelectedEmail = selectedEmailIdToRestore is null
                    ? Emails.FirstOrDefault()
                    : Emails.FirstOrDefault(email => email.Id == selectedEmailIdToRestore) ?? Emails.FirstOrDefault();
            }
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private async Task SyncFolderInBackgroundAsync(
        MailAccount account,
        MailFolderInfo folder,
        int version,
        string? preferredSelectedEmailId,
        Stopwatch totalStopwatch,
        CancellationToken cancellationToken)
    {
        var syncStopwatch = Stopwatch.StartNew();
        _isInitialImapSyncInProgress = true;
        UpdateListStatusAfterEmailChange(isSyncInProgress: true);
        Console.WriteLine($"Desktop startup: initial IMAP sync queued accountId={account.Id}, folder={folder.ImapFolderName}, elapsedSinceStartMs={_startupStopwatch.ElapsedMilliseconds}");

        try
        {
            Console.WriteLine($"Folder sync started: accountId={account.Id}, folder={folder.ImapFolderName}");
            await _syncService.SyncFolderAsync(account, folder.ImapFolderName, cancellationToken);
            Console.WriteLine(
                $"Folder change IMAP sync: accountId={account.Id}, folder={folder.ImapFolderName}, elapsedMs={syncStopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Folder sync cancelled: accountId={account.Id}, folder={folder.ImapFolderName}");
            return;
        }
        catch (ImapConnectionException exception)
        {
            HandleImapException(exception, logPrefix: "SelectedFolder");
            return;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"SelectedFolder sync error for {folder.ImapFolderName}: {exception}");
            SetAccountStatusResource(nameof(Strings.Status_CannotSyncActiveFolder));
            return;
        }
        finally
        {
            _isInitialImapSyncInProgress = false;
            UpdateListStatusAfterEmailChange(isSyncInProgress: false);
        }

        if (cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _contextVersion))
        {
            Console.WriteLine($"Folder sync result discarded: accountId={account.Id}, folder={folder.ImapFolderName}, reason=obsolete");
            return;
        }

        var refreshStopwatch = Stopwatch.StartNew();
        await ReloadEmailsAsync(
            reset: true,
            contextVersion: version,
            preferredSelectedEmailId: preferredSelectedEmailId,
            cancellationToken: cancellationToken);
        LogStartupEmailsVisibleIfNeeded(source: "imap-refresh");
        Console.WriteLine(
            $"Folder change refresh after sync: accountId={account.Id}, folder={folder.ImapFolderName}, elapsedMs={refreshStopwatch.ElapsedMilliseconds}, totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");

        if (cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _contextVersion))
        {
            Console.WriteLine($"Folder refresh discarded: accountId={account.Id}, folder={folder.ImapFolderName}, reason=obsolete");
            return;
        }

        StartBackgroundDownload(account, folder);
        UpdateListStatusAfterEmailChange(isSyncInProgress: false);
    }

    private async Task LoadMoreAsync()
    {
        await ReloadEmailsAsync(
            reset: false,
            contextVersion: Volatile.Read(ref _contextVersion),
            cancellationToken: _folderChangeCts?.Token ?? CancellationToken.None);
    }

    private void ApplyFilter()
    {
        var filterText = string.IsNullOrWhiteSpace(FilterText) ? null : FilterText.Trim();

        var filteredEmails = _allEmails.Where(email =>
        {
            var matchesText = filterText is null
                || (email.Subject?.Contains(filterText, StringComparison.OrdinalIgnoreCase) ?? false);
            var matchesUnread = !ShowUnreadOnly || !email.IsRead;

            return matchesText && matchesUnread;
        }).ToList();

        var selectedId = SelectedEmail?.Id;
        Emails.Clear();

        foreach (var email in filteredEmails)
        {
            Emails.Add(email);
        }

        if (selectedId is null)
        {
            NotifySelectedEmailContentChanged();
            return;
        }

        var nextSelected = Emails.FirstOrDefault(email => email.Id == selectedId);

        if (!ReferenceEquals(_selectedEmail, nextSelected))
        {
            SelectedEmail = nextSelected;
            return;
        }

        NotifySelectedEmailContentChanged();
    }

    private async Task HandleSelectedEmailChangedAsync(EmailMessage? email)
    {
        if (email is null)
        {
            return;
        }

        var selectionTokenSource = new CancellationTokenSource();
        _selectedEmailLoadCts = selectionTokenSource;

        if (!email.IsRead)
        {
            _ = MarkSelectedEmailAsReadAsync(email);
        }

        if (!HasUsableBody(email))
        {
            _ = LoadBodyAsync(email, selectionTokenSource.Token);
        }
    }

    private async Task MarkSelectedEmailAsReadAsync(EmailMessage email)
    {
        if (email.IsRead)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        email.IsRead = true;
        OnPropertyChanged(nameof(SelectedEmail));
        ApplyFilter();

        try
        {
            await _repository.MarkAsReadAsync(email.Id);
            Console.WriteLine(
                $"Email mark read: emailId={email.Id}, accountId={email.AccountId}, folder={email.Folder}, elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch
        {
            email.IsRead = false;
            OnPropertyChanged(nameof(SelectedEmail));
            ApplyFilter();
            throw;
        }
    }

    private async Task LoadBodyAsync(EmailMessage email, CancellationToken cancellationToken)
    {
        if (HasUsableBody(email))
        {
            UpdateSelectedEmailLoadingState(ReferenceEquals(email, SelectedEmail) ? email : null);
            return;
        }

        var account = SelectedAccount is not null && string.Equals(SelectedAccount.Id, email.AccountId, StringComparison.Ordinal)
            ? SelectedAccount
            : _accountLookup.GetValueOrDefault(email.AccountId);

        if (account is null || email.ImapUid == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        UpdateSelectedEmailLoadingState(ReferenceEquals(email, SelectedEmail) ? email : null);
        Console.WriteLine(
            $"Email body request queued: emailId={email.Id}, accountId={email.AccountId}, folder={email.Folder}, uid={email.ImapUid}, hasBody={email.HasBody}");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = await _bodySyncService.EnsureBodyDownloadedAsync(account, email, cancellationToken: cancellationToken);
            ClearAccountStatus();

            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(email, SelectedEmail))
            {
                Console.WriteLine(
                    $"Email body request discarded: emailId={email.Id}, folder={email.Folder}, reason=obsolete-selection");
                return;
            }

            if (!body.HasContent)
            {
                Console.WriteLine(
                    $"Email IMAP body download completed without content: emailId={email.Id}, accountId={email.AccountId}, folder={email.Folder}, uid={email.ImapUid}, elapsedMs={stopwatch.ElapsedMilliseconds}");
                return;
            }

            ApplyDownloadedBody(email, body);
            OnPropertyChanged(nameof(SelectedEmail));
            NotifySelectedEmailContentChanged();
            Console.WriteLine(
                $"Email IMAP body download completed: emailId={email.Id}, accountId={email.AccountId}, folder={email.Folder}, uid={email.ImapUid}, elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Email body request cancelled: emailId={email.Id}, folder={email.Folder}");
        }
        catch (ImapConnectionException exception)
        {
            Console.WriteLine(
                $"Email IMAP body download retry scheduled: emailId={email.Id}, accountId={email.AccountId}, folder={email.Folder}, uid={email.ImapUid}, elapsedMs={stopwatch.ElapsedMilliseconds}, reason={exception.FailureKind}");

            try
            {
                var retryStopwatch = Stopwatch.StartNew();
                cancellationToken.ThrowIfCancellationRequested();
                var body = await _bodySyncService.EnsureBodyDownloadedAsync(account, email, allowBlockedAccountRetry: true, cancellationToken: cancellationToken);
                ClearAccountStatus();

                if (cancellationToken.IsCancellationRequested || !ReferenceEquals(email, SelectedEmail))
                {
                    Console.WriteLine(
                        $"Email body retry discarded: emailId={email.Id}, folder={email.Folder}, reason=obsolete-selection");
                    return;
                }

                if (!body.HasContent)
                {
                    Console.WriteLine(
                        $"Email IMAP body retry completed without content: emailId={email.Id}, accountId={email.AccountId}, folder={email.Folder}, uid={email.ImapUid}, elapsedMs={retryStopwatch.ElapsedMilliseconds}");
                    return;
                }

                ApplyDownloadedBody(email, body);
                OnPropertyChanged(nameof(SelectedEmail));
                NotifySelectedEmailContentChanged();
                Console.WriteLine(
                    $"Email IMAP body retry completed: emailId={email.Id}, accountId={email.AccountId}, folder={email.Folder}, uid={email.ImapUid}, elapsedMs={retryStopwatch.ElapsedMilliseconds}");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"Email body retry cancelled: emailId={email.Id}, folder={email.Folder}");
            }
            catch (ImapConnectionException retryException)
            {
                HandleImapException(retryException, logPrefix: "LoadBodyAsync");
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"LoadBodyAsync IMAP error for email {email.Id}: {exception}");
            SetAccountStatusResource(nameof(Strings.Status_CannotDownloadSelectedMailBody));
        }
        finally
        {
            UpdateSelectedEmailLoadingState(ReferenceEquals(email, SelectedEmail) ? email : null);
        }
    }

    private void StartBackgroundDownload(MailAccount account, MailFolderInfo folder)
    {
        CancelBackgroundDownload();
        _backgroundDownloadCts = new CancellationTokenSource();
        var token = _backgroundDownloadCts.Token;

        _ = RunBackgroundDownloadAsync(account, folder, token);
    }

    private async Task RunBackgroundDownloadAsync(MailAccount account, MailFolderInfo folder, CancellationToken cancellationToken)
    {
        try
        {
            await _bodySyncService.RunAsync(account, folder.ImapFolderName, batchSize: 10, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Background body download cancelled: accountId={account.Id}, folder={folder.ImapFolderName}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Background body download error for {account.Id}/{folder.ImapFolderName}: {exception}");
            SetAccountStatusResource(nameof(Strings.Status_BackgroundDownloadStopped));
        }
    }

    private async Task ResetFolderAndEmailsAsync()
    {
        CancelFolderChangeOperations();
        Folders.Clear();
        SelectedFolder = null;
        await ResetEmailsAsync();
    }

    private Task ResetEmailsAsync()
    {
        CancelSelectedEmailLoad();
        _allEmails.Clear();
        Emails.Clear();
        SelectedEmail = null;
        HasMoreEmails = false;
        ClearListStatus();
        return Task.CompletedTask;
    }

    private void CancelSelectedEmailLoad()
    {
        if (_selectedEmailLoadCts is null)
        {
            return;
        }

        if (!_selectedEmailLoadCts.IsCancellationRequested)
        {
            Console.WriteLine($"Email body request cancelled by new selection: emailId={_selectedEmail?.Id ?? "-"}");
            _selectedEmailLoadCts.Cancel();
        }

        _selectedEmailLoadCts.Dispose();
        _selectedEmailLoadCts = null;
    }

    private void CancelFolderChangeOperations()
    {
        CancelSelectedEmailLoad();

        if (_folderChangeCts is null)
        {
            return;
        }

        if (!_folderChangeCts.IsCancellationRequested)
        {
            Console.WriteLine($"Folder change cancelled: folder={_selectedFolder?.ImapFolderName ?? "-"}");
            _folderChangeCts.Cancel();
        }

        _folderChangeCts.Dispose();
        _folderChangeCts = null;
    }

    private void CancelBackgroundDownload()
    {
        if (_backgroundDownloadCts is null)
        {
            return;
        }

        _backgroundDownloadCts.Cancel();
        _backgroundDownloadCts.Dispose();
        _backgroundDownloadCts = null;
    }

    private void SetActiveAccountLocally(string accountId)
    {
        foreach (var account in Accounts)
        {
            account.IsActive = string.Equals(account.Id, accountId, StringComparison.Ordinal);
        }
    }

    private void OnBodyDownloaded(string emailId, EmailBody body)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ClearAccountStatus();
            var email = _allEmails.FirstOrDefault(item => string.Equals(item.Id, emailId, StringComparison.Ordinal));

            if (email is null)
            {
                return;
            }

            ApplyDownloadedBody(email, body);

            if (ReferenceEquals(email, SelectedEmail))
            {
                UpdateSelectedEmailLoadingState(email);
                NotifySelectedEmailContentChanged();
                OnPropertyChanged(nameof(SelectedEmail));
            }
        });
    }

    private void OnBackgroundDownloadFailed(ImapConnectionException exception)
    {
        Dispatcher.UIThread.Post(() => HandleImapException(exception, logPrefix: "BackgroundDownloader"));
    }

    private void ApplyDownloadedBody(EmailMessage email, EmailBody body)
    {
        _displayBodyCache.Remove(email.Id);
        email.TextBody = body.TextBody;
        email.HtmlBody = body.HtmlBody;
        email.HasBody = body.HasContent;
    }

    private void AttachSelectedEmail(EmailMessage? email)
    {
        if (email is null)
        {
            SelectedEmailUsesHtmlFallback = false;
            return;
        }

        email.PropertyChanged += OnSelectedEmailPropertyChanged;
        UpdateSelectedEmailMode(email);
    }

    private void DetachSelectedEmail()
    {
        if (_selectedEmail is null)
        {
            return;
        }

        _selectedEmail.PropertyChanged -= OnSelectedEmailPropertyChanged;
    }

    private void OnSelectedEmailPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not EmailMessage email || !ReferenceEquals(email, SelectedEmail))
        {
            return;
        }

        if (e.PropertyName is nameof(EmailMessage.TextBody)
            or nameof(EmailMessage.HtmlBody)
            or nameof(EmailMessage.HasBody)
            or nameof(EmailMessage.Body))
        {
            UpdateSelectedEmailLoadingState(email);
            NotifySelectedEmailContentChanged();
            return;
        }

        if (e.PropertyName == nameof(EmailMessage.IsRead))
        {
            OnPropertyChanged(nameof(SelectedEmail));
        }
    }

    private void NotifySelectedEmailContentChanged()
    {
        UpdateSelectedEmailMode(SelectedEmail);
        OnPropertyChanged(nameof(SelectedEmailBody));
    }

    private void UpdateSelectedEmailMode(EmailMessage? email)
    {
        SelectedEmailUsesHtmlFallback = email is not null
            && string.IsNullOrWhiteSpace(email.TextBody)
            && !string.IsNullOrWhiteSpace(email.HtmlBody);
    }

    private void UpdateSelectedEmailLoadingState(EmailMessage? email)
    {
        SelectedEmailIsLoadingBody = email is not null && !HasUsableBody(email);
    }

    private static bool HasUsableBody(EmailMessage? email)
    {
        return email is not null
            && (email.HasBody
                || !string.IsNullOrWhiteSpace(email.TextBody)
                || !string.IsNullOrWhiteSpace(email.HtmlBody));
    }

    private void RaiseLoadMoreCanExecuteChanged()
    {
        if (LoadMoreCommand is AsyncCommand asyncCommand)
        {
            asyncCommand.RaiseCanExecuteChanged();
        }
    }

    private void HandleImapException(ImapConnectionException exception, string logPrefix)
    {
        var signature = $"{exception.FailureKind}|{exception.AccountId}|{exception.Email}|{exception.Username}|{exception.Host}|{exception.Port}|{exception.Folder}|{exception.Uid}";

        if (!string.Equals(_lastAccountErrorSignature, signature, StringComparison.Ordinal))
        {
            Console.WriteLine($"{logPrefix} IMAP error: {exception.Message} | {exception.ToDiagnosticString()}");
            _lastAccountErrorSignature = signature;
        }

        SetAccountStatus(() => exception.UserMessage);
    }

    private void SetAccountStatus(string message)
    {
        _accountStatusFactory = () => message;
        RefreshAccountStatusMessage();
    }

    private void SetAccountStatus(Func<string> messageFactory)
    {
        _accountStatusFactory = messageFactory;
        RefreshAccountStatusMessage();
    }

    private void SetAccountStatusResource(string resourceKey)
    {
        _accountStatusFactory = () => Strings.ResourceManager.GetString(resourceKey, Strings.Culture) ?? resourceKey;
        RefreshAccountStatusMessage();
    }

    public void SetStartupError(string message)
    {
        SetAccountStatus(message);
    }

    private void ClearAccountStatus()
    {
        _lastAccountErrorSignature = string.Empty;
        _accountStatusFactory = null;
        AccountStatusMessage = string.Empty;
    }

    private void RefreshAccountStatusMessage()
    {
        AccountStatusMessage = _accountStatusFactory?.Invoke() ?? string.Empty;
    }

    private void ApplySelectedLanguage(LanguageOption option)
    {
        var normalizedCulture = AppCulturePreferences.NormalizeCulture(option.Culture.Name);
        AppCulturePreferences.SavePreferredCulture(_dataDirectory, normalizedCulture);

        if (string.Equals(AppCulture.Current.Name, normalizedCulture.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppCulture.SetCulture(normalizedCulture);
    }

    private void OnCultureChanged(object? sender, CultureInfo culture)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RebuildLanguageOptions(culture.Name);
            RefreshFolderDisplayNames();
            RefreshAccountStatusMessage();
            RefreshListStatusMessage();
            OnPropertyChanged(nameof(SelectedEmailBody));
        });
    }

    private void RebuildLanguageOptions(string? selectedCultureName = null)
    {
        var resolvedCultureName = selectedCultureName ?? _selectedLanguage?.Culture.Name ?? AppCulture.Current.Name;
        var options = AppCulturePreferences.SupportedCultures
            .Select(culture => new LanguageOption
            {
                Culture = culture,
                DisplayName = GetLanguageDisplayName(culture.Name)
            })
            .ToList();

        AvailableLanguages.Clear();

        foreach (var option in options)
        {
            AvailableLanguages.Add(option);
        }

        var nextSelected = AvailableLanguages.FirstOrDefault(option =>
            string.Equals(option.Culture.Name, resolvedCultureName, StringComparison.OrdinalIgnoreCase))
            ?? AvailableLanguages.FirstOrDefault();

        if (!ReferenceEquals(_selectedLanguage, nextSelected))
        {
            _selectedLanguage = nextSelected;
            OnPropertyChanged(nameof(SelectedLanguage));
        }
    }

    private static string GetLanguageDisplayName(string cultureName)
    {
        return cultureName switch
        {
            "en" => Strings.Language_English,
            _ => Strings.Language_Spanish
        };
    }

    private void RefreshFolderDisplayNames()
    {
        if (Folders.Count == 0)
        {
            return;
        }

        var selectedKind = SelectedFolder?.Kind;
        var refreshedFolders = Folders
            .Select(folder => new MailFolderInfo
            {
                Kind = folder.Kind,
                DisplayName = GetFolderDisplayName(folder.Kind),
                ImapFolderName = folder.ImapFolderName
            })
            .ToList();

        Folders.Clear();

        foreach (var folder in refreshedFolders)
        {
            Folders.Add(folder);
        }

        var nextSelectedFolder = selectedKind is null
            ? Folders.FirstOrDefault()
            : Folders.FirstOrDefault(folder => folder.Kind == selectedKind);

        if (!ReferenceEquals(_selectedFolder, nextSelectedFolder))
        {
            _selectedFolder = nextSelectedFolder;
            OnPropertyChanged(nameof(SelectedFolder));
        }
    }

    private static string GetFolderDisplayName(MailFolderKind kind)
    {
        return kind switch
        {
            MailFolderKind.Sent => Strings.Folder_Sent,
            MailFolderKind.Drafts => Strings.Folder_Drafts,
            MailFolderKind.Trash => Strings.Folder_Trash,
            MailFolderKind.Archive => Strings.Folder_Archive,
            _ => Strings.Folder_Inbox
        };
    }

    private void SetListStatusResource(string resourceKey)
    {
        _listStatusFactory = () => Strings.ResourceManager.GetString(resourceKey, Strings.Culture) ?? resourceKey;
        RefreshListStatusMessage();
    }

    private void ClearListStatus()
    {
        _listStatusFactory = null;
        ListStatusMessage = string.Empty;
    }

    private void RefreshListStatusMessage()
    {
        ListStatusMessage = _listStatusFactory?.Invoke() ?? string.Empty;
    }

    private void UpdateListStatusAfterEmailChange(bool isSyncInProgress)
    {
        if (Emails.Count > 0)
        {
            ClearListStatus();
            return;
        }

        SetListStatusResource(isSyncInProgress
            ? nameof(Strings.Status_SyncingFolder)
            : nameof(Strings.Status_NoLocalEmailsYet));
    }

    private void LogStartupEmailsVisibleIfNeeded(string source)
    {
        if (_hasLoggedStartupEmailsVisible || Emails.Count == 0)
        {
            return;
        }

        _hasLoggedStartupEmailsVisible = true;
        Console.WriteLine(
            $"Desktop startup: emails visible source={source}, count={Emails.Count}, elapsedSinceViewModelMs={_startupStopwatch.ElapsedMilliseconds}, elapsedSinceWindowMs={(_windowOpenedOnViewModelElapsedMs >= 0 ? _startupStopwatch.ElapsedMilliseconds - _windowOpenedOnViewModelElapsedMs : -1)}");
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
