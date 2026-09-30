namespace FoodMarket;

public static class TelegramUserLink
{
    public static string Display(long userId, string? username) =>
        string.IsNullOrWhiteSpace(username) ? $"کاربر {userId}" : $"@{username}";

    // Telegram Bot API, InlineKeyboardButton.url / formatting options:
    // https://core.telegram.org/bots/api#inlinekeyboardbutton
    // https://core.telegram.org/bots/api#formatting-options
    // tg://user?id=... can mention users without a public username in inline buttons.
    public static string? IdUrl(long userId, string? username) =>
        userId > 0 && string.IsNullOrWhiteSpace(username) ? $"tg://user?id={userId}" : null;
}
