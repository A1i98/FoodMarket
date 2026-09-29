using System.Globalization;

namespace FoodMarket;

public static class PersianDateFormatter
{
    private static readonly PersianCalendar Calendar = new();

    public static string Format(DateOnly date)
    {
        var day = date.ToDateTime(TimeOnly.MinValue);
        var value = $"{Calendar.GetYear(day):0000}/{Calendar.GetMonth(day):00}/{Calendar.GetDayOfMonth(day):00}";
        return string.Concat(value.Select(c => c is >= '0' and <= '9' ? (char)(c - '0' + '۰') : c));
    }

    public static string Format(DateRange range) => $"{Format(range.Start)} تا {Format(range.End)}";

    public static string Format(DateOnly? date, DateRange? range, string? relative = null) => range is not null
        ? $"{(relative is null ? "" : relative + " · ")}{Format(range)}"
        : date.HasValue ? $"{(relative is null ? "" : relative + " · ")}{Format(date.Value)}" : "نامشخص";
}
