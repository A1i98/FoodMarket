using LiteDB;
using System.Text.RegularExpressions;

namespace FoodMarket;

public sealed class MarketStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly object _gate = new();

    public MarketStore(MarketOptions options)
    {
        var mapper = new BsonMapper();
        mapper.RegisterType<DateOnly>(v => new BsonValue(v.ToString("yyyy-MM-dd")), v => DateOnly.Parse(v.AsString));
        mapper.RegisterType<DateRange>(v => new BsonValue($"{v.Start:yyyy-MM-dd}|{v.End:yyyy-MM-dd}"),
            v => new DateRange(DateOnly.Parse(v.AsString.Split('|')[0]), DateOnly.Parse(v.AsString.Split('|')[1])));
        mapper.RegisterType<TimeOnly>(v => new BsonValue(v.ToString("HH:mm:ss")), v => TimeOnly.Parse(v.AsString));
        _db = new LiteDatabase(options.DatabasePath, mapper);
        _db.UtcDate = true;
        _db.GetCollection<Advertisement>("ads").EnsureIndex(a => a.Status);
        _db.GetCollection<Advertisement>("ads").EnsureIndex(a => a.OwnerId);
        _db.GetCollection<InstalledGroup>("installedGroups").EnsureIndex(g => g.Active);
        _db.GetCollection<MarketTransaction>("transactions").EnsureIndex(t => t.AdvertisementId);
        _db.GetCollection<MarketTransaction>("transactions").EnsureIndex(t => t.Status);
        _db.GetCollection<SupportTicket>("tickets").EnsureIndex(t => t.UserId);
        _db.GetCollection<SupportMessage>("ticketMessages").EnsureIndex(m => m.TicketId);
        if (_db.GetCollection<RuntimeSettings>("runtimeSettings").FindById(1) is { } settings)
            ApplySettings(settings, options);
        if (_db.GetCollection<Cafeteria>("cafeterias").Count() == 0)
            foreach (var name in options.InitialCafeterias)
                _db.GetCollection<Cafeteria>("cafeterias").Insert(new Cafeteria { Name = name });
    }

    public IReadOnlyList<string> Locations() { lock (_gate) return _db.GetCollection<Cafeteria>("cafeterias").Find(c => c.Active).Select(c => c.Name).ToList(); }
    public IReadOnlyList<Cafeteria> Cafeterias() { lock (_gate) return _db.GetCollection<Cafeteria>("cafeterias").FindAll().ToList(); }
    public bool RenameCafeteria(int id, string newName)
    {
        lock (_gate)
        {
            var collection = _db.GetCollection<Cafeteria>("cafeterias");
            var found = collection.FindById(id);
            if (found is null || collection.FindAll().Any(c => c.Id != id && PersianText.Normalize(c.Name) == PersianText.Normalize(newName))) return false;
            found.Name = newName;
            return collection.Update(found);
        }
    }
    public bool SetCafeteriaActive(int id, bool active)
    {
        lock (_gate)
        {
            var collection = _db.GetCollection<Cafeteria>("cafeterias");
            var found = collection.FindById(id);
            if (found is null) return false;
            found.Active = active;
            return collection.Update(found);
        }
    }
    public void AddCafeteria(string name)
    {
        lock (_gate)
        {
            var collection = _db.GetCollection<Cafeteria>("cafeterias");
            var existing = collection.FindAll().FirstOrDefault(c => PersianText.Normalize(c.Name) == PersianText.Normalize(name));
            if (existing is not null) { existing.Active = true; collection.Update(existing); }
            else collection.Insert(new Cafeteria { Name = name });
        }
    }
    public void RemoveCafeteria(string name)
    {
        lock (_gate)
        {
            var collection = _db.GetCollection<Cafeteria>("cafeterias");
            var found = collection.FindAll().FirstOrDefault(c => PersianText.Normalize(c.Name) == PersianText.Normalize(name));
            if (found is not null) { found.Active = false; collection.Update(found); }
        }
    }
    public MarketUser? User(long id) { lock (_gate) return _db.GetCollection<MarketUser>("users").FindById(id); }
    public void Save(MarketUser user) { lock (_gate) _db.GetCollection<MarketUser>("users").Upsert(user); }
    public UserSession Session(long id) { lock (_gate) return _db.GetCollection<UserSession>("sessions").FindById(id) ?? new UserSession { Id = id }; }
    public void Save(UserSession session) { lock (_gate) _db.GetCollection<UserSession>("sessions").Upsert(session); }
    public Advertisement? Ad(int id) { lock (_gate) return _db.GetCollection<Advertisement>("ads").FindById(id); }
    public IReadOnlyList<Advertisement> Active() { lock (_gate) return _db.GetCollection<Advertisement>("ads").Find(a => a.Status == ListingStatus.Active).ToList(); }
    public IReadOnlyList<Advertisement> AdsFor(long owner) { lock (_gate) return _db.GetCollection<Advertisement>("ads").Find(a => a.OwnerId == owner).OrderByDescending(a => a.CreatedUtc).ToList(); }
    public void Save(Advertisement ad) { lock (_gate) _db.GetCollection<Advertisement>("ads").Upsert(ad); }
    public MarketTransaction? Transaction(int id) { lock (_gate) return _db.GetCollection<MarketTransaction>("transactions").FindById(id); }
    public IReadOnlyList<MarketTransaction> TransactionsFor(long id) { lock (_gate) return _db.GetCollection<MarketTransaction>("transactions").Find(t => t.OwnerId == id || t.CounterpartyId == id).ToList(); }
    public IReadOnlyList<MarketTransaction> TransactionsForAd(int id) { lock (_gate) return _db.GetCollection<MarketTransaction>("transactions").Find(t => t.AdvertisementId == id).ToList(); }
    public IReadOnlyList<MarketTransaction> PendingTransactions() { lock (_gate) return _db.GetCollection<MarketTransaction>("transactions").Find(t => t.Status == TransactionStatus.Pending).OrderByDescending(t => t.CreatedUtc).ToList(); }
    public void Save(MarketTransaction transaction) { lock (_gate) _db.GetCollection<MarketTransaction>("transactions").Upsert(transaction); }
    public IReadOnlyList<SharedMessage> Shares(int adId) { lock (_gate) return _db.GetCollection<SharedMessage>("shares").Find(s => s.AdvertisementId == adId).ToList(); }
    public void Save(SharedMessage share) { lock (_gate) _db.GetCollection<SharedMessage>("shares").Insert(share); }
    public void Update(SharedMessage share) { lock (_gate) _db.GetCollection<SharedMessage>("shares").Update(share); }
    public IReadOnlyList<SharedMessage> SharesForGroup(long groupId) { lock (_gate) return _db.GetCollection<SharedMessage>("shares").Find(s => s.GroupChatId == groupId).ToList(); }
    public InstalledGroup? Group(long id) { lock (_gate) return _db.GetCollection<InstalledGroup>("installedGroups").FindById(id); }
    public IReadOnlyList<InstalledGroup> InstalledGroups() { lock (_gate) return _db.GetCollection<InstalledGroup>("installedGroups").Find(g => g.Active).OrderBy(g => g.Title).ToList(); }
    public void Save(InstalledGroup group) { lock (_gate) _db.GetCollection<InstalledGroup>("installedGroups").Upsert(group); }
    public void Save(InlinePrefill prefill) { lock (_gate) _db.GetCollection<InlinePrefill>("prefills").Upsert(prefill); }
    public string? Prefill(string id) { lock (_gate) return _db.GetCollection<InlinePrefill>("prefills").FindById(id)?.Food; }
    public void Save(ListingReport report) { lock (_gate) _db.GetCollection<ListingReport>("reports").Insert(report); }
    public ListingReport? Report(int id) { lock (_gate) return _db.GetCollection<ListingReport>("reports").FindById(id); }
    public ListingReport? OpenReport(int adId, long reporterId)
    { lock (_gate) return _db.GetCollection<ListingReport>("reports").FindOne(r => r.AdvertisementId == adId && r.ReporterId == reporterId && r.Status == ReportStatus.Open); }
    public void Update(ListingReport report) { lock (_gate) _db.GetCollection<ListingReport>("reports").Update(report); }
    public IReadOnlyList<ListingReport> Reports() { lock (_gate) return _db.GetCollection<ListingReport>("reports").FindAll().OrderByDescending(r => r.CreatedUtc).ToList(); }
    public SupportTicket? Ticket(int id) { lock (_gate) return _db.GetCollection<SupportTicket>("tickets").FindById(id); }
    public IReadOnlyList<SupportTicket> Tickets() { lock (_gate) return _db.GetCollection<SupportTicket>("tickets").FindAll().OrderByDescending(t => t.UpdatedUtc).ToList(); }
    public IReadOnlyList<SupportTicket> TicketsFor(long id) { lock (_gate) return _db.GetCollection<SupportTicket>("tickets").Find(t => t.UserId == id).OrderByDescending(t => t.UpdatedUtc).ToList(); }
    public void Save(SupportTicket ticket) { lock (_gate) _db.GetCollection<SupportTicket>("tickets").Upsert(ticket); }
    public IReadOnlyList<SupportMessage> TicketMessages(int id) { lock (_gate) return _db.GetCollection<SupportMessage>("ticketMessages").Find(m => m.TicketId == id).OrderBy(m => m.Id).ToList(); }
    public void Save(SupportMessage message) { lock (_gate) _db.GetCollection<SupportMessage>("ticketMessages").Insert(message); }
    public void SaveSettings(MarketOptions options)
    {
        lock (_gate) _db.GetCollection<RuntimeSettings>("runtimeSettings").Upsert(new RuntimeSettings
        {
            BarePriceMultiplier = options.BarePriceMultiplier, TimeZone = options.TimeZone,
            BreakfastExpirationTime = options.BreakfastExpirationTime, LunchExpirationTime = options.LunchExpirationTime,
            DinnerExpirationTime = options.DinnerExpirationTime, OtherExpirationTime = options.OtherExpirationTime,
            DuplicateWindowMinutes = options.DuplicateWindowMinutes, NotificationCooldownMinutes = options.NotificationCooldownMinutes
        });
    }
    private static void ApplySettings(RuntimeSettings value, MarketOptions options)
    {
        options.BarePriceMultiplier = value.BarePriceMultiplier;
        options.TimeZone = value.TimeZone;
        options.BreakfastExpirationTime = value.BreakfastExpirationTime;
        options.LunchExpirationTime = value.LunchExpirationTime;
        options.DinnerExpirationTime = value.DinnerExpirationTime;
        options.OtherExpirationTime = value.OtherExpirationTime;
        options.DuplicateWindowMinutes = value.DuplicateWindowMinutes;
        options.NotificationCooldownMinutes = value.NotificationCooldownMinutes;
    }
    public IReadOnlyList<TransactionRating> RatingsFor(long id) { lock (_gate) return _db.GetCollection<TransactionRating>("ratings").Find(r => r.RecipientId == id).ToList(); }
    public bool HasRated(int transactionId, long raterId) { lock (_gate) return _db.GetCollection<TransactionRating>("ratings").Exists(r => r.TransactionId == transactionId && r.RaterId == raterId); }
    public void Save(TransactionRating rating) { lock (_gate) _db.GetCollection<TransactionRating>("ratings").Insert(rating); }
    public void Dispose() => _db.Dispose();
}

