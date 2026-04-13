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
    private readonly ImapOperationCoordinator _imapOperationCoordinator;
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
    private InitialMailListState _initialListState;
    private Func<string>? _accountStatusFactory;
    private Func<string>? _listStatusFactory;
    private CancellationTokenSource? _selectedEmailLoadCts;
    private CancellationTokenSource? _folderChangeCts;
    private CancellationTokenSource? _backgroundDownloadCts;
    private bool _suppressFolderSelectionChange;
    private string _folderSelectionSuppressionReason = string.Empty;
    private int _contextVersion;
    private bool _hasCompletedInitialFolderLoad;
    private bool _hasLoggedStartupEmailsVisible;
    private bool _isInitialImapSyncInProgress;
    private long _windowOpenedOnViewModelElapsedMs = -1;

    public MainViewModel(
        IEmailRepository repository,
        IMailClient folderMailClient,
        ImapOperationCoordinator imapOperationCoordinator,
        SyncService syncService,
        BackgroundBodySyncService bodySyncService,
        string dataDirectory)
    {
        _repository = repository;
        _folderMailClient = folderMailClient;
        _imapOperationCoordinator = imapOperationCoordinator;
        _syncService = syncService;
        _bodySyncService = bodySyncService;
        _dataDirectory = dataDirectory;
        Console.WriteLine("Desktop startup: MainViewModel ctor");
        _bodySyncService.BodyDownloaded += OnBodyDownloaded;
        _bodySyncService.DownloadFailed += OnBackgroundDownloadFailed;
        AppCulture.CultureChanged += OnCultureChanged;
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, () => HasMoreEmails && !IsLoadingMore && SelectedAccount is not null && SelectedFolder is not null);
        RebuildLanguageOptions();
        SetListState(InitialMailListState.LoadingLocalData, nameof(Strings.Status_LoadingLocalEmails));
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

            SetSelectedAccountCore(value);
            RunFireAndForget(
                HandleSelectedAccountChangedAsync(value),
                operationName: "selected-account-change",
                cancellationFilter: ex => ex is OperationCanceledException or ObjectDisposedException);
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

            if (_suppressFolderSelectionChange)
            {
                if (value is null && _selectedFolder is not null)
                {
                    Console.WriteLine(
                        $"[FolderRefresh] same folder detected, preserving selection accountId={SelectedAccount?.Id ?? "-"} previousFolder={_selectedFolder.ImapFolderName} currentFolder={_selectedFolder.ImapFolderName} selectedEmail={SelectedEmail?.Id ?? "-"}");
                    Console.WriteLine(
                        $"[FolderRefresh] selected email clear skipped accountId={SelectedAccount?.Id ?? "-"} previousFolder={_selectedFolder.ImapFolderName} nextFolder=- folderChanged=false reason={_folderSelectionSuppressionReason}-transient-null");
                    return;
                }

                Console.WriteLine(
                    $"[FolderRefresh] SelectedFolder change suppressed reason={_folderSelectionSuppressionReason} accountId={SelectedAccount?.Id ?? "-"} previousFolder={_selectedFolder?.ImapFolderName ?? "-"} nextFolder={value?.ImapFolderName ?? "-"}");
                SetSelectedFolderCore(value);
                return;
            }

            SetSelectedFolderCore(value);
            RunFireAndForget(
                HandleSelectedFolderChangedAsync(value),
                operationName: "selected-folder-change",
                cancellationFilter: ex => ex is OperationCanceledException or ObjectDisposedException);
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
            UpdateSelectedEmailLoadingState(value, reason: "selection-changed");
            OnPropertyChanged();
            NotifySelectedEmailContentChanged();
            Console.WriteLine(
                $"Email selection UI: previous={previousEmailId}, next={value?.Id ?? "-"}, elapsedMs={stopwatch.ElapsedMilliseconds}, hasLocalBody={HasUsableBody(value)}");

            if (value is not null)
            {
                RunFireAndForget(
                    HandleSelectedEmailChangedAsync(value),
                    operationName: "selected-email-change",
                    cancellationFilter: ex => ex is OperationCanceledException or ObjectDisposedException);
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

    public InitialMailListState InitialListState
    {
        get => _initialListState;
        private set
        {
            if (_initialListState == value)
            {
                return;
            }

            _initialListState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLoadingLocalEmails));
            OnPropertyChanged(nameof(IsSyncingInitialFolder));
            OnPropertyChanged(nameof(IsInitialEmptyState));
            OnPropertyChanged(nameof(HasInitialLoadError));
        }
    }

    public bool IsLoadingLocalEmails => InitialListState == InitialMailListState.LoadingLocalData;

    public bool IsSyncingInitialFolder => InitialListState == InitialMailListState.Syncing;

    public bool IsInitialEmptyState => InitialListState == InitialMailListState.Empty;

    public bool HasInitialLoadError => InitialListState == InitialMailListState.Error;

    public void NotifyWindowOpened(long elapsedMs)
    {
        _windowOpenedOnViewModelElapsedMs = _startupStopwatch.ElapsedMilliseconds;
        LogStartupMetric("Window shown", $"processElapsedMs={elapsedMs}, viewModelElapsedMs={_windowOpenedOnViewModelElapsedMs}");
    }

    public async Task InitializeAsync()
    {
        var startupStopwatch = Stopwatch.StartNew();
        LogStartupMetric("Initialize start");

        try
        {
            await LoadAccountsAsync();
            LogStartupMetric("Accounts loaded", $"count={Accounts.Count}, durationMs={startupStopwatch.ElapsedMilliseconds}");

            var activeAccount = Accounts.FirstOrDefault(account => account.IsActive) ?? Accounts.FirstOrDefault();
            LogStartupMetric("Active account resolved", $"accountId={activeAccount?.Id ?? "-"}");

            if (activeAccount is null)
            {
                await ResetFolderAndEmailsAsync("initialize-no-active-account", accountChanged: true, folderChanged: true);
                SetAccountStatusResource(nameof(Strings.Status_NoActiveAccount));
                SetListState(InitialMailListState.Empty, nameof(Strings.Status_NoLocalEmailsYet));
                LogStartupMetric("Initialize complete", $"durationMs={startupStopwatch.ElapsedMilliseconds}, accountId=-, folder=-, emailsVisible={Emails.Count}");
                return;
            }

            SetSelectedAccountCore(activeAccount);
            await ActivateAccountAsync(activeAccount, persistSelection: false, triggeredByStartup: true);
            LogStartupMetric(
                "Initialize complete",
                $"durationMs={startupStopwatch.ElapsedMilliseconds}, accountId={activeAccount.Id}, folder={SelectedFolder?.ImapFolderName ?? "-"}, emailsVisible={Emails.Count}");
        }
        catch (Exception exception)
        {
            LogStartupMetric("Initialize failed", exception.ToString());
            SetAccountStatusResource(nameof(Strings.Status_InitialLoadFailed));
            SetListState(InitialMailListState.Error, nameof(Strings.Status_InitialLoadFailed));
            throw;
        }
    }

    public async Task AddAccountAsync(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var normalizedEmail = account.Email.Trim();

        if (Accounts.Any(existing =>
                string.Equals(existing.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(Strings.AccountDialog_ErrorDuplicateEmail);
        }

        account.Email = normalizedEmail;
        account.DisplayName = account.DisplayName.Trim();
        account.ImapHost = account.ImapHost.Trim();
        account.Username = account.Username.Trim();
        account.PreferredFolder = MailFolderCatalog.GetDefault(MailFolderKind.Inbox).ImapFolderName;

        Console.WriteLine($"[Accounts] Saving account from desktop UI: email={account.Email}, imapHost={account.ImapHost}, username={account.Username}, active={account.IsActive}.");

        try
        {
            await _repository.SaveAccountAsync(account);
            await LoadAccountsAsync();

            var savedAccount = Accounts.FirstOrDefault(existing => string.Equals(existing.Id, account.Id, StringComparison.Ordinal))
                               ?? Accounts.FirstOrDefault(existing => string.Equals(existing.Email, account.Email, StringComparison.OrdinalIgnoreCase));

            if (savedAccount is null)
            {
                throw new InvalidOperationException(Strings.AccountDialog_ErrorSaveFailed);
            }

            if (savedAccount.IsActive || Accounts.Count == 1)
            {
                Console.WriteLine($"[Accounts] Account '{savedAccount.Email}' marked as active from desktop UI.");
                SetSelectedAccountCore(savedAccount);
                await ActivateAccountAsync(savedAccount, persistSelection: false, triggeredByStartup: false);
            }
            else
            {
                Console.WriteLine($"[Accounts] Account '{savedAccount.Email}' saved without changing the active selection.");
            }
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            Console.WriteLine($"[Accounts] Failed to save account '{account.Email}' from desktop UI: {exception.Message}");
            throw new InvalidOperationException(Strings.AccountDialog_ErrorSaveFailed, exception);
        }
    }

    private async Task LoadAccountsAsync()
    {
        LogStartupMetric("SQLite accounts load start");
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
            LogStartupMetric("SQLite accounts load complete", $"count={Accounts.Count}");
            return;
        }

        var refreshedSelectedAccount = Accounts.FirstOrDefault(account => account.Id == selectedAccountId);

        if (refreshedSelectedAccount is not null && !ReferenceEquals(refreshedSelectedAccount, _selectedAccount))
        {
            _selectedAccount = refreshedSelectedAccount;
            OnPropertyChanged(nameof(SelectedAccount));
        }

        LogStartupMetric("SQLite accounts load complete", $"count={Accounts.Count}");
    }

    private async Task HandleSelectedAccountChangedAsync(MailAccount? account)
    {
        await ActivateAccountAsync(account, persistSelection: true, triggeredByStartup: false);
    }

    private async Task ActivateAccountAsync(MailAccount? account, bool persistSelection, bool triggeredByStartup)
    {
        var version = Interlocked.Increment(ref _contextVersion);
        CancelFolderChangeOperations();
        CancelBackgroundDownload();
        _hasCompletedInitialFolderLoad = false;

        if (account is null)
        {
            await ResetFolderAndEmailsAsync("active-account-null", accountChanged: true, folderChanged: true);
            SetAccountStatusResource(nameof(Strings.Status_NoActiveAccount));
            return;
        }

        ClearAccountStatus();

        try
        {
            if (persistSelection)
            {
                await _repository.SetActiveAccountAsync(account.Id);
            }

            SetActiveAccountLocally(account.Id);
            var localFolders = await ResolveLocalFoldersAsync(account);
            var initialFolder = DetermineInitialFolder(account, localFolders);
            LoadFoldersLocally(account, localFolders, initialFolder, version);

            if (initialFolder is null)
            {
                await ResetEmailsAsync("account-has-no-initial-folder", accountChanged: true, folderChanged: true);
                SetListState(InitialMailListState.Empty, nameof(Strings.Status_NoLocalEmailsYet));
                LogStartupMetric(
                    "Initial folder resolution",
                    $"accountId={account.Id}, folder=-, source=sqlite, localFolders={localFolders.Count}, triggeredByStartup={triggeredByStartup}");
            }
            else
            {
                LogStartupMetric(
                    "Initial folder resolution",
                    $"accountId={account.Id}, folder={initialFolder.ImapFolderName}, source=sqlite, localFolders={localFolders.Count}, triggeredByStartup={triggeredByStartup}");
                await StartFolderLoadAsync(account, initialFolder, version, isInitialFolderLoad: !_hasCompletedInitialFolderLoad);
            }

            RunFireAndForget(
                RefreshFoldersFromServerAsync(account, version),
                operationName: "refresh-folders-from-server",
                cancellationFilter: ex => ex is OperationCanceledException or ObjectDisposedException);
        }
        catch (ImapConnectionException exception)
        {
            HandleImapException(exception, logPrefix: "SelectedAccount");
            await ResetFolderAndEmailsAsync("active-account-imap-error", accountChanged: true, folderChanged: true);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"SelectedAccount error for {account.Id}: {exception}");
            SetAccountStatusResource(nameof(Strings.Status_CannotLoadActiveAccount));
            await ResetFolderAndEmailsAsync("active-account-generic-error", accountChanged: true, folderChanged: true);
        }
    }

    private async Task<IReadOnlyList<MailFolderInfo>> ResolveLocalFoldersAsync(MailAccount account)
    {
        var knownFolderNames = await _repository.GetKnownFoldersAsync(account.Id);
        var foldersByKind = MailFolderCatalog.Defaults.ToDictionary(
            folder => folder.Kind,
            folder => new MailFolderInfo
            {
                Kind = folder.Kind,
                DisplayName = folder.DisplayName,
                ImapFolderName = folder.ImapFolderName
            });

        foreach (var folderName in knownFolderNames)
        {
            if (!MailFolderCatalog.TryResolveKind(folderName, out var kind))
            {
                continue;
            }

            foldersByKind[kind] = new MailFolderInfo
            {
                Kind = kind,
                DisplayName = GetFolderDisplayName(kind),
                ImapFolderName = folderName
            };
        }

        return MailFolderCatalog.Defaults
            .Select(defaultFolder => foldersByKind[defaultFolder.Kind])
            .ToList();
    }

    private void LoadFoldersLocally(MailAccount account, IReadOnlyList<MailFolderInfo> folders, MailFolderInfo? initialFolder, int version)
    {
        ApplyFolders(folders, version, source: "sqlite", preferredFolder: initialFolder);
        LogStartupMetric("SQLite folders resolved", $"accountId={account.Id}, count={folders.Count}");
    }

    private MailFolderInfo? DetermineInitialFolder(MailAccount account, IReadOnlyList<MailFolderInfo> folders)
    {
        if (folders.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(account.PreferredFolder))
        {
            var preferred = folders.FirstOrDefault(folder =>
                string.Equals(folder.ImapFolderName, account.PreferredFolder, StringComparison.OrdinalIgnoreCase));

            if (preferred is not null)
            {
                return preferred;
            }

            if (MailFolderCatalog.TryResolveKind(account.PreferredFolder, out var preferredKind))
            {
                preferred = folders.FirstOrDefault(folder => folder.Kind == preferredKind);

                if (preferred is not null)
                {
                    return preferred;
                }
            }
        }

        return folders.FirstOrDefault(folder => folder.Kind == MailFolderKind.Inbox)
            ?? folders.FirstOrDefault(folder => !IsDeprioritizedStartupFolder(folder.ImapFolderName, folder.Kind))
            ?? folders.FirstOrDefault();
    }

    private async Task RefreshFoldersFromServerAsync(MailAccount account, int version)
    {
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"[FolderRefresh] folders_refresh started accountId={account.Id} currentFolder={SelectedFolder?.ImapFolderName ?? "-"}");

        try
        {
            var folders = await _imapOperationCoordinator.RunAsync(
                account,
                "folders_refresh",
                null,
                _ => _folderMailClient.GetKnownFoldersAsync(account));
            ApplyFolders(folders, version, source: "imap");
            Console.WriteLine($"[FolderRefresh] folders_refresh completed accountId={account.Id} currentFolder={SelectedFolder?.ImapFolderName ?? "-"} elapsedMs={stopwatch.ElapsedMilliseconds} count={folders.Count}");
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

    private void ApplyFolders(IReadOnlyList<MailFolderInfo> folders, int version, string source, MailFolderInfo? preferredFolder = null)
    {
        if (version != Volatile.Read(ref _contextVersion))
        {
            return;
        }

        var previousFolder = _selectedFolder;
        var previousFolderName = previousFolder?.ImapFolderName ?? "-";
        var previousAccountId = SelectedAccount?.Id ?? "-";
        var isFolderRefresh = string.Equals(source, "imap", StringComparison.OrdinalIgnoreCase);

        if (isFolderRefresh)
        {
            _suppressFolderSelectionChange = true;
            _folderSelectionSuppressionReason = "folders_refresh";
        }

        try
        {
            Console.WriteLine(
                $"[FolderRefresh] applying folders source={source} accountId={previousAccountId} previousFolder={previousFolderName} folderCount={folders.Count}");
            Folders.Clear();

            foreach (var folder in folders)
            {
                Folders.Add(folder);
            }

            var selectedFolder = ResolveFolderSelection(preferredFolder, source);

            LogStartupMetric("Folder applied", $"accountId={SelectedAccount?.Id ?? "-"}, folder={selectedFolder?.ImapFolderName ?? "-"}, source={source}");

            if (selectedFolder is null)
            {
                if (isFolderRefresh)
                {
                    Console.WriteLine($"[FolderRefresh] selected email clear skipped accountId={previousAccountId} previousFolder={previousFolderName} nextFolder=- folderChanged=false reason=no-folder-resolved-during-refresh");
                    Console.WriteLine($"[FolderRefresh] same folder detected, preserving email list accountId={previousAccountId} folder={previousFolderName} emails clear skipped");
                    return;
                }

                SetSelectedFolderCore(null);
                return;
            }

            var folderChanged = previousFolder is null
                || previousFolder.Kind != selectedFolder.Kind
                || !string.Equals(previousFolder.ImapFolderName, selectedFolder.ImapFolderName, StringComparison.OrdinalIgnoreCase);

            SetSelectedFolderCore(selectedFolder);

            if (!folderChanged)
            {
                if (isFolderRefresh)
                {
                    Console.WriteLine($"[FolderRefresh] same folder detected, preserving email list accountId={previousAccountId} folder={selectedFolder.ImapFolderName} emails clear skipped");
                    Console.WriteLine($"[FolderRefresh] same folder detected, preserving selection accountId={previousAccountId} previousFolder={previousFolderName} currentFolder={selectedFolder.ImapFolderName} selectedEmail={SelectedEmail?.Id ?? "-"}");
                }

                return;
            }

            if (isFolderRefresh)
            {
                Console.WriteLine($"[FolderRefresh] folder changed for real accountId={previousAccountId} previousFolder={previousFolderName} currentFolder={selectedFolder.ImapFolderName}");
                return;
            }
        }
        finally
        {
            if (isFolderRefresh)
            {
                _folderSelectionSuppressionReason = string.Empty;
                _suppressFolderSelectionChange = false;
            }
        }
    }

    private async Task HandleSelectedFolderChangedAsync(MailFolderInfo? folder)
    {
        var version = Interlocked.Increment(ref _contextVersion);
        CancelFolderChangeOperations();
        CancelBackgroundDownload();

        if (SelectedAccount is null || folder is null)
        {
            await ResetEmailsAsync("selected-folder-null", accountChanged: false, folderChanged: true);
            return;
        }

        if (!string.Equals(SelectedAccount.PreferredFolder, folder.ImapFolderName, StringComparison.OrdinalIgnoreCase))
        {
            SelectedAccount.PreferredFolder = folder.ImapFolderName;
            await _repository.SetPreferredFolderAsync(SelectedAccount.Id, folder.ImapFolderName);
            LogStartupMetric("Preferred folder saved", $"accountId={SelectedAccount.Id}, folder={folder.ImapFolderName}");
        }

        await StartFolderLoadAsync(SelectedAccount, folder, version, isInitialFolderLoad: !_hasCompletedInitialFolderLoad);
    }

    private async Task StartFolderLoadAsync(MailAccount account, MailFolderInfo folder, int version, bool isInitialFolderLoad)
    {
        var totalStopwatch = Stopwatch.StartNew();
        _folderChangeCts = new CancellationTokenSource();
        var cancellationToken = _folderChangeCts.Token;
        ClearAccountStatus();
        SetListState(InitialMailListState.LoadingLocalData, nameof(Strings.Status_LoadingLocalEmails));
        var preferredSelectedEmailId = SelectedEmail?.Id;

        var sqliteStopwatch = Stopwatch.StartNew();
        await ReloadEmailsAsync(
            reset: true,
            accountId: account.Id,
            folderName: folder.ImapFolderName,
            contextVersion: version,
            preferredSelectedEmailId: preferredSelectedEmailId,
            cancellationToken: cancellationToken);
        LogStartupMetric(
            "SQLite load",
            $"accountId={account.Id}, folder={folder.ImapFolderName}, emails={Emails.Count}, durationMs={sqliteStopwatch.ElapsedMilliseconds}, totalUiMs={totalStopwatch.ElapsedMilliseconds}, initial={isInitialFolderLoad}");

        if (cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _contextVersion))
        {
            Console.WriteLine($"Folder change discarded after SQLite load: accountId={account.Id}, folder={folder.ImapFolderName}, reason=obsolete");
            return;
        }

        _hasCompletedInitialFolderLoad = true;
        UpdateListStateAfterEmailChange(isSyncInProgress: true);
        LogStartupEmailsVisibleIfNeeded(source: "sqlite");
        RunFireAndForget(
            SyncFolderInBackgroundAsync(account, folder, version, preferredSelectedEmailId, totalStopwatch, cancellationToken),
            operationName: "sync-folder-background",
            cancellationFilter: ex => ex is OperationCanceledException or ObjectDisposedException);
    }

    private async Task ReloadEmailsAsync(
        bool reset,
        string? accountId = null,
        string? folderName = null,
        int? contextVersion = null,
        string? preferredSelectedEmailId = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveAccountId = accountId ?? SelectedAccount?.Id;
        var effectiveFolderName = folderName ?? SelectedFolder?.ImapFolderName;

        if (string.IsNullOrWhiteSpace(effectiveAccountId) || string.IsNullOrWhiteSpace(effectiveFolderName))
        {
            await ResetEmailsAsync("reload-missing-account-or-folder", accountChanged: false, folderChanged: true);
            return;
        }

        var sameAccount = string.Equals(SelectedAccount?.Id, effectiveAccountId, StringComparison.Ordinal);
        var sameFolder = string.Equals(SelectedFolder?.ImapFolderName, effectiveFolderName, StringComparison.OrdinalIgnoreCase);
        var sameContextRefresh = reset && sameAccount && sameFolder;
        var selectedEmailIdToRestore = sameContextRefresh
            ? SelectedEmail?.Id ?? preferredSelectedEmailId
            : preferredSelectedEmailId ?? SelectedEmail?.Id;

        Console.WriteLine(
            $"[EmailList] ReloadEmails selection preservation accountId={effectiveAccountId} folder={effectiveFolderName} reset={reset} sameAccount={sameAccount} sameFolder={sameFolder} selectedRestore={selectedEmailIdToRestore ?? "-"} currentSelected={SelectedEmail?.Id ?? "-"} preferredSelected={preferredSelectedEmailId ?? "-"}");

        if (IsLoadingMore)
        {
            return;
        }

        IsLoadingMore = true;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loadedCount = reset ? 0 : _allEmails.Count;
            var replacementEmails = new List<EmailMessage>();
            FolderLoadStats? folderLoadStats = null;

            if (!string.IsNullOrWhiteSpace(effectiveAccountId))
            {
                folderLoadStats = await _repository.GetFolderLoadStatsAsync(effectiveAccountId, effectiveFolderName);
                Console.WriteLine(
                    $"[SQLiteLoad] accountId={effectiveAccountId} folder={effectiveFolderName} totalInDb={folderLoadStats.TotalCount} visibleByDeletedFlag={folderLoadStats.VisibleCount} softDeleted={folderLoadStats.SoftDeletedCount} bodyUnavailable={folderLoadStats.BodyUnavailableCount} limit={PageSize} offset={loadedCount} reset={reset} filterText={(string.IsNullOrWhiteSpace(FilterText) ? "-" : FilterText)} unreadOnly={ShowUnreadOnly}");
            }

            var emails = await _repository.GetEmailsAsync(
                effectiveAccountId,
                effectiveFolderName,
                PageSize,
                loadedCount);
            Console.WriteLine(
                $"[SQLiteLoad] queryResult accountId={effectiveAccountId} folder={effectiveFolderName} returned={emails.Count} limit={PageSize} offset={loadedCount} reset={reset}");

            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine(
                    $"Folder load cancelled before apply: accountId={effectiveAccountId}, folder={effectiveFolderName}, reset={reset}");
                return;
            }

            if (contextVersion.HasValue && contextVersion.Value != Volatile.Read(ref _contextVersion))
            {
                Console.WriteLine(
                    $"Folder load discarded as obsolete: accountId={effectiveAccountId}, folder={effectiveFolderName}, reset={reset}");
                return;
            }

            if (reset)
            {
                Console.WriteLine($"[EmailList] Avoided destructive pre-clear for refresh accountId={effectiveAccountId} folder={effectiveFolderName} previousVisible={Emails.Count} previousLoaded={_allEmails.Count} restoreSelected={selectedEmailIdToRestore ?? "-"} accountChanged={!sameAccount} folderChanged={!sameFolder}");
                _allEmails.Clear();
                replacementEmails.AddRange(emails);
                HasMoreEmails = false;
            }
            else
            {
                foreach (var email in emails)
                {
                    _allEmails.Add(email);
                }
            }

            if (reset)
            {
                foreach (var email in replacementEmails)
                {
                    _allEmails.Add(email);
                }
            }

            HasMoreEmails = emails.Count == PageSize;
            ApplyFilter(selectedEmailIdToRestore);
            Console.WriteLine(
                $"[SQLiteLoad] uiBound accountId={effectiveAccountId} folder={effectiveFolderName} totalLoaded={_allEmails.Count} visibleAfterFilter={Emails.Count} limit={PageSize} offset={loadedCount} reset={reset}");
            UpdateListStateAfterEmailChange(isSyncInProgress: _isInitialImapSyncInProgress);
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
        var visibleCountBeforeSync = Emails.Count;
        SyncFolderResult? syncResult = null;
        _isInitialImapSyncInProgress = true;
        UpdateListStateAfterEmailChange(isSyncInProgress: true);
        LogStartupMetric("IMAP sync start", $"accountId={account.Id}, folder={folder.ImapFolderName}, visibleEmails={visibleCountBeforeSync}");

        try
        {
            syncResult = await _syncService.SyncFolderAsync(account, folder.ImapFolderName, cancellationToken);
            LogStartupMetric(
                "IMAP sync complete",
                $"accountId={account.Id}, folder={folder.ImapFolderName}, fetchedHeaders={syncResult.FetchedHeadersCount}, previousLastUid={syncResult.PreviousLastUid}, newLastUid={syncResult.NewLastUid}, resetFolder={syncResult.ResetFolder}, softDeleted={syncResult.SoftDeletedCount}, restored={syncResult.RestoredCount}, backfillTriggered={syncResult.BackfillTriggered}, backfilledHeaders={syncResult.BackfilledHeadersCount}, localVisibleBefore={syncResult.LocalVisibleCountBefore}, localVisibleAfter={syncResult.LocalVisibleCountAfter}, serverUidSnapshotCount={syncResult.ServerVisibleCount}, durationMs={syncStopwatch.ElapsedMilliseconds}");
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
            UpdateListStateAfterEmailChange(isSyncInProgress: false);
        }

        if (cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _contextVersion))
        {
            Console.WriteLine(
                $"Folder sync result discarded: accountId={account.Id}, folder={folder.ImapFolderName}, reason=obsolete, fetchedHeaders={syncResult?.FetchedHeadersCount ?? -1}, previousLastUid={syncResult?.PreviousLastUid ?? 0}, newLastUid={syncResult?.NewLastUid ?? 0}, resetFolder={syncResult?.ResetFolder ?? false}, applied=false");
            return;
        }

        var refreshStopwatch = Stopwatch.StartNew();
        await ReloadEmailsAsync(
            reset: true,
            accountId: account.Id,
            folderName: folder.ImapFolderName,
            contextVersion: version,
            preferredSelectedEmailId: preferredSelectedEmailId,
            cancellationToken: cancellationToken);
        LogStartupEmailsVisibleIfNeeded(source: "imap-refresh");
        LogStartupMetric(
            "SQLite refresh after IMAP",
            $"accountId={account.Id}, folder={folder.ImapFolderName}, emails={Emails.Count}, durationMs={refreshStopwatch.ElapsedMilliseconds}, totalUiMs={totalStopwatch.ElapsedMilliseconds}, deltaVisible={Emails.Count - visibleCountBeforeSync}");

        if (cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _contextVersion))
        {
            Console.WriteLine($"Folder refresh discarded: accountId={account.Id}, folder={folder.ImapFolderName}, reason=obsolete");
            return;
        }

        StartBackgroundDownload(account, folder);
        UpdateListStateAfterEmailChange(isSyncInProgress: false);
    }

    private async Task LoadMoreAsync()
    {
        await ReloadEmailsAsync(
            reset: false,
            accountId: SelectedAccount?.Id,
            folderName: SelectedFolder?.ImapFolderName,
            contextVersion: Volatile.Read(ref _contextVersion),
            cancellationToken: GetFolderChangeTokenOrNone());
    }

    private void ApplyFilter(string? selectedEmailIdOverride = null)
    {
        var selectedId = selectedEmailIdOverride ?? SelectedEmail?.Id;
        var filterText = string.IsNullOrWhiteSpace(FilterText) ? null : FilterText.Trim();

        var filteredEmails = _allEmails.Where(email =>
        {
            var matchesText = filterText is null
                || (email.Subject?.Contains(filterText, StringComparison.OrdinalIgnoreCase) ?? false);
            var matchesUnread = !ShowUnreadOnly
                || !email.IsRead
                || string.Equals(email.Id, selectedId, StringComparison.Ordinal);

            return matchesText && matchesUnread;
        }).ToList();

        Console.WriteLine($"[EmailList] Rebinding visible collection selected={selectedId ?? "-"} totalLoaded={_allEmails.Count} filteredCount={filteredEmails.Count} unreadOnly={ShowUnreadOnly} filterText={(filterText ?? "-")}");
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
            if (nextSelected is null && Emails.Count > 0)
            {
                var fallbackEmail = Emails.First();
                Console.WriteLine(
                    $"[EmailList] Selection fallback applied method=ApplyFilter reason=selected-email-not-visible accountId={SelectedAccount?.Id ?? "-"} folder={SelectedFolder?.ImapFolderName ?? "-"} previousSelected={selectedId ?? "-"} fallbackSelected={fallbackEmail.Id}");
                SelectedEmail = fallbackEmail;
                return;
            }

            if (nextSelected is null)
            {
                Console.WriteLine(
                    $"[EmailList] SelectedEmail cleared method=ApplyFilter reason=no-visible-email-after-filter accountId={SelectedAccount?.Id ?? "-"} folder={SelectedFolder?.ImapFolderName ?? "-"} previousSelected={selectedId ?? "-"} previousFolder={SelectedFolder?.ImapFolderName ?? "-"} accountChanged=false folderChanged=false");
            }

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
        var cancellationToken = selectionTokenSource.Token;
        _selectedEmailLoadCts = selectionTokenSource;
        Console.WriteLine($"[BodyLoad] Selection pipeline started emailId={email.Id} hasLocalBody={HasUsableBody(email)} bodyUnavailable={email.BodyUnavailable}");

        if (!email.IsRead)
        {
            RunFireAndForget(
                MarkSelectedEmailAsReadAsync(email),
                operationName: "mark-email-read",
                cancellationFilter: ex => ex is OperationCanceledException);
        }

        if (!HasUsableBody(email))
        {
            RunFireAndForget(
                LoadBodyAsync(email, cancellationToken),
                operationName: "load-email-body",
                cancellationFilter: ex => ex is OperationCanceledException or ObjectDisposedException);
        }

        await Task.CompletedTask;
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
            UpdateSelectedEmailLoadingState(ReferenceEquals(email, SelectedEmail) ? email : null, reason: "body-already-available");
            return;
        }

        if (email.BodyUnavailable)
        {
            UpdateSelectedEmailLoadingState(ReferenceEquals(email, SelectedEmail) ? email : null, reason: "body-marked-unavailable");
            return;
        }

        var account = SelectedAccount is not null && string.Equals(SelectedAccount.Id, email.AccountId, StringComparison.Ordinal)
            ? SelectedAccount
            : _accountLookup.GetValueOrDefault(email.AccountId);

        if (account is null || email.ImapUid == 0)
        {
            Console.WriteLine($"[BodyLoad] Cannot start emailId={email.Id} accountFound={account is not null} uid={email.ImapUid}. Clearing placeholder for current selection.");
            UpdateSelectedEmailLoadingState(ReferenceEquals(email, SelectedEmail) ? null : SelectedEmail, reason: "body-load-cannot-start");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"[BodyLoad] Started emailId={email.Id} folder={email.Folder} uid={email.ImapUid}");
        UpdateSelectedEmailLoadingState(ReferenceEquals(email, SelectedEmail) ? email : null, reason: "body-load-started");
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
            Console.WriteLine($"[BodyLoad] Completed emailId={email.Id} elapsedMs={stopwatch.ElapsedMilliseconds}");
            Console.WriteLine(
                $"Email IMAP body download completed: emailId={email.Id}, accountId={email.AccountId}, folder={email.Folder}, uid={email.ImapUid}, elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[BodyLoad] Cancelled emailId={email.Id} folder={email.Folder}");
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
                Console.WriteLine($"[BodyLoad] Retry completed emailId={email.Id} elapsedMs={retryStopwatch.ElapsedMilliseconds}");
                Console.WriteLine(
                    $"Email IMAP body retry completed: emailId={email.Id}, accountId={email.AccountId}, folder={email.Folder}, uid={email.ImapUid}, elapsedMs={retryStopwatch.ElapsedMilliseconds}");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[BodyLoad] Retry cancelled emailId={email.Id} folder={email.Folder}");
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
            UpdateSelectedEmailLoadingState(ReferenceEquals(email, SelectedEmail) ? email : null, reason: "body-load-finished");
        }
    }

    private void StartBackgroundDownload(MailAccount account, MailFolderInfo folder)
    {
        CancelBackgroundDownload();
        _backgroundDownloadCts = new CancellationTokenSource();
        var token = _backgroundDownloadCts.Token;

        RunFireAndForget(
            RunBackgroundDownloadAsync(account, folder, token),
            operationName: "background-body-download",
            cancellationFilter: ex => ex is OperationCanceledException or ObjectDisposedException);
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

    private async Task ResetFolderAndEmailsAsync(string reason, bool accountChanged, bool folderChanged)
    {
        CancelFolderChangeOperations();
        Console.WriteLine($"[EmailList] ResetFolderAndEmailsAsync reason={reason} accountId={SelectedAccount?.Id ?? "-"} folder={SelectedFolder?.ImapFolderName ?? "-"} accountChanged={accountChanged} folderChanged={folderChanged}");
        Folders.Clear();
        SetSelectedFolderCore(null);
        await ResetEmailsAsync(reason, accountChanged, folderChanged);
    }

    private Task ResetEmailsAsync(string reason, bool accountChanged, bool folderChanged)
    {
        Console.WriteLine($"[EmailList] ResetEmailsAsync clearing visible and loaded collections reason={reason} accountId={SelectedAccount?.Id ?? "-"} folder={SelectedFolder?.ImapFolderName ?? "-"} visibleBefore={Emails.Count} loadedBefore={_allEmails.Count} accountChanged={accountChanged} folderChanged={folderChanged}");
        CancelSelectedEmailLoad();
        _allEmails.Clear();
        Emails.Clear();
        Console.WriteLine($"[EmailList] SelectedEmail cleared reason={reason} accountChanged={accountChanged} folderChanged={folderChanged} previousSelected={_selectedEmail?.Id ?? "-"}");
        SelectedEmail = null;
        HasMoreEmails = false;
        ClearListStatus();
        InitialListState = InitialMailListState.None;
        return Task.CompletedTask;
    }

    private void CancelSelectedEmailLoad()
    {
        var cancellationTokenSource = _selectedEmailLoadCts;

        if (cancellationTokenSource is null)
        {
            return;
        }

        _selectedEmailLoadCts = null;

        if (!cancellationTokenSource.IsCancellationRequested)
        {
            Console.WriteLine($"[BodyLoad] Cancelling current selection body load emailId={_selectedEmail?.Id ?? "-"}");
            Console.WriteLine($"Email body request cancelled by new selection: emailId={_selectedEmail?.Id ?? "-"}");
            cancellationTokenSource.Cancel();
        }

        cancellationTokenSource.Dispose();
    }

    private void CancelFolderChangeOperations()
    {
        CancelSelectedEmailLoad();

        var cancellationTokenSource = _folderChangeCts;

        if (cancellationTokenSource is null)
        {
            return;
        }

        _folderChangeCts = null;

        if (!cancellationTokenSource.IsCancellationRequested)
        {
            Console.WriteLine($"Folder change cancelled: folder={_selectedFolder?.ImapFolderName ?? "-"}");
            cancellationTokenSource.Cancel();
        }

        cancellationTokenSource.Dispose();
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
                UpdateSelectedEmailLoadingState(email, reason: "background-body-downloaded");
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
            or nameof(EmailMessage.BodyUnavailable)
            or nameof(EmailMessage.Body))
        {
            UpdateSelectedEmailLoadingState(email, reason: $"selected-email-property-changed:{e.PropertyName}");
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

    private void UpdateSelectedEmailLoadingState(EmailMessage? email, string reason)
    {
        var isLoading = email is not null && !HasUsableBody(email) && !email.BodyUnavailable;
        Console.WriteLine($"[BodyLoad] Placeholder {(isLoading ? "set" : "cleared")} reason={reason} selectedEmailId={SelectedEmail?.Id ?? "-"} sourceEmailId={email?.Id ?? "-"}");
        SelectedEmailIsLoadingBody = isLoading;
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

        _suppressFolderSelectionChange = true;
        _folderSelectionSuppressionReason = "refresh-folder-display-names";

        try
        {
            Folders.Clear();

            foreach (var folder in refreshedFolders)
            {
                Folders.Add(folder);
            }
        }
        finally
        {
            _folderSelectionSuppressionReason = string.Empty;
            _suppressFolderSelectionChange = false;
        }

        var nextSelectedFolder = selectedKind is null
            ? Folders.FirstOrDefault()
            : Folders.FirstOrDefault(folder => folder.Kind == selectedKind);

        if (!ReferenceEquals(_selectedFolder, nextSelectedFolder))
        {
            SetSelectedFolderCore(nextSelectedFolder);
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

    private MailFolderInfo? ResolveFolderSelection(MailFolderInfo? preferredFolder, string source)
    {
        if (preferredFolder is not null)
        {
            var preferredByName = Folders.FirstOrDefault(folder =>
                string.Equals(folder.ImapFolderName, preferredFolder.ImapFolderName, StringComparison.OrdinalIgnoreCase));

            if (preferredByName is not null)
            {
                return preferredByName;
            }

            var preferredByKind = Folders.FirstOrDefault(folder => folder.Kind == preferredFolder.Kind);

            if (preferredByKind is not null)
            {
                return preferredByKind;
            }
        }

        if (string.Equals(source, "imap", StringComparison.OrdinalIgnoreCase) && _selectedFolder is not null)
        {
            var currentByName = Folders.FirstOrDefault(folder =>
                string.Equals(folder.ImapFolderName, _selectedFolder.ImapFolderName, StringComparison.OrdinalIgnoreCase));

            if (currentByName is not null)
            {
                return currentByName;
            }

            var currentByKind = Folders.FirstOrDefault(folder => folder.Kind == _selectedFolder.Kind);

            if (currentByKind is not null)
            {
                return currentByKind;
            }
        }

        return Folders.FirstOrDefault(folder => folder.Kind == MailFolderKind.Inbox)
            ?? Folders.FirstOrDefault(folder => !IsDeprioritizedStartupFolder(folder.ImapFolderName, folder.Kind))
            ?? Folders.FirstOrDefault();
    }

    private static bool IsDeprioritizedStartupFolder(string folderName, MailFolderKind kind)
    {
        if (kind == MailFolderKind.Trash)
        {
            return true;
        }

        var normalized = folderName.Trim().ToLowerInvariant();
        return normalized.Contains("spam", StringComparison.Ordinal)
            || normalized.Contains("junk", StringComparison.Ordinal)
            || normalized.Contains("papelera", StringComparison.Ordinal)
            || normalized.Contains("trash", StringComparison.Ordinal)
            || normalized.Contains("bin", StringComparison.Ordinal);
    }

    private void SetListStatusResource(string resourceKey)
    {
        _listStatusFactory = () => Strings.ResourceManager.GetString(resourceKey, Strings.Culture) ?? resourceKey;
        RefreshListStatusMessage();
    }

    private void SetListState(InitialMailListState state, string resourceKey)
    {
        InitialListState = state;
        SetListStatusResource(resourceKey);
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

    private void UpdateListStateAfterEmailChange(bool isSyncInProgress)
    {
        if (Emails.Count > 0)
        {
            InitialListState = InitialMailListState.None;
            ClearListStatus();
            return;
        }

        SetListState(
            isSyncInProgress ? InitialMailListState.Syncing : InitialMailListState.Empty,
            isSyncInProgress ? nameof(Strings.Status_SyncingFolder) : nameof(Strings.Status_NoLocalEmailsYet));
    }

    private void LogStartupEmailsVisibleIfNeeded(string source)
    {
        if (_hasLoggedStartupEmailsVisible || Emails.Count == 0)
        {
            return;
        }

        _hasLoggedStartupEmailsVisible = true;
        LogStartupMetric(
            "Mails visible",
            $"source={source}, emails={Emails.Count}, durationSinceViewModelMs={_startupStopwatch.ElapsedMilliseconds}, durationSinceWindowMs={(_windowOpenedOnViewModelElapsedMs >= 0 ? _startupStopwatch.ElapsedMilliseconds - _windowOpenedOnViewModelElapsedMs : -1)}, accountId={SelectedAccount?.Id ?? "-"}, folder={SelectedFolder?.ImapFolderName ?? "-"}");
    }

    private void LogStartupMetric(string stage, string? details = null)
    {
        var elapsedMs = _startupStopwatch.ElapsedMilliseconds;
        Console.WriteLine(
            details is null
                ? $"[Startup] {stage}: {elapsedMs} ms"
                : $"[Startup] {stage}: {elapsedMs} ms | {details}");
    }

    private void SetSelectedAccountCore(MailAccount? account)
    {
        if (ReferenceEquals(_selectedAccount, account))
        {
            return;
        }

        _selectedAccount = account;
        OnPropertyChanged(nameof(SelectedAccount));
    }

    private void SetSelectedFolderCore(MailFolderInfo? folder)
    {
        if (ReferenceEquals(_selectedFolder, folder))
        {
            return;
        }

        _selectedFolder = folder;
        OnPropertyChanged(nameof(SelectedFolder));
    }

    private CancellationToken GetFolderChangeTokenOrNone()
    {
        var cancellationTokenSource = _folderChangeCts;

        if (cancellationTokenSource is null)
        {
            return CancellationToken.None;
        }

        try
        {
            return cancellationTokenSource.Token;
        }
        catch (ObjectDisposedException)
        {
            return CancellationToken.None;
        }
    }

    private void RunFireAndForget(Task task, string operationName, Func<Exception, bool>? cancellationFilter = null)
    {
        _ = ObserveTaskAsync(task, operationName, cancellationFilter);
    }

    private async Task ObserveTaskAsync(Task task, string operationName, Func<Exception, bool>? cancellationFilter)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (cancellationFilter?.Invoke(exception) == true)
        {
        }
        catch (Exception exception)
        {
            Console.WriteLine($"{operationName} failed: {exception}");
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
