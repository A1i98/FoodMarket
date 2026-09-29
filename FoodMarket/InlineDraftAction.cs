namespace FoodMarket;

public sealed record InlineDraftAction(long OwnerId, string Token, string StartPayload)
{
    public static string Preview(long ownerId, string token) => $"draftgo_{ownerId}_{token}";

    public static string Create(ListingType type, long ownerId, string token) =>
        $"creatego_{(type == ListingType.Sell ? "sell" : type == ListingType.Buy ? "buy" : "exchange")}_{ownerId}_{token}";

    public static bool TryParse(string data, out InlineDraftAction? action)
    {
        action = null;
        var parts = data.Split('_');
        string payload;
        string token;
        long owner;
        if (parts is ["draftgo", var ownerText, var tokenText] && long.TryParse(ownerText, out owner))
        {
            token = tokenText;
            payload = $"draft_{token}";
        }
        else if (parts is ["creatego", var type, var ownerText2, var tokenText2] &&
                 (type is "sell" or "buy" or "exchange") && long.TryParse(ownerText2, out owner))
        {
            token = tokenText2;
            payload = $"create_{type}_{token}";
        }
        else return false;
        if (owner <= 0 || token.Length != 16 || !token.All(Uri.IsHexDigit)) return false;
        action = new InlineDraftAction(owner, token, payload);
        return true;
    }
}
