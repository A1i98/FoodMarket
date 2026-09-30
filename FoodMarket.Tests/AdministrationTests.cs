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
                Assert.False(admin.TryUpdateSetting("price", "2000"));
                Assert.False(admin.TryUpdateSetting("lunch", "29:99"));
                Assert.False(admin.TryUpdateSetting("timezone", "No/SuchZone"));
                Assert.True(admin.TryUpdateSetting("lunch", "17:30"));
                Assert.True(admin.TryUpdateSetting("notification", "120"));
                var parser = new RuleBasedPersianFoodListingParser(options, store.Locations);
                var result = await parser.ParseAsync("فروشی قیمه ۸۰", CancellationToken.None);
                Assert.Equal(80000, result.Price.Value);
            }
            var restarted = new MarketOptions { DatabasePath = path, AdminUserId = 99 };
            using (var store = new MarketStore(restarted))
            {
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

    [Fact]
    public async Task Only_admin_can_suspend_a_user_without_breaking_an_existing_trade()
    {
        var options = new MarketOptions { DatabasePath = ":memory:", AdminUserId = 9 };
        using var store = new MarketStore(options);
        var market = new Marketplace(store, options);
        var admin = new Administration(store, options);
        store.Save(new MarketUser { Id = 1, Onboarded = true });
        store.Save(new MarketUser { Id = 2, Onboarded = true });
        store.Save(new MarketUser { Id = 9, Onboarded = true });
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow.AddDays(1), TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone)));
        var first = market.Publish(new Advertisement { OwnerId = 1, Type = ListingType.Sell, FoodName = "قیمه", Date = date });
        var second = market.Publish(new Advertisement { OwnerId = 1, Type = ListingType.Sell, FoodName = "ساندویچ", Date = date });
        var trade = market.StartTransaction(first.Id, 2);
        Assert.False(admin.SetUserSuspended(2, 1, true));
        Assert.False(admin.SetUserSuspended(9, 9, true));
        Assert.True(admin.SetUserSuspended(9, 1, true));
        Assert.Contains(store.AdminAudits(), e => e.ActorId == 9 && e.TargetId == 1 && e.Action == "user_suspend");
        Assert.True(store.User(1)?.Suspended);
        Assert.Empty(market.Search(new ParsedListingResult { OriginalText = "" }));
        Assert.Throws<InvalidOperationException>(() => market.Publish(new Advertisement { OwnerId = 1, Type = ListingType.Sell, FoodName = "برنج", Date = date }));
        Assert.Throws<InvalidOperationException>(() => market.StartTransaction(second.Id, 2));
        Assert.Throws<InvalidOperationException>(() => market.AskQuestion(second.Id, 2, "قیمت؟"));
        Assert.Equal(TransactionStatus.Pending, market.ConfirmDelivery(trade.Id, 1).Status);
        Assert.Equal(TransactionStatus.Completed, market.ConfirmDelivery(trade.Id, 2).Status);
        Assert.True(admin.SetUserSuspended(9, 1, false));
        Assert.Contains(store.AdminAudits(), e => e.ActorId == 9 && e.TargetId == 1 && e.Action == "user_restore");
        var parser = new RuleBasedPersianFoodListingParser(options, store.Locations);
        Assert.Contains(market.Search(await parser.ParseAsync("ساندویچ", CancellationToken.None)), a => a.Id == second.Id);
    }

    [Fact]
    public void Admin_rating_and_trust_adjustment_remain_distinct_from_trade_votes()
    {
        var options = new MarketOptions { DatabasePath = ":memory:", AdminUserId = 9 };
        using var store = new MarketStore(options);
        var market = new Marketplace(store, options);
        var admin = new Administration(store, options);
        store.Save(new MarketUser { Id = 1, Onboarded = true });
        store.Save(new MarketUser { Id = 2, Onboarded = true });
        store.Save(new MarketUser { Id = 9, Onboarded = true });

        Assert.False(admin.SetManualRating(2, 1, 5));
        Assert.False(admin.SetManualRating(9, 9, 5));
        Assert.False(admin.SetManualRating(9, 1, 6));
        Assert.True(admin.SetManualRating(9, 1, 5));
        Assert.Equal(5, store.User(1)?.EffectiveRating);
        Assert.Equal(0, store.User(1)?.Rating);
        Assert.Equal(70, store.User(1)?.TrustScore);
        Assert.False(admin.AdjustTrust(2, 1, 10, "رفتار خوب"));
        Assert.False(admin.AdjustTrust(9, 9, 10, "رفتار خوب"));
        Assert.False(admin.AdjustTrust(9, 1, 31, "رفتار خوب"));
        Assert.False(admin.AdjustTrust(9, 1, 0, "رفتار خوب"));
        Assert.False(admin.AdjustTrust(9, 1, 10, "x"));
        Assert.True(admin.AdjustTrust(9, 1, 10, "معاملهٔ موفق"));
        Assert.Equal(80, store.User(1)?.TrustScore);

        var ad = market.Publish(new Advertisement { OwnerId = 1, Type = ListingType.Sell, FoodName = "قیمه",
            Date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow.AddDays(1), TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone))) });
        var trade = market.StartTransaction(ad.Id, 2);
        market.ConfirmDelivery(trade.Id, 1);
        market.ConfirmDelivery(trade.Id, 2);
        Assert.True(market.Rate(trade.Id, 2, 1));
        Assert.Equal(1, store.User(1)?.Rating); // Real transaction vote is preserved.
        Assert.Equal(5, store.User(1)?.EffectiveRating);
        Assert.Equal(82, store.User(1)?.TrustScore); // Manual +10 survives recalculation.
        Assert.Contains(store.AdminAudits(), e => e.Action == "user_trust_adjust" && e.Detail!.Contains("معاملهٔ موفق"));
        Assert.True(admin.SetManualRating(9, 1, null));
        Assert.Equal(1, store.User(1)?.EffectiveRating);
        Assert.Equal(42, store.User(1)?.TrustScore);
        Assert.False(admin.SetManualRating(9, 1, null));
    }

    [Fact]
    public void Ban_reason_and_manual_points_survive_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foodmarket-moderation-{Guid.NewGuid():N}.db");
        try
        {
            var options = new MarketOptions { DatabasePath = path, AdminUserId = 9 };
            using (var store = new MarketStore(options))
            {
                store.Save(new MarketUser { Id = 1, Onboarded = true });
                var admin = new Administration(store, options);
                Assert.True(admin.SetUserSuspended(9, 1, true, "نقض قوانین گروه"));
                Assert.False(admin.SetUserSuspended(9, 1, true, "تکراری"));
                Assert.True(admin.AdjustTrust(9, 1, -15, "نقض قوانین گروه"));
                Assert.True(admin.SetManualRating(9, 1, 4));
            }
            using (var store = new MarketStore(options))
            {
                Assert.True(store.User(1)?.Suspended);
                Assert.Equal("نقض قوانین گروه", store.User(1)?.SuspensionReason);
                Assert.Equal(45, store.User(1)?.TrustScore);
                Assert.Equal(-15, store.User(1)?.ManualTrustAdjustment);
                Assert.Equal(4, store.User(1)?.ManualRating);
                Assert.Equal(4, store.User(1)?.EffectiveRating);
                Assert.Contains(store.AdminAudits(), e => e.Action == "user_suspend" && e.Detail == "نقض قوانین گروه");
                Assert.True(new Administration(store, options).SetUserSuspended(9, 1, false));
                Assert.Null(store.User(1)?.SuspensionReason);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
