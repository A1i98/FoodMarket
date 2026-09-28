using FoodMarket;
using Xunit;

namespace FoodMarket.Tests;

public sealed class ParserTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private static readonly MarketOptions Options = new() { TimeZone = "Asia/Tehran" };
    private static RuleBasedPersianFoodListingParser Parser() => new(Options,
        () => ["طرشت ۳", "کاله", "سلف مرکزی"]);
    private static ParsedListingResult Parse(string text) => Parser().ParseAsync(text, CancellationToken.None).Result;

    [Fact]
    public void Sell_with_hashtags()
    {
        var r = Parse("#فروشی\n#آقایان\nخورش قیمه");
        Assert.Equal(ListingType.Sell, r.ListingType.Value);
        Assert.Equal("خورش قیمه", r.FoodName.Value);
        Assert.Equal(CafeteriaGender.Men, r.CafeteriaGender.Value);
    }

    [Fact]
    public void Bare_price_requires_confirmation()
    {
        var r = Parse("#فروشی\nقیمه بانوان ۷۰");
        Assert.Equal(ListingType.Sell, r.ListingType.Value);
        Assert.Equal("قیمه", r.FoodName.Value);
        Assert.Equal(CafeteriaGender.Women, r.CafeteriaGender.Value);
        Assert.Equal(70000, r.Price.Value);
        Assert.True(r.PriceConfidence < 1);
    }

    [Fact]
    public void Food_is_optional_for_buy()
    {
        var r = Parse("#خریدار\nناهار آقایان");
        Assert.Equal(ListingType.Buy, r.ListingType.Value);
        Assert.Equal(MealType.Lunch, r.Meal.Value);
        Assert.Equal(CafeteriaGender.Men, r.CafeteriaGender.Value);
        Assert.Null(r.FoodName.Value);
    }

    [Fact]
    public void Exchange_extracts_both_foods()
    {
        var r = Parse("#معاوضه\nقیمه دارم با ساندویچ دونر و پنیر عوض میکنم");
        Assert.Equal(ListingType.Exchange, r.ListingType.Value);
        Assert.Equal("قیمه", r.OfferedFood.Value);
        Assert.Equal("ساندویچ دونر و پنیر", r.WantedFood.Value);
    }

    [Fact]
    public void Urgent_short_buy()
    {
        var r = Parse("#خریدار ناهار\nخیلی فوری");
        Assert.Equal(ListingType.Buy, r.ListingType.Value);
        Assert.Equal(MealType.Lunch, r.Meal.Value);
        Assert.Equal(Urgency.High, r.Urgency.Value);
    }

    [Fact]
    public void Next_week_is_date_range()
    {
        var r = Parse("#فروشی شام هفته بعد");
        Assert.Equal(ListingType.Sell, r.ListingType.Value);
        Assert.Equal(MealType.Dinner, r.Meal.Value);
        Assert.NotNull(r.DateRange.Value);
        Assert.Equal(6, r.DateRange.Value.End.DayNumber - r.DateRange.Value.Start.DayNumber);
    }

    [Fact]
    public async Task Next_week_starts_on_upcoming_saturday_in_tehran()
    {
        // Tuesday in Tehran: the next Iranian week begins on the upcoming Saturday.
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var parser = new RuleBasedPersianFoodListingParser(Options, () => [], clock);
        var r = await parser.ParseAsync("فروشی شام هفته بعد", CancellationToken.None);
        Assert.Equal(new DateOnly(2026, 10, 3), r.DateRange.Value?.Start);
    }

    [Fact]
    public void Six_digits_are_sensitive_not_price()
    {
        var r = Parse("قیمه سلف اقایان\n301338");
        Assert.Equal("قیمه", r.FoodName.Value);
        Assert.Equal(CafeteriaGender.Men, r.CafeteriaGender.Value);
        Assert.Equal("301338", r.PossibleSensitiveCode.Value);
        Assert.Null(r.Price.Value);
        Assert.Contains(r.Numbers, n => n.Kind == NumberKind.SensitiveCodeCandidate);
    }

    [Fact]
    public void Multiple_prices_are_ambiguous()
    {
        var r = Parse("فروشی قیمه بانوان ۷۰ ۸۰");
        Assert.Null(r.Price.Value);
        Assert.Equal(0, r.PriceConfidence);
    }

    [Fact]
    public void Conflicting_cafeteria_gender_is_not_chosen()
    {
        var r = Parse("خریدار ناهار آقایان بانوان");
        Assert.Equal(CafeteriaGender.Unknown, r.CafeteriaGender.Value);
    }

    [Theory]
    [InlineData("فروشی قیمه بانوان ۸۰", 80000)]
    [InlineData("خریدار ناهار آقایان کاله ۱۳۰", 130000)]
    public void Normalized_prices_and_locations(string text, long price)
    {
        var r = Parse(text);
        Assert.Equal(price, r.Price.Value);
        if (text.Contains("کاله")) Assert.Equal("کاله", r.CafeteriaLocation.Value);
    }
}
