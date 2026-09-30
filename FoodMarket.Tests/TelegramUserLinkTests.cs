using FoodMarket;
using Xunit;

namespace FoodMarket.Tests;

public sealed class TelegramUserLinkTests
{
    [Fact]
    public void User_without_username_has_numeric_id_link_instead_of_fake_username()
    {
        Assert.Equal("کاربر 123456789", TelegramUserLink.Display(123456789, null));
        Assert.Equal("tg://user?id=123456789", TelegramUserLink.IdUrl(123456789, null));
        Assert.Equal("tg://user?id=123456789", TelegramUserLink.IdUrl(123456789, ""));
        Assert.Equal("@alice", TelegramUserLink.Display(123456789, "alice"));
        Assert.Null(TelegramUserLink.IdUrl(123456789, "alice"));
    }
}
