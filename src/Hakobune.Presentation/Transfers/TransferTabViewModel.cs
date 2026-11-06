using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Hakobune.Presentation.Transfers;

/// <summary>転送の一覧のタブ。</summary>
public enum TransferTab
{
    /// <summary>すべて。</summary>
    All,

    /// <summary>進行中（転送中と再試行待ち）。</summary>
    Active,

    /// <summary>待機中。</summary>
    Waiting,

    /// <summary>完了（スキップを含む）。</summary>
    Done,

    /// <summary>失敗。</summary>
    Failed,
}

/// <summary>転送の一覧のタブ 1 つ。</summary>
public sealed class TransferTabViewModel : ObservableObject
{
    internal TransferTabViewModel(TransferTab tab, string label)
    {
        Tab = tab;
        Label = label;
    }

    /// <summary>どのタブか。</summary>
    public TransferTab Tab { get; }

    /// <summary>名前。</summary>
    public string Label { get; }

    /// <summary>件数。</summary>
    public int Count
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
                OnBadgeChanged();
        }
    }

    /// <summary>件数の文字。</summary>
    public string CountText => Count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>選んでいるか。</summary>
    public bool IsSelected
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
                OnBadgeChanged();
        }
    }

    /// <summary>失敗があり、目立たせるか。</summary>
    public bool IsAlert => Tab == TransferTab.Failed && Count > 0;

    // 件数の丸の出し分け（選択中・失敗あり・ふつう）
    public bool ShowsSelectedBadge => IsSelected;

    public bool ShowsAlertBadge => !IsSelected && IsAlert;

    public bool ShowsNormalBadge => !IsSelected && !IsAlert;

    /// <summary>読み上げ用の説明。</summary>
    public string AccessibleName => $"{Label} {CountText} 件";

    private void OnBadgeChanged()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsAlert));
        OnPropertyChanged(nameof(ShowsSelectedBadge));
        OnPropertyChanged(nameof(ShowsAlertBadge));
        OnPropertyChanged(nameof(ShowsNormalBadge));
        OnPropertyChanged(nameof(AccessibleName));
    }
}
