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
        Assert.Equal(GroupCommandResult.Installed, groups.Handle(54431851, -100123, ChatType.Supergroup, "بازار سلف", "/install@MyFoodBot", "MyFoodBot"));
        Assert.Equal(GroupCommandResult.NotCommand, groups.Handle(54431851, -100223, ChatType.Supergroup, "سلف", "/install@OtherBot", "MyFoodBot"));
        Assert.Equal(GroupCommandResult.Installed, groups.Handle(54431851, -100123, ChatType.Supergroup, "بازار سلف", "نصب"));
        Assert.True(groups.IsInstalled(-100123));
        Assert.Contains(store.AdminAudits(), e => e.Action == "group_install" && e.TargetId == -100123 && e.ActorId == 54431851);
        Assert.Equal("قیمه", groups.AuthorizedSearchQuery(-100123, "/food قیمه", "MyFoodBot"));
        Assert.Null(groups.AuthorizedSearchQuery(-100999, "/food قیمه", "MyFoodBot"));
        Assert.Equal("بازار سلف", store.InstalledGroups().Single().Title);
        store.Save(new SharedMessage { AdvertisementId = 12, GroupChatId = -100123, GroupMessageId = 23 });
        store.Save(new SharedMessage { AdvertisementId = 12, InlineMessageId = "inline-123", CompactInlineCard = true });
        Assert.Equal(23, store.SharesForGroup(-100123).Single().GroupMessageId);
        Assert.True(store.Shares(12).Single(s => s.InlineMessageId == "inline-123").CompactInlineCard);
        Assert.Empty(store.SharesForGroup(-100999));
        Assert.False(groups.IsInstalled(-100999));
        Assert.Equal(GroupCommandResult.Uninstalled, groups.Handle(54431851, -100123, ChatType.Supergroup, "بازار سلف", "حذف نصب"));
        Assert.False(groups.IsInstalled(-100123));
        Assert.Contains(store.AdminAudits(), e => e.Action == "group_uninstall" && e.TargetId == -100123);
        Assert.Empty(store.InstalledGroups());
        Assert.Equal(GroupCommandResult.Installed, groups.Handle(54431851, -100123, ChatType.Supergroup, "بازار سلف", "/install"));
        groups.Deactivate(-100123);
        Assert.False(groups.IsInstalled(-100123));
        Assert.Null(groups.AuthorizedSearchQuery(-100123, "/food قیمه", "MyFoodBot"));
    }

    [Fact]
    public void Inline_group_results_are_disabled_without_a_verifiable_chat_id()
    {
        var options = new MarketOptions { DatabasePath = ":memory:", AdminUserId = 7 };
        using var store = new MarketStore(options);
        var groups = new GroupAccess(store, options);
        groups.Handle(7, -100123, ChatType.Supergroup, "سلف", "نصب");
        Assert.Equal(InlineScope.PrivateListings, GroupAccess.GetInlineScope(ChatType.Private));
        Assert.Equal(InlineScope.PrivateListings, GroupAccess.GetInlineScope(ChatType.Sender));
        Assert.Equal(InlineScope.None, GroupAccess.GetInlineScope(ChatType.Group));
        Assert.Equal(InlineScope.None, GroupAccess.GetInlineScope(ChatType.Supergroup));
        Assert.Equal(InlineScope.None, GroupAccess.GetInlineScope(null));
        Assert.Equal("قیمه", groups.AuthorizedSearchQuery(-100123, "/food قیمه", "MyFoodBot"));
        Assert.Equal("قیمه", groups.AuthorizedSearchQuery(-100123, "/food@MyFoodBot قیمه", "MyFoodBot"));
        Assert.Equal("", groups.AuthorizedSearchQuery(-100123, "/غذا", "MyFoodBot"));
        Assert.Null(groups.AuthorizedSearchQuery(-100123, "/food@OtherBot قیمه", "MyFoodBot"));
        Assert.Null(groups.AuthorizedSearchQuery(-100123, "قیمه", "MyFoodBot"));
    }
}
