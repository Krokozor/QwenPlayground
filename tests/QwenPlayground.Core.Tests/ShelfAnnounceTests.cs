using QwenPlayground.Core.Chat;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Анонс смены полок на доске сообщений state-блока: реальные изменения (Activated/
/// Reactivated/Deactivated) пушат «метка: сообщение» с исполнителем (owner/agent), no-op
/// (AlreadyActive/NotActive) — тишина. Сериализован с AnnouncementBoardTests (общая
/// статичная мусорка): параллельный Clear/Push/Drain из разных классов гонится.
/// </summary>
[Collection("announcement-board")]
public sealed class ShelfAnnounceTests
{
    [Fact]
    public void Activated_Owner_PushesOwnerLabeledNote()
    {
        AnnouncementBoard.Clear();
        ShelfAnnounce.Push(ShelfAnnounce.Owner, ShelfResult.Activated, ToolGroup.Browser);

        var note = Assert.Single(AnnouncementBoard.Drain());

        Assert.Equal("owner", note.Label);
        Assert.Equal("activated shelf 'Browser' — its tools join the prompt next turn",
            Assert.Single(note.Messages));
    }

    [Fact]
    public void Deactivated_Agent_PushesAgentLabeledNote()
    {
        AnnouncementBoard.Clear();
        ShelfAnnounce.Push(ShelfAnnounce.Agent, ShelfResult.Deactivated, ToolGroup.CSharp);

        var note = Assert.Single(AnnouncementBoard.Drain());

        Assert.Equal("agent", note.Label);
        Assert.Equal("scheduled deactivation of shelf 'CSharp' — it leaves the prompt at the next natural change",
            Assert.Single(note.Messages));
    }

    [Fact]
    public void Reactivated_Agent_PushesReactivationNote()
    {
        AnnouncementBoard.Clear();
        ShelfAnnounce.Push(ShelfAnnounce.Agent, ShelfResult.Reactivated, ToolGroup.Mcp);

        var note = Assert.Single(AnnouncementBoard.Drain());

        Assert.Equal("agent", note.Label);
        Assert.Equal("re-activated shelf 'Mcp' — pending deactivation canceled, no prompt change",
            Assert.Single(note.Messages));
    }

    [Theory]
    [InlineData(ShelfResult.AlreadyActive)]
    [InlineData(ShelfResult.NotActive)]
    public void NoChange_DoesNotPush(ShelfResult result)
    {
        AnnouncementBoard.Clear();
        ShelfAnnounce.Push(ShelfAnnounce.Owner, result, ToolGroup.Browser);

        Assert.Empty(AnnouncementBoard.Drain());
    }
}
