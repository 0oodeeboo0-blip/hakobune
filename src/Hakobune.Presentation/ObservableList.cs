using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Hakobune.Presentation;

/// <summary>
/// 画面の一覧に渡すコレクション。中身を入れ替えるときは、できるだけ少ない変更で済ませる。
/// 全部を消して入れ直すと、画面側で選択やスクロールの位置が失われるため。
/// </summary>
public sealed class ObservableList<T> : ObservableCollection<T>
{
    // 並び順が大きく変わったときは、1 件ずつ動かすより全部を入れ直したほうが速い
    private const int MoveLimit = 256;

    /// <summary>空で作る。</summary>
    public ObservableList()
    {
    }

    /// <summary>中身を <paramref name="desired"/> と同じにする。</summary>
    public void SyncTo(IReadOnlyList<T> desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var comparer = EqualityComparer<T>.Default;

        if (Count == desired.Count)
        {
            var same = true;
            for (var k = 0; k < desired.Count && same; k++)
                same = comparer.Equals(this[k], desired[k]);
            if (same)
                return;
        }

        if (Count == 0 || desired.Count == 0)
        {
            ReplaceAll(desired);
            return;
        }

        var wanted = new HashSet<T>(desired, comparer);
        var present = new HashSet<T>(this, comparer);
        var moves = 0;
        int i = 0, j = 0;
        while (j < desired.Count)
        {
            if (i < Count && !wanted.Contains(this[i]))
            {
                present.Remove(this[i]);
                RemoveAt(i);
                continue;
            }

            var item = desired[j];
            if (i < Count && comparer.Equals(this[i], item))
            {
                i++;
                j++;
                continue;
            }

            if (present.Contains(item))
            {
                // 並び順が変わった
                if (++moves > MoveLimit)
                {
                    ReplaceAll(desired);
                    return;
                }
                Move(IndexOf(item, i + 1), i);
            }
            else
            {
                Insert(i, item);
                present.Add(item);
            }
            i++;
            j++;
        }

        while (Count > desired.Count)
            RemoveAt(Count - 1);
    }

    /// <summary>中身を全部入れ替え、変更を 1 回だけ知らせる。</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private int IndexOf(T item, int start)
    {
        var comparer = EqualityComparer<T>.Default;
        for (var k = start; k < Count; k++)
        {
            if (comparer.Equals(this[k], item))
                return k;
        }
        return IndexOf(item);
    }
}
