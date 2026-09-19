using System.Collections.Frozen;
using System.Globalization;

namespace NetLights.Core;

public static class GeoCountryNames
{
    public const int TrayTooltipMaxChars = 63;

    public static string Tooltip(string iso2)
    {
        if (!GeoCountryParsers.IsIso3166Alpha2(iso2))
        {
            return GeoCountryDisplay.Unconfirmed.Tooltip;
        }

        string text = Russian(iso2) + " / " + English(iso2);
        return text.Length <= TrayTooltipMaxChars ? text : text[..TrayTooltipMaxChars];
    }

    public static string English(string iso2)
    {
        try
        {
            string name = new RegionInfo(iso2).EnglishName;
            return string.IsNullOrWhiteSpace(name) ? iso2 : name;
        }
        catch (ArgumentException)
        {
            return iso2;
        }
    }

    public static string Russian(string iso2)
        => Names.TryGetValue(iso2, out string? ru) ? ru : English(iso2);

    private static readonly FrozenDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AD"] = "Андорра",
        ["AE"] = "ОАЭ",
        ["AF"] = "Афганистан",
        ["AG"] = "Антигуа и Барбуда",
        ["AI"] = "Ангилья",
        ["AL"] = "Албания",
        ["AM"] = "Армения",
        ["AO"] = "Ангола",
        ["AR"] = "Аргентина",
        ["AT"] = "Австрия",
        ["AU"] = "Австралия",
        ["AZ"] = "Азербайджан",
        ["BA"] = "Босния и Герцеговина",
        ["BB"] = "Барбадос",
        ["BD"] = "Бангладеш",
        ["BE"] = "Бельгия",
        ["BG"] = "Болгария",
        ["BH"] = "Бахрейн",
        ["BR"] = "Бразилия",
        ["BY"] = "Беларусь",
        ["BZ"] = "Белиз",
        ["CA"] = "Канада",
        ["CH"] = "Швейцария",
        ["CL"] = "Чили",
        ["CN"] = "Китай",
        ["CO"] = "Колумбия",
        ["CR"] = "Коста-Рика",
        ["CU"] = "Куба",
        ["CY"] = "Кипр",
        ["CZ"] = "Чехия",
        ["DE"] = "Германия",
        ["DK"] = "Дания",
        ["DO"] = "Доминиканская Республика",
        ["DZ"] = "Алжир",
        ["EC"] = "Эквадор",
        ["EE"] = "Эстония",
        ["EG"] = "Египет",
        ["ES"] = "Испания",
        ["FI"] = "Финляндия",
        ["FR"] = "Франция",
        ["GB"] = "Великобритания",
        ["GE"] = "Грузия",
        ["GR"] = "Греция",
        ["HK"] = "Гонконг",
        ["HR"] = "Хорватия",
        ["HU"] = "Венгрия",
        ["ID"] = "Индонезия",
        ["IE"] = "Ирландия",
        ["IL"] = "Израиль",
        ["IN"] = "Индия",
        ["IQ"] = "Ирак",
        ["IR"] = "Иран",
        ["IS"] = "Исландия",
        ["IT"] = "Италия",
        ["JP"] = "Япония",
        ["KE"] = "Кения",
        ["KG"] = "Кыргызстан",
        ["KR"] = "Южная Корея",
        ["KZ"] = "Казахстан",
        ["LT"] = "Литва",
        ["LU"] = "Люксембург",
        ["LV"] = "Латвия",
        ["MA"] = "Марокко",
        ["MD"] = "Молдова",
        ["ME"] = "Черногория",
        ["MK"] = "Северная Македония",
        ["MX"] = "Мексика",
        ["MY"] = "Малайзия",
        ["NG"] = "Нигерия",
        ["NL"] = "Нидерланды",
        ["NO"] = "Норвегия",
        ["NZ"] = "Новая Зеландия",
        ["PA"] = "Панама",
        ["PE"] = "Перу",
        ["PH"] = "Филиппины",
        ["PK"] = "Пакистан",
        ["PL"] = "Польша",
        ["PT"] = "Португалия",
        ["QA"] = "Катар",
        ["RO"] = "Румыния",
        ["RS"] = "Сербия",
        ["RU"] = "Россия",
        ["SA"] = "Саудовская Аравия",
        ["SE"] = "Швеция",
        ["SG"] = "Сингапур",
        ["SI"] = "Словения",
        ["SK"] = "Словакия",
        ["TH"] = "Таиланд",
        ["TR"] = "Турция",
        ["TW"] = "Тайвань",
        ["UA"] = "Украина",
        ["US"] = "США",
        ["UZ"] = "Узбекистан",
        ["VN"] = "Вьетнам",
        ["XK"] = "Косово",
        ["ZA"] = "ЮАР"
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
