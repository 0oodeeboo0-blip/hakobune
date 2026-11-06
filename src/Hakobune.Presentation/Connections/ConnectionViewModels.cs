using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Hakobune.Presentation.Connections;

/// <summary>接続の方式。</summary>
public enum ConnectionProtocol
{
    /// <summary>FTP（暗号化なし）。</summary>
    Ftp,

    /// <summary>FTPS（明示的）。</summary>
    Ftps,

    /// <summary>FTPS（暗黙的）。</summary>
    FtpsImplicit,

    /// <summary>SFTP。</summary>
    Sftp,
}

/// <summary>接続先の一覧の 1 項目。</summary>
public sealed class ConnectionItemViewModel : ObservableObject
{
    /// <summary>接続先を作る。</summary>
    public ConnectionItemViewModel(string name, ConnectionProtocol protocol, string host)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(host);
        Name = name;
        Protocol = protocol;
        Host = host;
    }

    /// <summary>表示名。</summary>
    public string Name { get; }

    /// <summary>方式。</summary>
    public ConnectionProtocol Protocol { get; }

    /// <summary>ホスト名。</summary>
    public string Host { get; }

    /// <summary>方式の名前。</summary>
    public string ProtocolLabel => Protocol switch
    {
        ConnectionProtocol.Ftp => "FTP",
        ConnectionProtocol.Sftp => "SFTP",
        _ => "FTPS",
    };

    /// <summary>暗号化するか。</summary>
    public bool IsEncrypted => Protocol != ConnectionProtocol.Ftp;

    /// <summary>暗号化しない接続か（警告の印を出す）。</summary>
    public bool IsInsecure => !IsEncrypted;

    /// <summary>2 行目（例: SFTP・example.com）。</summary>
    public string Subtitle => $"{ProtocolLabel}・{Host}";

    /// <summary>選んでいるか。</summary>
    public bool IsSelected
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(IsNotSelected));
        }
    }

    /// <summary>選んでいないか。</summary>
    public bool IsNotSelected => !IsSelected;

    /// <summary>接続しているか。</summary>
    public bool IsConnected
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>読み上げ用の説明。</summary>
    public string AccessibleName =>
        $"{Name}、{Subtitle}" + (IsConnected ? "、接続中" : "") + (IsInsecure ? "、暗号化なしの接続" : "");

    /// <summary>読み上げるときの名前になる。</summary>
    public override string ToString() => AccessibleName;
}

/// <summary>接続先のまとまり（フォルダ）。</summary>
public sealed class ConnectionGroupViewModel : ObservableObject
{
    /// <summary>まとまりを作る。</summary>
    public ConnectionGroupViewModel(string name, IEnumerable<ConnectionItemViewModel> items)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(items);
        Name = name;
        Items = new ObservableCollection<ConnectionItemViewModel>(items);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>名前。</summary>
    public string Name { get; }

    /// <summary>接続先。</summary>
    public ObservableCollection<ConnectionItemViewModel> Items { get; }

    /// <summary>開いているか。</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(ChevronRotation));
        }
    } = true;

    /// <summary>開閉の矢印の向き（度）。</summary>
    public double ChevronRotation => IsExpanded ? 0 : -90;

    public IRelayCommand ToggleCommand { get; }
}

/// <summary>左の接続先の一覧。</summary>
public sealed class ConnectionListViewModel : ObservableObject
{
    /// <summary>空の一覧を作る。</summary>
    public ConnectionListViewModel()
    {
        SelectCommand = new RelayCommand<ConnectionItemViewModel>(Select);
    }

    /// <summary>まとまり。</summary>
    public ObservableCollection<ConnectionGroupViewModel> Groups { get; } = [];

    /// <summary>選んでいる接続先。</summary>
    public ConnectionItemViewModel? Selected { get; private set => SetProperty(ref field, value); }

    /// <summary>接続先がないか。</summary>
    public bool IsEmpty => Groups.All(group => group.Items.Count == 0);

    public IRelayCommand<ConnectionItemViewModel> SelectCommand { get; }

    /// <summary>まとまりを加える。</summary>
    public void Add(ConnectionGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        Groups.Add(group);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>接続先を選ぶ。</summary>
    public void Select(ConnectionItemViewModel? item)
    {
        if (ReferenceEquals(item, Selected))
            return;
        if (Selected is not null)
            Selected.IsSelected = false;
        Selected = item;
        if (item is not null)
            item.IsSelected = true;
    }
}

/// <summary>タイトルバーに出す、いまの接続の様子。</summary>
public sealed class ConnectionStatusViewModel : ObservableObject
{
    /// <summary>接続しているか。</summary>
    public bool IsConnected { get; private set => SetProperty(ref field, value); }

    /// <summary>暗号化した接続か。</summary>
    public bool ShowsSecure { get; private set => SetProperty(ref field, value); }

    /// <summary>暗号化していない接続か。</summary>
    public bool ShowsInsecure { get; private set => SetProperty(ref field, value); }

    /// <summary>方式と暗号化の有無（例: SFTP・暗号化）。</summary>
    public string SecurityText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>接続先の名前。</summary>
    public string Name { get; private set => SetProperty(ref field, value); } = "接続していません";

    /// <summary>ホスト名。</summary>
    public string Host { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>読み上げ用の説明。</summary>
    public string AccessibleName => IsConnected ? $"{SecurityText}で {Name}（{Host}）に接続中" : "接続していません";

    /// <summary>接続したことを出す。</summary>
    public void ShowConnected(ConnectionItemViewModel connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        IsConnected = true;
        ShowsSecure = connection.IsEncrypted;
        ShowsInsecure = !connection.IsEncrypted;
        SecurityText = connection.ProtocolLabel + (connection.IsEncrypted ? "・暗号化" : "・暗号化なし");
        Name = connection.Name;
        Host = connection.Host;
        OnPropertyChanged(nameof(AccessibleName));
    }

    /// <summary>接続していないことを出す。</summary>
    public void ShowDisconnected()
    {
        IsConnected = false;
        ShowsSecure = false;
        ShowsInsecure = false;
        SecurityText = "";
        Name = "接続していません";
        Host = "";
        OnPropertyChanged(nameof(AccessibleName));
    }
}
