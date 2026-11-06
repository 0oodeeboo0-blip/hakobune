using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakobune.Core.Browsing;
using Hakobune.Core.Connections;
using Hakobune.Core.Logging;
using Hakobune.Core.Remote;
using Hakobune.Core.Text;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Logging;
using Hakobune.Presentation.Settings;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation;

/// <summary>アプリが用意する、画面の外の仕組み。</summary>
/// <param name="DataDirectory">接続先や信頼した鍵を保存するフォルダ（例: %APPDATA%\Hakobune）。</param>
/// <param name="Secrets">パスワードの保管場所。</param>
/// <param name="Prompts">接続のときに利用者に聞く画面。</param>
/// <param name="Dialogs">接続先の作成・編集の画面。</param>
/// <param name="TransferDialogs">転送のときに聞く画面。null なら聞かずに上書きする（試験用）。</param>
/// <param name="FileDialogs">新しいフォルダや削除のときに聞く画面。null ならその操作はできない。</param>
/// <param name="LocalFiles">パソコンのファイルの操作（Windows ではごみ箱を使う）。null ならそのまま消す。</param>
/// <param name="SettingsDialogs">設定の画面。null なら設定の画面は出さない。</param>
public sealed record AppServices(
    string DataDirectory,
    ISecretStore Secrets,
    IConnectionPrompts Prompts,
    IConnectionDialogs Dialogs,
    ITransferDialogs? TransferDialogs = null,
    IFileDialogs? FileDialogs = null,
    ILocalFileOperations? LocalFiles = null,
    ISettingsDialogs? SettingsDialogs = null)
{
    /// <summary>接続先の一覧のファイル。</summary>
    public string ConnectionsFile => Path.Combine(DataDirectory, "connections.json");

    /// <summary>信頼したサーバーの鍵のファイル。</summary>
    public string TrustedKeysFile => Path.Combine(DataDirectory, "trusted-keys.json");

    /// <summary>接続先ごとの送信待ちのファイル。</summary>
    public string QueueFile(Guid connectionId) => Path.Combine(DataDirectory, "queues", $"{connectionId:N}.json");

    /// <summary>アプリの設定のファイル。</summary>
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
}

