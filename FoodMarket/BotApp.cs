using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;

namespace FoodMarket;

public sealed class BotApp(TelegramBotClient bot, MarketStore store, Marketplace market,
    IFoodListingParser parser, MarketOptions options)
{
    private readonly SemaphoreSlim _updates = new(1, 1);
    private readonly SupportService _support = new(store, options.AdminUserId);
    private readonly Administration _admin = new(store, options);
    private readonly GroupAccess _groups = new(store, options);
    private string _username = "";
    private void Log(Exception error)
    {
        var detail = error.ToString();
        var token = Environment.GetEnvironmentVariable("BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) token = options.BotToken;
        if (!string.IsNullOrEmpty(token)) detail = detail.Replace(token, "[redacted]", StringComparison.Ordinal);
        Console.Error.WriteLine(detail);
    }
    private static InlineKeyboardButton C(string title, string data) => InlineKeyboardButton.WithCallbackData(title, data);
    private InlineKeyboardButton Link(string title, string payload) => InlineKeyboardButton.WithUrl(title, $"https://t.me/{_username}?start={payload}");

    private static ReplyKeyboardMarkup PrivateKeyboard(bool admin)
    {
        var rows = new List<KeyboardButton[]>
        {
            new[] { new KeyboardButton("💰 فروش غذا"), new KeyboardButton("🛒 خرید غذا") },
            new[] { new KeyboardButton("🔄 معاوضه"), new KeyboardButton("🔍 جستجوی غذا") },
            new[] { new KeyboardButton("📋 آگهی‌های من"), new KeyboardButton("🤝 معاملات من") },
            new[] { new KeyboardButton("💬 پرسش‌های آگهی‌ها") },
            new[] { new KeyboardButton("⭐ پروفایل و اعتبار"), new KeyboardButton("⚙️ تنظیمات") },
            new[] { new KeyboardButton("💬 پشتیبانی"), new KeyboardButton("ℹ️ راهنما"), new KeyboardButton("🏠 منوی اصلی") }
        };
        if (admin) rows.Add([new("🛠 پنل مدیریت")]);
        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true, IsPersistent = true };
    }

    private Task ShowPrivateKeyboard(long id, CancellationToken ct) => bot.SendMessage(id,
        "⌨️ منوی ثابت زیر آماده است. برای ثبت سریع آگهی می‌توانی مستقیم هم متن بنویسی.",
        replyMarkup: PrivateKeyboard(IsAdmin(id)), cancellationToken: ct);

    public async Task RunAsync(CancellationToken ct)
    {
        var self = await bot.GetMe(ct);
        _username = self.Username ?? throw new InvalidOperationException("نام کاربری ربات لازم است.");
        Console.WriteLine($"Bot @{_username} listening. Inline: {self.SupportsInlineQueries}; group privacy disabled: {self.CanReadAllGroupMessages}");
        if (self.SupportsInlineQueries != true) Console.Error.WriteLine("Enable Inline Mode in BotFather with /setinline.");
        if (self.CanReadAllGroupMessages != true) Console.Error.WriteLine("Plain نصب in groups needs BotFather /setprivacy Disabled (or use /install).");
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        await CleanUpOldListings(ct);
        bot.StartReceiving(HandleUpdate, HandleError, receiverOptions: new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.InlineQuery,
                UpdateType.ChosenInlineResult, UpdateType.MyChatMember, UpdateType.ChatMember]
        }, cancellationToken: ct);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await CleanUpOldListings(ct);
            foreach (var pending in store.UnsyncedPublishedInlineDrafts().DistinctBy(m => (m.OwnerId, m.Token)).Take(20))
                if (store.InlineDraftMessages(pending.OwnerId, pending.Token).FirstOrDefault(m => m.PublishedAdvertisementId.HasValue) is { } draft &&
                    store.Ad(draft.PublishedAdvertisementId!.Value) is { } published)
                    await SyncInlineDraft(draft.OwnerId, draft.Token, published, ct);
            foreach (var question in store.PendingQuestionNotifications())
                await NotifyQuestion(question, ct);
        }
    }

    private Task HandleError(ITelegramBotClient _, Exception error, CancellationToken __)
    {
        Log(error);
        return Task.CompletedTask;
    }

    private async Task HandleUpdate(ITelegramBotClient _, Update update, CancellationToken ct)
    {
        if (update.InlineQuery is { } inline)
        {
            try { await Inline(inline, ct); }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (
                ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase)) { }
            catch (Exception ex) { Log(ex); }
            return;
        }
        await _updates.WaitAsync(ct);
        try
        {
            if (update.ChosenInlineResult is { InlineMessageId: { } inlineId } chosen &&
                     (chosen.ResultId.StartsWith("ad_", StringComparison.Ordinal) || chosen.ResultId.StartsWith("view_", StringComparison.Ordinal)) &&
                     int.TryParse(chosen.ResultId[(chosen.ResultId.IndexOf('_') + 1)..], out var adId))
                store.Save(new SharedMessage { AdvertisementId = adId, InlineMessageId = inlineId,
                    CompactInlineCard = chosen.ResultId.StartsWith("view_", StringComparison.Ordinal) });
            else if (update.ChosenInlineResult is { InlineMessageId: { } draftInlineId } selected &&
                     InlineDraftTokenFromResultId(selected.ResultId) is { } token &&
                     store.Prefill(token) is not null)
            {
                if (!store.HasInlineDraftMessage(selected.From.Id, draftInlineId))
                    store.Save(new InlineDraftMessage { OwnerId = selected.From.Id, Token = token,
                        InlineMessageId = draftInlineId });
            }
            else if (update.CallbackQuery is { } callback) await Callback(callback, ct);
            else if (update.Message is { } message) await Message(message, ct);
            else if (update.MyChatMember is { } membership && membership.Chat.Type is ChatType.Group or ChatType.Supergroup &&
                     membership.NewChatMember.Status is ChatMemberStatus.Left or ChatMemberStatus.Kicked)
                _groups.Deactivate(membership.Chat.Id);
            else if (update.ChatMember is { } changed && changed.NewChatMember.User.Id == options.AdminUserId &&
                     changed.NewChatMember.Status is ChatMemberStatus.Left or ChatMemberStatus.Kicked && _groups.IsInstalled(changed.Chat.Id))
            {
                _groups.Deactivate(changed.Chat.Id);
                await DisableGroupShares(changed.Chat.Id, ct);
            }
        }
        catch (Exception ex) { Log(ex); }
        finally { _updates.Release(); }
    }

    private static string MealName(MealType meal) => meal switch { MealType.Breakfast => "صبحانه", MealType.Lunch => "ناهار", MealType.Dinner => "شام", MealType.Other => "سایر", _ => "نامشخص" };
    private static string GenderName(CafeteriaGender gender) => gender switch { CafeteriaGender.Men => "👨 آقایان", CafeteriaGender.Women => "👩 بانوان", CafeteriaGender.Mixed => "مختلط", _ => "نامشخص" };
    private static string DateName(Advertisement ad) => PersianDateFormatter.Format(ad.Date, ad.DateRange);
    private static string PriceName(long? price) => price.HasValue ? PersianText.Digits(price.Value) + " تومان" : "توافقی / نامشخص";
    private static string ListingId(Advertisement ad) => $"#{(ad.Type == ListingType.Buy ? "B" : ad.Type == ListingType.Sell ? "F" : "E")}{ad.Id}";
    private string ListingId(int adId) => store.Ad(adId) is { } ad ? ListingId(ad) : $"#{adId}";
    private static string QuestionId(ListingQuestion question) => $"#Q{question.Id}";
    private static string Header(Advertisement ad) => ad.Type switch
    {
        ListingType.Sell => $"🍛 فروشی | {ad.FoodName ?? MealName(ad.Meal)}",
        ListingType.Buy => $"🛒 خریدار | {ad.FoodName ?? MealName(ad.Meal)}",
        _ => "🔄 معاوضه"
    };

    private string Card(Advertisement ad)
    {
        var user = store.User(ad.OwnerId);
        var s = new StringBuilder();
        if (ad.Status == ListingStatus.Cancelled) s.AppendLine("🚫 آگهی غیرفعال شد");
        else if (ad.Status == ListingStatus.Expired) s.AppendLine("⌛ آگهی منقضی شد");
        else if (ad.Status == ListingStatus.Sold) s.AppendLine(ad.Type switch
        {
            ListingType.Sell => "✅ فروخته شد", ListingType.Buy => "✅ خریداری شد", _ => "✅ معاوضه انجام شد"
        });
        else if (user?.Suspended == true) s.AppendLine("⛔ آگهی به‌دلیل محدودیت حساب غیرفعال است");
        else s.AppendLine(Header(ad));
        if (ad.Type == ListingType.Exchange)
            s.AppendLine($"🍛 می‌دهم: {ad.OfferedFood}").AppendLine($"🔁 می‌خواهم: {ad.WantedFood}");
        else if (ad.FoodName is null) s.AppendLine("🍛 نوع غذا: فرقی ندارد");
        s.AppendLine($"سلف: {GenderName(ad.Gender)}{(ad.Location is null ? "" : " | " + ad.Location)}");
        if (ad.Meal != MealType.Unknown) s.AppendLine($"🍽 {MealName(ad.Meal)}");
        if (ad.Price.HasValue) s.AppendLine($"💵 {(ad.Type == ListingType.Exchange ? "تفاوت قیمت: " : "")}{PriceName(ad.Price)}");
        else if (ad.Type != ListingType.Exchange) s.AppendLine("💵 قیمت: از آگهی‌دهنده بپرسید");
        s.AppendLine($"📅 {DateName(ad)}");
        s.AppendLine($"👤 {(ad.OwnerUsername is null ? "کاربر" : "@" + ad.OwnerUsername)}");
        if (user is not null) s.AppendLine($"⭐ {(user.Rating == 0 ? "بدون امتیاز" : user.Rating.ToString("0.0"))} | 🛡 {user.TrustScore}/100 | ✅ {user.SuccessfulTransactions} معامله");
        s.Append($"🆔 {ListingId(ad)}");
        return s.ToString();
    }

    private string InlineCard(Advertisement ad)
    {
        var card = Card(ad);
        if (ad.Status == ListingStatus.Active && store.User(ad.OwnerId)?.Suspended != true) return WebUtility.HtmlEncode(card);
        var body = Header(ad) + card[card.IndexOf('\n')..];
        return ad.Status == ListingStatus.Sold ? ListingCardStatus.Completed(body, ad.Type)
            : ad.Status == ListingStatus.Active ? ListingCardStatus.Suspended(body) : ListingCardStatus.Inactive(body, ad.Status);
    }

    private string CompactInlineCard(Advertisement ad)
    {
        var user = store.User(ad.OwnerId);
        var lines = new List<string>
        {
            Header(ad),
            $"{GenderName(ad.Gender)}{(ad.Location is null ? "" : " · " + ad.Location)} · {MealName(ad.Meal)}"
        };
        if (ad.Type == ListingType.Exchange)
        {
            lines.Add($"🍛 می‌دهم: {ad.OfferedFood}");
            lines.Add($"🔁 می‌خواهم: {ad.WantedFood}");
        }
        else if (ad.Type == ListingType.Buy && ad.FoodName is null) lines.Add("🍛 نوع غذا: فرقی ندارد");
        if (ad.Price.HasValue) lines.Add($"💵 {PriceName(ad.Price)}");
        else if (ad.Type != ListingType.Exchange) lines.Add("💵 قیمت: از آگهی‌دهنده بپرسید");
        if (ad.Date.HasValue || ad.DateRange is not null) lines.Add($"📅 {DateName(ad)}");
        if (user is not null) lines.Add($"👤 {(ad.OwnerUsername is null ? "کاربر" : "@" + ad.OwnerUsername)} · ⭐ {(user.Rating == 0 ? "بدون امتیاز" : user.Rating.ToString("0.0"))} · 🛡 {user.TrustScore}/100");
        lines.Add($"🆔 {ListingId(ad)}");
        if (ad.Status == ListingStatus.Sold) return ListingCardStatus.Completed(string.Join("\n", lines), ad.Type);
        if (user?.Suspended == true && ad.Status == ListingStatus.Active) return ListingCardStatus.Suspended(string.Join("\n", lines));
        if (ad.Status != ListingStatus.Active) return ListingCardStatus.Inactive(string.Join("\n", lines), ad.Status);
        return string.Join("\n", lines.Select(WebUtility.HtmlEncode));
    }

    private InlineKeyboardMarkup CardButtons(Advertisement ad, long? viewerId = null, bool compact = false)
    {
        if (ad.Status != ListingStatus.Active || store.User(ad.OwnerId)?.Suspended == true) return ClosedCardButtons(ad);
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { Link("💬 پرسش از آگهی‌دهنده", $"ask_{ad.Id}"),
                Link(ad.Type == ListingType.Exchange ? "🔄 پیشنهاد معاوضه" : ad.Type == ListingType.Buy ? "💰 پیشنهاد فروش" : "🛒 خرید", $"deal_{ad.Id}") },
            new[] { Link("⭐ اعتبار کاربر", $"profile_{ad.OwnerId}") }
        };
        if (viewerId is null || viewerId == ad.OwnerId)
        {
            if (ad.Type == ListingType.Sell) rows.Add([C("✅ فروخته شد", $"{(compact ? "soldc" : "sold")}_{ad.Id}"), Link("🚨 گزارش", $"report_{ad.Id}")]);
            else if (ad.Type == ListingType.Buy) rows.Add([C("✅ خریداری شد", $"{(compact ? "fulfilledc" : "fulfilled")}_{ad.Id}"), Link("🚨 گزارش", $"report_{ad.Id}")]);
            else rows.Add([Link("🚨 گزارش", $"report_{ad.Id}")]);
            rows.Add([Link("✏️ ویرایش آگهی", $"editad_{ad.Id}")]);
        }
        else rows.Add([Link("🚨 گزارش", $"report_{ad.Id}")]);
        return new InlineKeyboardMarkup(rows);
    }

    private InlineKeyboardMarkup ClosedCardButtons(Advertisement ad)
    {
        var completed = store.TransactionsForAd(ad.Id).FirstOrDefault(t => t.Status == TransactionStatus.Completed);
        var rows = new List<InlineKeyboardButton[]> { new[] { Link("⭐ اعتبار کاربر", $"profile_{ad.OwnerId}"), Link("🚨 گزارش", $"report_{ad.Id}") } };
        if (completed is not null) rows.Add([Link("⭐ امتیاز به طرف معامله", $"review_{completed.Id}")]);
        return new InlineKeyboardMarkup(rows);
    }

    private static InlineKeyboardMarkup OwnerButtons(Advertisement ad) => new(new[]
    {
        new[] { C("✏️ ویرایش آگهی", $"editad_{ad.Id}"), C("📢 انتشار در گروه", $"groups_{ad.Id}") },
        ad.Type == ListingType.Sell
            ? new[] { C("✅ فروخته شد", $"sold_{ad.Id}"), C(ad.MatchNotifications ? "🔕 قطع اعلان" : "🔔 اعلان مورد مناسب", $"notify_{ad.Id}") }
            : ad.Type == ListingType.Buy
            ? new[] { C("✅ خریداری شد", $"fulfilled_{ad.Id}"), C(ad.MatchNotifications ? "🔕 قطع اعلان" : "🔔 اعلان مورد مناسب", $"notify_{ad.Id}") }
            : new[] { C(ad.MatchNotifications ? "🔕 قطع اعلان" : "🔔 اعلان مورد مناسب", $"notify_{ad.Id}") },
        new[] { C("⛔ لغو آگهی", $"cancelad_{ad.Id}") }
    });

    private static InlineKeyboardMarkup TransactionButtons(MarketTransaction t, long viewer)
    {
        var seller = t.Type == TransactionType.Sale ? t.CounterpartyId : t.OwnerId;
        var label = t.Type == TransactionType.Exchange || viewer == seller ? "✅ غذایم را تحویل دادم" : "✅ غذا را دریافت کردم";
        return new InlineKeyboardMarkup(new[]
        {
            new[] { C(label, $"confirm_{t.Id}") },
            new[] { C("❌ لغو معامله", $"txn_cancel_{t.Id}") }
        });
    }

    private static InlineKeyboardMarkup PreviewButtons(Advertisement ad, bool updating = false) => new(new[]
    {
        new[] { C(updating ? "✅ ذخیرهٔ تغییرات" : "✅ درسته، منتشر کن", "publish") },
        new[] { C("✏️ نوع", "edit_type"), C("✏️ غذا", "edit_food"), C("✏️ قیمت", "edit_price") },
        new[] { C("✏️ سلف", "edit_gender"), C("✏️ محل", "edit_location"), C("✏️ وعده", "edit_meal") },
        new[] { C("✏️ تاریخ", "edit_date"), C("❌ لغو", "cancel") }
    });

    private string Preview(Advertisement ad)
    {
        var valid = Marketplace.IsValid(ad);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(market.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone)));
        var dateLabel = ad.Date == today ? $"امروز · {DateName(ad)}" : DateName(ad);
        return $"📝 اطلاعات آگهی را این‌طور متوجه شدم:\n\n" +
            $"{Header(ad)}\n" + (ad.Type == ListingType.Exchange ? $"🍛 می‌دهم: {ad.OfferedFood ?? "؟"}\n🔁 می‌خواهم: {ad.WantedFood ?? "؟"}\n" : "") +
            $"🍽 وعده: {MealName(ad.Meal)}\nسلف: {GenderName(ad.Gender)}\n📍 محل: {ad.Location ?? "نامشخص"}\n" +
            $"💵 {(ad.Type == ListingType.Exchange ? "تفاوت قیمت" : "قیمت")}: {PriceName(ad.Type == ListingType.Exchange ? ad.OptionalPriceDifference : ad.Price)}\n📅 تاریخ: {dateLabel}\n" +
            (valid ? "لطفاً جزئیات را تأیید کنید." : "برای ثبت، نوع آگهی و غذا یا وعده را مشخص کنید (در معاوضه هر دو غذا لازم‌اند). ");
    }

    private async Task ShowPreview(UserSession session, Advertisement ad, CancellationToken ct)
    {
        var text = (session.UpdatingAdvertisementId is { } updatingId ? $"✏️ ویرایش آگهی {ListingId(updatingId)}\n\n" : "") + Preview(ad);
        var keyboard = PreviewButtons(ad, session.UpdatingAdvertisementId.HasValue);
        if (session.PreviewMessageId is { } messageId)
        {
            try
            {
                await bot.EditMessageText(session.Id, messageId, text, replyMarkup: keyboard, cancellationToken: ct);
                return;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase)) { return; }
            catch (Telegram.Bot.Exceptions.ApiRequestException) { /* The old preview may have been deleted. */ }
        }
        var sent = await bot.SendMessage(session.Id, text, replyMarkup: keyboard, cancellationToken: ct);
        session.PreviewMessageId = sent.MessageId;
        store.Save(session);
    }

    private static Advertisement? Draft(UserSession session) => session.DraftJson is null ? null : JsonSerializer.Deserialize<Advertisement>(session.DraftJson);
    private void SaveDraft(UserSession session, Advertisement ad)
    {
        session.DraftJson = JsonSerializer.Serialize(ad);
        store.Save(session);
    }

    private async Task Message(Message msg, CancellationToken ct)
    {
        if (msg.Chat.Type != ChatType.Private)
        {
            if (string.IsNullOrWhiteSpace(msg.Text)) return;
            var command = _groups.Handle(msg.From?.Id ?? 0, msg.Chat.Id, msg.Chat.Type, msg.Chat.Title, msg.Text, _username);
            if (command is GroupCommandResult.Installed or GroupCommandResult.Uninstalled)
            {
                if (command == GroupCommandResult.Uninstalled) await DisableGroupShares(msg.Chat.Id, ct);
                else foreach (var adId in store.SharesForGroup(msg.Chat.Id).Select(s => s.AdvertisementId).Distinct())
                    await EditShares(adId, ct);
                await bot.SendMessage(msg.Chat.Id, command == GroupCommandResult.Installed
                    ? "✅ بازار غذا در این گروه نصب شد. کاربران می‌توانند اینجا /food قیمه را جست‌وجو کنند یا در خصوصی آگهی ثبت و همین گروه را برای انتشار انتخاب کنند. برای بررسی عضویت، ربات را ادمین گروه کنید."
                    : "⛔ بازار غذا از این گروه حذف شد و انتشار آگهی متوقف شد.", cancellationToken: ct);
                return;
            }
            if (command == GroupCommandResult.Unauthorized)
            {
                await bot.SendMessage(msg.Chat.Id, "⚠️ نصب و حذف نصب فقط با پیام ادمین اصلی انجام می‌شود. اگر ادمین هستی، ارسال ناشناس را خاموش کن. برای گروه‌های دارای Privacy Mode از /install استفاده کن.", cancellationToken: ct);
                return;
            }
            if (!_groups.IsInstalled(msg.Chat.Id)) return;
            if (_groups.AuthorizedSearchQuery(msg.Chat.Id, msg.Text, _username) is { } query)
            {
                if (string.IsNullOrWhiteSpace(query))
                { await bot.SendMessage(msg.Chat.Id, "برای جست‌وجو بنویس: /food قیمه (فقط در گروه‌های نصب‌شده).", cancellationToken: ct); return; }
                var search = await parser.ParseAsync(query, ct);
                var matches = market.Search(search).Take(3).ToList();
                if (matches.Count == 0)
                { await bot.SendMessage(msg.Chat.Id, "آگهی فعالی برای این جست‌وجو پیدا نشد.", cancellationToken: ct); return; }
                foreach (var found in matches)
                {
                    var sent = await bot.SendMessage(msg.Chat.Id, Card(found), replyMarkup: CardButtons(found), cancellationToken: ct);
                    store.Save(new SharedMessage { AdvertisementId = found.Id, GroupChatId = msg.Chat.Id,
                        GroupMessageId = sent.MessageId, GroupSearchResult = true });
                }
                return;
            }
            var groupParsed = await parser.ParseAsync(msg.Text, ct);
            if (groupParsed.PossibleSensitiveCode.Value is not null)
                await bot.SendMessage(msg.Chat.Id, "⚠️ عدد پیام ممکن است کد تحویل یا رزرو باشد. برای امنیت آن را در آگهی عمومی منتشر نکنید؛ با ربات در خصوصی ادامه دهید.", replyParameters: new ReplyParameters { MessageId = msg.MessageId }, cancellationToken: ct);
            return;
        }
        if (msg.From is null) return;
        var id = msg.From.Id;
        var session = store.Session(id);
        if (string.IsNullOrWhiteSpace(msg.Text))
        {
            if (session.EditingField is { } mode &&
                (mode.StartsWith("support:", StringComparison.Ordinal) || IsAdmin(id) && mode.StartsWith("adminreply:", StringComparison.Ordinal)))
                await SupportAttachment(msg, mode, ct);
            return;
        }
        var text = msg.Text.Trim();
        if (text.StartsWith("/start", StringComparison.Ordinal))
        {
            if (store.User(id)?.Suspended == true)
            { await bot.SendMessage(id, "⛔ دسترسی حساب شما محدود شده است. برای پیگیری /support و برای معاملات قبلی /mytrades را بزنید.", cancellationToken: ct); return; }
            var parameter = text.Split(' ', 2).ElementAtOrDefault(1) ?? "";
            if (store.User(id)?.Onboarded != true)
            {
                session.EditingField = "onboard:" + parameter;
                store.Save(session);
                await bot.SendMessage(id, "سلام 👋\nاین ربات برای خرید، فروش و معاوضه غذای دانشگاه ساخته شده.\nبا سیستم معامله و امتیازدهی می‌توانی با امنیت بیشتری خرید و فروش کنی.",
                    replyMarkup: new InlineKeyboardMarkup(new[] { new[] { C("✅ شروع", "onboard") } }), cancellationToken: ct);
                return;
            }
            session.EditingField = null; store.Save(session);
            await ShowPrivateKeyboard(id, ct);
            await StartParameter(id, parameter, ct);
            return;
        }
        if (store.User(id)?.Onboarded != true)
        {
            await bot.SendMessage(id, "برای ادامه ابتدا /start را بزنید.", cancellationToken: ct);
            return;
        }
        if (store.User(id) is { } profile && profile.Username != msg.From.Username)
        {
            profile.Username = msg.From.Username;
            store.Save(profile);
        }
        if (store.User(id)?.Suspended == true && text is not ("/support" or "/help" or "/cancel" or "/mytrades") &&
            !text.StartsWith("/code ", StringComparison.Ordinal) &&
            session.EditingField?.StartsWith("support:", StringComparison.Ordinal) != true)
        { await bot.SendMessage(id, "⛔ دسترسی حساب شما محدود شده است. برای پیگیری /support و برای معاملات قبلی /mytrades را بزنید.", cancellationToken: ct); return; }
        if (text == "/cancel")
        {
            session.EditingField = null;
            session.DraftJson = null;
            session.SensitiveNumber = null;
            session.DuplicateId = null;
            session.InlineDraftToken = null;
            session.UpdatingAdvertisementId = null;
            session.PreviewMessageId = null;
            store.Save(session);
            await bot.SendMessage(id, "از حالت گفتگو خارج شدی و پیش‌نویس کنار گذاشته شد. آگهی‌های ثبت‌شده تغییری نکردند.", cancellationToken: ct);
            return;
        }
        if (text == "/help") { await ShowHelp(id, ct); return; }
        if (text == "/support") { await BeginSupport(id, ct); return; }
        if (text == "/mytrades") { await UserTransactions(id, 0, ct); return; }
        if (text == "/admin" && IsAdmin(id)) { await AdminMenu(id, ct); return; }
        if (text == "/tickets" && IsAdmin(id)) { await TicketList(id, 0, ct); return; }
        if (text == "/transactions" && IsAdmin(id)) { await PendingTransactions(id, ct); return; }
        if (text == "/search")
        {
            session.EditingField = "search"; store.Save(session);
            await bot.SendMessage(id, "عبارت جست‌وجو را بنویس (مثلاً قیمه بانوان).", cancellationToken: ct);
            return;
        }
        if (text.StartsWith("/cafeteria", StringComparison.Ordinal) && id == options.AdminUserId)
        {
            var parts = text.Split(' ', 3);
            if (parts.Length == 3 && parts[1] == "add" && _admin.AddCafeteria(parts[2]))
                Audit(id, "cafeteria_update", 0, $"افزودن {parts[2][..Math.Min(parts[2].Length, 50)]}");
            if (parts.Length == 3 && parts[1] == "remove" && store.Cafeterias().Any(c => c.Active &&
                PersianText.Normalize(c.Name) == PersianText.Normalize(parts[2])))
            {
                store.RemoveCafeteria(parts[2]);
                Audit(id, "cafeteria_update", 0, $"حذف {parts[2][..Math.Min(parts[2].Length, 50)]}");
            }
            await CafeteriaMenu(id, 0, ct);
            return;
        }
        if (text == "/reports" && id == options.AdminUserId)
        {
            await ReportList(id, 0, ct);
            return;
        }
        if (msg.ReplyToMessage is { } replied &&
            store.QuestionByOwnerMessage(id, replied.MessageId) is { } repliedQuestion)
        {
            session.EditingField = $"answer:{repliedQuestion.Id}";
            store.Save(session);
        }
        if (await HandlePrivateMenu(id, text, msg.From.Username, session, ct)) return;
        if (text.StartsWith("/code ", StringComparison.Ordinal))
        {
            var parts = text.Split(' ', 3);
            if (parts.Length == 3 && int.TryParse(parts[1], out var transactionId))
            {
                try
                {
                    var recipient = market.SetFoodCode(transactionId, id, parts[2]);
                    await bot.SendMessage(id, "🔐 کد فقط در معامله ذخیره شد و برای طرف مقابل در خصوصی ارسال می‌شود.", cancellationToken: ct);
                    try { await bot.SendMessage(recipient, $"🔐 کد تحویل معامله #{transactionId}: {parts[2].Trim()}", cancellationToken: ct); }
                    catch (Exception ex) { Log(ex); await bot.SendMessage(id, "ارسال کد به طرف مقابل ممکن نشد؛ لطفاً از پشتیبانی کمک بگیر.", cancellationToken: ct); }
                }
                catch (InvalidOperationException ex) { await bot.SendMessage(id, ex.Message, cancellationToken: ct); }
            }
            else await bot.SendMessage(id, "فرمت: /code شماره_معامله کد (فقط تحویل‌دهندهٔ غذا)", cancellationToken: ct);
            return;
        }
        if (session.EditingField is { } field && !field.StartsWith("onboard:"))
        {
            if (field == "search")
            {
                session.EditingField = null; store.Save(session);
                await ShowPrivateSearch(id, text, ct);
                return;
            }
            if (field.StartsWith("ask:", StringComparison.Ordinal) && int.TryParse(field[4..], out var askedAdId))
            {
                try
                {
                    var question = market.AskQuestion(askedAdId, id, text, msg.MessageId);
                    session.EditingField = null; store.Save(session);
                    await bot.SendMessage(id, $"✅ پرسش {QuestionId(question)} دربارهٔ آگهی {ListingId(askedAdId)} ثبت شد. پاسخ زیر همین پیام می‌آید.",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId, AllowSendingWithoutReply = true }, cancellationToken: ct);
                    if (!await NotifyQuestion(question, ct))
                        await bot.SendMessage(id, "پرسش ذخیره شد؛ اعلان مالک دوباره تلاش می‌شود و در «پرسش‌های آگهی‌ها» هم هست.", cancellationToken: ct);
                }
                catch (InvalidOperationException ex)
                {
                    if (store.Ad(askedAdId) is not { Status: ListingStatus.Active })
                    { session.EditingField = null; store.Save(session); }
                    await bot.SendMessage(id, ex.Message, cancellationToken: ct);
                }
                return;
            }
            if (field.StartsWith("answer:", StringComparison.Ordinal) && int.TryParse(field[7..], out var answeredQuestionId))
            {
                try
                {
                    var question = market.AnswerQuestion(answeredQuestionId, id, text);
                    session.EditingField = null; store.Save(session);
                    await bot.SendMessage(id, $"✅ پاسخ به پرسش {QuestionId(question)} ثبت شد.",
                        replyParameters: new ReplyParameters { MessageId = msg.MessageId, AllowSendingWithoutReply = true }, cancellationToken: ct);
                    if (!await NotifyQuestion(question, ct))
                        await bot.SendMessage(id, "پاسخ ذخیره شد؛ ارسال آن به پرسش‌گر دوباره تلاش می‌شود.", cancellationToken: ct);
                }
                catch (InvalidOperationException ex)
                {
                    if (store.Question(answeredQuestionId)?.Answer is not null)
                    { session.EditingField = null; store.Save(session); }
                    await bot.SendMessage(id, ex.Message, cancellationToken: ct);
                }
                return;
            }
            if (field.StartsWith("support:", StringComparison.Ordinal) && int.TryParse(field[8..], out var ticketId) ||
                field.StartsWith("adminreply:", StringComparison.Ordinal) && int.TryParse(field[11..], out ticketId) && IsAdmin(id))
            {
                try
                {
                    var reply = _support.Post(ticketId, id, text);
                    var ticket = store.Ticket(ticketId)!;
                    var recipient = id == ticket.UserId ? options.AdminUserId : ticket.UserId;
                    await bot.SendMessage(id, $"✅ پیام در تیکت #{ticketId} ثبت شد. برای خروج /cancel را بزن.", cancellationToken: ct);
                    try
                    {
                        await bot.SendMessage(recipient, $"💬 تیکت #{ticketId} | {(id == ticket.UserId ? "پیام کاربر" : "پاسخ پشتیبانی")}\n{reply.Text}",
                            replyMarkup: new InlineKeyboardMarkup(new[] { new[] { C("↩️ پاسخ", id == ticket.UserId ? $"adm_ticket_{ticketId}" : $"support_ticket_{ticketId}") } }), cancellationToken: ct);
                    }
                    catch (Exception ex) { Log(ex); await bot.SendMessage(id, "پیام ذخیره شد ولی تحویل به طرف مقابل ممکن نشد؛ بعداً از تاریخچه تیکت پیگیری کنید.", cancellationToken: ct); }
                }
                catch (InvalidOperationException ex)
                {
                    if (store.Ticket(ticketId)?.Status != TicketStatus.Open) { session.EditingField = null; store.Save(session); }
                    await bot.SendMessage(id, ex.Message, cancellationToken: ct);
                }
                return;
            }
            if (IsAdmin(id) && await AdminInput(id, field, text, session, ct)) return;
            if (field.StartsWith("report:", StringComparison.Ordinal) && int.TryParse(field[7..], out var reportAdId))
            {
                if (store.Ad(reportAdId) is not null && store.OpenReport(reportAdId, id) is null)
                {
                    var report = new ListingReport { AdvertisementId = reportAdId, ReporterId = id, Reason = text[..Math.Min(1000, text.Length)] };
                    store.Save(report);
                    if (options.AdminUserId > 0)
                        try { await bot.SendMessage(options.AdminUserId, $"🚨 گزارش #{report.Id} | آگهی {ListingId(reportAdId)}\nاز کاربر {id}: {report.Reason}", replyMarkup: ReportButtons(report), cancellationToken: ct); }
                        catch (Exception ex) { Log(ex); }
                }
                session.EditingField = null; store.Save(session);
                await bot.SendMessage(id, "گزارش دریافت شد (گزارش تکراریِ باز دوباره ثبت نمی‌شود).", cancellationToken: ct);
                return;
            }
            var draft = Draft(session);
            if (draft is null) { session.EditingField = null; store.Save(session); return; }
            var parsed = await parser.ParseAsync(text, ct);
            switch (field)
            {
                case "food":
                    if (parsed.PossibleSensitiveCode.Value is not null)
                    {
                        await bot.SendMessage(id, "⚠️ متن غذا ممکن است کد تحویل داشته باشد. لطفاً نام غذا را بدون کد بفرستید.", cancellationToken: ct);
                        return;
                    }
                    if (draft.Type == ListingType.Exchange)
                    {
                        var foods = text.Split('|', 2);
                        if (foods.Length != 2) { await bot.SendMessage(id, "دو غذا را با | جدا کنید: غذای من | غذای موردنظر", cancellationToken: ct); return; }
                        draft.OfferedFood = foods[0].Trim(); draft.WantedFood = foods[1].Trim(); draft.FoodName = draft.OfferedFood;
                    }
                    else draft.FoodName = text == "-" ? null : PersianText.Normalize(text);
                    break;
                case "foodOrMeal":
                    if (parsed.Meal.Value != MealType.Unknown) draft.Meal = parsed.Meal.Value;
                    else draft.FoodName = PersianText.Normalize(text);
                    break;
                case "price":
                    if (text == "-") draft.Price = null;
                    else if (parsed.Price.Value.HasValue) draft.Price = parsed.Price.Value;
                    else if (PersianText.Normalize(text) is { } digits && digits.Length is >= 4 and <= 9 &&
                             digits.All(c => c is >= '0' and <= '9') && long.TryParse(digits, out var explicitPrice))
                    draft.Price = explicitPrice;
                    else { await bot.SendMessage(id, "قیمت را به تومان بنویسید (مثلاً ۸۰ یا ۸۰۰۰۰)، یا - برای حذف.", cancellationToken: ct); return; }
                    if (draft.Type == ListingType.Exchange) draft.OptionalPriceDifference = draft.Price;
                    break;
                case "gender": draft.Gender = parsed.CafeteriaGender.Value; break;
                case "location": draft.Location = text == "-" ? null : store.Locations().FirstOrDefault(l => PersianText.Normalize(l) == PersianText.Normalize(text));
                    if (text != "-" && draft.Location is null) { await bot.SendMessage(id, "محل‌های موجود: " + string.Join("، ", store.Locations()), cancellationToken: ct); return; } break;
                case "meal": draft.Meal = parsed.Meal.Value; break;
                case "date":
                    if (parsed.DateRange.Value is null && parsed.Date.SourceText is null) { await bot.SendMessage(id, "تاریخ را به‌شکل امروز، فردا، پس‌فردا، شنبه یا هفته بعد وارد کنید.", cancellationToken: ct); return; }
                    draft.Date = parsed.DateRange.Value is null ? parsed.Date.Value : null; draft.DateRange = parsed.DateRange.Value; break;
                case "type":
                    if (parsed.ListingType.Value == ListingType.Unknown) { await bot.SendMessage(id, "خریدار، فروشی یا معاوضه؟", cancellationToken: ct); return; }
                    draft.Type = parsed.ListingType.Value; break;
            }
            session.EditingField = null;
            SaveDraft(session, draft);
            if (!Marketplace.IsValid(draft))
            {
                session.EditingField = draft.Type == ListingType.Unknown ? "type" : draft.Type == ListingType.Exchange ? "food" : "foodOrMeal";
                store.Save(session);
                await bot.SendMessage(id, session.EditingField switch
                {
                    "type" => "خریدار، فروشی یا معاوضه؟",
                    "food" => "غذای خودم | غذای موردنظر را بنویسید.",
                    _ => "نام غذا یا وعده را بنویسید."
                }, cancellationToken: ct);
                return;
            }
            await ShowPreview(session, draft, ct);
            return;
        }
        await CreateDraftFromText(id, msg.From.Username, text, session, true, ct);
    }

    private async Task CreateDraftFromText(long id, string? username, string text, UserSession session,
        bool inheritType, CancellationToken ct, ListingType? forcedType = null, string? sourceToken = null)
    {
        var result = await parser.ParseAsync(text, ct);
        var ad = market.FromDraft(result, id, username);
        if (forcedType is { } chosenType)
        {
            ad.Type = chosenType;
            if (chosenType == ListingType.Exchange) ad.OfferedFood ??= ad.FoodName;
            else { ad.OfferedFood = null; ad.WantedFood = null; ad.OptionalPriceDifference = null; }
        }
        if (inheritType && ad.Type == ListingType.Unknown && Draft(session) is { } existing)
        {
            ad.Type = existing.Type;
            if (ad.FoodName is null) ad.FoodName = existing.FoodName;
            if (ad.Type == ListingType.Exchange) ad.OfferedFood ??= ad.FoodName;
        }
        session.PreviewMessageId = null;
        session.InlineDraftToken = sourceToken;
        session.UpdatingAdvertisementId = null;
        SaveDraft(session, ad);
        session.SensitiveNumber = result.PossibleSensitiveCode.Value;
        session.DuplicateId = null;
        store.Save(session);
        if (session.SensitiveNumber is { } number)
        {
            await bot.SendMessage(id, $"⚠️ عدد «{number}» شبیه کد تحویل یا رزرو است.\nبرای امنیت، این کد در آگهی عمومی نمایش داده نمی‌شود.",
                replyMarkup: new InlineKeyboardMarkup(new[] { new[] { C("🔐 کد غذاست", "number_code"), C("💰 قیمت است", "number_price") }, new[] { C("📝 اطلاعات دیگری است", "number_other") } }), cancellationToken: ct);
            return;
        }
        if (result.CafeteriaGender.SourceText == "آقایان / بانوان")
        {
            session.EditingField = "gender";
            store.Save(session);
            await bot.SendMessage(id, "سلف آقایان است یا بانوان؟", cancellationToken: ct);
            return;
        }
        if (ad.Type == ListingType.Unknown || ad.Type == ListingType.Exchange && (ad.OfferedFood is null || ad.WantedFood is null) ||
            ad.Type is ListingType.Buy or ListingType.Sell && ad.FoodName is null && ad.Meal == MealType.Unknown)
        {
            session.EditingField = ad.Type == ListingType.Unknown ? "type" : ad.Type == ListingType.Exchange ? "food" : "foodOrMeal";
            store.Save(session);
            await bot.SendMessage(id, session.EditingField switch
            {
                "type" => "خریدار، فروشی یا معاوضه؟",
                "food" => "غذای خودم | غذای موردنظر را بنویسید.",
                _ => "نام غذا یا وعده (مثلاً ناهار) را بنویسید."
            }, cancellationToken: ct);
            return;
        }
        await ShowPreview(session, ad, ct);
    }

    private async Task StartParameter(long id, string parameter, CancellationToken ct)
    {
        if (parameter.StartsWith("resume_", StringComparison.Ordinal))
        {
            var session = store.Session(id);
            if (session.InlineDraftToken == parameter[7..] && Draft(session) is { } pending)
                await ShowPreview(session, pending, ct);
            else await bot.SendMessage(id, "این پیش‌نویس فعال نیست؛ از منو آگهی تازه بساز.", cancellationToken: ct);
            return;
        }
        if (parameter.StartsWith("sold_", StringComparison.Ordinal) && int.TryParse(parameter[5..], out var soldId))
        { await MarkCompleted(id, soldId, false, ct); return; }
        if (parameter.StartsWith("fulfilled_", StringComparison.Ordinal) && int.TryParse(parameter[10..], out var fulfilledId))
        { await MarkCompleted(id, fulfilledId, true, ct); return; }
        if (parameter.StartsWith("review_", StringComparison.Ordinal) && int.TryParse(parameter[7..], out var reviewId))
        { await ShowRatingPrompt(id, reviewId, ct); return; }
        if (parameter.StartsWith("editad_", StringComparison.Ordinal) && int.TryParse(parameter[7..], out var editingId))
        { await BeginEditListing(id, editingId, ct); return; }
        if (parameter.StartsWith("ask_", StringComparison.Ordinal) && int.TryParse(parameter[4..], out var askedId))
        { await BeginQuestion(id, askedId, ct); return; }
        if (parameter.StartsWith("draft_", StringComparison.Ordinal) && store.Prefill(parameter[6..]) is { } draftText)
        {
            await CreateDraftFromText(id, store.User(id)?.Username, draftText, store.Session(id), false, ct, sourceToken: parameter[6..]);
            return;
        }
        if (parameter.StartsWith("view_", StringComparison.Ordinal) && int.TryParse(parameter[5..], out var viewId))
        {
            var found = store.Ad(viewId);
            await bot.SendMessage(id, found is null ? "آگهی پیدا نشد." : Card(found),
                replyMarkup: found is { Status: ListingStatus.Active } && !market.IsExpired(found) ? CardButtons(found, id) : null,
                cancellationToken: ct);
            return;
        }
        if (parameter.StartsWith("deal_", StringComparison.Ordinal) && int.TryParse(parameter[5..], out var adId))
        {
            try
            {
                var t = market.StartTransaction(adId, id);
                await bot.SendMessage(id, $"🤝 معامله #{t.Id} ثبت شد. بعد از تحویل، دکمهٔ تأیید را بزنید.",
                    replyMarkup: TransactionButtons(t, id), cancellationToken: ct);
                await bot.SendMessage(t.OwnerId, $"🤝 برای آگهی {ListingId(adId)} معامله #{t.Id} شروع شد. تحویل‌دهنده می‌تواند کد غذا را فقط در خصوصی با دستور /code {t.Id} کد ثبت کند؛ پس از تحویل تأیید کنید.",
                    replyMarkup: TransactionButtons(t, t.OwnerId), cancellationToken: ct);
            }
            catch (InvalidOperationException ex) { await bot.SendMessage(id, ex.Message, cancellationToken: ct); }
            return;
        }
        if (parameter.StartsWith("profile_", StringComparison.Ordinal) && long.TryParse(parameter[8..], out var userId))
        {
            var profile = store.User(userId);
            await bot.SendMessage(id, profile is null ? "کاربر پیدا نشد." : $"👤 @{profile.Username ?? "کاربر"}\n⭐ {profile.Rating:0.0} | 🛡 {profile.TrustScore}/100\n✅ {profile.SuccessfulTransactions} معامله موفق", cancellationToken: ct);
            return;
        }
        if (parameter.StartsWith("report_", StringComparison.Ordinal) && int.TryParse(parameter[7..], out var reportId) && store.Ad(reportId) is not null)
        {
            var s = store.Session(id);
            s.EditingField = $"report:{reportId}"; store.Save(s);
            await bot.SendMessage(id, "دلیل گزارش را در یک پیام بنویسید.", cancellationToken: ct);
            return;
        }
        if (parameter.StartsWith("create_", StringComparison.Ordinal))
        {
            var parts = parameter.Split('_', 3);
            if (parts.Length >= 2)
            {
                var type = parts[1] switch { "sell" => ListingType.Sell, "buy" => ListingType.Buy, "exchange" => ListingType.Exchange, _ => ListingType.Unknown };
                var input = parts.Length == 3 ? parts[2] == "gheimeh" ? "قیمه" : store.Prefill(parts[2]) : null;
                if (input is not null)
                {
                    await CreateDraftFromText(id, store.User(id)?.Username, input, store.Session(id), false, ct, type,
                        parts.Length == 3 && parts[2] != "gheimeh" ? parts[2] : null);
                    return;
                }
                var ad = new Advertisement { OwnerId = id, OwnerUsername = store.User(id)?.Username, Type = type,
                    Date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(market.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone))) };
                var session = store.Session(id);
                session.PreviewMessageId = null;
                session.InlineDraftToken = null;
                session.UpdatingAdvertisementId = null;
                SaveDraft(session, ad);
                await ShowPreview(session, ad, ct);
                return;
            }
        }
        if (parameter.StartsWith("search_", StringComparison.Ordinal) && store.Prefill(parameter[7..]) is { } queryText)
        {
            await ShowPrivateSearch(id, queryText, ct);
            return;
        }
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { C("💰 فروش غذا", "new_sell"), C("🛒 خرید غذا", "new_buy") },
            new[] { C("🔄 معاوضه", "new_exchange"), C("🔍 جستجوی غذا", "search") },
            new[] { C("📋 آگهی‌های من", "mine"), C("🤝 معاملات من", "transactions") },
            new[] { C("💬 پرسش‌های آگهی‌ها", "questions") },
            new[] { C("⭐ پروفایل و اعتبار", "profile"), C("⚙️ تنظیمات", "settings") },
            new[] { C("💬 گفتگو با پشتیبانی", "support"), C("ℹ️ راهنما", "help") }
        };
        if (IsAdmin(id)) rows.Add([C("🛠 پنل مدیریت", "adm_home")]);
        await bot.SendMessage(id, "🍽 بازار غذای دانشگاه\nچه کاری می‌خوای انجام بدی؟\nیا متن آگهی را همین‌جا بنویس:",
            replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task BeginListing(long id, string? username, ListingType type, UserSession session, CancellationToken ct)
    {
        session.EditingField = null;
        session.SensitiveNumber = null;
        session.DuplicateId = null;
        session.PreviewMessageId = null;
        session.InlineDraftToken = null;
        session.UpdatingAdvertisementId = null;
        var ad = new Advertisement { OwnerId = id, OwnerUsername = username, Type = type,
            Date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(market.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone))) };
        SaveDraft(session, ad);
        await ShowPreview(session, ad, ct);
    }

    private async Task BeginEditListing(long userId, int adId, CancellationToken ct)
    {
        var ad = store.Ad(adId);
        if (ad?.OwnerId != userId || ad.Status != ListingStatus.Active || market.IsExpired(ad) ||
            store.TransactionsForAd(adId).Any(t => t.Status == TransactionStatus.Pending))
        {
            await bot.SendMessage(userId, "این آگهی قابل ویرایش نیست؛ آگهی باید فعال و بدون معاملهٔ در جریان باشد.", cancellationToken: ct);
            return;
        }
        var session = store.Session(userId);
        session.EditingField = null;
        session.SensitiveNumber = null;
        session.DuplicateId = null;
        session.PreviewMessageId = null;
        session.InlineDraftToken = null;
        session.UpdatingAdvertisementId = adId;
        SaveDraft(session, ad);
        await ShowPreview(session, ad, ct);
    }

    private async Task BeginQuestion(long userId, int adId, CancellationToken ct)
    {
        var ad = store.Ad(adId);
        if (ad is null || ad.Status != ListingStatus.Active || market.IsExpired(ad) || ad.OwnerId == userId)
        { await bot.SendMessage(userId, "فقط دربارهٔ آگهی فعالِ شخص دیگری می‌توانی سؤال بپرسی.", cancellationToken: ct); return; }
        var session = store.Session(userId);
        session.EditingField = $"ask:{adId}"; store.Save(session);
        await bot.SendMessage(userId, $"💬 پرسش دربارهٔ آگهی {ListingId(ad)} را در یک پیام بنویس؛ مثلاً قیمت یا جزئیات تحویل را بپرس. با /cancel منصرف شو.", cancellationToken: ct);
    }

    private async Task BeginAnswer(long userId, int questionId, CancellationToken ct)
    {
        var question = store.Question(questionId);
        if (question?.OwnerId != userId || question.Answer is not null)
        { await bot.SendMessage(userId, "این پرسش برای پاسخ‌دادن در دسترس نیست.", cancellationToken: ct); return; }
        var session = store.Session(userId);
        session.EditingField = $"answer:{questionId}"; store.Save(session);
        await bot.SendMessage(userId, $"✍️ پاسخ به پرسش {QuestionId(question)} · آگهی {ListingId(question.AdvertisementId)}:\n{question.Text}\n\nپاسخت را بفرست؛ برای انصراف /cancel را بزن.",
            replyParameters: question.OwnerMessageId is { } messageId ? new ReplyParameters { MessageId = messageId, AllowSendingWithoutReply = true } : null,
            cancellationToken: ct);
    }

    private async Task ShowQuestions(long userId, CancellationToken ct, int page = 0)
    {
        var questions = store.UnansweredQuestions(userId);
        if (questions.Count == 0)
        { await bot.SendMessage(userId, "پرسش بی‌پاسخی دربارهٔ آگهی‌هایت نداری.", cancellationToken: ct); return; }
        page = Math.Clamp(page, 0, (questions.Count - 1) / 5);
        await bot.SendMessage(userId, $"💬 پرسش‌های بی‌پاسخ | صفحهٔ {page + 1}", cancellationToken: ct);
        foreach (var question in questions.Skip(page * 5).Take(5))
            await bot.SendMessage(userId, $"💬 پرسش {QuestionId(question)} · آگهی {ListingId(question.AdvertisementId)}\n{question.Text}",
                replyMarkup: new InlineKeyboardMarkup(new[] { new[] { C("✍️ پاسخ", $"answer_{question.Id}") } }), cancellationToken: ct);
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"questions_{page - 1}"));
        if ((page + 1) * 5 < questions.Count) navigation.Add(C("بعدی ➡️", $"questions_{page + 1}"));
        if (navigation.Count > 0)
            await bot.SendMessage(userId, "صفحهٔ بعد/قبل:", replyMarkup: new InlineKeyboardMarkup(new[] { navigation.ToArray() }), cancellationToken: ct);
    }

    private async Task<bool> NotifyQuestion(ListingQuestion question, CancellationToken ct)
    {
        try
        {
            if (store.Question(question.Id) is not { } current || current.Answer != question.Answer) return true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            if (question.Answer is null)
            {
                if (question.OwnerNotified) return true;
                var sent = await bot.SendMessage(question.OwnerId,
                    $"💬 پرسش {QuestionId(question)} · آگهی {ListingId(question.AdvertisementId)}\n{question.Text}\n\nبرای پاسخ روی همین پیام ریپلای کن یا دکمهٔ زیر را بزن.",
                    replyMarkup: new InlineKeyboardMarkup(new[] { new[] { C("✍️ پاسخ به پرسش", $"answer_{question.Id}") } }), cancellationToken: timeout.Token);
                current = store.Question(question.Id)!;
                current.OwnerMessageId = sent.MessageId;
                current.OwnerNotified = true;
            }
            else
            {
                if (question.RequesterNotified) return true;
                await bot.SendMessage(question.RequesterId,
                    $"💬 پاسخ به پرسش {QuestionId(question)} · آگهی {ListingId(question.AdvertisementId)}\n{question.Answer}",
                    replyParameters: question.RequesterMessageId is { } messageId
                        ? new ReplyParameters { MessageId = messageId, AllowSendingWithoutReply = true } : null,
                    replyMarkup: new InlineKeyboardMarkup(new[] { new[] { Link("👀 مشاهدهٔ آگهی", $"view_{question.AdvertisementId}") } }), cancellationToken: timeout.Token);
                current = store.Question(question.Id)!;
                current.RequesterNotified = true;
            }
            store.Update(current);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log(ex);
            if (store.Question(question.Id) is { } latest && latest.Answer == question.Answer)
            {
                latest.NextNotificationAttemptUtc = DateTime.UtcNow.AddMinutes(5);
                store.Update(latest);
            }
            return false;
        }
    }

    private async Task ShowPrivateSearch(long id, string text, CancellationToken ct)
    {
        var query = await parser.ParseAsync(text, ct);
        var matches = market.Search(query).Take(10).ToList();
        if (matches.Count == 0) await bot.SendMessage(id, "آگهی مرتبطی پیدا نشد.", cancellationToken: ct);
        foreach (var found in matches)
            await bot.SendMessage(id, Card(found), replyMarkup: CardButtons(found, id), cancellationToken: ct);
    }

    private async Task<bool> HandlePrivateMenu(long id, string text, string? username, UserSession session, CancellationToken ct)
    {
        if (text is not ("💰 فروش غذا" or "🛒 خرید غذا" or "🔄 معاوضه" or "🔍 جستجوی غذا" or
            "📋 آگهی‌های من" or "🤝 معاملات من" or "💬 پرسش‌های آگهی‌ها" or "⭐ پروفایل و اعتبار" or "⚙️ تنظیمات" or
            "💬 پشتیبانی" or "ℹ️ راهنما" or "🏠 منوی اصلی" or "🛠 پنل مدیریت")) return false;
        session.EditingField = null;
        store.Save(session);
        switch (text)
        {
            case "💰 فروش غذا": await BeginListing(id, username, ListingType.Sell, session, ct); break;
            case "🛒 خرید غذا": await BeginListing(id, username, ListingType.Buy, session, ct); break;
            case "🔄 معاوضه": await BeginListing(id, username, ListingType.Exchange, session, ct); break;
            case "🔍 جستجوی غذا":
                session.EditingField = "search"; store.Save(session);
                await bot.SendMessage(id, "نام غذا، وعده یا سلف را بنویس (مثلاً قیمه بانوان).", cancellationToken: ct);
                break;
            case "📋 آگهی‌های من": await MyListings(id, 0, ct); break;
            case "🤝 معاملات من": await UserTransactions(id, 0, ct); break;
            case "💬 پرسش‌های آگهی‌ها": await ShowQuestions(id, ct); break;
            case "⭐ پروفایل و اعتبار": await StartParameter(id, $"profile_{id}", ct); break;
            case "⚙️ تنظیمات": await UserSettings(id, ct); break;
            case "💬 پشتیبانی": await BeginSupport(id, ct); break;
            case "ℹ️ راهنما": await ShowHelp(id, ct); break;
            case "🏠 منوی اصلی": await StartParameter(id, "", ct); break;
            case "🛠 پنل مدیریت" when IsAdmin(id): await AdminMenu(id, ct); break;
        }
        return true;
    }

    private async Task Callback(CallbackQuery callback, CancellationToken ct)
    {
        var id = callback.From.Id;
        var data = callback.Data ?? "";
        if (store.User(id)?.Suspended == true && data is not ("support" or "help") &&
            !data.StartsWith("support_ticket_", StringComparison.Ordinal) &&
            !data.StartsWith("support_close_", StringComparison.Ordinal) &&
            !data.StartsWith("ticket_files_", StringComparison.Ordinal) &&
            !data.StartsWith("confirm_", StringComparison.Ordinal) &&
            !data.StartsWith("txn_cancel_", StringComparison.Ordinal) &&
            !data.StartsWith("rate_", StringComparison.Ordinal) &&
            !data.StartsWith("review_", StringComparison.Ordinal))
        {
            await bot.AnswerCallbackQuery(callback.Id, "دسترسی حساب محدود شده است؛ برای پیگیری /support را بزنید.", showAlert: true, cancellationToken: ct);
            return;
        }
        if (InlineDraftAction.TryParse(data, out var draftAction) && draftAction is not null)
        {
            if (store.Prefill(draftAction.Token) is null)
            {
                await bot.AnswerCallbackQuery(callback.Id, "پیش‌نویس پیدا نشد؛ دوباره Inline را باز کن.", showAlert: true, cancellationToken: ct);
                return;
            }
            if (id == draftAction.OwnerId && callback.InlineMessageId is { } inlineMessageId &&
                !store.HasInlineDraftMessage(id, inlineMessageId))
                store.Save(new InlineDraftMessage { OwnerId = id, Token = draftAction.Token, InlineMessageId = inlineMessageId });
            await bot.AnswerCallbackQuery(callback.Id,
                url: $"https://t.me/{_username}?start={draftAction.StartPayload}", cancellationToken: ct);
            return;
        }
        if (callback.Message is { Chat.Type: ChatType.Group or ChatType.Supergroup } groupMessage && !_groups.IsInstalled(groupMessage.Chat.Id))
        {
            await bot.AnswerCallbackQuery(callback.Id, "این گروه نصب نیست.", cancellationToken: ct);
            return;
        }
        var ownerOnlyId = data.StartsWith("soldc_", StringComparison.Ordinal) ? data[6..]
            : data.StartsWith("sold_", StringComparison.Ordinal) ? data[5..]
            : data.StartsWith("fulfilledc_", StringComparison.Ordinal) ? data[11..]
            : data.StartsWith("fulfilled_", StringComparison.Ordinal) ? data[10..] : null;
        if (ownerOnlyId is not null && int.TryParse(ownerOnlyId, out var ownerAdId) && store.Ad(ownerAdId)?.OwnerId != id)
        {
            await bot.AnswerCallbackQuery(callback.Id, "فقط مالک آگهی می‌تواند وضعیت آن را تغییر دهد.", showAlert: true, cancellationToken: ct);
            return;
        }
        await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
        if (data == "onboard")
        {
            var user = store.User(id) ?? new MarketUser { Id = id };
            user.Username = callback.From.Username;
            user.Onboarded = true;
            store.Save(user);
            var session = store.Session(id);
            var parameter = session.EditingField?.StartsWith("onboard:") == true ? session.EditingField[8..] : "";
            session.EditingField = null; store.Save(session);
            await ShowPrivateKeyboard(id, ct);
            await StartParameter(id, parameter, ct);
            return;
        }
        if (store.User(id)?.Onboarded != true) return;
        var s = store.Session(id);
        var ad = Draft(s);
        if ((data is "publish" or "confirm_price" or "cancel" || data.StartsWith("edit_", StringComparison.Ordinal)) &&
            callback.Message?.MessageId is { } clickedPreview && s.PreviewMessageId is { } latestPreview &&
            clickedPreview != latestPreview)
        {
            await bot.SendMessage(id, "این پیش‌نمایش قدیمی است؛ از آخرین پیام آگهی استفاده کن.", cancellationToken: ct);
            return;
        }
        if (data.StartsWith("new_", StringComparison.Ordinal))
        {
            var type = data[4..] switch { "sell" => ListingType.Sell, "buy" => ListingType.Buy, _ => ListingType.Exchange };
            await BeginListing(id, callback.From.Username, type, s, ct);
            return;
        }
        if (data.StartsWith("editad_", StringComparison.Ordinal) && int.TryParse(data[7..], out var editAdId))
        { await BeginEditListing(id, editAdId, ct); return; }
        if (data.StartsWith("cancelad_", StringComparison.Ordinal) && int.TryParse(data[9..], out var cancelAdId))
        {
            var owned = store.Ad(cancelAdId);
            if (owned?.OwnerId != id || owned.Status != ListingStatus.Active)
            { await bot.SendMessage(id, "این آگهی برای لغو در دسترس نیست.", cancellationToken: ct); return; }
            await bot.SendMessage(id, $"آگهی {ListingId(cancelAdId)} غیرفعال شود؟ این کار قابل بازگشت نیست.",
                replyMarkup: new InlineKeyboardMarkup(new[] { new[] { C("⛔ بله، لغو آگهی", $"canceladyes_{cancelAdId}"), C("↩️ نه", "canceladno") } }), cancellationToken: ct);
            return;
        }
        if (data.StartsWith("canceladyes_", StringComparison.Ordinal) && int.TryParse(data[12..], out var confirmedAdId))
        {
            if (market.CancelOwnListing(confirmedAdId, id))
            {
                await EditShares(confirmedAdId, ct);
                await bot.SendMessage(id, $"⛔ آگهی {ListingId(confirmedAdId)} لغو و پیام‌های منتشرشده حذف/غیرفعال شدند.", cancellationToken: ct);
            }
            else await bot.SendMessage(id, "لغو ممکن نیست؛ مالکیت، وضعیت آگهی یا معاملهٔ در جریان را بررسی کن.", cancellationToken: ct);
            return;
        }
        if (data == "canceladno")
        { await bot.SendMessage(id, "آگهی همچنان فعال است.", cancellationToken: ct); return; }
        if (data.StartsWith("edit_", StringComparison.Ordinal))
        {
            if (ad is null) return;
            s.EditingField = data[5..]; store.Save(s);
            var hint = s.EditingField switch
            {
                "food" when ad.Type == ListingType.Exchange => "غذای خودم | غذای موردنظر را بنویسید.",
                "food" => "نام غذا را بنویسید (- یعنی هر غذایی).",
                "foodOrMeal" => "نام غذا یا وعده را بنویسید.",
                "price" => "قیمت را به تومان بنویسید؛ برای حذف - بفرستید.",
                "date" => "امروز، فردا، پس‌فردا، روز هفته یا هفته بعد؟",
                "gender" => "آقایان، بانوان یا مختلط؟",
                "location" => "محل سلف را بنویسید (- برای نامشخص): " + string.Join("، ", store.Locations()),
                "type" => "خریدار، فروشی یا معاوضه؟",
                _ => "صبحانه، ناهار یا شام؟"
            };
            await bot.SendMessage(id, hint, cancellationToken: ct);
            return;
        }
        if (data.StartsWith("number_", StringComparison.Ordinal) && ad is not null && s.SensitiveNumber is { } number)
        {
            if (data == "number_price" && long.TryParse(number, out var price)) ad.Price = price;
            // A delivery code cannot be attached to a public listing. It may be sent via /code after a transaction starts.
            s.SensitiveNumber = null;
            SaveDraft(s, ad);
            await ShowPreview(s, ad, ct);
            return;
        }
        // Older previews may still have this button; replace it with the single publish button.
        if (data == "confirm_price" && ad is not null) { await ShowPreview(s, ad, ct); return; }
        if (data == "cancel")
        {
            s.DraftJson = null; s.EditingField = null; s.SensitiveNumber = null;
            s.PreviewMessageId = null;
            s.InlineDraftToken = null;
            s.UpdatingAdvertisementId = null;
            store.Save(s);
            await bot.SendMessage(id, "پیش‌نویس کنار گذاشته شد؛ آگهی‌های ثبت‌شده تغییری نکردند.", cancellationToken: ct); return;
        }
        if (data == "publish" && ad is not null)
        {
            if (s.SensitiveNumber is not null) { await bot.SendMessage(id, "اول نوع عدد مشکوک را مشخص کنید.", cancellationToken: ct); return; }
            if (s.UpdatingAdvertisementId is { } updatingId)
            { await Publish(s, ad, updatingId, ct); return; }
            if (s.DuplicateId is null && market.Duplicate(ad) is { } duplicate)
            {
                s.DuplicateId = duplicate.Id; store.Save(s);
                await bot.SendMessage(id, "شما یک آگهی مشابه فعال دارید.", replyMarkup: new InlineKeyboardMarkup(new[]
                { new[] { C("♻️ بروزرسانی آگهی قبلی", "dup_update") }, new[] { C("➕ ثبت آگهی جدید", "dup_new") } }), cancellationToken: ct);
                return;
            }
            await Publish(s, ad, null, ct); return;
        }
        if (data is "dup_update" or "dup_new" && ad is not null)
        {
            if (s.DuplicateId is null)
            { await bot.SendMessage(id, "این انتخاب قدیمی است؛ دوباره پیش‌نمایش آگهی را باز کن.", cancellationToken: ct); return; }
            await Publish(s, ad, data == "dup_update" ? s.DuplicateId : null, ct); return;
        }
        if (data.StartsWith("sold_", StringComparison.Ordinal) && int.TryParse(data[5..], out var soldId))
        {
            await MarkCompleted(id, soldId, false, ct, callback.InlineMessageId);
            return;
        }
        if (data.StartsWith("soldc_", StringComparison.Ordinal) && int.TryParse(data[6..], out var compactSoldId))
        { await MarkCompleted(id, compactSoldId, false, ct, callback.InlineMessageId, true); return; }
        if (data.StartsWith("fulfilled_", StringComparison.Ordinal) && int.TryParse(data[10..], out var fulfilledId))
        {
            await MarkCompleted(id, fulfilledId, true, ct, callback.InlineMessageId);
            return;
        }
        if (data.StartsWith("fulfilledc_", StringComparison.Ordinal) && int.TryParse(data[11..], out var compactFulfilledId))
        { await MarkCompleted(id, compactFulfilledId, true, ct, callback.InlineMessageId, true); return; }
        if (data.StartsWith("review_", StringComparison.Ordinal) && int.TryParse(data[7..], out var reviewId))
        { await ShowRatingPrompt(id, reviewId, ct); return; }
        if (data.StartsWith("answer_", StringComparison.Ordinal) && int.TryParse(data[7..], out var questionId))
        { await BeginAnswer(id, questionId, ct); return; }
        if (data == "questions" || data.StartsWith("questions_", StringComparison.Ordinal) && int.TryParse(data[10..], out _))
        { await ShowQuestions(id, ct, data == "questions" ? 0 : int.Parse(data[10..])); return; }
        if (data.StartsWith("confirm_", StringComparison.Ordinal) && int.TryParse(data[8..], out var tId))
        {
            try
            {
                var t = market.ConfirmDelivery(tId, id);
                await bot.SendMessage(id, t.Status == TransactionStatus.Completed ? "✅ معامله با تأیید هر دو طرف تکمیل شد." : "✅ تأیید ثبت شد؛ منتظر تأیید طرف مقابل هستیم.", cancellationToken: ct);
                if (t.Status == TransactionStatus.Completed)
                {
                    await EditShares(t.AdvertisementId, ct);
                    foreach (var participant in new[] { t.OwnerId, t.CounterpartyId })
                        await bot.SendMessage(participant, "⭐ به طرف معامله امتیاز بدهید:", replyMarkup: RatingButtons(t.Id), cancellationToken: ct);
                }
            }
            catch (InvalidOperationException ex) { await bot.SendMessage(id, ex.Message, cancellationToken: ct); }
            return;
        }
        if (data.StartsWith("txn_cancel_", StringComparison.Ordinal) && int.TryParse(data[11..], out var cancelledId))
        {
            try
            {
                var t = market.CancelTransaction(cancelledId, id);
                await bot.SendMessage(id, $"❌ معامله #{t.Id} لغو شد و آگهی در صورت معتبر بودن دوباره در دسترس است.", cancellationToken: ct);
                var other = t.OwnerId == id ? t.CounterpartyId : t.OwnerId;
                try { await bot.SendMessage(other, $"❌ معامله #{t.Id} لغو شد. برای پیگیری /support را بزنید.", cancellationToken: ct); }
                catch (Exception ex) { Log(ex); }
            }
            catch (InvalidOperationException ex) { await bot.SendMessage(id, ex.Message, cancellationToken: ct); }
            return;
        }
        if (data.StartsWith("rate_", StringComparison.Ordinal))
        {
            var parts = data.Split('_');
            if (parts.Length == 3 && int.TryParse(parts[1], out var ratingTransaction) && int.TryParse(parts[2], out var stars))
            {
                var rated = market.Rate(ratingTransaction, id, stars);
                await bot.SendMessage(id, rated ? "⭐ امتیاز شما ثبت شد." : "این امتیاز قابل ثبت نیست.", cancellationToken: ct);
                if (rated && store.Transaction(ratingTransaction) is { } transaction)
                    await EditShares(transaction.AdvertisementId, ct);
            }
            return;
        }
        if (data == "mine" || data.StartsWith("mine_", StringComparison.Ordinal) && int.TryParse(data[5..], out _))
        {
            await MyListings(id, data == "mine" ? 0 : int.Parse(data[5..]), ct);
            return;
        }
        if (data.StartsWith("groups_", StringComparison.Ordinal) && int.TryParse(data[7..], out var shareAdId))
        {
            await GroupPicker(id, shareAdId, 0, ct);
            return;
        }
        if (data.StartsWith("group_page_", StringComparison.Ordinal))
        {
            var parts = data.Split('_');
            if (parts.Length == 4 && int.TryParse(parts[2], out var pageAdId) && int.TryParse(parts[3], out var page))
                await GroupPicker(id, pageAdId, page, ct);
            return;
        }
        if (data.StartsWith("post_", StringComparison.Ordinal))
        {
            var parts = data.Split('_', 3);
            if (parts.Length == 3 && int.TryParse(parts[1], out var postAdId) && long.TryParse(parts[2], out var groupId))
                await PostInGroup(id, postAdId, groupId, ct);
            return;
        }
        if (data.StartsWith("notify_", StringComparison.Ordinal) && int.TryParse(data[7..], out var notifyId) && store.Ad(notifyId) is { } notifyAd && notifyAd.OwnerId == id)
        {
            notifyAd.MatchNotifications = !notifyAd.MatchNotifications; store.Save(notifyAd);
            await bot.SendMessage(id, notifyAd.MatchNotifications ? "🔔 اعلان موارد مناسب فعال شد." : "🔕 اعلان غیرفعال شد.", cancellationToken: ct); return;
        }
        if (data == "transactions" || data.StartsWith("transactions_", StringComparison.Ordinal) && int.TryParse(data[13..], out _))
        {
            await UserTransactions(id, data == "transactions" ? 0 : int.Parse(data[13..]), ct);
            return;
        }
        if (data == "profile") { await StartParameter(id, $"profile_{id}", ct); return; }
        if (data == "search") { s.EditingField = "search"; store.Save(s); await bot.SendMessage(id, $"عبارت موردنظرت را بنویس (مثلاً قیمه بانوان). در گروه هم می‌توانی @{_username} قیمه بنویسی.", cancellationToken: ct); return; }
        if (data == "settings") { s.EditingField = null; store.Save(s); await UserSettings(id, ct); return; }
        if (data == "user_notify")
        {
            var user = store.User(id)!;
            user.NotificationsEnabled = !user.NotificationsEnabled; store.Save(user);
            await UserSettings(id, ct); return;
        }
        if (data == "support") { await BeginSupport(id, ct); return; }
        if (data == "help") { await ShowHelp(id, ct); return; }
        if (data.StartsWith("support_ticket_", StringComparison.Ordinal) && int.TryParse(data[15..], out var userTicketId))
        {
            var ticket = store.Ticket(userTicketId);
            if (ticket?.UserId == id) { await ShowTicket(id, ticket, ct); return; }
        }
        if (data.StartsWith("support_close_", StringComparison.Ordinal) && int.TryParse(data[14..], out var closeId))
        {
            if (_support.Close(closeId, id))
            {
                if (s.EditingField?.StartsWith("support:", StringComparison.Ordinal) == true || s.EditingField?.StartsWith("adminreply:", StringComparison.Ordinal) == true) s.EditingField = null;
                store.Save(s);
                await bot.SendMessage(id, $"تیکت #{closeId} بسته شد.", cancellationToken: ct);
                var ticket = store.Ticket(closeId)!;
                var other = id == ticket.UserId ? options.AdminUserId : ticket.UserId;
                try { await bot.SendMessage(other, $"تیکت #{closeId} بسته شد. برای درخواست تازه /support را بزنید.", cancellationToken: ct); }
                catch (Exception ex) { Log(ex); }
            }
            return;
        }
        if (data.StartsWith("ticket_files_", StringComparison.Ordinal) && int.TryParse(data[13..], out var filesId))
        {
            var ticket = store.Ticket(filesId);
            if (ticket is null || ticket.UserId != id && !IsAdmin(id)) return;
            foreach (var attachment in store.TicketMessages(filesId).Where(m => m.AttachmentChatId.HasValue && m.AttachmentMessageId.HasValue).TakeLast(5))
                try { await bot.CopyMessage(id, attachment.AttachmentChatId!.Value, attachment.AttachmentMessageId!.Value, cancellationToken: ct); }
                catch (Exception ex) { Log(ex); await bot.SendMessage(id, "نمایش یکی از پیوست‌ها ممکن نشد.", cancellationToken: ct); }
            return;
        }
        if (IsAdmin(id) && await AdminCallback(id, data, s, ct)) return;
    }

    private static InlineKeyboardMarkup RatingButtons(int transactionId) => new(new[]
    {
        Enumerable.Range(1, 5).Select(n => C($"{n} ⭐", $"rate_{transactionId}_{n}")).ToArray()
    });

    private async Task MarkCompleted(long userId, int adId, bool buy, CancellationToken ct,
        string? inlineMessageId = null, bool compact = false)
    {
        if (buy ? market.MarkFulfilled(adId, userId) : market.MarkSold(adId, userId))
        {
            if (inlineMessageId is not null && !store.Shares(adId).Any(s => s.InlineMessageId == inlineMessageId))
                store.Save(new SharedMessage { AdvertisementId = adId, InlineMessageId = inlineMessageId, CompactInlineCard = compact });
            await EditShares(adId, ct);
            await bot.SendMessage(userId, buy ? "✅ درخواست خریداری‌شده ثبت شد و دکمه‌های معامله غیرفعال شدند." :
                "✅ آگهی فروخته‌شده ثبت شد و دکمه‌های خرید غیرفعال شدند.", cancellationToken: ct);
        }
        else await bot.SendMessage(userId, "این کار فقط برای مالک آگهی فعال، بدون معاملهٔ در جریان مجاز است. اگر معامله در ربات شروع شده، تأیید هر دو طرف لازم است.", cancellationToken: ct);
    }

    private async Task ShowRatingPrompt(long userId, int transactionId, CancellationToken ct)
    {
        var transaction = store.Transaction(transactionId);
        if (transaction is null || transaction.Status != TransactionStatus.Completed ||
            userId != transaction.OwnerId && userId != transaction.CounterpartyId)
        {
            await bot.SendMessage(userId, "امتیازدهی فقط پس از معاملهٔ کامل‌شده و توسط دو طرف آن ممکن است.", cancellationToken: ct);
            return;
        }
        if (store.HasRated(transactionId, userId))
        { await bot.SendMessage(userId, "قبلاً برای این معامله امتیاز ثبت کرده‌ای.", cancellationToken: ct); return; }
        await bot.SendMessage(userId, $"⭐ به طرف مقابلِ معامله #{transactionId} از ۱ تا ۵ امتیاز بده:",
            replyMarkup: RatingButtons(transactionId), cancellationToken: ct);
    }

    private bool IsAdmin(long id) => options.AdminUserId > 0 && id == options.AdminUserId;
    private void Audit(long actor, string action, long target, string? detail = null) =>
        store.Save(new AdminAuditEvent { ActorId = actor, Action = action, TargetId = target, Detail = detail });

    private async Task MyListings(long id, int page, CancellationToken ct)
    {
        var ads = store.AdsFor(id);
        if (ads.Count == 0) { await bot.SendMessage(id, "هنوز آگهی نداری. یک متن مثل «فروشی قیمه بانوان ۸۰» بفرست.", cancellationToken: ct); return; }
        page = Math.Clamp(page, 0, Math.Max(0, (ads.Count - 1) / 5));
        await bot.SendMessage(id, $"📋 آگهی‌های من | صفحه {page + 1} از {(ads.Count + 4) / 5}", cancellationToken: ct);
        foreach (var ad in ads.Skip(page * 5).Take(5))
            await bot.SendMessage(id, Card(ad), replyMarkup: ad.Status == ListingStatus.Active ? OwnerButtons(ad) : ClosedCardButtons(ad), cancellationToken: ct);
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"mine_{page - 1}"));
        if ((page + 1) * 5 < ads.Count) navigation.Add(C("بعدی ➡️", $"mine_{page + 1}"));
        if (navigation.Count > 0) await bot.SendMessage(id, "صفحهٔ بعد/قبل:", replyMarkup: new InlineKeyboardMarkup(new[] { navigation.ToArray() }), cancellationToken: ct);
    }

    private async Task UserTransactions(long id, int page, CancellationToken ct)
    {
        var transactions = store.TransactionsFor(id).OrderByDescending(t => t.CreatedUtc).ToList();
        if (transactions.Count == 0) { await bot.SendMessage(id, "هنوز معامله‌ای نداری.", cancellationToken: ct); return; }
        page = Math.Clamp(page, 0, Math.Max(0, (transactions.Count - 1) / 5));
        await bot.SendMessage(id, $"🤝 معاملات من | صفحه {page + 1} از {(transactions.Count + 4) / 5}", cancellationToken: ct);
        foreach (var t in transactions.Skip(page * 5).Take(5))
            await bot.SendMessage(id, $"معامله #{t.Id} | آگهی {ListingId(t.AdvertisementId)} | {t.Status}",
                replyMarkup: t.Status == TransactionStatus.Pending ? TransactionButtons(t, id)
                    : t.Status == TransactionStatus.Completed && !store.HasRated(t.Id, id)
                        ? new InlineKeyboardMarkup(new[] { new[] { C("⭐ امتیاز به طرف معامله", $"review_{t.Id}") } }) : null,
                cancellationToken: ct);
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"transactions_{page - 1}"));
        if ((page + 1) * 5 < transactions.Count) navigation.Add(C("بعدی ➡️", $"transactions_{page + 1}"));
        if (navigation.Count > 0) await bot.SendMessage(id, "صفحهٔ بعد/قبل:", replyMarkup: new InlineKeyboardMarkup(new[] { navigation.ToArray() }), cancellationToken: ct);
    }

    private Task ShowHelp(long userId, CancellationToken ct) => bot.SendMessage(userId,
        "ℹ️ راهنمای بازار غذا\n\n" +
        "• متن آگهی مثل «فروشی قیمه بانوان ۱۰۰» را بفرست؛ ۱۰۰ یعنی ۱۰۰ هزار تومان. قیمت اجباری نیست.\n" +
        "• پیش‌نمایش را بررسی کن و «منتشر کن» را بزن. از «آگهی‌های من» می‌توانی آگهی فعال را ویرایش، در گروه منتشر یا لغو کنی.\n" +
        "• فقط در گروه نصب‌شده با /food قیمه جست‌وجو کن؛ Inline مستقیم گروهی به‌دلیل نبود شناسهٔ گروه در API تلگرام فعال نیست. در خصوصی از @" + _username + " هم می‌توانی استفاده کنی.\n" +
        "• برای سؤال دربارهٔ قیمت، دکمهٔ «پرسش از آگهی‌دهنده» را بزن؛ این کار معامله را شروع نمی‌کند.\n" +
        "• برای شروع معامله دکمهٔ خرید/پیشنهاد را بزن؛ پس از تحویل هر دو طرف معامله را تأیید می‌کنند. کد غذا را فقط در خصوصی و با /code شماره_معامله کد بفرست.\n" +
        "• برای کنار گذاشتن پیش‌نویس یا خروج از گفتگو /cancel و برای پشتیبانی /support را بزن.",
        cancellationToken: ct);

    private async Task BeginSupport(long id, CancellationToken ct)
    {
        if (options.AdminUserId <= 0)
        {
            await bot.SendMessage(id, "پشتیبانی هنوز توسط مدیر سامانه تنظیم نشده است.", cancellationToken: ct);
            return;
        }
        if (IsAdmin(id)) { await TicketList(id, 0, ct); return; }
        try { await ShowTicket(id, _support.Open(id), ct); }
        catch (InvalidOperationException ex) { await bot.SendMessage(id, ex.Message, cancellationToken: ct); }
    }

    private async Task ShowTicket(long viewer, SupportTicket ticket, CancellationToken ct)
    {
        if (viewer != ticket.UserId && !IsAdmin(viewer)) return;
        var messages = store.TicketMessages(ticket.Id).TakeLast(5)
            .Select(m => $"{(m.SenderId == ticket.UserId ? "کاربر" : "پشتیبانی")}: {m.Text}");
        var history = string.Join("\n", messages);
        if (history.Length > 3000) history = history[^3000..];
        if (ticket.Status == TicketStatus.Open)
        {
            var session = store.Session(viewer);
            session.EditingField = viewer == ticket.UserId ? $"support:{ticket.Id}" : $"adminreply:{ticket.Id}";
            store.Save(session);
        }
        else
        {
            var session = store.Session(viewer);
            if (session.EditingField?.StartsWith("support:", StringComparison.Ordinal) == true ||
                session.EditingField?.StartsWith("adminreply:", StringComparison.Ordinal) == true)
            { session.EditingField = null; store.Save(session); }
        }
        var buttons = new List<InlineKeyboardButton[]>();
        if (store.TicketMessages(ticket.Id).Any(m => m.AttachmentMessageId.HasValue))
            buttons.Add([C("📎 نمایش ۵ پیوست اخیر", $"ticket_files_{ticket.Id}")]);
        if (ticket.Status == TicketStatus.Open) buttons.Add([C("✅ بستن گفتگو", $"support_close_{ticket.Id}")]);
        await bot.SendMessage(viewer, $"💬 تیکت #{ticket.Id} | {(ticket.Status == TicketStatus.Open ? "باز" : "بسته")}\n" +
            (history.Length == 0 ? "هنوز پیامی ثبت نشده است." : history) +
            (ticket.Status == TicketStatus.Open ? "\n\nپیام، عکس یا فایل بفرست. برای خروج /cancel را بزن." : ""),
            replyMarkup: buttons.Count > 0 ? new InlineKeyboardMarkup(buttons) : null,
            cancellationToken: ct);
    }

    private async Task SupportAttachment(Message msg, string mode, CancellationToken ct)
    {
        if (!int.TryParse(mode[(mode.IndexOf(':') + 1)..], out var ticketId)) return;
        var kind = msg.Photo is not null ? "عکس" : msg.Document is not null ? "فایل" :
            msg.Video is not null ? "ویدیو" : msg.Voice is not null ? "صدا" : null;
        if (kind is null) { await bot.SendMessage(msg.Chat.Id, "در پشتیبانی پیام متنی، عکس، فایل، ویدیو یا پیام صوتی بفرست.", cancellationToken: ct); return; }
        try
        {
            var message = _support.PostAttachment(ticketId, msg.From!.Id, msg.Chat.Id, msg.MessageId, kind, msg.Caption);
            var ticket = store.Ticket(ticketId)!;
            var recipient = msg.From.Id == ticket.UserId ? options.AdminUserId : ticket.UserId;
            await bot.SendMessage(msg.Chat.Id, "✅ پیوست در تیکت ثبت شد.", cancellationToken: ct);
            try
            {
                await bot.SendMessage(recipient, $"💬 تیکت #{ticketId} | {message.Text}",
                    replyMarkup: new InlineKeyboardMarkup(new[] { new[] { C("↩️ پاسخ", msg.From.Id == ticket.UserId ? $"adm_ticket_{ticketId}" : $"support_ticket_{ticketId}") } }), cancellationToken: ct);
                await bot.CopyMessage(recipient, msg.Chat.Id, msg.MessageId, cancellationToken: ct);
            }
            catch (Exception ex) { Log(ex); await bot.SendMessage(msg.Chat.Id, "پیوست ذخیره شد ولی تحویل ممکن نشد؛ بعداً در تاریخچهٔ گفتگو پیگیری کن.", cancellationToken: ct); }
        }
        catch (InvalidOperationException ex) { await bot.SendMessage(msg.Chat.Id, ex.Message, cancellationToken: ct); }
    }

    private async Task UserSettings(long id, CancellationToken ct)
    {
        var enabled = store.User(id)?.NotificationsEnabled == true;
        await bot.SendMessage(id, $"⚙️ تنظیمات\nاعلان تطبیق: {(enabled ? "روشن" : "خاموش")}\nبرای هر آگهی هم می‌توانی اعلان را جداگانه از «آگهی‌های من» تغییر بدهی.",
            replyMarkup: new InlineKeyboardMarkup(new[] { new[] { C(enabled ? "🔕 خاموش کردن اعلان‌ها" : "🔔 روشن کردن اعلان‌ها", "user_notify") }, new[] { C("💬 گفتگو با پشتیبانی", "support") } }), cancellationToken: ct);
    }

    private async Task AdminMenu(long id, CancellationToken ct)
    {
        if (!IsAdmin(id)) return;
        var ads = store.AllAds();
        var users = store.Users();
        var summary = $"👥 گروه مجاز: {store.InstalledGroups().Count} | 👤 کاربر: {users.Count} (محدود: {users.Count(u => u.Suspended)})\n" +
            $"📋 آگهی فعال: {ads.Count(a => a.Status == ListingStatus.Active && !market.IsExpired(a) && store.User(a.OwnerId)?.Suspended != true)} | ✅ تکمیل‌شده: {ads.Count(a => a.Status == ListingStatus.Sold)}\n" +
            $"💬 تیکت باز: {store.Tickets().Count(t => t.Status == TicketStatus.Open)} | 🚨 گزارش باز: {store.Reports().Count(r => r.Status == ReportStatus.Open)} | 🤝 معاملهٔ در جریان: {store.PendingTransactions().Count}";
        await bot.SendMessage(id, $"🛠 پنل مدیریت\n{summary}\n\nبخش موردنظر را انتخاب کن:", replyMarkup: new InlineKeyboardMarkup(new[]
        {
            new[] { C("📍 مدیریت محل‌های سلف", "adm_caf_0") },
            new[] { C("👥 گروه‌های نصب‌شده", "adm_groups_0") },
            new[] { C("📋 آگهی‌های فعال", "adm_ads_0"), C("👤 کاربران", "adm_users_0") },
            new[] { C("💬 گفتگوهای پشتیبانی", "adm_tickets_0"), C("🚨 گزارش‌ها", "adm_reports_0") },
            new[] { C("🤝 معاملات در جریان", "adm_pending_0") },
            new[] { C("⚙️ تنظیمات سامانه", "adm_settings"), C("🧾 اقدامات مدیر", "adm_audit_0") }
        }), cancellationToken: ct);
    }

    private async Task AdminAuditLog(long id, int page, CancellationToken ct)
    {
        if (!IsAdmin(id)) return;
        var events = store.AdminAudits();
        page = Math.Clamp(page, 0, Math.Max(0, (events.Count - 1) / 10));
        var names = new Dictionary<string, string>
        {
            ["user_suspend"] = "محدودسازی کاربر", ["user_restore"] = "رفع محدودیت کاربر",
            ["group_install"] = "نصب گروه", ["group_uninstall"] = "حذف نصب از گروه",
            ["group_disable"] = "غیرفعال‌سازی گروه", ["ad_disable"] = "غیرفعال‌سازی آگهی",
            ["report_resolve"] = "رسیدگی به گزارش", ["report_dismiss"] = "رد گزارش",
            ["report_remove"] = "حذف آگهی گزارش‌شده", ["transaction_cancel"] = "لغو معامله",
            ["cafeteria_update"] = "تغییر سلف", ["setting_update"] = "تغییر تنظیمات"
        };
        var lines = events.Skip(page * 10).Take(10).Select(e =>
            $"{e.CreatedUtc:yyyy/MM/dd HH:mm} UTC | {names.GetValueOrDefault(e.Action, e.Action)} | شناسه {e.TargetId} | مدیر {e.ActorId}" +
            (e.Detail is null ? "" : $" | {e.Detail}"));
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"adm_audit_{page - 1}"));
        if ((page + 1) * 10 < events.Count) navigation.Add(C("بعدی ➡️", $"adm_audit_{page + 1}"));
        var rows = new List<InlineKeyboardButton[]>();
        if (navigation.Count > 0) rows.Add(navigation.ToArray());
        rows.Add([C("↩️ مدیریت", "adm_home")]);
        await bot.SendMessage(id, events.Count == 0 ? "هنوز اقدامی ثبت نشده است." :
            $"🧾 اقدامات مدیر | صفحهٔ {page + 1}\n" + string.Join("\n", lines),
            replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task AdminGroups(long id, int page, CancellationToken ct)
    {
        if (!IsAdmin(id)) return;
        var groups = store.AllGroups();
        page = Math.Clamp(page, 0, Math.Max(0, (groups.Count - 1) / 8));
        var rows = groups.Skip(page * 8).Take(8).Select(g => g.Active
            ? new[] { C($"✅ {g.Title}", $"adm_group_info_{g.Id}"), C("⛔ حذف", $"adm_group_offask_{g.Id}") }
            : new[] { C($"⛔ {g.Title}", $"adm_group_info_{g.Id}") }).ToList();
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"adm_groups_{page - 1}"));
        if ((page + 1) * 8 < groups.Count) navigation.Add(C("بعدی ➡️", $"adm_groups_{page + 1}"));
        if (navigation.Count > 0) rows.Add(navigation.ToArray());
        rows.Add([C("↩️ مدیریت", "adm_home")]);
        await bot.SendMessage(id, groups.Count == 0
            ? "هیچ گروهی نصب نشده است. در گروه موردنظر «نصب» بنویس. ربات باید پیام‌های گروه را دریافت کند."
            : "👥 گروه‌های ثبت‌شده؛ گروه غیرفعال فقط با پیام «نصب» خود مدیر در همان گروه دوباره فعال می‌شود.",
            replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task AdminAds(long id, int page, CancellationToken ct)
    {
        if (!IsAdmin(id)) return;
        var ads = store.AllAds().Where(a => a.Status == ListingStatus.Active).ToList();
        page = Math.Clamp(page, 0, Math.Max(0, (ads.Count - 1) / 8));
        var rows = ads.Skip(page * 8).Take(8).Select(a => new[]
        { C($"{ListingId(a)} · {Header(a)[..Math.Min(30, Header(a).Length)]}", $"adm_ad_{a.Id}") }).ToList();
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"adm_ads_{page - 1}"));
        if ((page + 1) * 8 < ads.Count) navigation.Add(C("بعدی ➡️", $"adm_ads_{page + 1}"));
        if (navigation.Count > 0) rows.Add(navigation.ToArray());
        rows.Add([C("🔎 پیدا کردن شناسه", "adm_lookup"), C("↩️ مدیریت", "adm_home")]);
        await bot.SendMessage(id, ads.Count == 0 ? "آگهی فعالی وجود ندارد. با جست‌وجوی شناسه می‌توانی آگهی‌های بسته‌شده را هم ببینی." :
            $"📋 آگهی‌های فعال: {ads.Count} | صفحهٔ {page + 1}", replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task AdminAdDetails(long id, int adId, CancellationToken ct)
    {
        if (!IsAdmin(id)) return;
        var ad = store.Ad(adId);
        if (ad is null) { await bot.SendMessage(id, "آگهی پیدا نشد.", cancellationToken: ct); return; }
        var pending = store.TransactionsForAd(adId).Any(t => t.Status == TransactionStatus.Pending);
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { C("👤 مالک", $"adm_user_{ad.OwnerId}"), C("↩️ آگهی‌ها", "adm_ads_0") }
        };
        if (ad.Status == ListingStatus.Active && !pending)
            rows.Insert(0, [C("⛔ غیرفعال‌سازی آگهی", $"adm_adask_{ad.Id}")]);
        await bot.SendMessage(id, $"📋 آگهی {ListingId(ad)} | کاربر {ad.OwnerId} | {ad.Status} | پیام‌های ثبت‌شده: {store.Shares(ad.Id).Count}" +
            (pending ? "\n🤝 معاملهٔ در جریان دارد؛ ابتدا از بخش معاملات تعیین‌تکلیف کن." : "") + "\n\n" + Card(ad),
            replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task AdminUsers(long id, int page, CancellationToken ct)
    {
        if (!IsAdmin(id)) return;
        var users = store.Users();
        page = Math.Clamp(page, 0, Math.Max(0, (users.Count - 1) / 8));
        var rows = users.Skip(page * 8).Take(8).Select(u => new[]
        { C($"{(u.Suspended ? "⛔" : "👤")} {u.Id} · @{u.Username ?? "بدون‌نام"}", $"adm_user_{u.Id}") }).ToList();
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"adm_users_{page - 1}"));
        if ((page + 1) * 8 < users.Count) navigation.Add(C("بعدی ➡️", $"adm_users_{page + 1}"));
        if (navigation.Count > 0) rows.Add(navigation.ToArray());
        rows.Add([C("🔎 جست‌وجوی شناسهٔ کاربر", "adm_userlookup"), C("↩️ مدیریت", "adm_home")]);
        await bot.SendMessage(id, users.Count == 0 ? "کاربری ثبت نشده است." : $"👤 کاربران: {users.Count} | صفحهٔ {page + 1}",
            replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task AdminUserDetails(long id, long userId, CancellationToken ct)
    {
        if (!IsAdmin(id)) return;
        var user = store.User(userId);
        if (user is null) { await bot.SendMessage(id, "کاربر پیدا نشد.", cancellationToken: ct); return; }
        var ads = store.AdsFor(userId);
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { C("↩️ کاربران", "adm_users_0") }
        };
        if (userId != options.AdminUserId)
            rows.Insert(0, [C(user.Suspended ? "✅ رفع محدودیت" : "⛔ محدودسازی حساب",
                user.Suspended ? $"adm_restore_{userId}" : $"adm_suspendask_{userId}")]);
        await bot.SendMessage(id, $"👤 کاربر {userId} | @{user.Username ?? "بدون‌نام"}\n" +
            $"وضعیت: {(user.Suspended ? "⛔ محدود" : "✅ فعال")} | اعتبار: {user.TrustScore}/100 | امتیاز: {user.Rating:0.0}\n" +
            $"آگهی‌ها: {ads.Count} (فعال: {ads.Count(a => a.Status == ListingStatus.Active)}) | تیکت‌ها: {store.TicketsFor(userId).Count} | گزارش تخلف تأییدشده: {user.ConfirmedReports}",
            replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task CafeteriaMenu(long id, int page, CancellationToken ct)
    {
        if (!IsAdmin(id)) return;
        var all = store.Cafeterias().OrderBy(c => c.Id).ToList();
        page = Math.Clamp(page, 0, Math.Max(0, (all.Count - 1) / 8));
        var rows = all.Skip(page * 8).Take(8).Select(c => new[]
        {
            C($"{(c.Active ? "✅" : "⛔")} {c.Name}", $"adm_toggle_{c.Id}"), C("✏️ تغییر نام", $"adm_rename_{c.Id}")
        }).ToList();
        rows.Add([C("➕ افزودن محل", "adm_cafadd")]);
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"adm_caf_{page - 1}"));
        if ((page + 1) * 8 < all.Count) navigation.Add(C("بعدی ➡️", $"adm_caf_{page + 1}"));
        if (navigation.Count > 0) rows.Add(navigation.ToArray());
        rows.Add([C("↩️ مدیریت", "adm_home")]);
        await bot.SendMessage(id, "📍 محل‌های سلف؛ روی نام بزن تا فعال/غیرفعال شود. نام‌های قدیمی در آگهی‌های قبلی باقی می‌مانند.",
            replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private static InlineKeyboardMarkup ReportButtons(ListingReport report) => new(new[]
    {
        new[] { C("✅ رسیدگی شد", $"adm_report_resolve_{report.Id}"), C("🚫 رد گزارش", $"adm_report_dismiss_{report.Id}") },
        new[] { C("⛔ حذف آگهی", $"adm_report_cancel_{report.Id}"), C("💬 گفتگو با گزارش‌دهنده", $"adm_report_contact_{report.Id}") }
    });

    private async Task ReportList(long id, int page, CancellationToken ct)
    {
        if (!IsAdmin(id)) return;
        var all = store.Reports().Where(r => r.Status == ReportStatus.Open).ToList();
        page = Math.Clamp(page, 0, Math.Max(0, (all.Count - 1) / 8));
        var rows = all.Skip(page * 8).Take(8).Select(r => new[] { C($"🚨 گزارش #{r.Id} | آگهی {ListingId(r.AdvertisementId)}", $"adm_report_{r.Id}") }).ToList();
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"adm_reports_{page - 1}"));
        if ((page + 1) * 8 < all.Count) navigation.Add(C("بعدی ➡️", $"adm_reports_{page + 1}"));
        if (navigation.Count > 0) rows.Add(navigation.ToArray());
        rows.Add([C("↩️ مدیریت", "adm_home")]);
        await bot.SendMessage(id, all.Count == 0 ? "گزارش باز وجود ندارد." : $"🚨 {all.Count} گزارش باز:", replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task TicketList(long id, int page, CancellationToken ct)
    {
        if (!IsAdmin(id)) return;
        var all = store.Tickets().ToList();
        page = Math.Clamp(page, 0, Math.Max(0, (all.Count - 1) / 8));
        var rows = all.Skip(page * 8).Take(8).Select(t => new[]
        { C($"{(t.Status == TicketStatus.Open ? "💬" : "✅")} تیکت #{t.Id} | {t.UserId}", $"adm_ticket_{t.Id}") }).ToList();
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"adm_tickets_{page - 1}"));
        if ((page + 1) * 8 < all.Count) navigation.Add(C("بعدی ➡️", $"adm_tickets_{page + 1}"));
        if (navigation.Count > 0) rows.Add(navigation.ToArray());
        rows.Add([C("↩️ مدیریت", "adm_home")]);
        await bot.SendMessage(id, all.Count == 0 ? "گفتگویی وجود ندارد." : "💬 گفتگوهای پشتیبانی:", replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task PendingTransactions(long id, CancellationToken ct, int page = 0)
    {
        if (!IsAdmin(id)) return;
        var all = store.PendingTransactions();
        page = Math.Clamp(page, 0, Math.Max(0, (all.Count - 1) / 8));
        var rows = all.Skip(page * 8).Take(8).Select(t => new[]
        { C($"❌ لغو معامله #{t.Id} | آگهی {ListingId(t.AdvertisementId)}", $"adm_txn_cancel_{t.Id}") }).ToList();
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"adm_pending_{page - 1}"));
        if ((page + 1) * 8 < all.Count) navigation.Add(C("بعدی ➡️", $"adm_pending_{page + 1}"));
        if (navigation.Count > 0) rows.Add(navigation.ToArray());
        rows.Add([C("↩️ مدیریت", "adm_home")]);
        await bot.SendMessage(id, all.Count == 0 ? "معاملهٔ در جریان وجود ندارد." : "🤝 معاملات در جریان؛ لغو، رزرو را آزاد می‌کند:",
            replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task AdminSettings(long id, CancellationToken ct)
    {
        if (!IsAdmin(id)) return;
        await bot.SendMessage(id, $"⚙️ تنظیمات سامانه\nقیمت کوتاه: هر واحد ۱۰۰۰ تومان\nمنطقهٔ زمانی: {options.TimeZone}\nانقضای صبحانه: {options.BreakfastExpirationTime:HH:mm}\nناهار: {options.LunchExpirationTime:HH:mm}\nشام: {options.DinnerExpirationTime:HH:mm}\nسایر: {options.OtherExpirationTime:HH:mm}\nبازهٔ تکراری: {options.DuplicateWindowMinutes} دقیقه\nفاصلهٔ اعلان: {options.NotificationCooldownMinutes} دقیقه",
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { C("🌍 منطقهٔ زمانی", "adm_setting_timezone") },
                new[] { C("🍳 صبحانه", "adm_setting_breakfast"), C("🍽 ناهار", "adm_setting_lunch"), C("🌙 شام", "adm_setting_dinner"), C("سایر", "adm_setting_other") },
                new[] { C("♻️ بازهٔ تکراری", "adm_setting_duplicate"), C("🔔 فاصلهٔ اعلان", "adm_setting_notification") },
                new[] { C("↩️ مدیریت", "adm_home") }
            }), cancellationToken: ct);
    }

    private async Task<bool> AdminInput(long id, string field, string text, UserSession session, CancellationToken ct)
    {
        if (field == "adminlookup")
        {
            var value = PersianText.Normalize(text).Trim().TrimStart('F', 'B', 'E', 'f', 'b', 'e');
            if (!int.TryParse(value, out var adId) || adId <= 0)
            { await bot.SendMessage(id, "شناسهٔ عددی آگهی را بفرست (مثلاً ۱۲ یا F12).", cancellationToken: ct); return true; }
            session.EditingField = null; store.Save(session);
            await AdminAdDetails(id, adId, ct); return true;
        }
        if (field == "adminuserlookup")
        {
            if (!long.TryParse(PersianText.Normalize(text), out var userId) || userId <= 0)
            { await bot.SendMessage(id, "شناسهٔ عددی کاربر را بفرست.", cancellationToken: ct); return true; }
            session.EditingField = null; store.Save(session);
            await AdminUserDetails(id, userId, ct); return true;
        }
        if (field == "caf_add")
        {
            if (!_admin.AddCafeteria(text)) { await bot.SendMessage(id, "نام محل باید بین ۲ تا ۶۰ نویسه باشد.", cancellationToken: ct); return true; }
            Audit(id, "cafeteria_update", 0, text[..Math.Min(text.Length, 60)]);
            session.EditingField = null; store.Save(session); await CafeteriaMenu(id, 0, ct); return true;
        }
        if (field.StartsWith("caf_rename:", StringComparison.Ordinal) && int.TryParse(field[11..], out var cafeteriaId))
        {
            if (!_admin.RenameCafeteria(cafeteriaId, text)) { await bot.SendMessage(id, "نام تکراری یا نامعتبر است؛ نام دیگری وارد کن.", cancellationToken: ct); return true; }
            Audit(id, "cafeteria_update", cafeteriaId, text[..Math.Min(text.Length, 60)]);
            session.EditingField = null; store.Save(session); await CafeteriaMenu(id, 0, ct); return true;
        }
        if (field.StartsWith("setting:", StringComparison.Ordinal))
        {
            if (!_admin.TryUpdateSetting(field[8..], text)) { await bot.SendMessage(id, "مقدار نامعتبر است؛ برای ساعت HH:mm و برای منطقهٔ زمانی مثلاً Asia/Tehran وارد کن.", cancellationToken: ct); return true; }
            Audit(id, "setting_update", 0, $"{field[8..]}: {text[..Math.Min(text.Length, 60)]}");
            session.EditingField = null; store.Save(session); await AdminSettings(id, ct); return true;
        }
        return false;
    }

    private async Task<bool> AdminCallback(long id, string data, UserSession session, CancellationToken ct)
    {
        if (!IsAdmin(id) || !data.StartsWith("adm_", StringComparison.Ordinal)) return false;
        if (data == "adm_home") { await AdminMenu(id, ct); return true; }
        if (data.StartsWith("adm_audit_", StringComparison.Ordinal) && int.TryParse(data[10..], out var auditPage))
        { await AdminAuditLog(id, auditPage, ct); return true; }
        if (data.StartsWith("adm_ads_", StringComparison.Ordinal) && int.TryParse(data[8..], out var adsPage))
        { await AdminAds(id, adsPage, ct); return true; }
        if (data == "adm_lookup")
        {
            session.EditingField = "adminlookup"; store.Save(session);
            await bot.SendMessage(id, "شناسهٔ آگهی را بفرست (برای خروج /cancel).", cancellationToken: ct); return true;
        }
        if (data.StartsWith("adm_adask_", StringComparison.Ordinal) && int.TryParse(data[10..], out var askAdId))
        {
            var ad = store.Ad(askAdId);
            if (ad?.Status != ListingStatus.Active)
            { await bot.SendMessage(id, "این آگهی دیگر فعال نیست.", cancellationToken: ct); return true; }
            await bot.SendMessage(id, $"آگهی {ListingId(askAdId)} غیرفعال شود؟ این اقدام از جست‌وجو حذفش می‌کند و به مالک اطلاع می‌دهد.",
                replyMarkup: new InlineKeyboardMarkup(new[] { new[] { C("⛔ تأیید غیرفعال‌سازی", $"adm_adcancel_{askAdId}"), C("↩️ انصراف", $"adm_ad_{askAdId}") } }), cancellationToken: ct);
            return true;
        }
        if (data.StartsWith("adm_adcancel_", StringComparison.Ordinal) && int.TryParse(data[13..], out var cancelledAdId))
        {
            if (!market.CancelListing(cancelledAdId))
            { await bot.SendMessage(id, "آگهی فعال نیست یا معاملهٔ در جریان دارد؛ ابتدا معامله را تعیین‌تکلیف کن.", cancellationToken: ct); return true; }
            Audit(id, "ad_disable", cancelledAdId);
            await EditShares(cancelledAdId, ct);
            await bot.SendMessage(id, $"⛔ آگهی {ListingId(cancelledAdId)} غیرفعال شد.", cancellationToken: ct);
            try { await bot.SendMessage(store.Ad(cancelledAdId)!.OwnerId, $"⛔ آگهی {ListingId(cancelledAdId)} توسط مدیر غیرفعال شد. برای پیگیری /support را بزن.", cancellationToken: ct); }
            catch (Exception ex) { Log(ex); }
            return true;
        }
        if (data.StartsWith("adm_ad_", StringComparison.Ordinal) && int.TryParse(data[7..], out var adDetailsId))
        { await AdminAdDetails(id, adDetailsId, ct); return true; }
        if (data.StartsWith("adm_users_", StringComparison.Ordinal) && int.TryParse(data[10..], out var usersPage))
        { await AdminUsers(id, usersPage, ct); return true; }
        if (data == "adm_userlookup")
        {
            session.EditingField = "adminuserlookup"; store.Save(session);
            await bot.SendMessage(id, "شناسهٔ عددی کاربر را بفرست (برای خروج /cancel).", cancellationToken: ct); return true;
        }
        if (data.StartsWith("adm_suspendask_", StringComparison.Ordinal) && long.TryParse(data[15..], out var askUserId))
        {
            await bot.SendMessage(id, $"حساب کاربر {askUserId} محدود شود؟ آگهی‌هایش از جست‌وجو پنهان می‌شوند؛ معاملات جاری را همچنان می‌تواند تکمیل کند.",
                replyMarkup: new InlineKeyboardMarkup(new[] { new[] { C("⛔ تأیید محدودسازی", $"adm_suspend_{askUserId}"), C("↩️ انصراف", $"adm_user_{askUserId}") } }), cancellationToken: ct);
            return true;
        }
        if (data.StartsWith("adm_suspend_", StringComparison.Ordinal) && long.TryParse(data[12..], out var suspendedId) ||
            data.StartsWith("adm_restore_", StringComparison.Ordinal) && long.TryParse(data[12..], out suspendedId))
        {
            var suspended = data.StartsWith("adm_suspend_", StringComparison.Ordinal);
            if (!_admin.SetUserSuspended(id, suspendedId, suspended))
            { await bot.SendMessage(id, "تغییر وضعیت کاربر مجاز نیست.", cancellationToken: ct); return true; }
            foreach (var ad in store.AdsFor(suspendedId).Where(a => a.Status == ListingStatus.Active)) await EditShares(ad.Id, ct);
            await AdminUserDetails(id, suspendedId, ct);
            try { await bot.SendMessage(suspendedId, suspended
                ? "⛔ حساب شما موقتاً محدود شد. برای پیگیری /support را بزنید. معاملات در جریان قابل تکمیل‌اند."
                : "✅ محدودیت حساب شما برداشته شد.", cancellationToken: ct); }
            catch (Exception ex) { Log(ex); }
            return true;
        }
        if (data.StartsWith("adm_user_", StringComparison.Ordinal) && long.TryParse(data[9..], out var userDetailsId))
        { await AdminUserDetails(id, userDetailsId, ct); return true; }
        if (data.StartsWith("adm_groups_", StringComparison.Ordinal) && int.TryParse(data[11..], out var groupPage))
        { await AdminGroups(id, groupPage, ct); return true; }
        if (data.StartsWith("adm_group_info_", StringComparison.Ordinal) && long.TryParse(data[15..], out var groupInfoId))
        {
            var group = store.Group(groupInfoId);
            if (group is not null)
                await bot.SendMessage(id, $"👥 {group.Title}\nشناسه: {group.Id}\nوضعیت: {(group.Active ? "فعال" : "غیرفعال")}\nنصب‌کننده: {group.InstalledBy}\nتاریخ ثبت: {group.InstalledUtc:yyyy/MM/dd HH:mm} UTC\nپیام‌های ثبت‌شده: {store.SharesForGroup(group.Id).Count}", cancellationToken: ct);
            return true;
        }
        if (data.StartsWith("adm_group_offask_", StringComparison.Ordinal) && long.TryParse(data[17..], out var askGroupId))
        {
            await bot.SendMessage(id, $"گروه {store.Group(askGroupId)?.Title ?? askGroupId.ToString()} غیرفعال شود؟ پیام‌های قبلی هم بدون دکمه می‌شوند.",
                replyMarkup: new InlineKeyboardMarkup(new[] { new[] { C("⛔ بله، حذف نصب", $"adm_group_off_{askGroupId}"), C("↩️ انصراف", "adm_groups_0") } }), cancellationToken: ct);
            return true;
        }
        if (data.StartsWith("adm_group_off_", StringComparison.Ordinal) && long.TryParse(data[14..], out var disabledGroupId))
        {
            if (store.Group(disabledGroupId) is { Active: true } disabled)
            {
                disabled.Active = false; store.Save(disabled);
                Audit(id, "group_disable", disabledGroupId);
                await DisableGroupShares(disabledGroupId, ct);
            }
            await AdminGroups(id, 0, ct);
            return true;
        }
        if (data == "adm_cafadd")
        {
            session.EditingField = "caf_add"; store.Save(session);
            await bot.SendMessage(id, "نام محل جدید را بفرست (برای لغو /cancel).", cancellationToken: ct); return true;
        }
        if (data.StartsWith("adm_caf_", StringComparison.Ordinal) && int.TryParse(data[8..], out var cafeteriaPage))
        { await CafeteriaMenu(id, cafeteriaPage, ct); return true; }
        if (data.StartsWith("adm_toggle_", StringComparison.Ordinal) && int.TryParse(data[11..], out var toggleId))
        {
            var found = store.Cafeterias().FirstOrDefault(c => c.Id == toggleId);
            if (found is not null && store.SetCafeteriaActive(toggleId, !found.Active)) Audit(id, "cafeteria_update", toggleId, found.Active ? "غیرفعال" : "فعال");
            await CafeteriaMenu(id, 0, ct); return true;
        }
        if (data.StartsWith("adm_rename_", StringComparison.Ordinal) && int.TryParse(data[11..], out var renameId))
        {
            session.EditingField = $"caf_rename:{renameId}"; store.Save(session);
            await bot.SendMessage(id, "نام جدید محل را بفرست (برای لغو /cancel).", cancellationToken: ct); return true;
        }
        if (data.StartsWith("adm_tickets_", StringComparison.Ordinal) && int.TryParse(data[12..], out var ticketPage))
        { await TicketList(id, ticketPage, ct); return true; }
        if (data.StartsWith("adm_pending_", StringComparison.Ordinal) && int.TryParse(data[12..], out var pendingPage))
        { await PendingTransactions(id, ct, pendingPage); return true; }
        if (data.StartsWith("adm_txn_cancel_", StringComparison.Ordinal) && int.TryParse(data[15..], out var pendingId))
        {
            try
            {
                var t = market.CancelTransaction(pendingId, id);
                Audit(id, "transaction_cancel", pendingId);
                await bot.SendMessage(id, $"معامله #{t.Id} لغو شد.", cancellationToken: ct);
                foreach (var participant in new[] { t.OwnerId, t.CounterpartyId })
                    try { await bot.SendMessage(participant, $"❌ معامله #{t.Id} توسط مدیر لغو شد. برای پیگیری /support را بزنید.", cancellationToken: ct); }
                    catch (Exception ex) { Log(ex); }
            }
            catch (InvalidOperationException ex) { await bot.SendMessage(id, ex.Message, cancellationToken: ct); }
            return true;
        }
        if (data.StartsWith("adm_ticket_", StringComparison.Ordinal) && int.TryParse(data[11..], out var ticketId))
        {
            if (store.Ticket(ticketId) is { } ticket) await ShowTicket(id, ticket, ct);
            return true;
        }
        if (data.StartsWith("adm_reports_", StringComparison.Ordinal) && int.TryParse(data[12..], out var reportPage))
        { await ReportList(id, reportPage, ct); return true; }
        if (data.StartsWith("adm_report_contact_", StringComparison.Ordinal) && int.TryParse(data[19..], out var contactReportId))
        {
            var report = store.Report(contactReportId);
            if (report is not null) await ShowTicket(id, _support.Open(report.ReporterId), ct);
            return true;
        }
        foreach (var (prefix, status) in new[] { ("adm_report_resolve_", ReportStatus.Resolved), ("adm_report_dismiss_", ReportStatus.Dismissed), ("adm_report_cancel_", ReportStatus.Resolved) })
        {
            if (!data.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(data[prefix.Length..], out var reportId)) continue;
            var report = store.Report(reportId);
            if (report is null || report.Status != ReportStatus.Open) { await bot.SendMessage(id, "گزارش قبلاً رسیدگی شده است.", cancellationToken: ct); return true; }
            if (prefix == "adm_report_cancel_")
            {
                if (!market.RemoveReportedListing(report.AdvertisementId))
                {
                    await bot.SendMessage(id, "آگهی فعال نیست یا معاملهٔ در جریان دارد؛ ابتدا وضعیت معامله را تعیین کنید.", cancellationToken: ct);
                    return true;
                }
                await EditShares(report.AdvertisementId, ct);
                Audit(id, "report_remove", report.AdvertisementId);
                var ad = store.Ad(report.AdvertisementId)!;
                try { await bot.SendMessage(ad.OwnerId, $"⛔ آگهی {ListingId(ad)} پس از بررسی گزارش غیرفعال شد. برای پیگیری /support را بزنید.", cancellationToken: ct); }
                catch (Exception ex) { Log(ex); }
            }
            report.Status = status; store.Update(report);
            Audit(id, status == ReportStatus.Dismissed ? "report_dismiss" : "report_resolve", report.Id);
            await bot.SendMessage(id, "وضعیت گزارش ثبت شد.", cancellationToken: ct);
            try { await bot.SendMessage(report.ReporterId, $"نتیجهٔ بررسی گزارش #{report.Id}: {(status == ReportStatus.Dismissed ? "رد شد" : "رسیدگی شد")}. برای پیگیری /support را بزنید.", cancellationToken: ct); }
            catch (Exception ex) { Log(ex); }
            return true;
        }
        if (data.StartsWith("adm_report_", StringComparison.Ordinal) && int.TryParse(data[11..], out var viewedReportId))
        {
            var report = store.Report(viewedReportId);
            if (report is not null)
                await bot.SendMessage(id, $"🚨 گزارش #{report.Id} | آگهی {ListingId(report.AdvertisementId)} | {report.Status}\nگزارش‌دهنده: {report.ReporterId}\n{report.Reason}\n\n{(store.Ad(report.AdvertisementId) is { } reportedAd ? Card(reportedAd) : "آگهی یافت نشد")}",
                    replyMarkup: report.Status == ReportStatus.Open ? ReportButtons(report) : null, cancellationToken: ct);
            return true;
        }
        if (data == "adm_settings") { await AdminSettings(id, ct); return true; }
        if (data.StartsWith("adm_setting_", StringComparison.Ordinal))
        {
            session.EditingField = "setting:" + data[12..]; store.Save(session);
            await bot.SendMessage(id, "مقدار جدید را بفرست. ساعت: HH:mm؛ فاصله‌ها: عدد صحیح؛ منطقهٔ زمانی: Asia/Tehran. لغو: /cancel", cancellationToken: ct);
            return true;
        }
        return true;
    }

    private async Task Publish(UserSession session, Advertisement ad, int? updateId, CancellationToken ct)
    {
        try { market.Publish(ad, updateId); }
        catch (InvalidOperationException ex) { await bot.SendMessage(ad.OwnerId, ex.Message, replyMarkup: PreviewButtons(ad), cancellationToken: ct); return; }
        var previewMessageId = session.PreviewMessageId;
        var inlineDraftToken = session.InlineDraftToken;
        session.DraftJson = null; session.DuplicateId = null; session.PreviewMessageId = null;
        session.InlineDraftToken = null;
        session.UpdatingAdvertisementId = null;
        store.Save(session);
        var updated = false;
        if (previewMessageId is { } messageId)
        {
            try
            {
                await bot.EditMessageText(ad.OwnerId, messageId, $"✅ آگهی با شناسه {ListingId(ad)} ثبت شد.\n\n{Card(ad)}",
                    replyMarkup: OwnerButtons(ad), cancellationToken: ct);
                updated = true;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) { Log(ex); }
        }
        if (!updated) await bot.SendMessage(ad.OwnerId, Card(ad), replyMarkup: OwnerButtons(ad), cancellationToken: ct);
        await SyncInlineDraft(ad.OwnerId, inlineDraftToken, ad, ct);
        if (updateId.HasValue) await EditShares(ad.Id, ct);
        foreach (var (other, score) in market.Matches(ad).Where(m => m.Score >= 40 && market.CanNotify(m.Ad)).Take(10))
        {
            if (store.User(other.OwnerId)?.Onboarded != true) continue;
            try
            {
                await bot.SendMessage(other.OwnerId, $"🔔 یک آگهی جدید با درخواست شما مطابقت دارد.\n{Card(ad)}",
                    replyMarkup: new InlineKeyboardMarkup(new[] { new[] { Link("مشاهده آگهی", $"deal_{ad.Id}") } }), cancellationToken: ct);
                var recipient = store.User(other.OwnerId)!;
                recipient.LastNotificationUtc = market.UtcNow; store.Save(recipient);
            }
            catch (Exception ex) { Log(ex); }
        }
    }

    private async Task<bool> MemberOfGroup(long groupId, long userId, CancellationToken ct)
    {
        try
        {
            var member = await bot.GetChatMember(groupId, userId, ct);
            return member.Status is ChatMemberStatus.Creator or ChatMemberStatus.Administrator or ChatMemberStatus.Member ||
                member is Telegram.Bot.Types.ChatMemberRestricted { IsMember: true };
        }
        catch (Exception ex) { Log(ex); return false; }
    }

    private async Task GroupPicker(long userId, int adId, int page, CancellationToken ct)
    {
        var ad = store.Ad(adId);
        if (ad?.OwnerId != userId || ad.Status != ListingStatus.Active || market.IsExpired(ad))
        { await bot.SendMessage(userId, "آگهی فعال برای انتشار پیدا نشد.", cancellationToken: ct); return; }
        var groups = store.InstalledGroups();
        if (groups.Count == 0)
        { await bot.SendMessage(userId, "هنوز گروه مجازی نصب نشده است؛ ادمین اصلی باید در گروه «نصب» بنویسد.", cancellationToken: ct); return; }
        page = Math.Clamp(page, 0, Math.Max(0, (groups.Count - 1) / 8));
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var group in groups.Skip(page * 8).Take(8))
            if (await MemberOfGroup(group.Id, userId, ct)) rows.Add([C($"📢 {group.Title}", $"post_{adId}_{group.Id}")]);
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"group_page_{adId}_{page - 1}"));
        if ((page + 1) * 8 < groups.Count) navigation.Add(C("بعدی ➡️", $"group_page_{adId}_{page + 1}"));
        if (navigation.Count > 0) rows.Add(navigation.ToArray());
        await bot.SendMessage(userId, rows.Count == 0 ? "در این صفحه گروهی که عضوش باشی پیدا نشد. ربات باید ادمین گروه باشد تا عضویت را بررسی کند." :
            "گروهی را که عضوش هستی انتخاب کن؛ ربات فقط در گروه‌های نصب‌شده منتشر می‌کند:",
            replyMarkup: rows.Count > 0 ? new InlineKeyboardMarkup(rows) : null, cancellationToken: ct);
    }

    private async Task PostInGroup(long userId, int adId, long groupId, CancellationToken ct)
    {
        var ad = store.Ad(adId);
        if (ad?.OwnerId != userId || ad.Status != ListingStatus.Active || market.IsExpired(ad) || !_groups.IsInstalled(groupId))
        { await bot.SendMessage(userId, "آگهی یا گروه برای انتشار فعال نیست.", cancellationToken: ct); return; }
        if (!await MemberOfGroup(groupId, options.AdminUserId, ct))
        {
            _groups.Deactivate(groupId);
            await bot.SendMessage(userId, "ادمین اصلی دیگر عضو این گروه نیست یا بررسی عضویتش ممکن نیست. او باید دوباره در گروه «نصب» بنویسد.", cancellationToken: ct);
            return;
        }
        if (!await MemberOfGroup(groupId, userId, ct))
        { await bot.SendMessage(userId, "باید عضو گروه باشی و ربات برای بررسی عضویت ادمین گروه باشد.", cancellationToken: ct); return; }
        try
        {
            var old = store.Shares(adId).FirstOrDefault(s => s.GroupChatId == groupId && s.GroupMessageId.HasValue && !s.GroupSearchResult);
            if (old is not null)
            {
                try
                {
                    await bot.EditMessageText(groupId, old.GroupMessageId!.Value, Card(ad), replyMarkup: CardButtons(ad), cancellationToken: ct);
                    await bot.SendMessage(userId, "♻️ پیام آگهی در همان گروه بروزرسانی شد.", cancellationToken: ct);
                    return;
                }
                catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
                {
                    await bot.SendMessage(userId, "آگهی قبلاً در این گروه منتشر شده است.", cancellationToken: ct);
                    return;
                }
                catch (Telegram.Bot.Exceptions.ApiRequestException) { /* The previous message may have been deleted. */ }
            }
            var sent = await bot.SendMessage(groupId, Card(ad), replyMarkup: CardButtons(ad), cancellationToken: ct);
            if (old is null) store.Save(new SharedMessage { AdvertisementId = adId, GroupChatId = groupId, GroupMessageId = sent.MessageId });
            else { old.GroupMessageId = sent.MessageId; store.Update(old); }
            await bot.SendMessage(userId, $"✅ آگهی در گروه «{store.Group(groupId)?.Title}» منتشر شد.", cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
        {
            Log(ex);
            await bot.SendMessage(userId, "انتشار ممکن نشد. مطمئن شو ربات عضو و ادمین گروه است و اجازهٔ ارسال پیام دارد.", cancellationToken: ct);
        }
    }

    private async Task DisableGroupShares(long groupId, CancellationToken ct)
    {
        foreach (var share in store.SharesForGroup(groupId))
        {
            var ad = store.Ad(share.AdvertisementId);
            if (ad is null || share.GroupMessageId is null) continue;
            try { await bot.EditMessageText(groupId, share.GroupMessageId.Value,
                "⛔ این گروه دیگر فعال نیست.\n" + Card(ad), cancellationToken: ct); }
            catch (Exception ex) { Log(ex); }
        }
    }

    private async Task CleanUpOldListings(CancellationToken ct)
    {
        market.Expire();
        foreach (var ad in store.AllAds().Where(a => a.Status is ListingStatus.Expired or ListingStatus.Cancelled))
            await CleanUpShares(ad, ct);
    }

    private async Task CleanUpShares(Advertisement ad, CancellationToken ct)
    {
        foreach (var share in store.Shares(ad.Id).Where(s => !s.CleanupCompleted))
        {
            try
            {
                if (share.GroupChatId is { } groupId && share.GroupMessageId is { } messageId)
                {
                    try { await bot.DeleteMessage(groupId, messageId, ct); }
                    catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (
                        ex.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase)) { /* Already removed. */ }
                    catch (Telegram.Bot.Exceptions.ApiRequestException)
                    {
                        // Telegram does not delete messages older than 48 hours. Keep a visibly
                        // inactive card without buttons if the old message can still be edited.
                        try { await bot.EditMessageText(groupId, messageId, InlineCard(ad), parseMode: ParseMode.Html, cancellationToken: ct); }
                        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (
                            ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)) { }
                    }
                }
                else if (share.InlineMessageId.Length > 0)
                {
                    // Telegram offers no delete API for inline messages: deactivate their cards.
                    try { await bot.EditMessageText(inlineMessageId: share.InlineMessageId,
                        text: share.CompactInlineCard ? CompactInlineCard(ad) : InlineCard(ad), parseMode: ParseMode.Html,
                        cancellationToken: ct); }
                    catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (
                        ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)) { }
                }
                share.CleanupCompleted = true;
                store.Update(share);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log(ex); }
        }
    }

    private async Task EditShares(int id, CancellationToken ct)
    {
        var ad = store.Ad(id)!;
        if (ad.Status is ListingStatus.Expired or ListingStatus.Cancelled)
        {
            await CleanUpShares(ad, ct);
            return;
        }
        foreach (var share in store.Shares(id))
            try
            {
                if (share.GroupChatId is { } groupId && share.GroupMessageId is { } messageId)
                    await bot.EditMessageText(groupId, messageId,
                        _groups.IsInstalled(groupId) ? InlineCard(ad) : "⛔ این گروه دیگر فعال نیست.\n" + InlineCard(ad),
                        parseMode: ParseMode.Html,
                        replyMarkup: _groups.IsInstalled(groupId) ? CardButtons(ad) : null, cancellationToken: ct);
                else if (share.InlineMessageId.Length > 0)
                    await bot.EditMessageText(inlineMessageId: share.InlineMessageId,
                        text: share.CompactInlineCard ? CompactInlineCard(ad) : InlineCard(ad), parseMode: ParseMode.Html,
                        replyMarkup: CardButtons(ad), cancellationToken: ct);
            }
            catch (Exception ex) { Log(ex); }
    }

    private async Task Inline(InlineQuery inline, CancellationToken ct)
    {
        var scope = GroupAccess.GetInlineScope(inline.ChatType);
        if (scope == InlineScope.None || store.User(inline.From.Id)?.Suspended == true)
        {
            await bot.AnswerInlineQuery(inline.Id, Array.Empty<InlineQueryResult>(), cacheTime: 0, isPersonal: true, cancellationToken: ct);
            return;
        }
        var results = new List<InlineQueryResult>();
        var query = inline.Query.Trim();
        if (scope == InlineScope.PrivateListings && query.StartsWith("id:", StringComparison.Ordinal) && int.TryParse(query[3..], out var id))
        {
            var own = store.Ad(id);
            if (own is not null && own.OwnerId == inline.From.Id && !market.IsExpired(own))
                results.Add(new InlineQueryResultArticle($"ad_{id}", Header(own), new InputTextMessageContent(InlineCard(own)) { ParseMode = ParseMode.Html })
                { Description = "کارت آگهی من و مدیریت وضعیت", ReplyMarkup = CardButtons(own, inline.From.Id) });
        }
        else
        {
            var parsed = await parser.ParseAsync(query, ct);
            var draftToken = query.Length > 0 ? SavePrefill(query) : null;
            if (query.Length > 0)
            {
                var needsDraft = parsed.ListingType.Value != ListingType.Unknown || parsed.Price.Value.HasValue ||
                    parsed.PossibleSensitiveCode.Value is not null;
                var previewLink = $"https://t.me/{_username}?start={(needsDraft ? "draft" : "search")}_{draftToken}";
                results.Add(new InlineQueryResultArticle($"preview_{draftToken}", InlineListingPreview.Title(parsed),
                    new InputTextMessageContent(InlineListingPreview.Format(parsed, options, draftToken)))
                {
                    Description = "👁 اول بررسی کن؛ انتشار پس از تأیید در خصوصی",
                    ReplyMarkup = new InlineKeyboardMarkup(new[] { new[] { needsDraft && draftToken is not null
                        ? C("📝 بررسی و تأیید در خصوصی", InlineDraftAction.Preview(inline.From.Id, draftToken))
                        : InlineKeyboardButton.WithUrl("🔍 جستجو در خصوصی", previewLink) } })
                });
            }
            else
            {
                var home = $"https://t.me/{_username}?start=home";
                results.Add(new InlineQueryResultArticle("preview", "👁 بازار غذای دانشگاه",
                    new InputTextMessageContent("🍽 بازار غذای دانشگاه\nبرای خرید، فروش، معاوضه یا جستجو دکمه را بزن."))
                { Description = "برای جستجو نام غذا یا وعده را بنویس", ReplyMarkup = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithUrl("🏠 باز کردن ربات", home) } }) });
            }
            foreach (var (type, chosenType) in new[] { ("sell", ListingType.Sell), ("buy", ListingType.Buy), ("exchange", ListingType.Exchange) })
            {
                var link = $"https://t.me/{_username}?start=create_{type}" + (draftToken is null ? "" : "_" + draftToken);
                var similar = $"https://t.me/{_username}?start=" + (draftToken is null ? "home" : "search_" + draftToken);
                var profile = $"https://t.me/{_username}?start=profile_{inline.From.Id}";
                results.Add(new InlineQueryResultArticle(draftToken is null ? $"create_{type}" : $"create_{type}_{draftToken}", InlineListingPreview.CreationTitle(chosenType, parsed),
                    new InputTextMessageContent(InlineListingPreview.FormatCreation(chosenType, parsed, options, draftToken)))
                {
                    Description = "اطلاعات شناخته‌شده پیش‌پر می‌شود؛ تأیید در خصوصی",
                    ReplyMarkup = new InlineKeyboardMarkup(new[]
                    {
                        new[] { draftToken is null ? InlineKeyboardButton.WithUrl("✏️ تکمیل و تأیید", link)
                            : C("✏️ تکمیل و تأیید", InlineDraftAction.Create(chosenType, inline.From.Id, draftToken)) },
                        new[] { InlineKeyboardButton.WithUrl("🔍 موارد مشابه", similar), InlineKeyboardButton.WithUrl("⭐ امتیاز آگهی‌دهنده", profile) }
                    })
                });
            }
            foreach (var ad in market.Search(parsed).Take(20))
                results.Add(new InlineQueryResultArticle($"ad_{ad.Id}", Header(ad), new InputTextMessageContent(InlineCard(ad)) { ParseMode = ParseMode.Html })
                { Description = $"{GenderName(ad.Gender)} · {MealName(ad.Meal)} · {PriceName(ad.Price)}",
                  ReplyMarkup = CardButtons(ad, inline.From.Id) });
        }
        await bot.AnswerInlineQuery(inline.Id, results, cacheTime: 0, isPersonal: true, cancellationToken: ct);
    }

    private string SavePrefill(string text)
    {
        var normalized = PersianText.Normalize(text);
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
        store.Save(new InlinePrefill { Id = token, Food = normalized });
        return token;
    }

    private static string? InlineDraftTokenFromResultId(string resultId)
    {
        if (resultId.StartsWith("preview_", StringComparison.Ordinal))
        {
            var token = resultId[8..];
            return token.Length == 16 && token.All(Uri.IsHexDigit) ? token : null;
        }
        var parts = resultId.Split('_', 3);
        if (parts.Length == 3 && parts[0] == "create" && (parts[1] is "sell" or "buy" or "exchange") &&
            parts[2].Length == 16 && parts[2].All(Uri.IsHexDigit))
            return parts[2];
        return null;
    }

    private async Task SyncInlineDraft(long ownerId, string? token, Advertisement ad, CancellationToken ct)
    {
        if (token is null) return;
        foreach (var message in store.InlineDraftMessages(ownerId, token).Where(m =>
            m.PublishedAdvertisementId is null || m.PublishedAdvertisementId == ad.Id))
        {
            if (message.PublishedAdvertisementId is null)
            {
                message.PublishedAdvertisementId = ad.Id;
                store.Update(message);
            }
            var applied = false;
            try
            {
                var text = $"✅ آگهی ثبت شد\n{CompactInlineCard(ad)}";
                var markup = CardButtons(ad, ad.OwnerId, compact: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await bot.EditMessageText(inlineMessageId: message.InlineMessageId, text: text, parseMode: ParseMode.Html,
                    replyMarkup: markup, cancellationToken: timeout.Token);
                applied = true;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase)) { applied = true; }
            catch (Exception ex) { Log(ex); }
            if (applied)
            {
                message.Finalized = true;
                store.Update(message);
                if (!store.Shares(ad.Id).Any(s => s.InlineMessageId == message.InlineMessageId))
                    store.Save(new SharedMessage { AdvertisementId = ad.Id, InlineMessageId = message.InlineMessageId,
                        CompactInlineCard = true });
            }
        }
    }
}
