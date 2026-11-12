use gpui_kit::{App, Global};
use netlights_core::locale::Language;

/// Derived from the root view's persisted language setting. This GPUI global
/// lets shared presentation helpers use the same language as the owning view.
pub struct UiLanguage(pub Language);
impl Global for UiLanguage {}
pub fn language(cx: &App) -> Language {
    cx.try_global::<UiLanguage>()
        .map(|l| l.0)
        .unwrap_or(Language::Ru)
}
pub fn tr(cx: &App, key: &str) -> String {
    translate(language(cx), key).to_string()
}
pub fn translate(lang: Language, key: &str) -> &str {
    if lang == Language::Ru {
        return key;
    }
    TEXT.iter()
        .find(|(ru, _)| *ru == key)
        .map(|(_, en)| *en)
        .unwrap_or(key)
}
const TEXT: &[(&str, &str)] = &[
    (
        "Недостаточно данных для оценки доступности",
        "Not enough data to estimate availability",
    ),
    ("Состояние", "Status"),
    ("Локации", "Locations"),
    ("Диагностика", "Diagnostics"),
    ("Параметры", "Settings"),
    ("О программе", "About"),
    ("Скопировать версию", "Copy version"),
    ("Версия скопирована.", "Version copied."),
    ("История версий", "Release history"),
    ("Текущая сборка", "Current build"),
    (
        "Rust и GPUI Kit. Нативный интерфейс Windows.",
        "Rust and GPUI Kit. Native Windows interface.",
    ),
    ("Данные приложения", "Application data"),
    (
        "Настройки и обе истории сохраняются локально. Срок хранения истории – 7 дней.",
        "Settings and both histories are saved locally. History is kept for 7 days.",
    ),
    ("Открыть папку", "Open folder"),
    ("Открытые компоненты", "Open-source components"),
    (
        "GPUI Kit и источники геоданных: условия использования и файлы лицензий.",
        "GPUI Kit and geodata sources: terms and license files.",
    ),
    ("Доступность", "Availability"),
    ("История доступности", "Availability history"),
    ("Последние события", "Recent events"),
    ("Текущая диагностика", "Current diagnostics"),
    ("Провайдер", "Provider"),
    ("Мир", "World"),
    ("Доступно", "Available"),
    ("Ограничено", "Limited"),
    ("Нет ответа", "No response"),
    ("Нет свежих данных", "No recent data"),
    ("Неизвестно", "Unknown"),
    ("Пауза", "Paused"),
    ("Нет записей", "No records"),
    (
        "В выбранном периоде нет наблюдений",
        "No observations in the selected period",
    ),
    ("Выбранное событие", "Selected event"),
    ("Увеличивать короткие отметки", "Enlarge short events"),
    (
        "Увеличивает короткие события только на графике. В подсказке показана фактическая длительность.",
        "Makes short events wider on the chart only. Tooltips show their actual duration.",
    ),
    ("Локальная ошибка монитора.", "Internal monitor error."),
    ("Нет сетевого подключения.", "No network connection."),
    (
        "Не менее двух независимых HTTPS-подтверждений.",
        "At least two independent HTTPS confirmations.",
    ),
    (
        "Одно независимое HTTPS-подтверждение.",
        "One independent HTTPS confirmation.",
    ),
    (
        "Контрольные HTTPS-направления недоступны.",
        "The monitored HTTPS destinations are unavailable.",
    ),
    (
        "Недостаточно свежих доказательств.",
        "Not enough recent evidence.",
    ),
    ("Проверки и трафик", "Checks and traffic"),
    ("Доступность сайтов", "Website availability"),
    (
        "По умолчанию каждые 5 секунд. Более редкие проверки уменьшают трафик.",
        "Every 5 seconds by default. Less frequent checks reduce traffic.",
    ),
    ("Интервал доступности", "Availability interval"),
    ("Определение страны", "Country detection"),
    (
        "По умолчанию каждые 10 секунд.",
        "Every 10 seconds by default.",
    ),
    ("Интервал страны", "Country interval"),
    ("Предупреждать о трафике", "Traffic warning"),
    (
        "После 10 минут измерения. Загрузки баз и установщиков исключены.",
        "After a 10-minute baseline. Database and installer downloads are excluded.",
    ),
    ("Порог трафика", "Traffic threshold"),
    ("Локация", "Location"),
    ("Определять страну", "Detect country"),
    (
        "Онлайн по публичному IP; локальные базы доступны для сравнения.",
        "Online using the public IP; local databases are available for comparison.",
    ),
    ("Значок страны в трее", "Country tray icon"),
    (
        "История сохраняется независимо от значка.",
        "History is saved regardless of the tray icon.",
    ),
    ("Значок страны", "Country icon"),
    ("Размер букв", "Letter size"),
    (
        "Для значка страны в области уведомлений.",
        "For the country icon in the notification area.",
    ),
    ("Приложение", "Application"),
    ("Автозапуск", "Start with Windows"),
    (
        "Доступен в установленной версии, вне тестового профиля.",
        "Available in the installed version, outside a test profile.",
    ),
    (
        "Доступно в установленной версии, вне тестового профиля.",
        "Available in the installed version, outside a test profile.",
    ),
    (
        "Запускать вместе с Windows.",
        "Start automatically when you sign in to Windows.",
    ),
    ("Автообновление", "Automatic updates"),
    (
        "Обновления с проверкой и восстановлением.",
        "Verified updates with recovery.",
    ),
    ("Оформление", "Appearance"),
    (
        "Светлая, тёмная или системная тема.",
        "Light, dark, or system theme.",
    ),
    ("Язык интерфейса", "Display language"),
    (
        "Применяется сразу. История и настройки сохраняются.",
        "Applies immediately. History and settings are preserved.",
    ),
    ("Как в системе", "System default"),
    ("Как в Windows", "System default"),
    ("Светлая", "Light"),
    ("Тёмная", "Dark"),
    ("После замера", "After baseline"),
    ("64 МиБ за час", "64 MiB per hour"),
    ("256 МиБ за час", "256 MiB per hour"),
    ("1 ГиБ за час", "1 GiB per hour"),
    ("Маленькие", "Small"),
    ("Средние", "Medium"),
    ("Крупные", "Large"),
    ("Открыть папку настроек", "Open settings folder"),
    ("Сбросить размер окна", "Reset window size"),
    ("Проверить обновление", "Check for updates"),
    ("Выйти из приложения", "Quit application"),
    (
        "Не удалось открыть папку настроек.",
        "Couldn't open the settings folder.",
    ),
    (
        "Windows не удалось применить размер окна.",
        "Windows couldn't apply the window size.",
    ),
    (
        "Rust и GPUI Kit. Данные хранятся локально 7 дней.",
        "Rust and GPUI Kit. History is stored locally for 7 days.",
    ),
    ("Источники и лицензии", "Sources and licenses"),
    (
        "GPUI Kit – Apache-2.0. V2Fly GeoIP – CC BY-SA 4.0. IPLocate ip-country – CC BY-SA 4.0. Онлайн-источники: country.is, резервный ipwho.is. Базы обновляются в фоне, результаты источников не равнозначны точному местоположению.",
        "GPUI Kit – Apache-2.0. V2Fly GeoIP – CC BY-SA 4.0. IPLocate ip-country – CC BY-SA 4.0. Online sources: country.is, with ipwho.is as fallback. Databases update in the background. Results don't represent an exact physical location.",
    ),
    ("Исходный код", "Source code"),
    ("Файлы лицензий", "License files"),
    (
        "Архив лицензий отсутствует рядом с программой. Он включён в готовую сборку.",
        "The license archive is missing beside the application. It's included in the published build.",
    ),
    ("Определение страны выключено", "Country detection is off"),
    (
        "Сохранённая история доступна. Включить новые проверки можно в параметрах.",
        "Saved history is available. Enable new checks in Settings.",
    ),
    ("История страны", "Country history"),
    ("Частота смен страны", "Country change frequency"),
    (
        "Голубой фон – нет наблюдений. Ноль – наблюдения есть, смен не было.",
        "Blue shading means no observations. Zero means observations without country changes.",
    ),
    ("Debug: источники определения", "Debug: detection sources"),
    (
        "Сохранённые результаты проверок. Старые онлайн-записи без имени источника не приписываются country.is.",
        "Recorded check results. Old online records without a source name aren't attributed to country.is.",
    ),
    ("Периоды наблюдения", "Observation periods"),
    (
        "В выбранном периоде нет записей.",
        "No records in the selected period.",
    ),
    ("1 проверка", "1 check"),
    ("  Текущая", "  Current"),
    ("Проверки адресов", "Endpoint checks"),
    (
        "Цвет состояния определяется HTTPS. Ручная проверка использует общие ограничения запросов.",
        "Status is determined by HTTPS checks. Manual checks share the same request limits.",
    ),
    (
        "Время проверки не подтверждено",
        "Check time isn't confirmed",
    ),
    ("Ещё не проверялся", "Not checked yet"),
    ("Адрес", "Address"),
    ("Проверяется", "Checking"),
    ("Проверить", "Check"),
    ("Debug: трафик приложения", "Debug: application traffic"),
    (
        "С начала запуска. TCP-данные с TLS; без IP-заголовков и повторной передачи. Загрузки баз и обновлений учитываются отдельно.",
        "Since startup. TCP data including TLS, excluding IP headers and retransmissions. Database and update downloads are counted separately.",
    ),
    ("Сохранение...", "Saving..."),
    ("Сохранить диагностику", "Export diagnostics"),
    (
        "Архив не содержит IP, полных URL, токенов и персональных данных.",
        "The archive excludes IPs, full URLs, tokens, and personal data.",
    ),
    ("Проверить сейчас", "Check now"),
    ("Продолжить", "Resume"),
    (
        "Сохранение истории и завершение...",
        "Saving history and shutting down...",
    ),
    (
        "Не удалось применить настройку: очередь занята. Повторите действие.",
        "Couldn't apply the setting: the queue is busy. Try again.",
    ),
    (
        "Команда не принята: повторите действие через секунду.",
        "The command wasn't accepted. Try again in a second.",
    ),
    (
        "Монитор остановлен из-за внутренней ошибки.",
        "The monitor stopped because of an internal error.",
    ),
    (
        "Монитор остановлен из-за внутренней ошибки. История сохранена до последней записи; перезапустите приложение. Подробности – в папке настроек, каталог logs.",
        "The monitor stopped because of an internal error. History is saved through the last record. Restart the application. Details are in the logs directory in the settings folder.",
    ),
    (
        "Не удалось подтвердить запуск после обновления.",
        "Couldn't confirm startup after the update.",
    ),
    ("Проверки провайдера", "Provider checks"),
    ("Проверки мира", "World checks"),
    ("Страна: country.is", "Country: country.is"),
    ("Страна: ipwho.is", "Country: ipwho.is"),
    ("База V2Fly", "V2Fly database"),
    ("База IPLocate", "IPLocate database"),
    ("Обновления", "Updates"),
    ("Прочее", "Other"),
    ("Не загружена", "Not loaded"),
    ("Монитор на паузе", "Monitor paused"),
    ("версия не записана", "version not recorded"),
    ("Последний известный IP", "Last known IP"),
    ("не записан", "not recorded"),
    ("\nИсточники расходятся.", "\nSources disagree."),
    (
        "\nОценка по базе; онлайн-страна не подтверждена.",
        "\nDatabase estimate; the online country isn't confirmed.",
    ),
    (
        "\nВ этом интервале нет наблюдений страны.",
        "\nNo country observations in this interval.",
    ),
];

#[cfg(test)]
mod tests {
    use super::{TEXT, translate};
    use netlights_core::locale::Language;
    #[test]
    fn translations_are_unambiguous_complete_pairs_without_cyrillic_english() {
        let mut keys = std::collections::HashSet::new();
        for (ru, en) in TEXT {
            assert!(keys.insert(*ru), "Duplicate translation key: {ru}");
            assert!(!en.is_empty());
            assert!(
                !en.chars()
                    .any(|c| ('А'..='я').contains(&c) || c == 'ё' || c == 'Ё'),
                "{en}"
            );
            assert_eq!(translate(Language::Ru, ru), *ru);
            assert_eq!(translate(Language::En, ru), *en);
        }
        assert_eq!(translate(Language::En, "country.is"), "country.is");
    }
}