/// <summary>メイン画面全体。</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices? _services;
    private readonly ConnectionService? _connector;

    // クイック接続で URL に書いてあったパスワード（保存はせず、アプリを閉じるまで覚えておく）
    private readonly Dictionary<Guid, string> _sessionPasswords = [];
    private FilePaneViewModel _activePane;
    private ConnectResult? _session;
    private ConnectionItemViewModel? _connected;
    private CancellationTokenSource? _connecting;
    private ConnectionItemViewModel? _connectingItem;

    // 接続先のファイルを読めなかったときは、上書きして消してしまわないよう、保存しない
    private bool _saveBlocked;

    // 転送が終わったら一覧を取り直す（まとめて 1 回にする）
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromSeconds(1.5);
    private readonly HashSet<FilePaneViewModel> _refreshScheduled = [];
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _context;
    private TransferController? _transferController;

    /// <summary>部品を組み合わせて作る。<paramref name="services"/> が null なら、接続はしない（見本の画面）。</summary>
    public MainViewModel(
        FilePaneViewModel local,
        FilePaneViewModel remote,
        TransferListViewModel transfers,
        ConnectionListViewModel connections,
        ConnectionStatusViewModel connection,
        AppServices? services = null,
        ConnectionService? connector = null,
        TimeProvider? timeProvider = null,
        ActivityLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(transfers);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(connection);
        Local = local;
        Remote = remote;
        Transfers = transfers;
        Connections = connections;
        Connection = connection;
        _services = services;
        _connector = connector;
        _time = timeProvider ?? TimeProvider.System;
        _context = SynchronizationContext.Current;
        Log = log ?? new ActivityLog(timeProvider: _time);
        LogView = new LogViewModel(Log, _time, _context);
        local.Log = Log;
        remote.Log = Log;
        local.Exclusions = Settings.Exclusions.ToRules();
        _activePane = local;
        local.IsActive = true;
        local.ActivationRequested += (_, _) => ActivePane = local;
        remote.ActivationRequested += (_, _) => ActivePane = remote;

        ToggleSidebarCommand = new RelayCommand(() => IsSidebarOpen = !IsSidebarOpen);
        RefreshCommand = new AsyncRelayCommand(() => ActivePane.RefreshAsync());
        // 接続中でも別の接続先を選べるようにする（前の接続はやめる）
        ConnectCommand = new AsyncRelayCommand<ConnectionItemViewModel>(
            item => item is null ? Task.CompletedTask : ConnectAsync(item),
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected || IsConnecting);
        // 見本の画面でもボタンは押せる見た目にする（押しても何もしない）
        NewConnectionCommand = new AsyncRelayCommand(NewConnectionAsync);
        QuickConnectCommand = new AsyncRelayCommand(AskQuickConnectAsync);
        EditConnectionCommand = new AsyncRelayCommand<ConnectionItemViewModel>(EditConnectionAsync);
        DuplicateConnectionCommand = new AsyncRelayCommand<ConnectionItemViewModel>(DuplicateConnectionAsync);
        DeleteConnectionCommand = new AsyncRelayCommand<ConnectionItemViewModel>(DeleteConnectionAsync);
        UploadCommand = new AsyncRelayCommand(() => UploadEntriesAsync([.. Local.SelectedRows.Select(row => row.Entry)]), () => CanUpload);
        DownloadCommand = new AsyncRelayCommand(() => DownloadEntriesAsync([.. Remote.SelectedRows.Select(row => row.Entry)]), () => CanDownload);
        NewFolderCommand = new AsyncRelayCommand(() => NewFolderAsync(ActivePane), () => CanCreateFolder(ActivePane));
        RenameCommand = new AsyncRelayCommand(() => RenameAsync(ActivePane), () => CanRename(ActivePane));
        DeleteCommand = new AsyncRelayCommand(() => DeleteAsync(ActivePane), () => CanDelete(ActivePane));
        ChangePermissionsCommand = new AsyncRelayCommand(() => ChangePermissionsAsync(Remote), () => CanChangePermissions);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, () => _services?.SettingsDialogs is not null);
        ShowLogCommand = new RelayCommand(Transfers.SelectLog);
        ToggleHiddenFilesCommand = new AsyncRelayCommand(() => ApplySettingsAsync(Settings with { ShowHiddenFiles = !Settings.ShowHiddenFiles }));
        ToggleCompactRowsCommand = new AsyncRelayCommand(() => ApplySettingsAsync(Settings with { UseCompactRows = !Settings.UseCompactRows }));
        local.PropertyChanged += OnPanePropertyChanged;
        remote.PropertyChanged += OnPanePropertyChanged;
        local.ActionRequested += OnPaneActionRequested;
        remote.ActionRequested += OnPaneActionRequested;
        // ドラッグ＆ドロップ: パソコン側からサーバー側へ落とすとアップロード、その逆はダウンロード
        remote.RowsDropped += (_, e) => _ = UploadEntriesAsync([.. e.Rows.Select(row => row.Entry)]);
        remote.FilesDropped += (_, paths) => _ = UploadAsync(paths);
        local.RowsDropped += (_, e) => _ = DownloadEntriesAsync([.. e.Rows.Select(row => row.Entry)]);

        // 接続先がまだ無いときは、サーバー側の案内で作り方を教える
        connections.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionListViewModel.IsEmpty))
                UpdateDisconnectedHint();
        };
        UpdateDisconnectedHint();
    }

    /// <summary>パソコン側の一覧。</summary>
    public FilePaneViewModel Local { get; }

    /// <summary>サーバー側の一覧。</summary>
    public FilePaneViewModel Remote { get; }

    /// <summary>転送の一覧。</summary>
    public TransferListViewModel Transfers { get; }

    /// <summary>接続先の一覧。</summary>
    public ConnectionListViewModel Connections { get; }

    /// <summary>いまの接続の様子。</summary>
    public ConnectionStatusViewModel Connection { get; }

    /// <summary>ツールバーの操作が効く側（最後に触った側）。</summary>
    public FilePaneViewModel ActivePane
    {
        get => _activePane;
        private set
        {
            var previous = _activePane;
            if (!SetProperty(ref _activePane, value))
                return;
            previous.IsActive = false;
            value.IsActive = true;
            NotifyTransferCommands();
        }
    }

    /// <summary>接続先の一覧を開いているか。</summary>
    public bool IsSidebarOpen { get; set => SetProperty(ref field, value); } = true;

    /// <summary>サーバーに接続しているか。</summary>
    public bool IsConnected
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                DisconnectCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>接続しようとしているところか。</summary>
    public bool IsConnecting
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                DisconnectCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>いまの接続（転送の接続を作るのに使う）。</summary>
    public ConnectResult? Session => _session;

    public IRelayCommand ToggleSidebarCommand { get; }

    /// <summary>操作の対象の側の一覧を取り直す（ツールバーの「更新」）。</summary>
    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand<ConnectionItemViewModel> ConnectCommand { get; }

    public IAsyncRelayCommand DisconnectCommand { get; }

    public IAsyncRelayCommand NewConnectionCommand { get; }

    /// <summary>保存せずに接続する（クイック接続。Ctrl+Q）。</summary>
    public IAsyncRelayCommand QuickConnectCommand { get; }

    public IAsyncRelayCommand<ConnectionItemViewModel> EditConnectionCommand { get; }

    public IAsyncRelayCommand<ConnectionItemViewModel> DuplicateConnectionCommand { get; }

    public IAsyncRelayCommand<ConnectionItemViewModel> DeleteConnectionCommand { get; }

    /// <summary>選んだファイルをサーバーへ送る（Ctrl+U）。</summary>
    public IAsyncRelayCommand UploadCommand { get; }

    /// <summary>選んだファイルをパソコンへ受け取る（Ctrl+D）。</summary>
    public IAsyncRelayCommand DownloadCommand { get; }

    /// <summary>操作の対象の側に、新しいフォルダを作る（Ctrl+K）。</summary>
    public IAsyncRelayCommand NewFolderCommand { get; }

    /// <summary>選んだものの名前を変える（F2、Ctrl+N）。</summary>
    public IAsyncRelayCommand RenameCommand { get; }

    /// <summary>選んだものを消す（Delete）。パソコン側はごみ箱へ移す。</summary>
    public IAsyncRelayCommand DeleteCommand { get; }

    /// <summary>サーバー側で選んだものの属性を変える（Ctrl+T）。</summary>
    public IAsyncRelayCommand ChangePermissionsCommand { get; }

    /// <summary>設定の画面を出す。</summary>
    public IAsyncRelayCommand OpenSettingsCommand { get; }

    /// <summary>下の一覧で「ログ」を出す。</summary>
    public IRelayCommand ShowLogCommand { get; }

    /// <summary>パソコン側の隠しファイルを出す・出さない（設定にも残る）。</summary>
    public IAsyncRelayCommand ToggleHiddenFilesCommand { get; }

    /// <summary>一覧の行を詰める・詰めない（設定にも残る）。</summary>
    public IAsyncRelayCommand ToggleCompactRowsCommand { get; }

    /// <summary>アプリのログ（接続、転送、ファイルの操作、通信の詳細）。</summary>
    public ActivityLog Log { get; }

    /// <summary>下の「ログ」。</summary>
    public LogViewModel LogView { get; }

    /// <summary>サーバー側で選んだものの属性を変えられるか。</summary>
    public bool CanChangePermissions => _session is not null && Remote.HasLoaded && Remote.SelectedRows.Count > 0 && _services?.FileDialogs is not null;

    /// <summary>アップロードできるか（接続していて、パソコン側で何か選んでいる）。</summary>
    public bool CanUpload =>
        _transferController is not null && Remote.HasLoaded
        && Local.SelectedRows.Any(row => row.Entry.Type is FileEntryType.File or FileEntryType.Directory);

    /// <summary>ダウンロードできるか（接続していて、サーバー側で何か選んでいて、パソコン側でフォルダを開いている）。</summary>
    public bool CanDownload =>
        _transferController is not null && Local.HasLoaded && Local.CurrentPath.Length > 0 && Remote.SelectedRows.Count > 0;

    /// <summary>まだ終わっていない転送の数。</summary>
    public int PendingTransferCount => _transferController?.PendingCount ?? 0;

    /// <summary>接続した、または切った。</summary>
    public event EventHandler? SessionChanged;

    /// <summary>
    /// ふだんの起動のときの画面。パソコン側はホームフォルダを開き、保存した接続先を並べる。
    /// </summary>
    public static async Task<MainViewModel> CreateAsync(AppServices services, TimeProvider? timeProvider = null, AppSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var time = timeProvider ?? TimeProvider.System;
        settings ??= new AppSettings();

        IReadOnlyList<ConnectionProfile> profiles = [];
        TransferFailure? loadFailure = null;
        try
        {
            profiles = await ConnectionStore.LoadAsync(services.ConnectionsFile).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            loadFailure = FailureClassifier.FromException(ex);
        }

        TrustStore trust;
        try
        {
            trust = await TrustStore.LoadAsync(services.TrustedKeysFile, time).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めなければ、信頼した鍵をもう一度確かめてもらう（危なくはない）。ファイルは上書きしない。
            trust = TrustStore.InMemory(time);
        }

        var local = new FilePaneViewModel(PaneKind.Local, time);
        var remote = new FilePaneViewModel(PaneKind.Remote, time);
        var transfers = new TransferListViewModel(EmptyTransferSource.Instance, time, SynchronizationContext.Current);
        var log = new ActivityLog(timeProvider: time);
        var main = new MainViewModel(
            local,
            remote,
            transfers,
            new ConnectionListViewModel(profiles),
            new ConnectionStatusViewModel(),
            services,
            new ConnectionService(trust, services.Secrets, services.Prompts, log: log),
            time,
            log);
        if (loadFailure is not null)
        {
            main._saveBlocked = true;
            remote.ShowNotice("接続先を読み込めませんでした", DisplayText.Describe(loadFailure), "アプリを開き直してください。それまでは接続先を保存しません");
        }

        // 前回の様子に戻す（前に開いていたフォルダが無くなっていれば、ホームから）
        main.Settings = settings;
        main.IsSidebarOpen = settings.IsSidebarOpen;
        transfers.IsExpanded = settings.IsTransferListExpanded;
        transfers.PanelHeight = settings.TransferListHeight;
        local.UseCompactRows = remote.UseCompactRows = settings.UseCompactRows;
        main.UpdateExclusions();
        var localSource = new LocalFileSource(time) { ShowHidden = settings.ShowHiddenFiles };
        var start = settings.LastLocalFolder is { Length: > 0 } last && Directory.Exists(last) ? last : null;
        await local.SetSourceAsync(localSource, start).ConfigureAwait(true);
        return main;
    }

    /// <summary>いまの設定（開いた・閉じた、見た目など）。</summary>
    public AppSettings Settings { get; private set => SetProperty(ref field, value); } = new();

    /// <summary>設定を変える（画面の色は画面が、隠しファイルはここで反映する）。保存もする。</summary>
    public async Task ApplySettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var hiddenChanged = settings.ShowHiddenFiles != Settings.ShowHiddenFiles;
        var exclusionsChanged = !Equals(settings.Exclusions, Settings.Exclusions);
        Settings = settings;
        Local.UseCompactRows = Remote.UseCompactRows = settings.UseCompactRows;
        if (exclusionsChanged)
            UpdateExclusions();
        if (hiddenChanged && Local.Source is LocalFileSource source)
        {
            source.ShowHidden = settings.ShowHiddenFiles;
            await Local.RefreshAsync().ConfigureAwait(true);
        }
        SaveSettings(window: null);
    }

    /// <summary>いま使う送らないファイルの決まり（設定と、つないでいる接続先ごとの名前）。</summary>
    public ExclusionRules CurrentExclusions => Settings.Exclusions.ToRules(_connected?.Profile.ExcludePatternList);

    // パソコン側の一覧の「送らない」の印を、いまの決まりに合わせる
    private void UpdateExclusions() => Local.Exclusions = CurrentExclusions;

    // 「自動」で決まった方式を、この接続先に覚える（次からはその方式で直接つなぐ。暗号化できたなら、暗号化なしには戻らない）
    private async Task RememberProtocolAsync(ConnectionItemViewModel item, ConnectionProtocol protocol)
    {
        item.Profile = item.Profile with { Protocol = protocol };
        Log.Info($"方式は「{ProtocolName(protocol)}」に決まりました。次からもこの方式で接続します", item.Name);
        if (!item.IsTemporary)
            await SaveConnectionsAsync().ConfigureAwait(true);
    }

    /// <summary>方式の選び直しの案。</summary>
    private sealed record ProtocolSuggestion(ConnectionProtocol Protocol, string Hint, string ButtonText);

    // 方式が違うために接続できなかったらしいときの案（ロリポップ！のように、暗号化しないと入れないサーバーなど）
    private static ProtocolSuggestion? SuggestProtocol(ConnectionProfile profile, TransferFailure failure)
    {
        if (profile.Protocol == ConnectionProtocol.Ftp && failure.Reason == FailureReason.EncryptionRequired)
        {
            return new ProtocolSuggestion(
                ConnectionProtocol.FtpsExplicit,
                "方式を「FTPS（明示的）」にすると接続できます",
                "FTPS（明示的）でつなぎ直す");
        }
        if (profile.Protocol == ConnectionProtocol.FtpsImplicit
            && failure.Reason is FailureReason.Timeout or FailureReason.ConnectionLost or FailureReason.SecureConnectionFailed)
        {
            return new ProtocolSuggestion(
                ConnectionProtocol.FtpsExplicit,
                "FTPS（暗黙的）はポート 990 を使います。多くのレンタルサーバーは「FTPS（明示的）」です",
                "FTPS（明示的）でつなぎ直す");
        }
        return null;
    }

    // 方式を変えて保存し、つなぎ直す（暗黙的の 990 番を指定していたなら、ポートも既定に戻す）
    private async Task SwitchProtocolAndConnectAsync(ConnectionItemViewModel item, ConnectionProtocol protocol, string? password)
    {
        var port = item.Profile.Port == ConnectionProfile.DefaultPort(item.Profile.Protocol) ? 0 : item.Profile.Port;
        item.Profile = item.Profile with { Protocol = protocol, Port = port };
        Log.Info($"方式を「{ProtocolName(protocol)}」に変えました", item.Name);
        if (!item.IsTemporary)
            await SaveConnectionsAsync().ConfigureAwait(true);
        await ConnectAsync(item, password).ConfigureAwait(true);
    }

    // 画面に出す方式の名前（接続先の編集の選択肢と同じ言い方）
    private static string ProtocolName(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Sftp => "SFTP",
        ConnectionProtocol.FtpsExplicit => "FTPS（明示的）",
        ConnectionProtocol.FtpsImplicit => "FTPS（暗黙的）",
        ConnectionProtocol.Auto => "自動",
        _ => "FTP（暗号化なし）",
    };

    // ログに書く接続先（例: SFTP・example.com:22）
    private static string DescribeTarget(ConnectionProfile profile)
    {
        var protocol = profile.Protocol switch
        {
            ConnectionProtocol.Sftp => "SFTP",
            ConnectionProtocol.FtpsExplicit => "FTPS",
            ConnectionProtocol.FtpsImplicit => "FTPS（暗黙）",
            ConnectionProtocol.Auto => "自動",
            _ => "FTP",
        };
        return $"{protocol}・{profile.Host}:{profile.EffectivePort}";
    }

    // 設定の画面を出す。変えたものはその場で反映・保存されるので、閉じたら反映が終わるのを待つだけ
    private async Task OpenSettingsAsync()
    {
        if (_services?.SettingsDialogs is not { } dialogs)
            return;
        var settings = new SettingsViewModel(this, _services.DataDirectory);
        await dialogs.ShowSettingsAsync(settings).ConfigureAwait(true);
        await settings.Applying.ConfigureAwait(true);
    }

    /// <summary>
    /// 設定と、いまの画面の様子（ウインドウの位置、開いているフォルダなど）を保存する。<paramref name="window"/> が null なら前回のまま。
    /// </summary>
    public void SaveSettings(WindowPlacement? window)
    {
        if (_services is null)
            return;
        Settings = Settings with
        {
            Window = window ?? Settings.Window,
            LastLocalFolder = Local.HasLoaded && Local.CurrentPath.Length > 0 ? Local.CurrentPath : Settings.LastLocalFolder,
            IsSidebarOpen = IsSidebarOpen,
            IsTransferListExpanded = Transfers.IsExpanded,
            TransferListHeight = Transfers.PanelHeight,
        };
        AppSettings.TrySave(_services.SettingsFile, Settings);
    }

    /// <summary>
    /// 接続先に接続し、サーバー側の一覧を出す。いまの接続は先に切る。接続できなければ理由を出す。
    /// 接続しているあいだに別の接続先を選ぶと、前の接続はやめる。
    /// </summary>
    public async Task ConnectAsync(ConnectionItemViewModel item, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_connector is null || (ReferenceEquals(item, _connected) && IsConnected) || ReferenceEquals(item, _connectingItem))
        {
            Connections.Select(item); // 見本の画面では選ぶだけ
            return;
        }
        // 別の接続先にすると、いまの接続の転送は止まる
        if (_session is not null && !await ConfirmStopTransfersAsync(closingApp: false).ConfigureAwait(true))
            return;
        Connections.Select(item);

        // ほかの接続の試みはやめる（その後始末は、そちらが自分でする）
        _connecting?.Cancel();
        using var connecting = new CancellationTokenSource();
        _connecting = connecting;
        _connectingItem = item;
        IsConnecting = true;
        item.IsConnecting = true;

        ConnectResult result;
        try
        {
            await CloseSessionAsync().ConfigureAwait(true);
            if (connecting.IsCancellationRequested)
                return; // 切るのを待つあいだに、別の接続先を選んだ
            Connection.ShowConnecting(item);
            await Remote.SetSourceAsync(null).ConfigureAwait(true); // 前の失敗の知らせを消す
            Remote.SetBusy($"{item.Name} に接続しています…");
            Log.Info($"{item.Name} に接続しています（{DescribeTarget(item.Profile)}）", item.Name);
            password ??= _sessionPasswords.GetValueOrDefault(item.Profile.Id);
            try
            {
                result = await _connector.ConnectAsync(item.Profile, password, connecting.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (connecting.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 思いがけないエラーでも、アプリは落とさずに理由を出す
                result = ConnectResult.Failed(item.Profile, FailureClassifier.FromException(ex));
            }
        }
        finally
        {
            item.IsConnecting = false;
            // 別の接続先を選んだあとなら、画面はそちらのものなので触らない
            if (ReferenceEquals(_connecting, connecting))
            {
                _connecting = null;
                _connectingItem = null;
                IsConnecting = false;
                Remote.SetBusy(null);
            }
        }

        if (connecting.IsCancellationRequested)
        {
            await result.DisposeQuietlyAsync().ConfigureAwait(true);
            return;
        }
        if (result.IsCancelled)
        {
            Log.Info("接続をやめました", item.Name);
            Connection.ShowDisconnected();
            return;
        }
        if (!result.Succeeded)
        {
            var reason = DisplayText.Describe(result.Failure!);
            Log.Error($"接続できませんでした: {reason}", item.Name);
            Connection.ShowDisconnected();
            if (SuggestProtocol(item.Profile, result.Failure!) is { } suggestion)
            {
                // 方式が合っていないらしい。直し方を書き、ボタン 1 つで直してつなぎ直せるようにする
                Remote.ShowNotice(
                    "接続できませんでした",
                    reason,
                    suggestion.Hint,
                    () => SwitchProtocolAndConnectAsync(item, suggestion.Protocol, password),
                    suggestion.ButtonText);
            }
            else
            {
                Remote.ShowNotice("接続できませんでした", reason, "", () => ConnectAsync(item, password));
            }
            return;
        }

        if (result.Profile.Protocol != item.Profile.Protocol)
            await RememberProtocolAsync(item, result.Profile.Protocol).ConfigureAwait(true);
        _session = result;
        _connected = item;
        UpdateExclusions();
        item.IsConnected = true;
        IsConnected = true;
        Connection.ShowConnected(item);
        Log.Success($"接続しました（{Connection.SecurityText}）", item.Name);
        Remote.LogSource = item.Name;
        var profile = item.Profile;
        await AttachTransfersAsync(result, item).ConfigureAwait(true);
        var start = profile.RemoteDirectory.Length > 0 ? profile.RemoteDirectory : result.HomeDirectory;
        await Remote.SetSourceAsync(new RemoteFileSource(result.Browser!, profile.Host), start).ConfigureAwait(true);
        if (profile.LocalDirectory.Length > 0 && Directory.Exists(profile.LocalDirectory))
            await Local.NavigateAsync(profile.LocalDirectory).ConfigureAwait(true);
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// URL（例: sftp://user@example.com/var/www）で接続する。接続先は保存せず、一覧の「クイック接続」に置く。
    /// URL にパスワードがあれば、保存はせずにアプリを閉じるまで覚えておく。
    /// </summary>
    public Task QuickConnectAsync(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!ConnectionProfile.TryParseUrl(url, out var profile, out var password))
        {
            Remote.ShowNotice("接続できませんでした", "URL を読み取れませんでした", url.Trim());
            return Task.CompletedTask;
        }
        return QuickConnectAsync(profile, password);
    }

    /// <summary>保存せずに接続する。一覧の「クイック接続」に置き、パスワードはアプリを閉じるまで覚えておく。</summary>
    public async Task QuickConnectAsync(ConnectionProfile profile, string? password)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var item = Connections.AddTemporary(profile);
        if (password is not null)
            _sessionPasswords[item.Profile.Id] = password;
        await ConnectAsync(item, password).ConfigureAwait(true);
    }

    /// <summary>接続を切る。接続しているところなら、やめる。転送が残っていれば、止めてよいか聞く。</summary>
    public async Task DisconnectAsync()
    {
        if (_session is not null && !await ConfirmStopTransfersAsync(closingApp: false).ConfigureAwait(true))
            return;
        _connecting?.Cancel();
        Connection.ShowDisconnected();
        await CloseSessionAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// アプリを閉じる前に呼ぶ。転送が残っていれば止めてよいか聞き、よければ切断して送信待ちを保存する。閉じてよければ true。
    /// </summary>
    public async Task<bool> PrepareToCloseAsync()
    {
        if (!await ConfirmStopTransfersAsync(closingApp: true).ConfigureAwait(true))
            return false;
        _connecting?.Cancel();
        await CloseSessionAsync().ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// パソコンのファイルやフォルダ（パスで指定）を、サーバーのいまのフォルダへ送る（ドラッグ＆ドロップなど）。送る件数を返す。
    /// </summary>
    public Task<int> UploadAsync(IEnumerable<string> localPaths)
    {
        ArgumentNullException.ThrowIfNull(localPaths);
        var entries = new List<FileEntry>();
        foreach (var path in localPaths)
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                entries.Add(new FileEntry(info.Name, info.FullName, FileEntryType.Directory, 0, info.LastWriteTimeUtc));
            }
            else if (File.Exists(path))
            {
                var info = new FileInfo(path);
                entries.Add(new FileEntry(info.Name, info.FullName, FileEntryType.File, info.Length, info.LastWriteTimeUtc));
            }
        }
        return UploadEntriesAsync(entries);
    }

    // いまの接続を閉じ、サーバー側の一覧を空にする。転送は止めて、送信待ちを保存する
    private async Task CloseSessionAsync()
    {
        var session = _session;
        var connected = _connected;
        _session = null;
        _connected = null;
        UpdateExclusions();
        if (connected is not null)
            connected.IsConnected = false;
        IsConnected = false;
        await DetachTransfersAsync().ConfigureAwait(true);
        if (session is null)
            return;
        await Remote.SetSourceAsync(null).ConfigureAwait(true);
        await session.DisposeQuietlyAsync().ConfigureAwait(true);
        Log.Info("切断しました", connected?.Name);
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    // 接続先ごとの送信待ちを用意する。前回の残りがあれば読み込む（勝手には始めない）
    private async Task AttachTransfersAsync(ConnectResult result, ConnectionItemViewModel item)
    {
        var profile = item.Profile;
        var options = new TransferOptions { MaxConcurrency = Math.Clamp(profile.MaxConcurrency, 1, 10) };
        var queueFile = item.IsTemporary || _services is null ? null : _services.QueueFile(profile.Id);
        var controller = new TransferController(result.Factory!, options, queueFile, _time, Log, item.Name);
        await controller.RestoreAsync().ConfigureAwait(true);
        controller.ItemChanged += OnTransferItemChanged;
        _transferController = controller;
        Transfers.SetSource(controller);
        Transfers.Start();
        NotifyTransferCommands();
    }

    private async Task DetachTransfersAsync()
    {
        if (_transferController is not { } controller)
            return;
        _transferController = null;
        controller.ItemChanged -= OnTransferItemChanged;
        Transfers.SetSource(EmptyTransferSource.Instance);
        NotifyTransferCommands();
        await controller.DisposeAsync().ConfigureAwait(true);
    }

    private async Task<bool> ConfirmStopTransfersAsync(bool closingApp)
    {
        // 止めてある送信待ちは、閉じても保存されて次に続けられるので、聞かない
        if (_transferController is not { IsPaused: false } controller || controller.PendingCount == 0)
            return true;
        if (_services?.TransferDialogs is not { } dialogs)
            return true;
        return await dialogs.ConfirmStopTransfersAsync(controller.PendingCount, closingApp).ConfigureAwait(true);
    }

    private async Task<int> UploadEntriesAsync(IReadOnlyList<FileEntry> entries)
    {
        if (_transferController is not { } controller || Local.Source is not { } local || !Remote.HasLoaded)
            return 0;
        var selected = entries.Where(entry => entry.Type is FileEntryType.File or FileEntryType.Directory).ToList();
        if (selected.Count == 0)
            return 0;
        // 送らないファイル（Thumbs.db など）は、選んであっても送らない
        var exclusions = CurrentExclusions;
        var targets = selected.Where(entry => exclusions.MatchLast(entry.Name, entry.IsDirectory) is null).ToList();
        var excludedHere = selected.Count - targets.Count;
        if (targets.Count == 0)
        {
            Local.ShowNotice("送るものがありません", "選んだものは、すべて送らないファイルです", "設定の「送らないファイル」で変えられます");
            return 0;
        }
        var remoteFolder = Remote.CurrentPath;
        var conflicts = targets.Where(entry => Remote.HasItemNamed(entry.Name)).Select(entry => entry.Name).ToList();
        if (await ChooseConflictPolicyAsync(TransferDirection.Upload, conflicts).ConfigureAwait(true) is not { } policy)
            return 0;

        TransferPlan plan;
        try
        {
            plan = await TransferPlanner.PlanUploadAsync(local, targets, remoteFolder, policy, exclusions).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Local.ShowNotice("アップロードできませんでした", DisplayText.Describe(FailureClassifier.FromException(ex)));
            return 0;
        }
        return Enqueue(controller, plan with { ExcludedCount = plan.ExcludedCount + excludedHere }, Local);
    }

    private async Task DownloadEntriesAsync(IReadOnlyList<FileEntry> entries)
    {
        if (_transferController is not { } controller || Remote.Source is not { } remote
            || !Local.HasLoaded || Local.CurrentPath.Length == 0 || entries.Count == 0)
            return;
        var targets = entries.Where(entry => entry.Type != FileEntryType.Drive).ToList();
        var localFolder = Local.CurrentPath;
        var conflicts = targets.Where(entry => Local.HasItemNamed(TransferPlanner.ToLocalName(entry.Name))).Select(entry => entry.Name).ToList();
        if (await ChooseConflictPolicyAsync(TransferDirection.Download, conflicts).ConfigureAwait(true) is not { } policy)
            return;

        TransferPlan plan;
        try
        {
            plan = await TransferPlanner.PlanDownloadAsync(remote, targets, localFolder, policy).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Remote.ShowNotice("ダウンロードできませんでした", DisplayText.Describe(FailureClassifier.FromException(ex)));
            return;
        }
        Enqueue(controller, plan, Remote);
    }

    // 依頼を送信待ちに入れる。中を見られなかったフォルダがあれば、選んだ側に知らせる
    private int Enqueue(TransferController controller, TransferPlan plan, FilePaneViewModel from)
    {
        // 依頼を作っているあいだに切断した
        if (!ReferenceEquals(controller, _transferController))
            return 0;
        if (plan.Requests.Count > 0)
        {
            controller.Enqueue(plan.Requests);
            var upload = plan.Requests[0].Direction == TransferDirection.Upload;
            var size = plan.Requests.Sum(request => Math.Max(0, request.ExpectedBytes ?? 0));
            var excluded = plan.ExcludedCount > 0 ? $"。送らないファイル {plan.ExcludedCount:N0} 件を除きました" : "";
            Log.Info(
                $"{(upload ? "アップロード" : "ダウンロード")}に {plan.Requests.Count:N0} 件を加えました（{DisplayText.Size(size)}{excluded}）",
                _connected?.Name);
        }
        else if (plan.ExcludedCount > 0)
        {
            from.ShowNotice("送るものがありません", "フォルダの中は、すべて送らないファイルです", "設定の「送らないファイル」で変えられます");
        }
        if (plan.UnreadableFolders.Count > 0)
        {
            var folders = string.Join("、", plan.UnreadableFolders.Take(3)) + (plan.UnreadableFolders.Count > 3 ? " ほか" : "");
            from.ShowNotice("一部のフォルダは送りませんでした", "中を見られないフォルダがあります", folders);
            Log.Warning($"中を見られないフォルダは送りませんでした: {folders}", _connected?.Name);
        }
        else if (plan.TooMany)
        {
            from.ShowNotice("一部だけ送ります", $"一度に送れるのは {TransferPlanner.MaxFiles:N0} ファイルまでです");
            Log.Warning($"一度に送れるのは {TransferPlanner.MaxFiles:N0} ファイルまでなので、一部だけ送ります", _connected?.Name);
        }
        return plan.Requests.Count;
    }

    private async Task<ConflictPolicy?> ChooseConflictPolicyAsync(TransferDirection direction, IReadOnlyList<string> conflicts)
    {
        if (conflicts.Count == 0)
            return ConflictPolicy.Overwrite; // 送り先にないものだけ（フォルダの中にも、ぶつかるものはない）
        if (_services?.TransferDialogs is not { } dialogs)
            return ConflictPolicy.Overwrite;
        return await dialogs.AskConflictAsync(direction, [.. conflicts.Take(5)], conflicts.Count).ConfigureAwait(true);
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilePaneViewModel.SelectedRows) or nameof(FilePaneViewModel.CurrentPath) or nameof(FilePaneViewModel.HasSource))
            NotifyTransferCommands();
    }

    private void NotifyTransferCommands()
    {
        UploadCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ChangePermissionsCommand.NotifyCanExecuteChanged();
    }

    private void OnPaneActionRequested(object? sender, PaneAction action)
    {
        if (sender is not FilePaneViewModel pane)
            return;
        ActivePane = pane;
        _ = action switch
        {
            PaneAction.Transfer when pane.Kind == PaneKind.Local => UploadEntriesAsync([.. pane.SelectedRows.Select(row => row.Entry)]),
            PaneAction.Transfer => DownloadEntriesAsync([.. pane.SelectedRows.Select(row => row.Entry)]),
            PaneAction.NewFolder when CanCreateFolder(pane) => NewFolderAsync(pane),
            PaneAction.Rename when CanRename(pane) => RenameAsync(pane),
            PaneAction.Delete when CanDelete(pane) => DeleteAsync(pane),
            PaneAction.ChangePermissions when pane.Kind == PaneKind.Remote && CanChangePermissions => ChangePermissionsAsync(pane),
            _ => Task.CompletedTask,
        };
    }

    // ---- 新しいフォルダ・名前の変更・削除・属性の変更 ----

    private ILocalFileOperations LocalFiles => _services?.LocalFiles ?? LocalFileOperations.Instance;

    private bool CanCreateFolder(FilePaneViewModel pane) =>
        _services?.FileDialogs is not null && pane.HasLoaded
        && (pane.Kind == PaneKind.Local ? pane.CurrentPath.Length > 0 : _session is not null);

    private bool CanRename(FilePaneViewModel pane) =>
        CanCreateFolder(pane) && pane.SelectedRows.Count == 1 && pane.SelectedRows[0].Entry.Type != FileEntryType.Drive;

    private bool CanDelete(FilePaneViewModel pane) =>
        CanCreateFolder(pane) && pane.SelectedRows.Count > 0 && pane.SelectedRows.All(row => row.Entry.Type != FileEntryType.Drive);

    private string CombineIn(FilePaneViewModel pane, string name) =>
        pane.Kind == PaneKind.Local ? Path.Combine(pane.CurrentPath, name) : RemotePath.Combine(pane.CurrentPath, name);

    // サーバーの操作は画面用の接続で行う（つなぎ直すときに鍵を聞くことがあるので、画面のスレッドでは動かさない）
    private Task OnServerAsync(Func<RemoteBrowser, Task> operation)
    {
        var browser = _session?.Browser ?? throw new InvalidOperationException("接続していません。");
        return Task.Run(() => operation(browser));
    }

    private async Task NewFolderAsync(FilePaneViewModel pane)
    {
        if (_services?.FileDialogs is not { } dialogs || !CanCreateFolder(pane))
            return;
        var initial = "新しいフォルダ";
        for (var i = 2; pane.HasItemNamed(initial); i++)
            initial = $"新しいフォルダ ({i})";
        var name = await dialogs.AskNameAsync("新しいフォルダ", initial, "作成", text =>
            FileNames.Validate(text, pane.Kind) ?? (pane.HasItemNamed(text.Trim()) ? "同じ名前のものがあります" : null)).ConfigureAwait(true);
        if (name is null)
            return;
        name = name.Trim();
        var path = CombineIn(pane, name);
        try
        {
            if (pane.Kind == PaneKind.Local)
                await Task.Run(() => LocalFiles.CreateDirectory(path)).ConfigureAwait(true);
            else
                await OnServerAsync(browser => browser.CreateDirectoryAsync(path, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = DisplayText.Describe(FailureClassifier.FromException(ex));
            pane.ShowNotice("フォルダを作れませんでした", reason, name);
            Log.Error($"フォルダを作れませんでした: {path}: {reason}", SourceOf(pane));
            return;
        }
        Log.Info($"フォルダを作りました: {path}", SourceOf(pane));
        await pane.RefreshAsync(name).ConfigureAwait(true);
    }

    private async Task RenameAsync(FilePaneViewModel pane)
    {
        if (_services?.FileDialogs is not { } dialogs || !CanRename(pane))
            return;
        var row = pane.SelectedRows[0];
        var oldName = row.Name;
        var comparison = pane.Kind == PaneKind.Local ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var name = await dialogs.AskNameAsync("名前の変更", oldName, "変更", text =>
        {
            var trimmed = text.Trim();
            if (string.Equals(trimmed, oldName, StringComparison.Ordinal))
                return null;
            // 大文字と小文字だけの違いは、パソコン側でも変えられる
            var conflict = pane.HasItemNamed(trimmed) && !string.Equals(trimmed, oldName, comparison);
            return FileNames.Validate(trimmed, pane.Kind) ?? (conflict ? "同じ名前のものがあります" : null);
        }).ConfigureAwait(true);
        if (name is null || string.Equals(name.Trim(), oldName, StringComparison.Ordinal))
            return;
        name = name.Trim();
        var from = row.Entry.FullPath;
        var to = CombineIn(pane, name);
        try
        {
            if (pane.Kind == PaneKind.Local)
                await Task.Run(() => LocalFiles.Rename(from, to)).ConfigureAwait(true);
            else
                await OnServerAsync(browser => browser.RenameAsync(from, to, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = DisplayText.Describe(FailureClassifier.FromException(ex));
            pane.ShowNotice("名前を変えられませんでした", reason, oldName);
            Log.Error($"名前を変えられませんでした: {from}: {reason}", SourceOf(pane));
            return;
        }
        Log.Info($"名前を変えました: {from} → {name}", SourceOf(pane));
        await pane.RefreshAsync(name).ConfigureAwait(true);
    }

    private async Task DeleteAsync(FilePaneViewModel pane)
    {
        if (_services?.FileDialogs is not { } dialogs || !CanDelete(pane))
            return;
        var rows = pane.SelectedRows.ToList();
        var remote = pane.Kind == PaneKind.Remote;
        if (!await dialogs.ConfirmDeleteAsync([.. rows.Select(row => row.Name)], remote, !remote && LocalFiles.UsesRecycleBin).ConfigureAwait(true))
            return;

        var failed = new List<string>();
        TransferFailure? firstFailure = null;
        if (remote)
        {
            // 1 つ失敗しても、ほかは消す
            foreach (var row in rows)
            {
                var path = row.Entry.FullPath;
                try
                {
                    if (row.Entry.Type == FileEntryType.Directory)
                        await OnServerAsync(browser => browser.DeleteDirectoryAsync(path, CancellationToken.None)).ConfigureAwait(true);
                    else
                        await OnServerAsync(browser => browser.DeleteFileAsync(path, CancellationToken.None)).ConfigureAwait(true);
                    Log.Info($"削除しました: {path}", SourceOf(pane));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add(row.Name);
                    var failure = FailureClassifier.FromException(ex);
                    firstFailure ??= failure;
                    Log.Error($"削除できませんでした: {path}: {DisplayText.Describe(failure)}", SourceOf(pane));
                }
            }
        }
        else
        {
            var paths = rows.Select(row => row.Entry.FullPath).ToList();
            try
            {
                await Task.Run(() => LocalFiles.Delete(paths)).ConfigureAwait(true);
                var where = LocalFiles.UsesRecycleBin ? "ごみ箱へ移しました" : "削除しました";
                foreach (var path in paths)
                    Log.Info($"{where}: {path}", SourceOf(pane));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.AddRange(rows.Select(row => row.Name));
                firstFailure = FailureClassifier.FromException(ex);
                Log.Error($"削除できませんでした: {string.Join("、", paths)}: {DisplayText.Describe(firstFailure)}", SourceOf(pane));
            }
        }

        await pane.RefreshAsync().ConfigureAwait(true);
        if (firstFailure is not null)
        {
            pane.ShowNotice(
                failed.Count == rows.Count ? "削除できませんでした" : "一部を削除できませんでした",
                DisplayText.Describe(firstFailure),
                string.Join("、", failed.Take(3)) + (failed.Count > 3 ? " ほか" : ""));
        }
    }

    private async Task ChangePermissionsAsync(FilePaneViewModel pane)
    {
        if (_services?.FileDialogs is not { } dialogs || !CanChangePermissions)
            return;
        var rows = pane.SelectedRows.ToList();
        var current = rows.Count == 1 ? UnixMode.FromText(rows[0].Entry.Permissions) : null;
        if (await dialogs.AskPermissionsAsync([.. rows.Select(row => row.Name)], current).ConfigureAwait(true) is not { } mode)
            return;

        TransferFailure? firstFailure = null;
        var failed = new List<string>();
        foreach (var row in rows)
        {
            var path = row.Entry.FullPath;
            try
            {
                await OnServerAsync(browser => browser.SetPermissionsAsync(path, mode, CancellationToken.None)).ConfigureAwait(true);
                Log.Info($"属性を {UnixMode.Format(mode)} にしました: {path}", SourceOf(pane));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(row.Name);
                var failure = FailureClassifier.FromException(ex);
                firstFailure ??= failure;
                Log.Error($"属性を変えられませんでした: {path}: {DisplayText.Describe(failure)}", SourceOf(pane));
            }
        }
        await pane.RefreshAsync().ConfigureAwait(true);
        if (firstFailure is not null)
            pane.ShowNotice("属性を変えられませんでした", DisplayText.Describe(firstFailure), string.Join("、", failed.Take(3)));
    }

    // ログに書く「どこのことか」（サーバー側なら接続先の名前、パソコン側は書かない）
    private string? SourceOf(FilePaneViewModel pane) => pane.Kind == PaneKind.Remote ? _connected?.Name : null;

    // 送り終わったファイルが、開いているフォルダの中なら、一覧を取り直す（FFFTP のように一覧が消えたりはしない）
    private void OnTransferItemChanged(object? sender, TransferItemSnapshot snapshot)
    {
        if (snapshot.State != TransferState.Completed)
            return;
        var upload = snapshot.Direction == TransferDirection.Upload;
        Post(() => RequestRefresh(upload ? Remote : Local, upload ? snapshot.RemotePath : snapshot.LocalPath));
    }

    private void RequestRefresh(FilePaneViewModel pane, string changedPath)
    {
        if (!pane.HasLoaded || !IsInside(pane, changedPath))
            return;
        lock (_refreshScheduled)
        {
            if (!_refreshScheduled.Add(pane))
                return;
        }
        _ = RefreshLaterAsync(pane);
    }

    private async Task RefreshLaterAsync(FilePaneViewModel pane)
    {
        try
        {
            await Task.Delay(RefreshDelay, _time).ConfigureAwait(true);
        }
        finally
        {
            lock (_refreshScheduled)
                _refreshScheduled.Remove(pane);
        }
        if (pane.HasLoaded)
            await pane.RefreshAsync().ConfigureAwait(true);
    }

    private static bool IsInside(FilePaneViewModel pane, string path)
    {
        var folder = pane.CurrentPath;
        if (pane.Kind == PaneKind.Remote)
        {
            var prefix = folder.EndsWith('/') ? folder : folder + "/";
            return path.StartsWith(prefix, StringComparison.Ordinal);
        }
        var localPrefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private void Post(Action action)
    {
        if (_context is null)
            action();
        else
            _context.Post(_ => action(), null);
    }

    private async Task NewConnectionAsync()
    {
        if (_services is null)
            return;
        var result = await _services.Dialogs.EditAsync(null, hasSavedPassword: false).ConfigureAwait(true);
        if (result is null)
            return;
        StorePassword(result);
        var item = Connections.Add(result.Profile);
        await SaveConnectionsAsync().ConfigureAwait(true);
        await ConnectAsync(item, result.Password).ConfigureAwait(true);
    }

    private async Task AskQuickConnectAsync()
    {
        if (_services is null)
            return;
        var result = await _services.Dialogs.AskQuickConnectAsync().ConfigureAwait(true);
        if (result is not null)
            await QuickConnectAsync(result.Profile, result.Password).ConfigureAwait(true);
    }

    private void UpdateDisconnectedHint() =>
        Remote.DisconnectedHint = Connections.IsEmpty && _services is not null
            ? "左の「新しい接続」で接続先を登録すると、ここにサーバーのファイルが出ます"
            : "左の接続先を選ぶと、ここにサーバーのファイルが出ます";

    private async Task EditConnectionAsync(ConnectionItemViewModel? item)
    {
        if (_services is null || item is null)
            return;
        var id = item.Profile.Id;
        var sessionPassword = _sessionPasswords.GetValueOrDefault(id);
        var hasSaved = sessionPassword is not null || _services.Secrets.TryRead(id) is not null;
        var result = await _services.Dialogs.EditAsync(item.Profile, hasSaved).ConfigureAwait(true);
        if (result is null)
            return;
        if (item.IsTemporary)
        {
            // クイック接続を保存する。URL のパスワードは、保存すると決めたときだけ保管場所に移す
            if (result.Password is null && sessionPassword is not null)
                result = result with { Password = sessionPassword };
            _sessionPasswords.Remove(id);
        }
        StorePassword(result);
        var wasConnected = ReferenceEquals(item, _connected);
        Connections.Update(item, result.Profile);
        await SaveConnectionsAsync().ConfigureAwait(true);
        if (!wasConnected)
            return;
        UpdateExclusions();
        if (RequiresReconnect(_session?.Profile, item.Profile))
        {
            // 接続のしかたが変わったので、つなぎ直す
            await DisconnectAsync().ConfigureAwait(true);
            await ConnectAsync(item, result.Password).ConfigureAwait(true);
        }
        else
        {
            Connection.ShowConnected(item); // 名前が変わったかもしれない
        }
    }

    private async Task DuplicateConnectionAsync(ConnectionItemViewModel? item)
    {
        if (_services is null || item is null || item.IsTemporary)
            return;
        var copy = item.Profile with { Id = Guid.NewGuid(), Name = $"{item.Profile.DisplayName} のコピー" };
        Connections.Select(Connections.Add(copy));
        await SaveConnectionsAsync().ConfigureAwait(true);
    }

    private async Task DeleteConnectionAsync(ConnectionItemViewModel? item)
    {
        if (_services is null || item is null)
            return;
        // クイック接続のものは保存していないので、確かめずに一覧から外す
        if (!item.IsTemporary && !await _services.Dialogs.ConfirmDeleteAsync(item.Profile).ConfigureAwait(true))
            return;
        if (ReferenceEquals(item, _connected) || ReferenceEquals(item, _connectingItem))
        {
            await DisconnectAsync().ConfigureAwait(true);
            if (ReferenceEquals(item, _connected))
                return; // 転送を止めたくないので、切断をやめた
        }
        _sessionPasswords.Remove(item.Profile.Id);
        if (!item.IsTemporary)
            _services.Secrets.TryDelete(item.Profile.Id);
        var wasTemporary = item.IsTemporary;
        Connections.Remove(item);
        if (!wasTemporary)
            await SaveConnectionsAsync().ConfigureAwait(true);
    }

    // 名前やまとまり、最初に開くフォルダだけなら、つなぎ直さない
    private static bool RequiresReconnect(ConnectionProfile? before, ConnectionProfile after) =>
        before is null
        || before.Protocol != after.Protocol
        || !string.Equals(before.Host.Trim(), after.Host.Trim(), StringComparison.OrdinalIgnoreCase)
        || before.EffectivePort != after.EffectivePort
        || !string.Equals(before.UserName, after.UserName, StringComparison.Ordinal)
        || !string.Equals(before.PrivateKeyPath, after.PrivateKeyPath, StringComparison.Ordinal)
        || !string.Equals(before.FileNameEncoding, after.FileNameEncoding, StringComparison.OrdinalIgnoreCase)
        || before.UsePassiveMode != after.UsePassiveMode;

    private void StorePassword(ConnectionEditResult result)
    {
        if (_services is null)
            return;
        if (!result.SavePassword)
            _services.Secrets.TryDelete(result.Profile.Id);
        else if (result.Password is not null)
            _services.Secrets.TryWrite(result.Profile.Id, result.Password);
    }

    private async Task SaveConnectionsAsync()
    {
        if (_services is null)
            return;
        if (_saveBlocked)
        {
            Remote.ShowNotice("接続先を保存できませんでした", "接続先のファイルを読み込めなかったため", "アプリを開き直してください");
            return;
        }
        try
        {
            await ConnectionStore.SaveAsync(_services.ConnectionsFile, Connections.Profiles).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Remote.ShowNotice("接続先を保存できませんでした", DisplayText.Describe(FailureClassifier.FromException(ex)));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connecting?.Cancel();
        if (_transferController is { } controller)
            _ = controller.DisposeAsync().AsTask();
        if (_session is { } session)
            _ = session.DisposeQuietlyAsync();
        Transfers.Dispose();
        LogView.Dispose();
    }
}
