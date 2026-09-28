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

    public async Task RunAsync(CancellationToken ct)
    {
        _username = (await bot.GetMe(ct)).Username ?? throw new InvalidOperationException("نام کاربری ربات لازم است.");
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        bot.StartReceiving(HandleUpdate, HandleError, receiverOptions: new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.InlineQuery,
                UpdateType.ChosenInlineResult, UpdateType.MyChatMember, UpdateType.ChatMember]
        }, cancellationToken: ct);
        while (await timer.WaitForNextTickAsync(ct))
            foreach (var id in market.Expire()) await EditShares(id, ct);
    }

    private Task HandleError(ITelegramBotClient _, Exception error, CancellationToken __)
    {
        Log(error);
        return Task.CompletedTask;
    }

    private async Task HandleUpdate(ITelegramBotClient _, Update update, CancellationToken ct)
    {
        await _updates.WaitAsync(ct);
        try
        {
            if (update.InlineQuery is { } inline) await Inline(inline, ct);
            else if (update.ChosenInlineResult is { InlineMessageId: { } inlineId } chosen &&
                     chosen.ResultId.StartsWith("ad_", StringComparison.Ordinal) &&
                     int.TryParse(chosen.ResultId[3..], out var adId))
                store.Save(new SharedMessage { AdvertisementId = adId, InlineMessageId = inlineId });
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
    private static string DateName(Advertisement ad) => ad.DateRange is { } range ? $"{range.Start:yyyy/MM/dd} تا {range.End:yyyy/MM/dd}" : ad.Date?.ToString("yyyy/MM/dd") ?? "نامشخص";
    private static string PriceName(long? price) => price.HasValue ? PersianText.Digits(price.Value) + " تومان" : "توافقی / نامشخص";
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
        else if (ad.Status == ListingStatus.Sold) s.AppendLine(ad.Type == ListingType.Sell ? "✅ فروخته شد" : "✅ تکمیل شد");
        else s.AppendLine(Header(ad));
        if (ad.Type == ListingType.Exchange)
            s.AppendLine($"🍛 می‌دهم: {ad.OfferedFood}").AppendLine($"🔁 می‌خواهم: {ad.WantedFood}");
        else if (ad.FoodName is null) s.AppendLine("🍛 نوع غذا: فرقی ندارد");
        s.AppendLine($"سلف: {GenderName(ad.Gender)}{(ad.Location is null ? "" : " | " + ad.Location)}");
        if (ad.Meal != MealType.Unknown) s.AppendLine($"🍽 {MealName(ad.Meal)}");
        if (ad.Price.HasValue) s.AppendLine($"💵 {(ad.Type == ListingType.Exchange ? "تفاوت قیمت: " : "")}{PriceName(ad.Price)}");
        s.AppendLine($"📅 {DateName(ad)}");
        s.AppendLine($"👤 {(ad.OwnerUsername is null ? "کاربر" : "@" + ad.OwnerUsername)}");
        if (user is not null) s.AppendLine($"⭐ {(user.Rating == 0 ? "بدون امتیاز" : user.Rating.ToString("0.0"))} | 🛡 {user.TrustScore}/100 | ✅ {user.SuccessfulTransactions} معامله");
        s.Append($"🆔 #{(ad.Type == ListingType.Buy ? "B" : ad.Type == ListingType.Sell ? "F" : "E")}{ad.Id}");
        return s.ToString();
    }

    private string InlineCard(Advertisement ad)
    {
        var card = Card(ad);
        if (ad.Status is not (ListingStatus.Sold or ListingStatus.Expired or ListingStatus.Cancelled)) return WebUtility.HtmlEncode(card);
        var newline = card.IndexOf('\n');
        return $"<s>{WebUtility.HtmlEncode(Header(ad))}</s>\n" + WebUtility.HtmlEncode(card[..newline] + card[newline..]);
    }

    private InlineKeyboardMarkup CardButtons(Advertisement ad) => new(new[]
    {
        new[] { Link(ad.Type == ListingType.Exchange ? "🔄 پیشنهاد معاوضه" : ad.Type == ListingType.Buy ? "💰 پیشنهاد فروش" : "🛒 خرید", $"deal_{ad.Id}"), Link("👤 اعتبار کاربر", $"profile_{ad.OwnerId}") },
        new[] { Link("🚨 گزارش", $"report_{ad.Id}") }
    });

    private static InlineKeyboardMarkup OwnerButtons(Advertisement ad) => new(new[]
    {
        new[] { C("📢 انتشار در گروه", $"groups_{ad.Id}") },
        ad.Type == ListingType.Sell
            ? new[] { C("✅ فروخته شد", $"sold_{ad.Id}"), C(ad.MatchNotifications ? "🔕 قطع اعلان" : "🔔 اعلان مورد مناسب", $"notify_{ad.Id}") }
            : new[] { C(ad.MatchNotifications ? "🔕 قطع اعلان" : "🔔 اعلان مورد مناسب", $"notify_{ad.Id}") }
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

    private static InlineKeyboardMarkup PreviewButtons(Advertisement ad) => new(new[]
    {
        new[] { C("✅ درسته، منتشر کن", "publish") },
        new[] { C("✏️ نوع", "edit_type"), C("✏️ غذا", "edit_food"), C("✏️ قیمت", "edit_price") },
        new[] { C("✏️ سلف", "edit_gender"), C("✏️ محل", "edit_location"), C("✏️ وعده", "edit_meal") },
        new[] { C("✏️ تاریخ", "edit_date"), C("❌ لغو", "cancel") }
    });

    private string Preview(Advertisement ad)
    {
        var valid = Marketplace.IsValid(ad);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(market.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone)));
        var dateLabel = ad.Date == today ? "امروز" : DateName(ad);
        return $"📝 اطلاعات آگهی را این‌طور متوجه شدم:\n\n" +
            $"{Header(ad)}\n" + (ad.Type == ListingType.Exchange ? $"🍛 می‌دهم: {ad.OfferedFood ?? "؟"}\n🔁 می‌خواهم: {ad.WantedFood ?? "؟"}\n" : "") +
            $"🍽 وعده: {MealName(ad.Meal)}\nسلف: {GenderName(ad.Gender)}\n📍 محل: {ad.Location ?? "نامشخص"}\n" +
            $"💵 {(ad.Type == ListingType.Exchange ? "تفاوت قیمت" : "قیمت")}: {PriceName(ad.Type == ListingType.Exchange ? ad.OptionalPriceDifference : ad.Price)}{(ad.Price.HasValue ? " (لطفاً تأیید کنید)" : "")}\n📅 تاریخ: {dateLabel}\n" +
            (valid ? "لطفاً جزئیات را تأیید کنید." : "برای ثبت، نوع آگهی و غذا یا وعده را مشخص کنید (در معاوضه هر دو غذا لازم‌اند). ");
    }

    private static Advertisement? Draft(UserSession session) => session.DraftJson is null ? null : JsonSerializer.Deserialize<Advertisement>(session.DraftJson);
    private void SaveDraft(UserSession session, Advertisement ad)
    {
        session.DraftJson = JsonSerializer.Serialize(ad);
        store.Save(session);
    }

    private async Task Message(Message msg, CancellationToken ct)
    {
        if (msg.From is null) return;
        if (msg.Chat.Type != ChatType.Private)
        {
            if (string.IsNullOrWhiteSpace(msg.Text)) return;
            var command = _groups.Handle(msg.From.Id, msg.Chat.Id, msg.Chat.Type, msg.Chat.Title, msg.Text);
            if (command is GroupCommandResult.Installed or GroupCommandResult.Uninstalled)
            {
                if (command == GroupCommandResult.Uninstalled) await DisableGroupShares(msg.Chat.Id, ct);
                else foreach (var adId in store.SharesForGroup(msg.Chat.Id).Select(s => s.AdvertisementId).Distinct())
                    await EditShares(adId, ct);
                await bot.SendMessage(msg.Chat.Id, command == GroupCommandResult.Installed
                    ? "✅ بازار غذا در این گروه نصب شد. کاربران می‌توانند آگهی را در خصوصی ثبت کنند و از دکمهٔ «انتشار در گروه» همین گروه را انتخاب کنند. برای بررسی عضویت، ربات را ادمین گروه کنید."
                    : "⛔ بازار غذا از این گروه حذف شد و انتشار آگهی متوقف شد.", cancellationToken: ct);
                return;
            }
            if (!_groups.IsInstalled(msg.Chat.Id)) return;
            if (command == GroupCommandResult.Unauthorized)
            {
                await bot.SendMessage(msg.Chat.Id, "نصب و حذف نصب فقط با پیام ادمین اصلی انجام می‌شود.", cancellationToken: ct);
                return;
            }
            var groupParsed = await parser.ParseAsync(msg.Text, ct);
            if (groupParsed.PossibleSensitiveCode.Value is not null)
                await bot.SendMessage(msg.Chat.Id, "⚠️ عدد پیام ممکن است کد تحویل یا رزرو باشد. برای امنیت آن را در آگهی عمومی منتشر نکنید؛ با ربات در خصوصی ادامه دهید.", replyParameters: new ReplyParameters { MessageId = msg.MessageId }, cancellationToken: ct);
            return;
        }
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
        if (text == "/cancel")
        {
            session.EditingField = null; store.Save(session);
            await bot.SendMessage(id, "از حالت گفتگو/ویرایش خارج شدی. برای منو /start را بزن.", cancellationToken: ct);
            return;
        }
        if (text is "/support" or "/help") { await BeginSupport(id, ct); return; }
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
            if (parts.Length == 3 && parts[1] == "add") _admin.AddCafeteria(parts[2]);
            if (parts.Length == 3 && parts[1] == "remove") store.RemoveCafeteria(parts[2]);
            await CafeteriaMenu(id, 0, ct);
            return;
        }
        if (text == "/reports" && id == options.AdminUserId)
        {
            await ReportList(id, 0, ct);
            return;
        }
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
                var query = await parser.ParseAsync(text, ct);
                var matches = market.Search(query).Take(10).ToList();
                if (matches.Count == 0) await bot.SendMessage(id, "آگهی مرتبطی پیدا نشد.", cancellationToken: ct);
                foreach (var found in matches)
                    await bot.SendMessage(id, Card(found), replyMarkup: CardButtons(found), cancellationToken: ct);
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
                        try { await bot.SendMessage(options.AdminUserId, $"🚨 گزارش #{report.Id} | آگهی #{reportAdId}\nاز کاربر {id}: {report.Reason}", replyMarkup: ReportButtons(report), cancellationToken: ct); }
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
            await bot.SendMessage(id, Preview(draft), replyMarkup: PreviewButtons(draft), cancellationToken: ct);
            return;
        }
        var result = await parser.ParseAsync(text, ct);
        var ad = market.FromDraft(result, id, msg.From.Username);
        if (ad.Type == ListingType.Unknown && Draft(session) is { } existing)
        {
            ad.Type = existing.Type;
            if (ad.FoodName is null) ad.FoodName = existing.FoodName;
            if (ad.Type == ListingType.Exchange) ad.OfferedFood ??= ad.FoodName;
        }
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
        if (result.Price.SourceText == "چند عدد احتمالی" || result.CafeteriaGender.SourceText == "آقایان / بانوان")
        {
            session.EditingField = result.Price.SourceText == "چند عدد احتمالی" ? "price" : "gender";
            store.Save(session);
            await bot.SendMessage(id, session.EditingField == "price" ?
                "چند عدد در پیام بود؛ قیمت دقیق را به تومان بنویسید (یا - برای نامشخص)." :
                "سلف آقایان است یا بانوان؟", cancellationToken: ct);
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
        await bot.SendMessage(id, Preview(ad), replyMarkup: PreviewButtons(ad), cancellationToken: ct);
    }

    private async Task StartParameter(long id, string parameter, CancellationToken ct)
    {
        if (parameter.StartsWith("deal_", StringComparison.Ordinal) && int.TryParse(parameter[5..], out var adId))
        {
            try
            {
                var t = market.StartTransaction(adId, id);
                await bot.SendMessage(id, $"🤝 معامله #{t.Id} ثبت شد. بعد از تحویل، دکمهٔ تأیید را بزنید.",
                    replyMarkup: TransactionButtons(t, id), cancellationToken: ct);
                await bot.SendMessage(t.OwnerId, $"🤝 برای آگهی #{adId} معامله #{t.Id} شروع شد. تحویل‌دهنده می‌تواند کد غذا را فقط در خصوصی با دستور /code {t.Id} کد ثبت کند؛ پس از تحویل تأیید کنید.",
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
                var food = parts.Length == 3 ? parts[2] == "gheimeh" ? "قیمه" : store.Prefill(parts[2]) : null;
                var ad = new Advertisement { OwnerId = id, OwnerUsername = store.User(id)?.Username, Type = type,
                    FoodName = food, OfferedFood = type == ListingType.Exchange ? food : null,
                    Date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(market.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone))) };
                SaveDraft(store.Session(id), ad);
                await bot.SendMessage(id, Preview(ad), replyMarkup: PreviewButtons(ad), cancellationToken: ct);
                return;
            }
        }
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { C("💰 فروش غذا", "new_sell"), C("🛒 خرید غذا", "new_buy") },
            new[] { C("🔄 معاوضه", "new_exchange"), C("🔍 جستجوی غذا", "search") },
            new[] { C("📋 آگهی‌های من", "mine"), C("🤝 معاملات من", "transactions") },
            new[] { C("⭐ پروفایل و اعتبار", "profile"), C("⚙️ تنظیمات", "settings") },
            new[] { C("💬 گفتگو با پشتیبانی", "support") }
        };
        if (IsAdmin(id)) rows.Add([C("🛠 پنل مدیریت", "adm_home")]);
        await bot.SendMessage(id, "🍽 بازار غذای دانشگاه\nچه کاری می‌خوای انجام بدی؟\nیا متن آگهی را همین‌جا بنویس:",
            replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task Callback(CallbackQuery callback, CancellationToken ct)
    {
        var id = callback.From.Id;
        var data = callback.Data ?? "";
        if (callback.Message is { Chat.Type: ChatType.Group or ChatType.Supergroup } groupMessage && !_groups.IsInstalled(groupMessage.Chat.Id))
        {
            await bot.AnswerCallbackQuery(callback.Id, "این گروه نصب نیست.", cancellationToken: ct);
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
            await StartParameter(id, parameter, ct);
            return;
        }
        if (store.User(id)?.Onboarded != true) return;
        var s = store.Session(id);
        var ad = Draft(s);
        if (data.StartsWith("new_", StringComparison.Ordinal))
        {
            s.EditingField = null;
            s.SensitiveNumber = null;
            s.DuplicateId = null;
            store.Save(s);
            var type = data[4..] switch { "sell" => ListingType.Sell, "buy" => ListingType.Buy, _ => ListingType.Exchange };
            ad = new Advertisement { OwnerId = id, OwnerUsername = callback.From.Username, Type = type,
                Date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(market.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone))) };
            SaveDraft(s, ad);
            await bot.SendMessage(id, "یک پیام کوتاه بنویس (مثلاً فروشی قیمه بانوان ۸۰) یا همین پیش‌نمایش را ویرایش کن:",
                replyMarkup: PreviewButtons(ad), cancellationToken: ct);
            return;
        }
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
            s.SensitiveNumber = null; SaveDraft(s, ad);
            await bot.SendMessage(id, Preview(ad), replyMarkup: PreviewButtons(ad), cancellationToken: ct);
            return;
        }
        if (data == "cancel")
        {
            s.DraftJson = null; s.EditingField = null; s.SensitiveNumber = null; store.Save(s);
            await bot.SendMessage(id, "آگهی لغو شد.", cancellationToken: ct); return;
        }
        if (data == "publish" && ad is not null)
        {
            if (s.SensitiveNumber is not null) { await bot.SendMessage(id, "اول نوع عدد مشکوک را مشخص کنید.", cancellationToken: ct); return; }
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
            await Publish(s, ad, data == "dup_update" ? s.DuplicateId : null, ct); return;
        }
        if (data.StartsWith("sold_", StringComparison.Ordinal) && int.TryParse(data[5..], out var soldId))
        {
            if (market.MarkSold(soldId, id)) { await EditShares(soldId, ct); await bot.SendMessage(id, "✅ آگهی فروخته‌شده ثبت شد.", cancellationToken: ct); }
            else await bot.SendMessage(id, "آگهی قابل علامت‌گذاری نیست؛ اگر معاملهٔ در جریان دارد، هر دو طرف باید آن را تأیید کنند.", cancellationToken: ct);
            return;
        }
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
                await bot.SendMessage(id, market.Rate(ratingTransaction, id, stars) ? "⭐ امتیاز شما ثبت شد." : "این امتیاز قابل ثبت نیست.", cancellationToken: ct);
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

    private bool IsAdmin(long id) => options.AdminUserId > 0 && id == options.AdminUserId;

    private async Task MyListings(long id, int page, CancellationToken ct)
    {
        var ads = store.AdsFor(id);
        if (ads.Count == 0) { await bot.SendMessage(id, "هنوز آگهی نداری. یک متن مثل «فروشی قیمه بانوان ۸۰» بفرست.", cancellationToken: ct); return; }
        page = Math.Clamp(page, 0, Math.Max(0, (ads.Count - 1) / 5));
        await bot.SendMessage(id, $"📋 آگهی‌های من | صفحه {page + 1} از {(ads.Count + 4) / 5}", cancellationToken: ct);
        foreach (var ad in ads.Skip(page * 5).Take(5))
            await bot.SendMessage(id, Card(ad), replyMarkup: ad.Status == ListingStatus.Active ? OwnerButtons(ad) : null, cancellationToken: ct);
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
            await bot.SendMessage(id, $"معامله #{t.Id} | آگهی #{t.AdvertisementId} | {t.Status}",
                replyMarkup: t.Status == TransactionStatus.Pending ? TransactionButtons(t, id) : null, cancellationToken: ct);
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"transactions_{page - 1}"));
        if ((page + 1) * 5 < transactions.Count) navigation.Add(C("بعدی ➡️", $"transactions_{page + 1}"));
        if (navigation.Count > 0) await bot.SendMessage(id, "صفحهٔ بعد/قبل:", replyMarkup: new InlineKeyboardMarkup(new[] { navigation.ToArray() }), cancellationToken: ct);
    }

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
        var summary = $"👥 {store.InstalledGroups().Count} گروه | 💬 {store.Tickets().Count(t => t.Status == TicketStatus.Open)} تیکت باز | 🚨 {store.Reports().Count(r => r.Status == ReportStatus.Open)} گزارش باز | 🤝 {store.PendingTransactions().Count} معامله در جریان";
        await bot.SendMessage(id, $"🛠 پنل مدیریت\n{summary}\n\nمدیریت سلف‌ها، گفتگوها، گزارش‌ها و تنظیمات:", replyMarkup: new InlineKeyboardMarkup(new[]
        {
            new[] { C("📍 مدیریت محل‌های سلف", "adm_caf_0") },
            new[] { C("👥 گروه‌های نصب‌شده", "adm_groups_0") },
            new[] { C("💬 گفتگوهای پشتیبانی", "adm_tickets_0"), C("🚨 گزارش‌ها", "adm_reports_0") },
            new[] { C("🤝 معاملات در جریان", "adm_pending_0") },
            new[] { C("⚙️ تنظیمات سامانه", "adm_settings") }
        }), cancellationToken: ct);
    }

    private async Task AdminGroups(long id, int page, CancellationToken ct)
    {
        if (!IsAdmin(id)) return;
        var groups = store.InstalledGroups();
        page = Math.Clamp(page, 0, Math.Max(0, (groups.Count - 1) / 8));
        var rows = groups.Skip(page * 8).Take(8).Select(g => new[] { C($"⛔ حذف {g.Title}", $"adm_group_off_{g.Id}") }).ToList();
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(C("⬅️ قبلی", $"adm_groups_{page - 1}"));
        if ((page + 1) * 8 < groups.Count) navigation.Add(C("بعدی ➡️", $"adm_groups_{page + 1}"));
        if (navigation.Count > 0) rows.Add(navigation.ToArray());
        rows.Add([C("↩️ مدیریت", "adm_home")]);
        await bot.SendMessage(id, groups.Count == 0
            ? "هیچ گروهی نصب نشده است. در گروه موردنظر «نصب» بنویس. ربات باید پیام‌های گروه را دریافت کند."
            : "👥 گروه‌های مجاز؛ برای نصب، خودت در گروه «نصب» بنویس. حذف از اینجا یا با «حذف نصب» در گروه انجام می‌شود.",
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
        var rows = all.Skip(page * 8).Take(8).Select(r => new[] { C($"🚨 گزارش #{r.Id} | آگهی #{r.AdvertisementId}", $"adm_report_{r.Id}") }).ToList();
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
        { C($"❌ لغو معامله #{t.Id} | آگهی #{t.AdvertisementId}", $"adm_txn_cancel_{t.Id}") }).ToList();
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
        await bot.SendMessage(id, $"⚙️ تنظیمات سامانه\nضریب قیمت کوتاه: {options.BarePriceMultiplier}\nمنطقهٔ زمانی: {options.TimeZone}\nانقضای صبحانه: {options.BreakfastExpirationTime:HH:mm}\nناهار: {options.LunchExpirationTime:HH:mm}\nشام: {options.DinnerExpirationTime:HH:mm}\nسایر: {options.OtherExpirationTime:HH:mm}\nبازهٔ تکراری: {options.DuplicateWindowMinutes} دقیقه\nفاصلهٔ اعلان: {options.NotificationCooldownMinutes} دقیقه",
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { C("💵 ضریب قیمت", "adm_setting_price"), C("🌍 منطقهٔ زمانی", "adm_setting_timezone") },
                new[] { C("🍳 صبحانه", "adm_setting_breakfast"), C("🍽 ناهار", "adm_setting_lunch"), C("🌙 شام", "adm_setting_dinner"), C("سایر", "adm_setting_other") },
                new[] { C("♻️ بازهٔ تکراری", "adm_setting_duplicate"), C("🔔 فاصلهٔ اعلان", "adm_setting_notification") },
                new[] { C("↩️ مدیریت", "adm_home") }
            }), cancellationToken: ct);
    }

    private async Task<bool> AdminInput(long id, string field, string text, UserSession session, CancellationToken ct)
    {
        if (field == "caf_add")
        {
            if (!_admin.AddCafeteria(text)) { await bot.SendMessage(id, "نام محل باید بین ۲ تا ۶۰ نویسه باشد.", cancellationToken: ct); return true; }
            session.EditingField = null; store.Save(session); await CafeteriaMenu(id, 0, ct); return true;
        }
        if (field.StartsWith("caf_rename:", StringComparison.Ordinal) && int.TryParse(field[11..], out var cafeteriaId))
        {
            if (!_admin.RenameCafeteria(cafeteriaId, text)) { await bot.SendMessage(id, "نام تکراری یا نامعتبر است؛ نام دیگری وارد کن.", cancellationToken: ct); return true; }
            session.EditingField = null; store.Save(session); await CafeteriaMenu(id, 0, ct); return true;
        }
        if (field.StartsWith("setting:", StringComparison.Ordinal))
        {
            if (!_admin.TryUpdateSetting(field[8..], text)) { await bot.SendMessage(id, "مقدار نامعتبر است؛ برای ساعت HH:mm و برای منطقهٔ زمانی مثلاً Asia/Tehran وارد کن.", cancellationToken: ct); return true; }
            session.EditingField = null; store.Save(session); await AdminSettings(id, ct); return true;
        }
        return false;
    }

    private async Task<bool> AdminCallback(long id, string data, UserSession session, CancellationToken ct)
    {
        if (!IsAdmin(id) || !data.StartsWith("adm_", StringComparison.Ordinal)) return false;
        if (data == "adm_home") { await AdminMenu(id, ct); return true; }
        if (data.StartsWith("adm_groups_", StringComparison.Ordinal) && int.TryParse(data[11..], out var groupPage))
        { await AdminGroups(id, groupPage, ct); return true; }
        if (data.StartsWith("adm_group_off_", StringComparison.Ordinal) && long.TryParse(data[14..], out var disabledGroupId))
        {
            if (store.Group(disabledGroupId) is { Active: true } disabled)
            {
                disabled.Active = false; store.Save(disabled);
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
            if (found is not null) store.SetCafeteriaActive(toggleId, !found.Active);
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
                var ad = store.Ad(report.AdvertisementId)!;
                try { await bot.SendMessage(ad.OwnerId, $"⛔ آگهی #{ad.Id} پس از بررسی گزارش غیرفعال شد. برای پیگیری /support را بزنید.", cancellationToken: ct); }
                catch (Exception ex) { Log(ex); }
            }
            report.Status = status; store.Update(report);
            await bot.SendMessage(id, "وضعیت گزارش ثبت شد.", cancellationToken: ct);
            try { await bot.SendMessage(report.ReporterId, $"نتیجهٔ بررسی گزارش #{report.Id}: {(status == ReportStatus.Dismissed ? "رد شد" : "رسیدگی شد")}. برای پیگیری /support را بزنید.", cancellationToken: ct); }
            catch (Exception ex) { Log(ex); }
            return true;
        }
        if (data.StartsWith("adm_report_", StringComparison.Ordinal) && int.TryParse(data[11..], out var viewedReportId))
        {
            var report = store.Report(viewedReportId);
            if (report is not null)
                await bot.SendMessage(id, $"🚨 گزارش #{report.Id} | آگهی #{report.AdvertisementId} | {report.Status}\nگزارش‌دهنده: {report.ReporterId}\n{report.Reason}\n\n{(store.Ad(report.AdvertisementId) is { } reportedAd ? Card(reportedAd) : "آگهی یافت نشد")}",
                    replyMarkup: report.Status == ReportStatus.Open ? ReportButtons(report) : null, cancellationToken: ct);
            return true;
        }
        if (data == "adm_settings") { await AdminSettings(id, ct); return true; }
        if (data.StartsWith("adm_setting_", StringComparison.Ordinal))
        {
            session.EditingField = "setting:" + data[12..]; store.Save(session);
            await bot.SendMessage(id, "مقدار جدید را بفرست. ساعت: HH:mm؛ دقیقه/ضریب: عدد صحیح؛ منطقهٔ زمانی: Asia/Tehran. لغو: /cancel", cancellationToken: ct);
            return true;
        }
        return true;
    }

    private async Task Publish(UserSession session, Advertisement ad, int? updateId, CancellationToken ct)
    {
        try { market.Publish(ad, updateId); }
        catch (InvalidOperationException ex) { await bot.SendMessage(ad.OwnerId, ex.Message, replyMarkup: PreviewButtons(ad), cancellationToken: ct); return; }
        session.DraftJson = null; session.DuplicateId = null; store.Save(session);
        await bot.SendMessage(ad.OwnerId, Card(ad), replyMarkup: OwnerButtons(ad), cancellationToken: ct);
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
            var old = store.Shares(adId).FirstOrDefault(s => s.GroupChatId == groupId && s.GroupMessageId.HasValue);
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

    private async Task EditShares(int id, CancellationToken ct)
    {
        var ad = store.Ad(id)!;
        foreach (var share in store.Shares(id))
            try
            {
                if (share.GroupChatId is { } groupId && share.GroupMessageId is { } messageId)
                    await bot.EditMessageText(groupId, messageId,
                        _groups.IsInstalled(groupId) ? InlineCard(ad) : "⛔ این گروه دیگر فعال نیست.\n" + InlineCard(ad),
                        parseMode: ParseMode.Html,
                        replyMarkup: ad.Status == ListingStatus.Active && _groups.IsInstalled(groupId) ? CardButtons(ad) : null, cancellationToken: ct);
                else if (share.InlineMessageId.Length > 0)
                    await bot.EditMessageText(inlineMessageId: share.InlineMessageId, text: InlineCard(ad), parseMode: ParseMode.Html,
                        replyMarkup: ad.Status == ListingStatus.Active ? CardButtons(ad) : null, cancellationToken: ct);
            }
            catch (Exception ex) { Log(ex); }
    }

    private async Task Inline(InlineQuery inline, CancellationToken ct)
    {
        // Telegram exposes only the chat TYPE in an inline query, not its ID. Group inline results
        // cannot be checked against the installed-group list; official group posts use PostInGroup.
        if (!GroupAccess.AllowsInline(inline.ChatType))
        {
            await bot.AnswerInlineQuery(inline.Id, Array.Empty<InlineQueryResult>(), cacheTime: 0, isPersonal: true, cancellationToken: ct);
            return;
        }
        foreach (var expiredId in market.Expire()) await EditShares(expiredId, ct);
        var results = new List<InlineQueryResult>();
        var query = inline.Query.Trim();
        if (query.StartsWith("id:", StringComparison.Ordinal) && int.TryParse(query[3..], out var id))
        {
            var own = store.Ad(id);
            if (own is not null && own.OwnerId == inline.From.Id && own.Status == ListingStatus.Active && !market.IsExpired(own))
                results.Add(new InlineQueryResultArticle($"ad_{id}", Header(own), new InputTextMessageContent(InlineCard(own)) { ParseMode = ParseMode.Html }) { ReplyMarkup = CardButtons(own) });
        }
        else
        {
            var parsed = await parser.ParseAsync(query, ct);
            var food = parsed.FoodName.Value;
            if (query.Length > 0)
            {
                foreach (var (type, title) in new[] { ("sell", "➕ فروش"), ("buy", "🛒 خریدار"), ("exchange", "🔄 معاوضه") })
                {
                    var suffix = food is null ? "" : " " + food;
                    string? token = null;
                    if (food is not null)
                    {
                        token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(food)))[..16].ToLowerInvariant();
                        store.Save(new InlinePrefill { Id = token, Food = food });
                    }
                    var link = $"https://t.me/{_username}?start=create_{type}" + (token is null ? "" : "_" + token);
                    // Selecting a result shares only a link to the private creation flow; no draft is posted in public.
                    results.Add(new InlineQueryResultArticle($"create_{type}", title + suffix,
                        new InputTextMessageContent($"ساخت آگهی {title}{suffix}: {link}")) { Description = "ادامه در چت خصوصی", ReplyMarkup = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithUrl("✏️ ساخت در خصوصی", link) } }) });
                }
            }
            foreach (var ad in market.Search(parsed).Take(20))
                results.Add(new InlineQueryResultArticle($"ad_{ad.Id}", Header(ad), new InputTextMessageContent(InlineCard(ad)) { ParseMode = ParseMode.Html })
                { Description = $"{GenderName(ad.Gender)} | {MealName(ad.Meal)}", ReplyMarkup = CardButtons(ad) });
        }
        await bot.AnswerInlineQuery(inline.Id, results, cacheTime: 0, isPersonal: true, cancellationToken: ct);
    }
}
