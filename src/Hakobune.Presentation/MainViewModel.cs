using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation;

/// <summary>メイン画面全体。</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>部品を組み合わせて作る。</summary>
    public MainViewModel(
        FilePaneViewModel local,
        FilePaneViewModel remote,
        TransferListViewModel transfers,
        ConnectionListViewModel connections,
        ConnectionStatusViewModel connection)
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
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarOpen = !IsSidebarOpen);
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

    /// <summary>接続先の一覧を開いているか。</summary>
    public bool IsSidebarOpen { get; set => SetProperty(ref field, value); } = true;

    public IRelayCommand ToggleSidebarCommand { get; }

    /// <summary>
    /// ふだんの起動のときの画面。パソコン側はホームフォルダを開き、サーバー側は接続していない状態にする。
    /// </summary>
    public static async Task<MainViewModel> CreateAsync(TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var local = new FilePaneViewModel(PaneKind.Local, time);
        var remote = new FilePaneViewModel(PaneKind.Remote, time);
        var transfers = new TransferListViewModel(EmptyTransferSource.Instance, time, SynchronizationContext.Current);
        var main = new MainViewModel(local, remote, transfers, new ConnectionListViewModel(), new ConnectionStatusViewModel());
        await local.SetSourceAsync(new LocalFileSource(time)).ConfigureAwait(true);
        return main;
    }

    /// <inheritdoc />
    public void Dispose() => Transfers.Dispose();
}
