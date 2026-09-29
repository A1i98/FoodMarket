using System.Text.RegularExpressions;

namespace FoodMarket;

public static class PersianText
{
    public static string Normalize(string input) => Regex.Replace(
        Regex.Replace(input.Replace('ي', 'ی').Replace('ك', 'ک').Replace('‌', ' ')
            .Replace('ۀ', 'ه').Replace('أ', 'ا').Replace('إ', 'ا')
            .Replace('۰', '0').Replace('۱', '1').Replace('۲', '2').Replace('۳', '3')
            .Replace('۴', '4').Replace('۵', '5').Replace('۶', '6').Replace('۷', '7')
            .Replace('۸', '8').Replace('۹', '9')
            .Replace('٠', '0').Replace('١', '1').Replace('٢', '2').Replace('٣', '3')
            .Replace('٤', '4').Replace('٥', '5').Replace('٦', '6').Replace('٧', '7')
            .Replace('٨', '8').Replace('٩', '9').Replace('#', ' '), @"\s+", " ").Trim(),
        @"\s+", " ");

    public static string Digits(long number) => string.Concat(number.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
        .Select(c => c is >= '0' and <= '9' ? (char)(c - '0' + '۰') : c));
}

public sealed class RuleBasedPersianFoodListingParser(
    MarketOptions options, Func<IReadOnlyList<string>> locations, TimeProvider? clock = null) : IFoodListingParser
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static readonly RegexOptions Flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly string[] KnownFoods = ["ساندویچ دونر و پنیر", "خورشت قیمه", "خورش قیمه", "قیمه", "کلانا", "ساندویچ"];
    private const string MenTerms = @"آقایان|اقایان|آقایون|اقایون|آقا|مردانه";
    private const string WomenTerms = @"بانوان|بانوا|خانم\s*ها|خانما|خانم|زنانه";

    public Task<ParsedListingResult> ParseAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = PersianText.Normalize(text);
        var result = new ParsedListingResult { OriginalText = text };

        var exchange = Regex.Match(normalized, @"(?<off>.+?)\s+دارم\s+با\s+(?<want>.+?)\s+(?:عوض\s*می\s*کنم|معاوضه\s*می\s*کنم|تعویض\s*می\s*کنم)", Flags);
        SetType(result, normalized, exchange.Success);
        MatchEnum(normalized, MenTerms, CafeteriaGender.Men, result, (r, v) => r.CafeteriaGender = v);
        var men = result.CafeteriaGender.Value == CafeteriaGender.Men;
        MatchEnum(normalized, WomenTerms, CafeteriaGender.Women, result, (r, v) => r.CafeteriaGender = v);
        if (men && result.CafeteriaGender.Value == CafeteriaGender.Women)
            result.CafeteriaGender = new(CafeteriaGender.Unknown, 0, "آقایان / بانوان");
        MatchEnum(normalized, @"مختلط", CafeteriaGender.Mixed, result, (r, v) => r.CafeteriaGender = v);
        MatchEnum(normalized, @"صبحانه", MealType.Breakfast, result, (r, v) => r.Meal = v);
        MatchEnum(normalized, @"ناهار|نهار", MealType.Lunch, result, (r, v) => r.Meal = v);
        MatchEnum(normalized, @"شام", MealType.Dinner, result, (r, v) => r.Meal = v);
        MatchEnum(normalized, @"سایر|متفرقه", MealType.Other, result, (r, v) => r.Meal = v);
        var urgent = Regex.Match(normalized, @"خیلی\s*فوری|فوری|الان\s*می\s*خوام", Flags);
        if (urgent.Success) result.Urgency = new(Urgency.High, .98, urgent.Value);

        foreach (var location in locations().OrderByDescending(l => l.Length))
        {
            var match = Regex.Match(normalized, @"(?<!\S)" + Regex.Escape(PersianText.Normalize(location)) + @"(?!\S)", Flags);
            if (!match.Success) continue;
            result.CafeteriaLocation = new(location, .96, match.Value);
            break;
        }

        ParseDate(normalized, result);
        ParseNumbers(normalized, result);
        if (result.Numbers.Count(n => n.Kind == NumberKind.PriceCandidate) > 1)
            result.Price = new(null, 0, "چند عدد احتمالی");
        if (result.ListingType.Value == ListingType.Exchange) result.OptionalPriceDifference = result.Price;
        if (exchange.Success)
        {
            var offered = CleanFood(exchange.Groups["off"].Value, result);
            var wanted = CleanFood(exchange.Groups["want"].Value, result);
            result.OfferedFood = new(offered, .9, exchange.Groups["off"].Value);
            result.WantedFood = new(wanted, .9, exchange.Groups["want"].Value);
            result.FoodName = result.OfferedFood;
            result.OfferedMeal = result.Meal;
            result.WantedMeal = result.Meal;
        }
        else
        {
            foreach (var food in KnownFoods)
            {
                var match = Regex.Match(normalized, @"(?<!\S)" + Regex.Escape(food) + @"(?!\S)", Flags);
                if (!match.Success) continue;
                result.FoodName = new(food, .96, match.Value);
                break;
            }
            // Preserve unrecognized food phrases as candidates rather than forcing a known dish.
            if (result.FoodName.Value is null)
            {
                var candidate = CleanFood(normalized, result);
                if (!string.IsNullOrWhiteSpace(candidate) && !Regex.IsMatch(candidate, @"^(سلف|خیلی|فوری)$"))
                    result.FoodName = new(candidate, .52, candidate);
            }
        }

        var coreConfidence = result.ListingType.Value == ListingType.Unknown ? .25
            : result.ListingType.Value == ListingType.Exchange &&
              (result.OfferedFood.Value is null || result.WantedFood.Value is null) ? .45
            : result.FoodName.Value is null && result.Meal.Value == MealType.Unknown ? .5
            : Math.Min(result.ListingType.Confidence, result.FoodName.Value is null ? result.Meal.Confidence : result.FoodName.Confidence);
        var confidences = new List<double> { coreConfidence };
        if (result.Price.Value.HasValue || result.Price.SourceText is not null) confidences.Add(result.Price.Confidence);
        if (result.CafeteriaGender.SourceText is not null) confidences.Add(result.CafeteriaGender.Confidence);
        if (result.DateRange.Value is not null) confidences.Add(result.DateRange.Confidence);
        else confidences.Add(result.Date.Confidence);
        if (result.PossibleSensitiveCode.Value is not null) confidences.Add(.5);
        result.OverallConfidence = confidences.Min();
        return Task.FromResult(result);
    }

    private static void SetType(ParsedListingResult r, string text, bool exchange)
    {
        var ex = Regex.Match(text, @"معاوضه|تعویض|عوض\s*می\s*کنم", Flags);
        var sell = Regex.Match(text, @"فروشی|فروش|می\s*فروشم", Flags);
        var buy = Regex.Match(text, @"خریدار|خریداری|خرید|می\s*خرم", Flags);
        if (exchange || ex.Success) r.ListingType = new(ListingType.Exchange, .98, ex.Value);
        else if (sell.Success && !buy.Success) r.ListingType = new(ListingType.Sell, .98, sell.Value);
        else if (buy.Success && !sell.Success) r.ListingType = new(ListingType.Buy, .98, buy.Value);
    }

    private static void MatchEnum<T>(string text, string pattern, T value, ParsedListingResult result,
        Action<ParsedListingResult, ParsedField<T>> assign)
    {
        var match = Regex.Match(text, @"(?<!\S)(?:" + pattern + @")(?!\S)", Flags);
        if (match.Success) assign(result, new(value, .97, match.Value));
    }

    private void ParseDate(string text, ParsedListingResult r)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), zone).DateTime);
        var nextWeek = Regex.Match(text, @"هفته\s*بعد", Flags);
        if (nextWeek.Success)
        {
            var daysToSaturday = ((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7;
            var start = today.AddDays(daysToSaturday == 0 ? 7 : daysToSaturday);
            r.DateRange = new(new(start, start.AddDays(6)), .9, nextWeek.Value);
            return;
        }
        var relative = Regex.Match(text, @"پس\s*فردا|فردا|امروز|شنبه|یکشنبه|دوشنبه|سه\s*شنبه|چهارشنبه|پنجشنبه|جمعه", Flags);
        if (!relative.Success)
        {
            // Default today is a candidate, not an explicit date supplied by the user.
            r.Date = new(today, .65, null);
            return;
        }
        var days = relative.Value.Replace(" ", "") switch
        {
            "امروز" => 0, "فردا" => 1, "پسفردا" => 2,
            "شنبه" => (7 + (int)DayOfWeek.Saturday - (int)today.DayOfWeek) % 7,
            "یکشنبه" => (7 + (int)DayOfWeek.Sunday - (int)today.DayOfWeek) % 7,
            "دوشنبه" => (7 + (int)DayOfWeek.Monday - (int)today.DayOfWeek) % 7,
            "سهشنبه" => (7 + (int)DayOfWeek.Tuesday - (int)today.DayOfWeek) % 7,
            "چهارشنبه" => (7 + (int)DayOfWeek.Wednesday - (int)today.DayOfWeek) % 7,
            "پنجشنبه" => (7 + (int)DayOfWeek.Thursday - (int)today.DayOfWeek) % 7,
            _ => (7 + (int)DayOfWeek.Friday - (int)today.DayOfWeek) % 7
        };
        r.Date = new(today.AddDays(days), .95, relative.Value);
    }

    private void ParseNumbers(string text, ParsedListingResult r)
    {
        foreach (Match match in Regex.Matches(text, @"(?<!\d)\d[\d,،]*(?!\d)"))
        {
            var number = match.Value.Replace(",", "").Replace("،", "");
            if (!long.TryParse(number, out var parsed)) continue;
            if (r.CafeteriaLocation.SourceText?.Contains(match.Value, StringComparison.Ordinal) == true) continue;
            var explicitPrice = Regex.IsMatch(text[..match.Index], @"(?:قیمت|تومان|تومن)\s*$", Flags)
                || Regex.IsMatch(text[(match.Index + match.Length)..], @"^\s*(?:هزار|تومان|تومن)", Flags);
            if (number.Length >= 5 && !explicitPrice)
            {
                r.Numbers.Add(new(match.Value, NumberKind.SensitiveCodeCandidate));
                r.PossibleSensitiveCode = new(match.Value, .95, match.Value);
            }
            else if (explicitPrice || number.Length is 2 or 3 && parsed is >= 10 and <= 999)
            {
                var thousands = number.Length <= 3 && parsed < 1000;
                r.Price = new(thousands ? parsed * 1000 : parsed, explicitPrice ? .9 : .7, match.Value);
                r.Numbers.Add(new(match.Value, NumberKind.PriceCandidate));
            }
            else r.Numbers.Add(new(match.Value, NumberKind.Unknown));
        }
    }

    private static string? CleanFood(string text, ParsedListingResult r)
    {
        text = PersianText.Normalize(text);
        foreach (var pattern in new[]
        {
            @"معاوضه|تعویض|فروشی|فروش|می\s*فروشم|خریدار|خریداری|خرید|می\s*خرم",
            MenTerms + "|" + WomenTerms + @"|مختلط|سلف",
            @"صبحانه|ناهار|نهار|شام|سایر|متفرقه|خیلی\s*فوری|فوری|الان\s*می\s*خوام",
            @"امروز|پس\s*فردا|فردا|هفته\s*بعد|شنبه|یکشنبه|دوشنبه|سه\s*شنبه|چهارشنبه|پنجشنبه|جمعه",
            @"\d[\d,،]*|قیمت|تومان|تومن|هزار"
        }) text = Regex.Replace(text, @"(?<!\S)(?:" + pattern + @")(?!\S)", " ", Flags);
        if (r.CafeteriaLocation.SourceText is { } location) text = text.Replace(location, " ", StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(text, @"\s+", " ").Trim(' ', '،', ',', '.', ':');
    }
}
