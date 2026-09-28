namespace FoodMarket;

public enum ListingType { Unknown, Buy, Sell, Exchange }
public enum MealType { Unknown, Breakfast, Lunch, Dinner, Other }
public enum CafeteriaGender { Unknown, Men, Women, Mixed }
public enum Urgency { Normal, High }
public enum ListingStatus { Active, Sold, Expired, Cancelled }
public enum TransactionType { Purchase, Sale, Exchange }
public enum TransactionStatus { Pending, Completed, Cancelled }
public enum NumberKind { Unknown, PriceCandidate, SensitiveCodeCandidate }
public enum TicketStatus { Open, Closed }
public enum ReportStatus { Open, Resolved, Dismissed }

public sealed record ParsedField<T>(T? Value, double Confidence, string? SourceText);
public sealed record DateRange(DateOnly Start, DateOnly End);
public sealed record PossibleNumber(string Text, NumberKind Kind);

public sealed class ParsedListingResult
{
    public required string OriginalText { get; init; }
    public ParsedField<ListingType> ListingType { get; set; } = new(FoodMarket.ListingType.Unknown, 0, null);
    public ParsedField<string> FoodName { get; set; } = new(null, 0, null);
    public ParsedField<MealType> Meal { get; set; } = new(MealType.Unknown, 0, null);
    public ParsedField<CafeteriaGender> CafeteriaGender { get; set; } = new(FoodMarket.CafeteriaGender.Unknown, 0, null);
    public ParsedField<string> CafeteriaLocation { get; set; } = new(null, 0, null);
    public ParsedField<long?> Price { get; set; } = new(null, 0, null);
    public double PriceConfidence => Price.Confidence;
    public ParsedField<DateOnly?> Date { get; set; } = new(null, 0, null);
    public ParsedField<DateRange> DateRange { get; set; } = new(null, 0, null);
    public ParsedField<Urgency> Urgency { get; set; } = new(FoodMarket.Urgency.Normal, 1, null);
    public ParsedField<string> OfferedFood { get; set; } = new(null, 0, null);
    public ParsedField<string> WantedFood { get; set; } = new(null, 0, null);
    public ParsedField<MealType> OfferedMeal { get; set; } = new(MealType.Unknown, 0, null);
    public ParsedField<MealType> WantedMeal { get; set; } = new(MealType.Unknown, 0, null);
    public ParsedField<long?> OptionalPriceDifference { get; set; } = new(null, 0, null);
    public ParsedField<string> PossibleSensitiveCode { get; set; } = new(null, 0, null);
    public List<PossibleNumber> Numbers { get; } = [];
    public double OverallConfidence { get; set; }
}

public interface IFoodListingParser
{
    Task<ParsedListingResult> ParseAsync(string text, CancellationToken cancellationToken);
}

public sealed class MarketOptions
{
    public string? BotToken { get; set; }
    public int BarePriceMultiplier { get; set; } = 1000;
    public string TimeZone { get; set; } = "Asia/Tehran";
    public TimeOnly BreakfastExpirationTime { get; set; } = new(10, 0);
    public TimeOnly LunchExpirationTime { get; set; } = new(16, 0);
    public TimeOnly DinnerExpirationTime { get; set; } = new(23, 30);
    public TimeOnly OtherExpirationTime { get; set; } = new(23, 59);
    public int DuplicateWindowMinutes { get; set; } = 60;
    public int NotificationCooldownMinutes { get; set; } = 60;
    public long AdminUserId { get; set; }
    public string? Socks5ProxyUrl { get; set; }
    public string DatabasePath { get; set; } = "foodmarket.db";
    public string[] InitialCafeterias { get; set; } = ["طرشت ۳", "کاله", "سلف مرکزی"];
}

public sealed class Advertisement
{
    public int Id { get; set; }
    public long OwnerId { get; set; }
    public string? OwnerUsername { get; set; }
    public ListingType Type { get; set; }
    public string? FoodName { get; set; }
    public MealType Meal { get; set; }
    public CafeteriaGender Gender { get; set; }
    public string? Location { get; set; }
    public long? Price { get; set; }
    public DateOnly? Date { get; set; }
    public DateRange? DateRange { get; set; }
    public Urgency Urgency { get; set; }
    public string? OfferedFood { get; set; }
    public string? WantedFood { get; set; }
    public MealType OfferedMeal { get; set; }
    public MealType WantedMeal { get; set; }
    public long? OptionalPriceDifference { get; set; }
    public string? Description { get; set; }
    public ListingStatus Status { get; set; } = ListingStatus.Active;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public bool MatchNotifications { get; set; }
    public long? GroupChatId { get; set; }
    public int? GroupMessageId { get; set; }
    public DateTime? LastNotificationUtc { get; set; }
}

public sealed class MarketUser
{
    public long Id { get; set; }
    public string? Username { get; set; }
    public bool Onboarded { get; set; }
    public int SuccessfulTransactions { get; set; }
    public int ConfirmedReports { get; set; }
    public int TrustScore { get; set; } = 50;
    public double Rating { get; set; }
    public DateTime? LastNotificationUtc { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
}

public sealed class MarketTransaction
{
    public int Id { get; set; }
    public int AdvertisementId { get; set; }
    public TransactionType Type { get; set; }
    public long OwnerId { get; set; }
    public long CounterpartyId { get; set; }
    public TransactionStatus Status { get; set; } = TransactionStatus.Pending;
    public bool OwnerDelivered { get; set; }
    public bool CounterpartyDelivered { get; set; }
    // Delivery code belongs to the private transaction, never to an advertisement.
    public string? FoodCode { get; set; }
    // For exchanges both participants may need to transfer their own code privately.
    public string? CounterpartyFoodCode { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Cafeteria
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class UserSession
{
    public long Id { get; set; }
    public string? DraftJson { get; set; }
    public string? EditingField { get; set; }
    public int? DuplicateId { get; set; }
    public string? SensitiveNumber { get; set; }
}

public sealed class SharedMessage
{
    public int Id { get; set; }
    public int AdvertisementId { get; set; }
    public string InlineMessageId { get; set; } = "";
    public long? GroupChatId { get; set; }
    public int? GroupMessageId { get; set; }
}

public sealed class InstalledGroup
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public long InstalledBy { get; set; }
    public bool Active { get; set; }
    public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;
}

public sealed class InlinePrefill
{
    public string Id { get; set; } = "";
    public string Food { get; set; } = "";
}

public sealed class ListingReport
{
    public int Id { get; set; }
    public int AdvertisementId { get; set; }
    public long ReporterId { get; set; }
    public string Reason { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public ReportStatus Status { get; set; } = ReportStatus.Open;
}

public sealed class SupportTicket
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SupportMessage
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public long SenderId { get; set; }
    public string Text { get; set; } = "";
    public long? AttachmentChatId { get; set; }
    public int? AttachmentMessageId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class RuntimeSettings
{
    public int Id { get; set; } = 1;
    public int BarePriceMultiplier { get; set; }
    public string TimeZone { get; set; } = "Asia/Tehran";
    public TimeOnly BreakfastExpirationTime { get; set; }
    public TimeOnly LunchExpirationTime { get; set; }
    public TimeOnly DinnerExpirationTime { get; set; }
    public TimeOnly OtherExpirationTime { get; set; }
    public int DuplicateWindowMinutes { get; set; }
    public int NotificationCooldownMinutes { get; set; }
}

public sealed class TransactionRating
{
    public int Id { get; set; }
    public int TransactionId { get; set; }
    public long RaterId { get; set; }
    public long RecipientId { get; set; }
    public int Stars { get; set; }
}
