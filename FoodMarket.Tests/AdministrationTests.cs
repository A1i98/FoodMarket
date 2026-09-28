using FoodMarket;
using Xunit;

namespace FoodMarket.Tests;

public sealed class AdministrationTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Tickets_are_private_two_way_rate_limited_and_closeable()
    {
        var options = new MarketOptions { DatabasePath = ":memory:", AdminUserId = 9 };
        using var store = new MarketStore(options);
        store.Save(new MarketUser { Id = 1, Onboarded = true });
        store.Save(new MarketUser { Id = 2, Onboarded = true });
        var clock = new Clock();
        var service = new SupportService(store, 9, clock);
        var ticket = service.Open(1);
        Assert.Equal(ticket.Id, service.Open(1).Id);
        Assert.Throws<InvalidOperationException>(() => service.Post(ticket.Id, 2, "غیرمجاز"));
        service.Post(ticket.Id, 1, "سلام");
        Assert.Throws<InvalidOperationException>(() => service.Post(ticket.Id, 1, "پیام تکراری"));
        service.Post(ticket.Id, 9, "سلام، در خدمتم");
        Assert.Equal(2, store.TicketMessages(ticket.Id).Count);
        Assert.False(service.Close(ticket.Id, 2));
        Assert.True(service.Close(ticket.Id, 1));
        Assert.Throws<InvalidOperationException>(() => service.Post(ticket.Id, 9, "بسته"));
        Assert.NotEqual(ticket.Id, service.Open(1).Id);
    }

    [Fact]
    public void Attachments_are_limited_to_the_private_ticket_participants()
    {
        var options = new MarketOptions { DatabasePath = ":memory:", AdminUserId = 9 };
        using var store = new MarketStore(options);
        store.Save(new MarketUser { Id = 1, Onboarded = true });
        var service = new SupportService(store, 9);
        var ticket = service.Open(1);
        Assert.Throws<InvalidOperationException>(() => service.PostAttachment(ticket.Id, 2, 2, 5, "عکس", null));
        Assert.Throws<InvalidOperationException>(() => service.PostAttachment(ticket.Id, 1, 2, 5, "عکس", null));
        var sent = service.PostAttachment(ticket.Id, 1, 1, 5, "عکس", "تصویر رسید");
        Assert.Equal(1, sent.AttachmentChatId);
        Assert.Equal(5, store.TicketMessages(ticket.Id).Single().AttachmentMessageId);
        Assert.Contains("تصویر رسید", sent.Text);
    }

    [Fact]
    public async Task Cafeterias_can_be_added_renamed_disabled_and_used_by_parser()
    {
        var options = new MarketOptions { DatabasePath = ":memory:" };
        using var store = new MarketStore(options);
        var admin = new Administration(store, options);
        Assert.True(admin.AddCafeteria("سلف جدید"));
        Assert.False(admin.AddCafeteria("x"));
        var cafe = Assert.Single(store.Cafeterias(), c => c.Name == "سلف جدید");
        Assert.True(admin.RenameCafeteria(cafe.Id, "سلف شمالی"));
        Assert.False(admin.RenameCafeteria(cafe.Id, "کاله"));
        var parser = new RuleBasedPersianFoodListingParser(options, store.Locations);
        Assert.Equal("سلف شمالی", (await parser.ParseAsync("فروشی قیمه سلف شمالی", CancellationToken.None)).CafeteriaLocation.Value);
        Assert.True(store.SetCafeteriaActive(cafe.Id, false));
        Assert.DoesNotContain("سلف شمالی", store.Locations());
    }

    [Fact]
    public async Task Settings_are_validated_and_persisted_without_overriding_admin_identity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foodmarket-settings-{Guid.NewGuid():N}.db");
        try
        {
            var options = new MarketOptions { DatabasePath = path, AdminUserId = 42 };
            using (var store = new MarketStore(options))
            {
                var admin = new Administration(store, options);
                Assert.False(admin.TryUpdateSetting("price", "0"));
                Assert.False(admin.TryUpdateSetting("lunch", "29:99"));
                Assert.False(admin.TryUpdateSetting("timezone", "No/SuchZone"));
                Assert.True(admin.TryUpdateSetting("price", "۲۰۰۰"));
                Assert.True(admin.TryUpdateSetting("lunch", "17:30"));
                Assert.True(admin.TryUpdateSetting("notification", "120"));
                var parser = new RuleBasedPersianFoodListingParser(options, store.Locations);
                var result = await parser.ParseAsync("فروشی قیمه ۸۰", CancellationToken.None);
                Assert.Equal(160000, result.Price.Value);
            }
            var restarted = new MarketOptions { DatabasePath = path, AdminUserId = 99 };
            using (var store = new MarketStore(restarted))
            {
                Assert.Equal(2000, restarted.BarePriceMultiplier);
                Assert.Equal(new TimeOnly(17, 30), restarted.LunchExpirationTime);
                Assert.Equal(120, restarted.NotificationCooldownMinutes);
                Assert.Equal(99, restarted.AdminUserId);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Moderation_does_not_break_a_pending_transaction()
    {
        var options = new MarketOptions { DatabasePath = ":memory:" };
        using var store = new MarketStore(options);
        var market = new Marketplace(store, options);
        store.Save(new MarketUser { Id = 1, Onboarded = true });
        store.Save(new MarketUser { Id = 2, Onboarded = true });
        var ad = market.Publish(new Advertisement { OwnerId = 1, Type = ListingType.Sell, FoodName = "قیمه",
            Date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow.AddDays(1), TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone))) });
        market.StartTransaction(ad.Id, 2);
        Assert.False(market.CancelListing(ad.Id));
        Assert.Equal(ListingStatus.Active, store.Ad(ad.Id)?.Status);
    }

    [Fact]
    public void Reports_keep_their_status_and_moderation_removes_active_listing()
    {
        var options = new MarketOptions { DatabasePath = ":memory:" };
        using var store = new MarketStore(options);
        var market = new Marketplace(store, options);
        store.Save(new MarketUser { Id = 1, Onboarded = true });
        var ad = market.Publish(new Advertisement { OwnerId = 1, Type = ListingType.Sell, FoodName = "قیمه",
            Date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow.AddDays(1), TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone))) });
        var report = new ListingReport { AdvertisementId = ad.Id, ReporterId = 2, Reason = "اطلاعات نادرست" };
        store.Save(report);
        Assert.Equal(report.Id, store.OpenReport(ad.Id, 2)?.Id);
        Assert.True(market.RemoveReportedListing(ad.Id));
        report.Status = ReportStatus.Resolved;
        store.Update(report);
        Assert.Null(store.OpenReport(ad.Id, 2));
        Assert.Equal(ReportStatus.Resolved, store.Report(report.Id)?.Status);
        Assert.Equal(ListingStatus.Cancelled, store.Ad(ad.Id)?.Status);
        Assert.Equal(1, store.User(1)?.ConfirmedReports);
        Assert.Equal(40, store.User(1)?.TrustScore);
        Assert.Empty(market.Search(new ParsedListingResult { OriginalText = "" }));
    }
}
