using System.Collections.Specialized;

namespace Hakobune.Presentation.Tests;

public class ObservableListTests
{
    [Fact]
    public void Small_changes_are_applied_one_by_one_so_the_screen_keeps_its_place()
    {
        var list = new ObservableList<int>();
        list.ReplaceAll([1, 2, 3, 4, 5]);
        var actions = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => actions.Add(e.Action);

        list.SyncTo([1, 3, 4, 6, 5]);

        Assert.Equal(new[] { 1, 3, 4, 6, 5 }, list);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add }, actions);
    }

    [Fact]
    public void Nothing_is_announced_when_nothing_changed()
    {
        var list = new ObservableList<string>();
        list.ReplaceAll(["a", "b"]);
        var count = 0;
        list.CollectionChanged += (_, _) => count++;

        list.SyncTo(["a", "b"]);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Any_change_ends_in_the_desired_list()
    {
        var random = new Random(5);
        for (var round = 0; round < 300; round++)
        {
            var list = new ObservableList<int>();
            list.ReplaceAll(Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(0, 40)).Distinct());
            var desired = Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(0, 40)).Distinct().ToList();

            list.SyncTo(desired);

            Assert.Equal(desired, list);
        }
    }

    [Fact]
    public void A_complete_reorder_is_done_in_one_go()
    {
        var list = new ObservableList<int>();
        list.ReplaceAll(Enumerable.Range(0, 1000));
        var actions = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => actions.Add(e.Action);

        var reversed = Enumerable.Range(0, 1000).Reverse().ToList();
        list.SyncTo(reversed);

        Assert.Equal(reversed, list);
        Assert.Contains(NotifyCollectionChangedAction.Reset, actions);
        Assert.True(actions.Count < 300);
    }
}
