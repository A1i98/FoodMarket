using FoodMarket;
using Xunit;

namespace FoodMarket.Tests;

public sealed class MarketplaceTests
{
    private sealed class AdjustableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static (MarketStore Store, Marketplace Market) Setup()
    {
        var options = new MarketOptions { DatabasePath = ":memory:", TimeZone = "Asia/Tehran" };
        var store = new MarketStore(options);
        return (store, new Marketplace(store, options));
    }

    private static Advertisement Listing(long owner, ListingType type, string? food = null, MealType meal = MealType.Lunch) => new()
    {
        OwnerId = owner, Type = type, FoodName = food, Meal = meal,
        Gender = CafeteriaGender.Men, Date = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTime.UtcNow.AddDays(1), TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran")))
    };

    [Fact]
    public async Task Broad_buy_matches_food_sale_and_search_is_normalized()
    {
        var (store, market) = Setup();
        using (store)
        {
            var buy = market.Publish(Listing(1, ListingType.Buy));
            var sell = market.Publish(Listing(2, ListingType.Sell, "خورش قیمه"));
            Assert.True(market.MatchScore(buy, sell) >= 40);
            var parser = new RuleBasedPersianFoodListingParser(new MarketOptions(), store.Locations);
            var query = await parser.ParseAsync("قيمه آقايان", CancellationToken.None);
            Assert.Contains(market.Search(query), a => a.Id == sell.Id);
        }
    }

    [Fact]
    public void Duplicate_requires_same_owner_and_similar_active_ad()
    {
        var (store, market) = Setup();
        using (store)
        {
            var first = market.Publish(Listing(1, ListingType.Sell, "خورش قیمه"));
            Assert.Equal(first.Id, market.Duplicate(Listing(1, ListingType.Sell, "قیمه"))?.Id);
            Assert.Null(market.Duplicate(Listing(2, ListingType.Sell, "قیمه")));
        }
    }

    [Fact]
    public void Exchange_needs_two_confirmations_and_prevents_inconsistent_sold()
    {
        var (store, market) = Setup();
        using (store)
        {
            store.Save(new MarketUser { Id = 1, Onboarded = true });
            store.Save(new MarketUser { Id = 2, Onboarded = true });
            var ad = Listing(1, ListingType.Exchange, "قیمه");
            ad.OfferedFood = "قیمه";
            ad.WantedFood = "ساندویچ";
            market.Publish(ad);
            var transaction = market.StartTransaction(ad.Id, 2);
            Assert.Equal(TransactionType.Exchange, transaction.Type);
            Assert.False(market.MarkSold(ad.Id, 1));
            Assert.Equal(TransactionStatus.Pending, market.ConfirmDelivery(transaction.Id, 1).Status);
            Assert.Equal(ListingStatus.Active, store.Ad(ad.Id)?.Status);
            Assert.Equal(TransactionStatus.Completed, market.ConfirmDelivery(transaction.Id, 2).Status);
            Assert.Equal(ListingStatus.Sold, store.Ad(ad.Id)?.Status);
            Assert.Equal(52, store.User(1)?.TrustScore);
            Assert.True(market.Rate(transaction.Id, 2, 5));
            Assert.False(market.Rate(transaction.Id, 2, 1));
        }
    }

    [Fact]
    public async Task Code_never_enters_public_ad()
    {
        var (store, market) = Setup();
        using (store)
        {
            var parser = new RuleBasedPersianFoodListingParser(new MarketOptions(), store.Locations);
            var result = await parser.ParseAsync("فروشی قیمه آقایان ۳۰۱۳۳۸", CancellationToken.None);
            var ad = market.FromDraft(result, 1, null);
            Assert.DoesNotContain("301338", ad.Description);
            Assert.Null(ad.Price);
        }
    }

    [Fact]
    public void Lunch_expires_at_configured_local_time_and_is_removed_from_search()
    {
        var options = new MarketOptions { DatabasePath = ":memory:", TimeZone = "Asia/Tehran" };
        var clock = new AdjustableClock();
        using var store = new MarketStore(options);
        var market = new Marketplace(store, options, clock);
        var ad = new Advertisement { OwnerId = 1, Type = ListingType.Sell, FoodName = "قیمه",
            Meal = MealType.Lunch, Date = new DateOnly(2026, 9, 29) };
        market.Publish(ad);
        clock.Now = new DateTimeOffset(2026, 9, 29, 12, 30, 0, TimeSpan.Zero); // 16:00 in Tehran
        market.Expire();
        Assert.Equal(ListingStatus.Expired, store.Ad(ad.Id)?.Status);
        Assert.Empty(market.Search(new ParsedListingResult { OriginalText = "" }));
    }

    [Theory]
    [InlineData(ListingType.Sell, 1L, 2L)]
    [InlineData(ListingType.Buy, 2L, 1L)]
    public void Only_food_provider_can_share_a_private_delivery_code(ListingType type, long provider, long recipient)
    {
        var options = new MarketOptions { DatabasePath = ":memory:" };
        using var store = new MarketStore(options);
        var market = new Marketplace(store, options);
        store.Save(new MarketUser { Id = 1, Onboarded = true });
        store.Save(new MarketUser { Id = 2, Onboarded = true });
        var ad = market.Publish(Listing(1, type, "قیمه"));
        var transaction = market.StartTransaction(ad.Id, 2);
        Assert.Throws<InvalidOperationException>(() => market.SetFoodCode(transaction.Id, recipient, "301338"));
        Assert.Equal(recipient, market.SetFoodCode(transaction.Id, provider, "301338"));
        Assert.Equal("301338", store.Transaction(transaction.Id)?.FoodCode);
        Assert.Null(store.Ad(ad.Id)?.Description);
        Assert.Throws<InvalidOperationException>(() => market.SetFoodCode(transaction.Id, provider, "x"));
    }

    [Fact]
    public void Exchange_keeps_each_private_code_separate_and_cancel_releases_reservation()
    {
        var options = new MarketOptions { DatabasePath = ":memory:", AdminUserId = 9 };
        using var store = new MarketStore(options);
        var market = new Marketplace(store, options);
        store.Save(new MarketUser { Id = 1, Onboarded = true });
        store.Save(new MarketUser { Id = 2, Onboarded = true });
        var ad = Listing(1, ListingType.Exchange, "قیمه");
        ad.OfferedFood = "قیمه";
        ad.WantedFood = "ساندویچ";
        market.Publish(ad);
        var transaction = market.StartTransaction(ad.Id, 2);
        Assert.Equal(2, market.SetFoodCode(transaction.Id, 1, "111222"));
        Assert.Equal(1, market.SetFoodCode(transaction.Id, 2, "333444"));
        Assert.Equal("111222", store.Transaction(transaction.Id)?.FoodCode);
        Assert.Equal("333444", store.Transaction(transaction.Id)?.CounterpartyFoodCode);
        Assert.Throws<InvalidOperationException>(() => market.CancelTransaction(transaction.Id, 3));
        Assert.Equal(TransactionStatus.Cancelled, market.CancelTransaction(transaction.Id, 9).Status);
        Assert.Null(store.Transaction(transaction.Id)?.FoodCode);
        Assert.Null(store.Transaction(transaction.Id)?.CounterpartyFoodCode);
        Assert.Throws<InvalidOperationException>(() => market.ConfirmDelivery(transaction.Id, 1));
        Assert.NotEqual(transaction.Id, market.StartTransaction(ad.Id, 2).Id);
    }
}
