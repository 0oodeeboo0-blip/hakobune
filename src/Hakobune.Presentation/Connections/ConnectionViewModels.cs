using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakobune.Core.Connections;

namespace Hakobune.Presentation.Connections;

/// <summary>接続先の一覧の 1 項目。</summary>
public sealed class ConnectionItemViewModel : ObservableObject
{
    /// <summary>保存した接続先から作る。<paramref name="isTemporary"/> なら保存しない（クイック接続）。</summary>
    public ConnectionItemViewModel(ConnectionProfile profile, bool isTemporary = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile;
        IsTemporary = isTemporary;
    }

    /// <summary>見本や試験のために、名前と方式とホストから作る。</summary>
    public ConnectionItemViewModel(string name, ConnectionProtocol protocol, string host)
        : this(new ConnectionProfile { Name = name, Protocol = protocol, Host = host })
    {
    }

    /// <summary>保存した接続先。</summary>
    public ConnectionProfile Profile
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetProperty(ref field, value))
                return;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Host));
            OnPropertyChanged(nameof(ProtocolLabel));
            OnPropertyChanged(nameof(IsEncrypted));
            OnPropertyChanged(nameof(IsInsecure));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>表示名。</summary>
    public string Name => Profile.DisplayName;

    /// <summary>ホスト名。</summary>
    public string Host => Profile.Host;

    /// <summary>方式の名前。</summary>
    public string ProtocolLabel => ConnectionProfile.ProtocolLabel(Profile.Protocol);

    /// <summary>暗号化するか。</summary>
    public bool IsEncrypted => Profile.IsEncrypted;

    /// <summary>暗号化しない接続か（警告の印を出す）。</summary>
    public bool IsInsecure => !IsEncrypted;

    /// <summary>2 行目（例: SFTP・example.com）。</summary>
    public string Subtitle => Profile.Describe();

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

    /// <summary>接続しようとしているところか。</summary>
    public bool IsConnecting { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// 保存しない接続先か（URL を指定してつないだもの）。アプリを閉じると消える。編集して保存すると、ふつうの接続先になる。
    /// </summary>
    public bool IsTemporary { get; internal set => SetProperty(ref field, value); }

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
        ArgumentNullException.ThrowIfNull(name);
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

/// <summary>左の接続先の一覧。保存した接続先を、まとまりごとに並べる。</summary>
public sealed class ConnectionListViewModel : ObservableObject
{
    /// <summary>まとまりの名前がない接続先を入れる見出し。</summary>
    public const string DefaultGroupName = "接続先";

    /// <summary>保存しない接続先（クイック接続）を入れる見出し。</summary>
    public const string TemporaryGroupName = "クイック接続";

    private readonly List<ConnectionItemViewModel> _items = [];

    /// <summary>空の一覧を作る。</summary>
    public ConnectionListViewModel()
    {
        SelectCommand = new RelayCommand<ConnectionItemViewModel>(Select);
    }

    /// <summary>接続先を並べた一覧を作る。</summary>
    public ConnectionListViewModel(IEnumerable<ConnectionProfile> profiles)
        : this()
    {
        ArgumentNullException.ThrowIfNull(profiles);
        foreach (var profile in profiles)
            _items.Add(new ConnectionItemViewModel(profile));
        Rebuild();
    }

    /// <summary>まとまり。</summary>
    public ObservableCollection<ConnectionGroupViewModel> Groups { get; } = [];

    /// <summary>すべての接続先（保存する順）。</summary>
    public IReadOnlyList<ConnectionItemViewModel> Items => _items;

    /// <summary>保存する中身（クイック接続のものは入れない）。</summary>
    public IReadOnlyList<ConnectionProfile> Profiles => [.. _items.Where(item => !item.IsTemporary).Select(item => item.Profile)];

    /// <summary>選んでいる接続先。</summary>
    public ConnectionItemViewModel? Selected { get; private set => SetProperty(ref field, value); }

    /// <summary>接続先がないか（案内を出す）。</summary>
    public bool IsEmpty => _items.Count == 0;

    public IRelayCommand<ConnectionItemViewModel> SelectCommand { get; }

    /// <summary>接続先が加わった・変わった・消えた（保存のきっかけ）。</summary>
    public event EventHandler? Changed;

    /// <summary>見本のために、まとまりをそのまま加える。</summary>
    public void Add(ConnectionGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        _items.AddRange(group.Items);
        Groups.Add(group);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>接続先を加える。</summary>
    public ConnectionItemViewModel Add(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var item = new ConnectionItemViewModel(profile);
        _items.Add(item);
        Rebuild();
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    /// <summary>
    /// 保存しない接続先（クイック接続）を加える。同じサーバー・同じユーザーのものがあれば、新しい設定にしてそれを返す。
    /// </summary>
    public ConnectionItemViewModel AddTemporary(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var existing = _items.FirstOrDefault(item => item.IsTemporary
            && item.Profile.Protocol == profile.Protocol
            && string.Equals(item.Profile.Host.Trim(), profile.Host.Trim(), StringComparison.OrdinalIgnoreCase)
            && item.Profile.EffectivePort == profile.EffectivePort
            && string.Equals(item.Profile.UserName, profile.UserName, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.Profile = profile with { Id = existing.Profile.Id };
            return existing;
        }
        var added = new ConnectionItemViewModel(profile, isTemporary: true);
        _items.Add(added);
        Rebuild();
        return added;
    }

    /// <summary>接続先を書き換える（まとまりが変わったら並べ直す）。クイック接続のものは、ふつうの接続先になる。</summary>
    public void Update(ConnectionItemViewModel item, ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(profile);
        var regroup = item.IsTemporary || !string.Equals(item.Profile.Group, profile.Group, StringComparison.Ordinal);
        item.Profile = profile with { Id = item.Profile.Id };
        item.IsTemporary = false;
        if (regroup)
            Rebuild();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>接続先を消す。</summary>
    public void Remove(ConnectionItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_items.Remove(item))
            return;
        if (ReferenceEquals(Selected, item))
            Select(null);
        Rebuild();
        Changed?.Invoke(this, EventArgs.Empty);
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

    /// <summary>ID で接続先を探す。</summary>
    public ConnectionItemViewModel? Find(Guid id) => _items.FirstOrDefault(item => item.Profile.Id == id);

    // まとまりごとに並べ直す。まとまりは最初に出てきた順、中は加えた順。開閉の状態は残す。
    private void Rebuild()
    {
        var expanded = Groups.ToDictionary(group => group.Name, group => group.IsExpanded, StringComparer.Ordinal);
        var groups = _items
            .GroupBy(GroupName, StringComparer.Ordinal)
            .Select(group => new ConnectionGroupViewModel(group.Key, group)
            {
                IsExpanded = expanded.GetValueOrDefault(group.Key, true),
            })
            .ToList();
        Groups.Clear();
        foreach (var group in groups)
            Groups.Add(group);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string GroupName(ConnectionItemViewModel item)
    {
        if (item.IsTemporary)
            return TemporaryGroupName;
        return string.IsNullOrWhiteSpace(item.Profile.Group) ? DefaultGroupName : item.Profile.Group.Trim();
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
    public string AccessibleName => !IsConnected ? Name : Host.Length > 0 ? $"{SecurityText}で {Name}（{Host}）に接続中" : $"{SecurityText}で {Name} に接続中";

    /// <summary>接続したことを出す。</summary>
    public void ShowConnected(ConnectionItemViewModel connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        IsConnected = true;
        ShowsSecure = connection.IsEncrypted;
        ShowsInsecure = !connection.IsEncrypted;
        SecurityText = connection.ProtocolLabel + (connection.IsEncrypted ? "・暗号化" : "・暗号化なし");
        Name = connection.Name;
        // 名前を付けていない接続先は、名前がホスト名なので、2 回並べない
        Host = string.Equals(connection.Name, connection.Host, StringComparison.OrdinalIgnoreCase) ? "" : connection.Host;
        OnPropertyChanged(nameof(AccessibleName));
    }

    /// <summary>接続しようとしているところを出す。</summary>
    public void ShowConnecting(ConnectionItemViewModel connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ShowDisconnected();
        Name = $"{connection.Name} に接続しています…";
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
