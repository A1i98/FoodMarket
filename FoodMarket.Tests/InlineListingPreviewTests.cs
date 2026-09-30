using FoodMarket;
using Xunit;

namespace FoodMarket.Tests;

public sealed class InlineListingPreviewTests
{
    private static readonly RuleBasedPersianFoodListingParser Parser = new(new MarketOptions(), () => []);

    [Fact]
    public async Task Preview_is_short_and_never_exposes_a_possible_food_code()
    {
        var result = await Parser.ParseAsync("فروشی قیمه بانوان ۸۰ ۳۰۱۳۳۸", CancellationToken.None);
        var preview = InlineListingPreview.Format(result);
        Assert.StartsWith("👁 پیش‌نمایش کوتاه", preview);
        Assert.Contains("قیمه", preview);
        Assert.Contains("۸۰,۰۰۰", preview);
        Assert.DoesNotContain("301338", preview);
        Assert.DoesNotContain("۳۰۱۳۳۸", preview);
        Assert.DoesNotContain("https://", preview);
        Assert.StartsWith("👁 پیش‌نمایش فروش", InlineListingPreview.Title(result));
    }

    [Fact]
    public async Task Exchange_preview_identifies_both_foods_without_publishing()
    {
        var result = await Parser.ParseAsync("معاوضه قیمه دارم با ساندویچ عوض میکنم", CancellationToken.None);
        var preview = InlineListingPreview.Format(result);
        Assert.Contains("می‌دهم: قیمه", preview);
        Assert.Contains("می‌خواهم: ساندویچ", preview);
        Assert.Contains("برای ادامه", preview);
    }

    [Fact]
    public async Task Creation_shortcuts_keep_the_full_query_and_show_known_fields()
    {
        const string text = "قیمه سلف اقایان امروز";
        var options = new MarketOptions { DatabasePath = ":memory:" };
        using var store = new MarketStore(options);
        var parser = new RuleBasedPersianFoodListingParser(options, store.Locations);
        store.Save(new InlinePrefill { Id = "sample", Food = PersianText.Normalize(text) });
        var parsed = await parser.ParseAsync(store.Prefill("sample")!, CancellationToken.None);

        Assert.Equal("➕ #فروشی قیمه", InlineListingPreview.CreationTitle(ListingType.Sell, parsed));
        Assert.Equal("🛒 #خریدار قیمه", InlineListingPreview.CreationTitle(ListingType.Buy, parsed));
        var sell = InlineListingPreview.FormatCreation(ListingType.Sell, parsed);
        Assert.Contains("سلف: آقایان", sell);
        Assert.Contains("📅 امروز", sell);
        Assert.Contains("وعده: نامشخص", sell);
        Assert.Contains("قیمت: توافقی / نامشخص", sell);
        Assert.DoesNotContain("https://", sell);
        Assert.Contains("#D12345678", InlineListingPreview.FormatCreation(ListingType.Sell, parsed,
            options, "1234567890abcdef"));
        Assert.Equal(CafeteriaGender.Men, parsed.CafeteriaGender.Value);
        Assert.Equal("امروز", parsed.Date.SourceText);
        Assert.Contains(PersianDateFormatter.Format(parsed.Date.Value!.Value), InlineListingPreview.FormatCreation(ListingType.Sell, parsed));
    }

    [Fact]
    public async Task Missing_and_low_prices_do_not_request_extra_confirmation()
    {
        Assert.Equal("۱۴۰۵/۰۷/۰۷", PersianDateFormatter.Format(new DateOnly(2026, 9, 29)));
        Assert.Equal("۱۴۰۵/۰۷/۰۷ تا ۱۴۰۵/۰۷/۰۹", PersianDateFormatter.Format(new DateRange(
            new DateOnly(2026, 9, 29), new DateOnly(2026, 10, 1))));
        var missing = await Parser.ParseAsync("فروشی قیمه آقایان", CancellationToken.None);
        Assert.Null(missing.Price.Value);
        Assert.Contains("قیمت: توافقی / نامشخص", InlineListingPreview.FormatCreation(ListingType.Sell, missing));
        Assert.DoesNotContain("نیاز به تأیید", InlineListingPreview.FormatCreation(ListingType.Sell, missing));
        var low = await Parser.ParseAsync("فروشی قیمه ۲۰ تومن", CancellationToken.None);
        Assert.Equal(20000, low.Price.Value);
        Assert.DoesNotContain("نیاز به تأیید", InlineListingPreview.FormatCreation(ListingType.Sell, low));
        var confident = await Parser.ParseAsync("قیمت ۸۰۰۰۰ تومان قیمه فروشی", CancellationToken.None);
        Assert.DoesNotContain("نیاز به تأیید", InlineListingPreview.FormatCreation(ListingType.Sell, confident));
    }

