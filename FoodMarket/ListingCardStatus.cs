using System.Net;

namespace FoodMarket;

public static class ListingCardStatus
{
    public static string Completed(string text, ListingType type) =>
        Struck(text, type switch
        {
            ListingType.Sell => "✅ فروش رفته",
            ListingType.Buy => "✅ خریداری شد",
            _ => "✅ معاوضه شد"
        });

    public static string Inactive(string text, ListingStatus status) =>
        Struck(text, status == ListingStatus.Expired ? "⌛ منقضی شد" : "⛔ آگهی لغو شد");

    public static string Suspended(string text) => Struck(text, "⛔ آگهی به‌دلیل محدودیت حساب غیرفعال است");

    private static string Struck(string text, string status) => $"<s>{WebUtility.HtmlEncode(text)}</s>\n{status}";
}