public sealed class Marketplace(MarketStore store, MarketOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private DateTime LocalNow => TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone)).DateTime;

    public static bool IsValid(Advertisement ad) => ad.Type is ListingType.Buy or ListingType.Sell &&
        (ad.FoodName is not null || ad.Meal != MealType.Unknown) ||
        ad.Type == ListingType.Exchange && ad.OfferedFood is not null && ad.WantedFood is not null;

    public Advertisement FromDraft(ParsedListingResult r, long owner, string? username) => new()
    {
        OwnerId = owner, OwnerUsername = username, Type = r.ListingType.Value,
        FoodName = r.FoodName.Value, Meal = r.Meal.Value, Gender = r.CafeteriaGender.Value,
        Location = r.CafeteriaLocation.Value, Price = r.Price.Value, Date = r.Date.Value,
        DateRange = r.DateRange.Value, Urgency = r.Urgency.Value, OfferedFood = r.OfferedFood.Value,
        WantedFood = r.WantedFood.Value, OfferedMeal = r.OfferedMeal.Value,
        WantedMeal = r.WantedMeal.Value, OptionalPriceDifference = r.OptionalPriceDifference.Value,
        Description = Redact(r.OriginalText, r.PossibleSensitiveCode.Value)
    };

    public static string Redact(string text, string? sensitive)
    {
        text = PersianText.Normalize(text);
        if (sensitive is not null) text = text.Replace(sensitive, "", StringComparison.Ordinal);
        return text.Trim();
    }

    public DateTime ExpirationUtc(Advertisement ad)
    {
        var endDate = ad.DateRange?.End ?? ad.Date ?? DateOnly.FromDateTime(LocalNow);
        var time = ad.Meal switch
        {
            MealType.Breakfast => options.BreakfastExpirationTime,
            MealType.Lunch => options.LunchExpirationTime,
            MealType.Dinner => options.DinnerExpirationTime,
            _ => options.OtherExpirationTime
        };
        return TimeZoneInfo.ConvertTimeToUtc(endDate.ToDateTime(time), TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone));
    }

    public bool IsExpired(Advertisement ad) => UtcNow >= ExpirationUtc(ad);
    public IReadOnlyList<int> Expire()
    {
        var expired = new List<int>();
        foreach (var ad in store.Active().Where(IsExpired))
        {
            ad.Status = ListingStatus.Expired;
            store.Save(ad);
            expired.Add(ad.Id);
        }
        return expired;
    }

    public Advertisement? Duplicate(Advertisement ad)
    {
        var recent = UtcNow.AddMinutes(-options.DuplicateWindowMinutes);
        return store.Active().Where(a => a.OwnerId == ad.OwnerId && a.CreatedUtc >= recent && a.Type == ad.Type)
            .FirstOrDefault(a => Similarity(a, ad) >= 80);
    }

    public Advertisement Publish(Advertisement ad, int? updateId = null)
    {
        if (!IsValid(ad)) throw new InvalidOperationException("نوع آگهی و غذا یا وعده لازم است (برای معاوضه هر دو غذا). ");
        if (new[] { ad.FoodName, ad.OfferedFood, ad.WantedFood, ad.Location }.Any(f =>
            f is not null && Regex.IsMatch(PersianText.Normalize(f), @"(?<!\d)\d{5,}(?!\d)")))
            throw new InvalidOperationException("کد احتمالی را از فیلدهای عمومی آگهی حذف کنید.");
        if (IsExpired(ad)) throw new InvalidOperationException("زمان این آگهی گذشته؛ تاریخ یا وعده را تغییر دهید.");
        if (updateId.HasValue)
        {
            var previous = store.Ad(updateId.Value);
            if (previous is null || previous.OwnerId != ad.OwnerId || previous.Status != ListingStatus.Active)
                throw new InvalidOperationException("آگهی قبلی قابل بروزرسانی نیست.");
            ad.Id = previous.Id;
            ad.CreatedUtc = previous.CreatedUtc;
            ad.GroupChatId = previous.GroupChatId;
            ad.GroupMessageId = previous.GroupMessageId;
        }
        ad.UpdatedUtc = UtcNow;
        store.Save(ad);
        return ad;
    }

    private static string Food(string? name) => PersianText.Normalize(name ?? "").Replace("خورش ", "").Replace("خورشت ", "");
    private static bool Compatible(string? a, string? b) => a is null || b is null || Food(a).Contains(Food(b), StringComparison.OrdinalIgnoreCase) || Food(b).Contains(Food(a), StringComparison.OrdinalIgnoreCase);
    private static int Similarity(Advertisement a, Advertisement b)
    {
        if (a.Type == ListingType.Exchange && (!Compatible(a.OfferedFood, b.OfferedFood) ||
            !Compatible(a.WantedFood, b.WantedFood))) return 0;
        if (!Compatible(a.FoodName, b.FoodName)) return 0;
        if (a.Meal != b.Meal && a.Meal != MealType.Unknown && b.Meal != MealType.Unknown) return 0;
        if (a.Gender != b.Gender && a.Gender != CafeteriaGender.Unknown && b.Gender != CafeteriaGender.Unknown) return 0;
        if (a.Date != b.Date || a.DateRange != b.DateRange) return 0;
        return a.FoodName is not null && b.FoodName is not null ? 95 : 85;
    }

    public int MatchScore(Advertisement a, Advertisement b)
    {
        if (a.OwnerId == b.OwnerId || a.Type == b.Type ||
            a.Type == ListingType.Exchange && b.Type == ListingType.Buy ||
            b.Type == ListingType.Exchange && a.Type == ListingType.Buy) return 0;
        if (a.Gender != b.Gender && a.Gender != CafeteriaGender.Unknown && b.Gender != CafeteriaGender.Unknown) return 0;
        if (a.Location is not null && b.Location is not null && PersianText.Normalize(a.Location) != PersianText.Normalize(b.Location)) return 0;
        if (a.Meal != b.Meal && a.Meal != MealType.Unknown && b.Meal != MealType.Unknown) return 0;
        if (a.Date.HasValue && b.Date.HasValue && a.Date != b.Date) return 0;
        if (a.DateRange is not null && b.Date.HasValue && (b.Date < a.DateRange.Start || b.Date > a.DateRange.End) ||
            b.DateRange is not null && a.Date.HasValue && (a.Date < b.DateRange.Start || a.Date > b.DateRange.End)) return 0;
        if (a.DateRange is { } ar && b.DateRange is { } br && (ar.End < br.Start || br.End < ar.Start)) return 0;
        if (a.Type == ListingType.Exchange || b.Type == ListingType.Exchange)
        {
            if (a.Type != ListingType.Exchange || b.Type != ListingType.Exchange ||
                !Compatible(a.OfferedFood, b.WantedFood) || !Compatible(a.WantedFood, b.OfferedFood)) return 0;
        }
        else if (!Compatible(a.FoodName, b.FoodName)) return 0;

        var score = 30;
        if (a.FoodName is not null && b.FoodName is not null || a.Type == ListingType.Exchange) score += 30;
        if (a.Meal != MealType.Unknown && a.Meal == b.Meal) score += 10;
        if (a.Gender != CafeteriaGender.Unknown && a.Gender == b.Gender) score += 10;
        if (a.Location is not null && b.Location is not null) score += 8;
        if (a.Date == b.Date && a.Date.HasValue) score += 7;
        var seller = a.Type == ListingType.Sell ? a : b;
        var buyer = a.Type == ListingType.Buy ? a : b;
        if (seller.Price.HasValue && buyer.Price.HasValue && seller.Price <= buyer.Price) score += 5;
        return Math.Min(100, score);
    }

    public IReadOnlyList<(Advertisement Ad, int Score)> Matches(Advertisement ad) => store.Active()
        .Where(a => a.Id != ad.Id && !IsExpired(a)).Select(a => (Ad: a, Score: MatchScore(ad, a)))
        .Where(pair => pair.Score >= 40).OrderByDescending(pair => pair.Score).ThenByDescending(pair => pair.Ad.CreatedUtc).ToList();

    public IReadOnlyList<Advertisement> Search(ParsedListingResult query)
    {
        var word = Food(query.FoodName.Value);
        return store.Active().Where(a => !IsExpired(a)
            && (word.Length == 0 || Food(a.FoodName).Contains(word, StringComparison.OrdinalIgnoreCase)
                || Food(a.OfferedFood).Contains(word, StringComparison.OrdinalIgnoreCase)
                || Food(a.WantedFood).Contains(word, StringComparison.OrdinalIgnoreCase))
            && (query.Meal.Value == MealType.Unknown || a.Meal == query.Meal.Value)
            && (query.CafeteriaGender.Value == CafeteriaGender.Unknown || a.Gender == query.CafeteriaGender.Value)
            && (query.CafeteriaLocation.Value is null || a.Location == query.CafeteriaLocation.Value))
            .OrderByDescending(a => a.CreatedUtc).Take(30).ToList();
    }

    public MarketTransaction StartTransaction(int adId, long counterparty)
    {
        var ad = store.Ad(adId);
        if (ad is null || ad.Status != ListingStatus.Active || IsExpired(ad) || ad.OwnerId == counterparty || store.User(counterparty)?.Onboarded != true)
            throw new InvalidOperationException("آگهی فعال نیست یا طرف مقابل هنوز ربات را شروع نکرده است.");
        if (store.TransactionsForAd(adId).Any(t => t.Status == TransactionStatus.Pending))
            throw new InvalidOperationException("این آگهی معاملهٔ در جریان دارد.");
        var t = new MarketTransaction { AdvertisementId = adId, OwnerId = ad.OwnerId,
            CounterpartyId = counterparty, Type = ad.Type switch
            { ListingType.Exchange => TransactionType.Exchange, ListingType.Buy => TransactionType.Sale, _ => TransactionType.Purchase } };
        store.Save(t);
        return t;
    }

    public MarketTransaction ConfirmDelivery(int transactionId, long actor)
    {
        var t = store.Transaction(transactionId);
        if (t is null || t.Status != TransactionStatus.Pending || actor != t.OwnerId && actor != t.CounterpartyId)
            throw new InvalidOperationException("این تأیید مجاز نیست.");
        if (actor == t.OwnerId) t.OwnerDelivered = true;
        else t.CounterpartyDelivered = true;
        if (t.OwnerDelivered && t.CounterpartyDelivered)
        {
            t.Status = TransactionStatus.Completed;
            var ad = store.Ad(t.AdvertisementId)!;
            ad.Status = ListingStatus.Sold;
            store.Save(ad);
            foreach (var id in new[] { t.OwnerId, t.CounterpartyId })
            {
                var user = store.User(id)!;
                user.SuccessfulTransactions++;
                user.TrustScore = CalculateTrust(user);
                store.Save(user);
            }
        }
        store.Save(t);
        return t;
    }

    public MarketTransaction CancelTransaction(int transactionId, long actor)
    {
        var t = store.Transaction(transactionId);
        if (t is null || t.Status != TransactionStatus.Pending ||
            actor != t.OwnerId && actor != t.CounterpartyId && (options.AdminUserId == 0 || actor != options.AdminUserId))
            throw new InvalidOperationException("لغو این معامله مجاز نیست.");
        t.Status = TransactionStatus.Cancelled;
        t.FoodCode = null;
        t.CounterpartyFoodCode = null;
        store.Save(t);
        return t;
    }

    public long SetFoodCode(int transactionId, long actor, string code)
    {
        var t = store.Transaction(transactionId);
        code = code.Trim();
        if (t is null || t.Status != TransactionStatus.Pending ||
            code.Length is < 3 or > 32 || code.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("شمارهٔ معامله یا کد معتبر نیست.");
        if (t.Type == TransactionType.Exchange)
        {
            if (actor == t.OwnerId) t.FoodCode = code;
            else if (actor == t.CounterpartyId) t.CounterpartyFoodCode = code;
            else throw new InvalidOperationException("کد فقط توسط طرف‌های معاوضه ثبت می‌شود.");
        }
        else if (actor == (t.Type == TransactionType.Sale ? t.CounterpartyId : t.OwnerId)) t.FoodCode = code;
        else throw new InvalidOperationException("کد غذا را فقط تحویل‌دهندهٔ غذا می‌تواند ثبت کند.");
        store.Save(t);
        return actor == t.OwnerId ? t.CounterpartyId : t.OwnerId;
    }

    public bool MarkSold(int adId, long owner)
    {
        var ad = store.Ad(adId);
        if (ad is null || ad.Type != ListingType.Sell || ad.OwnerId != owner || ad.Status != ListingStatus.Active ||
            store.TransactionsForAd(adId).Any(t => t.Status == TransactionStatus.Pending)) return false;
        ad.Status = ListingStatus.Sold;
        store.Save(ad);
        return true;
    }

    public bool CancelListing(int adId)
    {
        var ad = store.Ad(adId);
        if (ad is null || ad.Status != ListingStatus.Active ||
            store.TransactionsForAd(adId).Any(t => t.Status == TransactionStatus.Pending)) return false;
        ad.Status = ListingStatus.Cancelled;
        store.Save(ad);
        return true;
    }

    public bool RemoveReportedListing(int adId)
    {
        if (!CancelListing(adId)) return false;
        var ad = store.Ad(adId)!;
        var user = store.User(ad.OwnerId);
        if (user is not null)
        {
            user.ConfirmedReports++;
            user.TrustScore = CalculateTrust(user);
            store.Save(user);
        }
        return true;
    }

    private static int CalculateTrust(MarketUser user) => Math.Clamp(50 + Math.Min(25, user.SuccessfulTransactions * 2) +
        (user.Rating == 0 ? 0 : (int)Math.Round((user.Rating - 3) * 10)) - user.ConfirmedReports * 10, 0, 100);

    public bool Rate(int transactionId, long raterId, int stars)
    {
        var transaction = store.Transaction(transactionId);
        if (transaction is null || transaction.Status != TransactionStatus.Completed || stars is < 1 or > 5 ||
            raterId != transaction.OwnerId && raterId != transaction.CounterpartyId ||
            store.HasRated(transactionId, raterId)) return false;
        var other = raterId == transaction.OwnerId ? transaction.CounterpartyId : transaction.OwnerId;
        store.Save(new TransactionRating { TransactionId = transactionId, RaterId = raterId, RecipientId = other, Stars = stars });
        var user = store.User(other)!;
        var ratings = store.RatingsFor(other);
        user.Rating = ratings.Average(r => r.Stars);
        user.TrustScore = CalculateTrust(user);
        store.Save(user);
        return true;
    }

    public bool CanNotify(Advertisement ad) => ad.MatchNotifications && store.User(ad.OwnerId)?.NotificationsEnabled == true &&
        (store.User(ad.OwnerId)?.LastNotificationUtc is not { } last || last <= UtcNow.AddMinutes(-options.NotificationCooldownMinutes));
}