    [Fact]
    public void Draft_inline_messages_belong_only_to_the_user_who_selected_them()
    {
        using var store = new MarketStore(new MarketOptions { DatabasePath = ":memory:" });
        var message = new InlineDraftMessage { OwnerId = 1, Token = "draft1", InlineMessageId = "message1" };
        store.Save(message);
        Assert.Single(store.InlineDraftMessages(1, "draft1"));
        Assert.Empty(store.InlineDraftMessages(2, "draft1"));
        Assert.Empty(store.InlineDraftMessages(1, "draft2"));
        message.PublishedAdvertisementId = 25;
        store.Update(message);
        Assert.Single(store.UnsyncedPublishedInlineDrafts());
        message.Finalized = true;
        store.Update(message);
        Assert.Empty(store.InlineDraftMessages(1, "draft1"));
        Assert.Empty(store.UnsyncedPublishedInlineDrafts());
        Assert.True(store.HasInlineDraftMessage(1, "message1"));
    }

    [Fact]
    public void Inline_callback_carries_draft_owner_and_token_for_immediate_message_editing()
    {
        const string token = "1234567890abcdef";
        var previewData = InlineDraftAction.Preview(54431851, token);
        Assert.True(InlineDraftAction.TryParse(previewData, out var preview));
        Assert.Equal(54431851, preview!.OwnerId);
        Assert.Equal("draft_" + token, preview.StartPayload);
        var creationData = InlineDraftAction.Create(ListingType.Sell, 54431851, token);
        Assert.True(InlineDraftAction.TryParse(creationData, out var creation));
        Assert.Equal("create_sell_" + token, creation!.StartPayload);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(creationData), 1, 64);
        Assert.False(InlineDraftAction.TryParse("creatego_buy_0_" + token, out _));
        Assert.False(InlineDraftAction.TryParse("draftgo_123_invalid", out _));
    }

    [Fact]
    public void Preview_message_and_inline_draft_survive_session_storage()
    {
        using var store = new MarketStore(new MarketOptions { DatabasePath = ":memory:" });
        store.Save(new UserSession { Id = 42, PreviewMessageId = 100,
            InlineDraftToken = "1234567890abcdef", UpdatingAdvertisementId = 25 });
        var session = store.Session(42);
        Assert.Equal(100, session.PreviewMessageId);
        Assert.Equal("1234567890abcdef", session.InlineDraftToken);
        Assert.Equal(25, session.UpdatingAdvertisementId);
    }

    [Theory]
    [InlineData(ListingType.Sell, "✅ فروش رفته")]
    [InlineData(ListingType.Buy, "✅ خریداری شد")]
    public void Completion_strikes_entire_inline_card_and_keeps_status_visible(ListingType type, string status)
    {
        var result = ListingCardStatus.Completed("🍛 قیمه <قیمت>\n👤 @seller\n🆔 #F12", type);
        Assert.Equal("<s>" + System.Net.WebUtility.HtmlEncode("🍛 قیمه <قیمت>\n👤 @seller\n🆔 #F12") +
            "</s>\n" + status, result);
    }

    [Theory]
    [InlineData(ListingStatus.Cancelled, "⛔ آگهی لغو شد")]
    [InlineData(ListingStatus.Expired, "⌛ منقضی شد")]
    public void Inactive_inline_card_strikes_details_but_not_the_status(ListingStatus status, string label)
    {
        var text = "🍛 قیمه\n💵 ۱۰۰٬۰۰۰ تومان\n🆔 #F12";
        Assert.Equal("<s>" + System.Net.WebUtility.HtmlEncode(text) + "</s>\n" + label,
            ListingCardStatus.Inactive(text, status));
    }
}
