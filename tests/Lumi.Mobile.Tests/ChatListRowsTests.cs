using System.Collections.Specialized;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class ChatListRowsTests
{
    [Fact]
    public void RowsMirrorGroupsThroughEveryKindOfChange()
    {
        var random = new Random(1234);
        var groups = new System.Collections.ObjectModel.ObservableCollection<ChatGroupViewModel>();
        var rows = new ChatListRows(groups);
        // Apply every notification to an independent copy, as an items control would.
        var observed = new List<object>();
        rows.CollectionChanged += (_, e) => Apply(observed, e, rows);

        var next = 0;
        ChatListItemViewModel NewChat() =>
            new(new RemoteChat { Id = Guid.NewGuid(), Title = $"Chat {next++}" });

        for (var step = 0; step < 2_000; step++)
        {
            switch (random.Next(9))
            {
                case 0 or 1:
                    var group = new ChatGroupViewModel($"Group {next++}");
                    for (var i = random.Next(4); i > 0; i--)
                        group.Chats.Add(NewChat());
                    groups.Insert(random.Next(groups.Count + 1), group);
                    break;
                case 2 when groups.Count > 0:
                    groups.RemoveAt(random.Next(groups.Count));
                    break;
                case 3 when groups.Count > 1:
                    groups.Move(random.Next(groups.Count), random.Next(groups.Count));
                    break;
                case 4 or 5 when groups.Count > 0:
                    var target = groups[random.Next(groups.Count)].Chats;
                    target.Insert(random.Next(target.Count + 1), NewChat());
                    break;
                case 6 when groups.Count > 0:
                    var shrinking = groups[random.Next(groups.Count)].Chats;
                    if (shrinking.Count > 0)
                        shrinking.RemoveAt(random.Next(shrinking.Count));
                    break;
                case 7 when groups.Count > 0:
                    var moving = groups[random.Next(groups.Count)].Chats;
                    if (moving.Count > 1)
                        moving.Move(random.Next(moving.Count), random.Next(moving.Count));
                    break;
                case 8 when groups.Count > 0 && random.Next(20) == 0:
                    groups.Clear();
                    break;
            }

            var expected = groups.SelectMany(g => new object[] { g }.Concat(g.Chats)).ToList();
            Assert.Equal(expected, rows.ToList());
            Assert.Equal(expected, observed);
        }
    }

    [Fact]
    public void RemovedGroupsNoLongerAffectTheRows()
    {
        var groups = new System.Collections.ObjectModel.ObservableCollection<ChatGroupViewModel>();
        var rows = new ChatListRows(groups);
        var removed = new ChatGroupViewModel("Today");
        groups.Add(removed);
        groups.Remove(removed);
        var changes = 0;
        rows.CollectionChanged += (_, _) => changes++;

        removed.Chats.Add(new ChatListItemViewModel(new RemoteChat { Id = Guid.NewGuid(), Title = "Late" }));

        Assert.Empty(rows);
        Assert.Equal(0, changes);
    }

    private static void Apply(List<object> list, NotifyCollectionChangedEventArgs e, ChatListRows source)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                list.InsertRange(e.NewStartingIndex, e.NewItems!.Cast<object>());
                break;
            case NotifyCollectionChangedAction.Remove:
                Assert.Equal(e.OldItems!.Cast<object>(), list.GetRange(e.OldStartingIndex, e.OldItems!.Count));
                list.RemoveRange(e.OldStartingIndex, e.OldItems.Count);
                break;
            case NotifyCollectionChangedAction.Reset:
                list.Clear();
                list.AddRange(source);
                break;
            default:
                throw new InvalidOperationException($"Unexpected {e.Action}.");
        }
    }
}
