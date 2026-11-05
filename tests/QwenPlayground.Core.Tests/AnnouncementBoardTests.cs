using QwenPlayground.Core.MetaInfo;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Статичная мусорка анонсов: push/drain, кап, пустые входы, адаптер-анонсер.
/// Потребители статичного состояния мусорки (сюда и ShelfAnnounceTests) сериализованы
/// коллекцией announcement-board: параллельный Clear/Push/Drain из разных классов гонится.
/// Clear в начале каждого теста — изоляция внутри класса.
/// </summary>
[Collection("announcement-board")]
public sealed class AnnouncementBoardTests
{
    [Fact]
    public void Push_Drain_RoundTrips_AndDrainsQueue()
    {
        AnnouncementBoard.Clear();
        AnnouncementBoard.Push("test", "first");
        AnnouncementBoard.Push("test", "second");

        var drained = AnnouncementBoard.Drain();

        Assert.Equal(2, drained.Count);
        Assert.Equal("test", drained[0].Label);
        Assert.Equal("first", Assert.Single(drained[0].Messages));
        Assert.Equal("second", Assert.Single(drained[1].Messages));
        Assert.Empty(AnnouncementBoard.Drain()); // очередь выгреблена
    }

    [Fact]
    public void Push_IgnoresBlankLabelOrMessage()
    {
        AnnouncementBoard.Clear();
        AnnouncementBoard.Push("", "пустая метка");
        AnnouncementBoard.Push("метка", "   ");

        Assert.Empty(AnnouncementBoard.Drain());
    }

    [Fact]
    public void Push_CapsQueue_DropsOldest()
    {
        AnnouncementBoard.Clear();
        for (var i = 0; i < 60; i++)
        {
            AnnouncementBoard.Push("test", $"msg {i}");
        }

        var drained = AnnouncementBoard.Drain();

        Assert.Equal(50, drained.Count); // кап отбросил 10 старейших
        Assert.Equal("msg 10", Assert.Single(drained[0].Messages));
        Assert.Equal("msg 59", Assert.Single(drained[^1].Messages));
        AnnouncementBoard.Clear();
    }

    [Fact]
    public void BoardAnnouncer_DrainsThroughInterface()
    {
        AnnouncementBoard.Clear();
        AnnouncementBoard.Push("via", "interface");

        var announces = new BoardAnnouncer().GetAnnounces();

        var announce = Assert.Single(announces);
        Assert.Equal("via", announce.Label);
        Assert.Equal("interface", Assert.Single(announce.Messages));
        Assert.Empty(new BoardAnnouncer().GetAnnounces()); // второй дрейн — пусто
        AnnouncementBoard.Clear();
    }
}
