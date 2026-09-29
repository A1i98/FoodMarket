using System.Net;

namespace FoodMarket;

public static class ListingCardStatus
{
    public static string Completed(string text, ListingType type) =>
        $"<s>{WebUtility.HtmlEncode(text)}</s>\n" + (type switch
        {
            ListingType.Sell => "✅ فروش رفته",
            ListingType.Buy => "✅ خریداری شد",
            _ => "✅ معاوضه شد"
        });
}
