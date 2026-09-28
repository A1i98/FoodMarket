using FoodMarket;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace FoodMarket.Tests;

public sealed class GroupAccessTests
{
    [Fact]
    public void Only_primary_admin_can_install_or_remove_a_group()
    {
        var options = new MarketOptions { DatabasePath = ":memory:", AdminUserId = 54431851 };
        using var store = new MarketStore(options);
        var groups = new GroupAccess(store, options);
        Assert.Equal(GroupCommandResult.Unauthorized, groups.Handle(99, -100123, ChatType.Supergroup, "سلف", "نصب"));
        Assert.False(groups.IsInstalled(-100123));
        Assert.Equal(GroupCommandResult.NotCommand, groups.Handle(54431851, -100123, ChatType.Private, "سلف", "نصب"));
        Assert.Equal(GroupCommandResult.NotCommand, groups.Handle(54431851, -100123, ChatType.Supergroup, "سلف", "نصب غذا"));
        Assert.Equal(GroupCommandResult.Installed, groups.Handle(54431851, -100123, ChatType.Supergroup, "بازار سلف", "نصب"));
        Assert.True(groups.IsInstalled(-100123));
        Assert.Equal("بازار سلف", store.InstalledGroups().Single().Title);
        store.Save(new SharedMessage { AdvertisementId = 12, GroupChatId = -100123, GroupMessageId = 23 });
        Assert.Equal(23, store.SharesForGroup(-100123).Single().GroupMessageId);
        Assert.Empty(store.SharesForGroup(-100999));
        Assert.False(groups.IsInstalled(-100999));
        Assert.Equal(GroupCommandResult.Uninstalled, groups.Handle(54431851, -100123, ChatType.Supergroup, "بازار سلف", "حذف نصب"));
        Assert.False(groups.IsInstalled(-100123));
        Assert.Empty(store.InstalledGroups());
        Assert.Equal(GroupCommandResult.Installed, groups.Handle(54431851, -100123, ChatType.Supergroup, "بازار سلف", "/install"));
        groups.Deactivate(-100123);
        Assert.False(groups.IsInstalled(-100123));
    }

    [Fact]
    public void Unknown_inline_group_cannot_bypass_installed_group_gate()
    {
        Assert.True(GroupAccess.AllowsInline(ChatType.Private));
        Assert.True(GroupAccess.AllowsInline(ChatType.Sender));
        Assert.False(GroupAccess.AllowsInline(ChatType.Group));
        Assert.False(GroupAccess.AllowsInline(ChatType.Supergroup));
        Assert.False(GroupAccess.AllowsInline(null));
    }
}
