using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakobune.Core.Connections;
using Hakobune.Core.Text;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation;

/// <summary>アプリが用意する、画面の外の仕組み。</summary>
/// <param name="DataDirectory">接続先や信頼した鍵を保存するフォルダ（例: %APPDATA%\Hakobune）。</param>
/// <param name="Secrets">パスワードの保管場所。</param>
/// <param name="Prompts">接続のときに利用者に聞く画面。</param>
/// <param name="Dialogs">接続先の作成・編集の画面。</param>
public sealed record AppServices(string DataDirectory, ISecretStore Secrets, IConnectionPrompts Prompts, IConnectionDialogs Dialogs)
{
    /// <summary>接続先の一覧のファイル。</summary>
    public string ConnectionsFile => Path.Combine(DataDirectory, "connections.json");

    /// <summary>信頼したサーバーの鍵のファイル。</summary>
    public string TrustedKeysFile => Path.Combine(DataDirectory, "trusted-keys.json");
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

    /// <summary>部品を組み合わせて作る。<paramref name="services"/> が null なら、接続はしない（見本の画面）。</summary>
    public MainViewModel(
        FilePaneViewModel local,
        FilePaneViewModel remote,
        TransferListViewModel transfers,
        ConnectionListViewModel connections,
        ConnectionStatusViewModel connection,
        AppServices? services = null,
        ConnectionService? connector = null)
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

    /// <summary>接続した、または切った。</summary>
    public event EventHandler? SessionChanged;

    /// <summary>
    /// ふだんの起動のときの画面。パソコン側はホームフォルダを開き、保存した接続先を並べる。
    /// </summary>
    public static async Task<MainViewModel> CreateAsync(AppServices services, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var time = timeProvider ?? TimeProvider.System;

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
        var main = new MainViewModel(
            local,
            remote,
            transfers,
            new ConnectionListViewModel(profiles),
            new ConnectionStatusViewModel(),
            services,
            new ConnectionService(trust, services.Secrets, services.Prompts));
        if (loadFailure is not null)
        {
            main._saveBlocked = true;
            remote.ShowNotice("接続先を読み込めませんでした", DisplayText.Describe(loadFailure), "アプリを開き直してください。それまでは接続先を保存しません");
        }
        await local.SetSourceAsync(new LocalFileSource(time)).ConfigureAwait(true);
        return main;
    }

    /// <summary>
    /// 接続先に接続し、サーバー側の一覧を出す。いまの接続は先に切る。接続できなければ理由を出す。
    /// 接続しているあいだに別の接続先を選ぶと、前の接続はやめる。
    /// </summary>
    public async Task ConnectAsync(ConnectionItemViewModel item, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        Connections.Select(item);
        if (_connector is null)
            return; // 見本の画面では選ぶだけ
        if ((ReferenceEquals(item, _connected) && IsConnected) || ReferenceEquals(item, _connectingItem))
            return;

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
            Connection.ShowDisconnected();
            return;
        }
        if (!result.Succeeded)
        {
            Connection.ShowDisconnected();
            Remote.ShowNotice("接続できませんでした", DisplayText.Describe(result.Failure!), "", () => ConnectAsync(item, password));
            return;
        }

        _session = result;
        _connected = item;
        item.IsConnected = true;
        IsConnected = true;
        Connection.ShowConnected(item);
        var profile = item.Profile;
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

    /// <summary>接続を切る。接続しているところなら、やめる。</summary>
    public async Task DisconnectAsync()
    {
        _connecting?.Cancel();
        Connection.ShowDisconnected();
        await CloseSessionAsync().ConfigureAwait(true);
    }

    // いまの接続を閉じ、サーバー側の一覧を空にする
    private async Task CloseSessionAsync()
    {
        var session = _session;
        var connected = _connected;
        _session = null;
        _connected = null;
        if (connected is not null)
            connected.IsConnected = false;
        IsConnected = false;
        if (session is null)
            return;
        await Remote.SetSourceAsync(null).ConfigureAwait(true);
        await session.DisposeQuietlyAsync().ConfigureAwait(true);
        SessionChanged?.Invoke(this, EventArgs.Empty);
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
            await DisconnectAsync().ConfigureAwait(true);
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
        if (_session is { } session)
            _ = session.DisposeQuietlyAsync();
        Transfers.Dispose();
    }
}
