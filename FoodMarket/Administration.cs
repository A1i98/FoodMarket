using System.Globalization;

namespace FoodMarket;

public sealed class SupportService(MarketStore store, long adminUserId, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public SupportTicket Open(long userId)
    {
        if (adminUserId <= 0 || userId == adminUserId || store.User(userId)?.Onboarded != true)
            throw new InvalidOperationException("پشتیبانی در دسترس نیست.");
        var existing = store.TicketsFor(userId).FirstOrDefault(t => t.Status == TicketStatus.Open);
        if (existing is not null) return existing;
        var ticket = new SupportTicket { UserId = userId, CreatedUtc = _clock.GetUtcNow().UtcDateTime,
            UpdatedUtc = _clock.GetUtcNow().UtcDateTime };
        store.Save(ticket);
        return ticket;
    }

    public SupportMessage Post(int ticketId, long senderId, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000)
            throw new InvalidOperationException("متن پیام باید بین ۱ تا ۲۰۰۰ نویسه باشد.");
        return SaveMessage(ticketId, senderId, text.Trim(), null, null);
    }

    public SupportMessage PostAttachment(int ticketId, long senderId, long chatId, int messageId, string kind, string? caption)
    {
        if (senderId != chatId || messageId <= 0 || caption?.Length > 1000 || kind is not ("عکس" or "فایل" or "ویدیو" or "صدا"))
            throw new InvalidOperationException("پیوست نامعتبر است.");
        return SaveMessage(ticketId, senderId, $"[{kind}]{(string.IsNullOrWhiteSpace(caption) ? "" : " " + caption)}", chatId, messageId);
    }

    private SupportMessage SaveMessage(int ticketId, long senderId, string text, long? attachmentChatId, int? attachmentMessageId)
    {
        var ticket = store.Ticket(ticketId);
        if (ticket is null || ticket.Status != TicketStatus.Open || senderId != ticket.UserId && senderId != adminUserId)
            throw new InvalidOperationException("این تیکت بسته شده یا ارسال پیام مجاز نیست.");
        var last = store.TicketMessages(ticketId).LastOrDefault(m => m.SenderId == senderId);
        if (senderId != adminUserId && last is not null && _clock.GetUtcNow().UtcDateTime - last.CreatedUtc < TimeSpan.FromSeconds(3))
            throw new InvalidOperationException("لطفاً چند ثانیه صبر کنید و دوباره بفرستید.");
        var message = new SupportMessage { TicketId = ticketId, SenderId = senderId, Text = text,
            AttachmentChatId = attachmentChatId, AttachmentMessageId = attachmentMessageId,
            CreatedUtc = _clock.GetUtcNow().UtcDateTime };
        store.Save(message);
        ticket.UpdatedUtc = message.CreatedUtc;
        store.Save(ticket);
        return message;
    }

    public bool Close(int ticketId, long actor)
    {
        var ticket = store.Ticket(ticketId);
        if (ticket is null || ticket.Status != TicketStatus.Open || actor != ticket.UserId && actor != adminUserId) return false;
        ticket.Status = TicketStatus.Closed;
        ticket.UpdatedUtc = _clock.GetUtcNow().UtcDateTime;
        store.Save(ticket);
        return true;
    }
}

public sealed class Administration(MarketStore store, MarketOptions options)
{
    public bool TryUpdateSetting(string key, string input)
    {
        var value = PersianText.Normalize(input);
        if (key is "breakfast" or "lunch" or "dinner" or "other")
        {
            if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return false;
            switch (key)
            {
                case "breakfast": options.BreakfastExpirationTime = time; break;
                case "lunch": options.LunchExpirationTime = time; break;
                case "dinner": options.DinnerExpirationTime = time; break;
                case "other": options.OtherExpirationTime = time; break;
            }
        }
        else if (key == "timezone")
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(value); }
            catch (TimeZoneNotFoundException) { return false; }
            catch (InvalidTimeZoneException) { return false; }
            options.TimeZone = value;
        }
        else
        {
            if (!int.TryParse(value, out var n)) return false;
            switch (key)
            {
                case "duplicate" when n is >= 1 and <= 1440: options.DuplicateWindowMinutes = n; break;
                case "notification" when n is >= 1 and <= 1440: options.NotificationCooldownMinutes = n; break;
                default: return false;
            }
        }
        store.SaveSettings(options);
        return true;
    }

    public bool AddCafeteria(string name)
    {
        name = PersianText.Normalize(name).Trim();
        if (name.Length is < 2 or > 60) return false;
        store.AddCafeteria(name);
        return true;
    }

    public bool RenameCafeteria(int id, string name)
    {
        name = PersianText.Normalize(name).Trim();
        return name.Length is >= 2 and <= 60 && store.RenameCafeteria(id, name);
    }
}
