namespace FoodMarket;

public static class InlineListingPreview
{
    public static string Title(ParsedListingResult parsed) => parsed.ListingType.Value switch
    {
        ListingType.Sell => $"👁 پیش‌نمایش فروش {parsed.FoodName.Value ?? Meal(parsed.Meal.Value)}",
        ListingType.Buy => $"👁 پیش‌نمایش خرید {parsed.FoodName.Value ?? Meal(parsed.Meal.Value)}",
        ListingType.Exchange => "👁 پیش‌نمایش معاوضه",
        _ => $"👁 پیش‌نمایش جستجو {parsed.FoodName.Value ?? Meal(parsed.Meal.Value)}"
    };

    public static string Format(ParsedListingResult parsed, MarketOptions? options = null, string? draftToken = null)
    {
        options ??= new MarketOptions();
        var type = parsed.ListingType.Value switch
        {
            ListingType.Sell => "💰 فروش", ListingType.Buy => "🛒 خرید",
            ListingType.Exchange => "🔄 معاوضه", _ => "🔍 جستجو"
        };
        var lines = new List<string> { "👁 پیش‌نمایش کوتاه", $"نوع: {type}" };
        if (parsed.ListingType.Value == ListingType.Exchange)
        {
            if (parsed.OfferedFood.Value is not null) lines.Add($"🍛 می‌دهم: {parsed.OfferedFood.Value}");
            if (parsed.WantedFood.Value is not null) lines.Add($"🔁 می‌خواهم: {parsed.WantedFood.Value}");
        }
        else if (parsed.FoodName.Value is not null) lines.Add($"🍛 غذا: {parsed.FoodName.Value}");
        if (parsed.Meal.Value != MealType.Unknown) lines.Add($"🍽 {Meal(parsed.Meal.Value)}");
        if (parsed.CafeteriaGender.Value != CafeteriaGender.Unknown)
            lines.Add($"سلف: {Gender(parsed.CafeteriaGender.Value)}");
        if (parsed.CafeteriaLocation.Value is not null) lines.Add($"📍 {parsed.CafeteriaLocation.Value}");
        if (parsed.Price.Value is { } price) lines.Add($"💵 {PersianText.Digits(price)} تومان");
        else if (parsed.ListingType.Value is ListingType.Sell or ListingType.Buy) lines.Add("💵 قیمت: توافقی / نامشخص");
        if (parsed.DateRange.Value is not null) lines.Add($"📅 {PersianDateFormatter.Format(parsed.Date.Value, parsed.DateRange.Value, parsed.DateRange.SourceText)}");
        else if (parsed.Date.Value.HasValue) lines.Add($"📅 {PersianDateFormatter.Format(parsed.Date.Value, null, parsed.Date.SourceText ?? "احتمالاً امروز")}");
        lines.Add(DraftId(draftToken));
        lines.Add("📝 برای ادامه، دکمهٔ زیر را بزن.");
        return string.Join("\n", lines);
    }

    public static string CreationTitle(ListingType type, ParsedListingResult parsed)
    {
        var name = parsed.FoodName.Value ?? (parsed.Meal.Value == MealType.Unknown ? "غذا" : Meal(parsed.Meal.Value));
        return type switch
        {
            ListingType.Sell => $"➕ #فروشی {name}",
            ListingType.Buy => $"🛒 #خریدار {name}",
            _ => $"🔄 #معاوضه {name}"
        };
    }

    public static string FormatCreation(ListingType type, ParsedListingResult parsed, MarketOptions? options = null, string? draftToken = null)
    {
        options ??= new MarketOptions();
        var lines = new List<string> { CreationTitle(type, parsed) };
        if (type == ListingType.Exchange)
        {
            lines.Add($"🍛 می‌دهم: {parsed.OfferedFood.Value ?? parsed.FoodName.Value ?? "نامشخص"}");
            lines.Add($"🔁 می‌خواهم: {parsed.WantedFood.Value ?? "نامشخص"}");
        }
        else lines.Add($"🍛 غذا: {parsed.FoodName.Value ?? (type == ListingType.Buy ? "فرقی ندارد" : "نامشخص")}");
        lines.Add($"🍽 وعده: {(parsed.Meal.Value == MealType.Unknown ? "نامشخص" : Meal(parsed.Meal.Value))}");
        lines.Add($"سلف: {(parsed.CafeteriaGender.Value == CafeteriaGender.Unknown ? "نامشخص" : Gender(parsed.CafeteriaGender.Value))}" +
            (parsed.CafeteriaLocation.Value is null ? "" : $" · {parsed.CafeteriaLocation.Value}"));
        if (parsed.Price.Value is { } price) lines.Add($"💵 {PersianText.Digits(price)} تومان");
        else if (type != ListingType.Exchange) lines.Add("💵 قیمت: توافقی / نامشخص");
        if (parsed.DateRange.Value is not null) lines.Add($"📅 {PersianDateFormatter.Format(parsed.Date.Value, parsed.DateRange.Value, parsed.DateRange.SourceText)}");
        else lines.Add($"📅 {PersianDateFormatter.Format(parsed.Date.Value, null, parsed.Date.SourceText ?? "احتمالاً امروز")}");
        if (parsed.Urgency.Value == Urgency.High) lines.Add("⚡ فوری");
        lines.Add(DraftId(draftToken));
        lines.Add("📝 پیش‌نویس است؛ برای انتشار نهایی دکمهٔ زیر را بزن.");
        return string.Join("\n", lines);
    }

    private static string Meal(MealType meal) => meal switch
    {
        MealType.Breakfast => "صبحانه", MealType.Lunch => "ناهار", MealType.Dinner => "شام", _ => "غذا"
    };

    private static string Gender(CafeteriaGender gender) => gender switch
    {
        CafeteriaGender.Men => "آقایان", CafeteriaGender.Women => "بانوان", _ => "مختلط"
    };

    private static string DraftId(string? token) => token is { Length: >= 8 }
        ? $"🆔 پیش‌نویس: #D{token[..8].ToUpperInvariant()}"
        : "🆔 شناسهٔ آگهی پس از ثبت ساخته می‌شود";
}
