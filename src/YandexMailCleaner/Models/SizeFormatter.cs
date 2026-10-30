namespace YandexMailCleaner.Models;

public static class SizeFormatter
{
    private static readonly string[] Units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };

    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 Б";

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {Units[unit]}"
            : $"{value:0.#} {Units[unit]}";
    }
}
