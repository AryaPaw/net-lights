//! Explicit UI language and bilingual runtime text. Persisted observations stay language-neutral.
use std::{fmt, ops::Deref};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Language {
    Ru,
    En,
}

impl Language {
    pub fn resolve(setting: &str) -> Self {
        match setting {
            "ru" => Self::Ru,
            "en" => Self::En,
            _ => Self::system(),
        }
    }
    pub fn system() -> Self {
        #[cfg(windows)]
        {
            // Display/UI language, independent of region, keyboard and number formats.
            let id = unsafe { windows::Win32::Globalization::GetUserDefaultUILanguage() };
            Self::from_ui_language(id)
        }
        #[cfg(not(windows))]
        Self::En
    }
    fn from_ui_language(id: u16) -> Self {
        if id & 0x03ff == 0x0019 {
            Self::Ru
        } else {
            Self::En
        }
    }
    pub fn choose<'a>(self, ru: &'a str, en: &'a str) -> &'a str {
        match self {
            Self::Ru => ru,
            Self::En => en,
        }
    }
    pub fn duration(self, milliseconds: i64) -> String {
        let seconds = milliseconds.max(0) / 1000;
        if seconds < 60 {
            format!("{seconds} {}", self.choose("с", "s"))
        } else if seconds < 3600 {
            format!("{} {}", seconds / 60, self.choose("мин", "min"))
        } else if seconds < 86400 {
            format!(
                "{} {} {} {}",
                seconds / 3600,
                self.choose("ч", "h"),
                seconds % 3600 / 60,
                self.choose("мин", "min")
            )
        } else {
            format!(
                "{} {} {} {}",
                seconds / 86400,
                self.choose("д", "d"),
                seconds % 86400 / 3600,
                self.choose("ч", "h")
            )
        }
    }
    pub fn bytes(self, bytes: u64) -> String {
        if bytes >= 1024 * 1024 * 1024 {
            format!(
                "{:.2} {}",
                bytes as f64 / (1024.0 * 1024.0 * 1024.0),
                self.choose("ГиБ", "GiB")
            )
        } else if bytes >= 1024 * 1024 {
            format!(
                "{:.1} {}",
                bytes as f64 / (1024.0 * 1024.0),
                self.choose("МиБ", "MiB")
            )
        } else if bytes >= 1024 {
            format!("{:.1} {}", bytes as f64 / 1024.0, self.choose("КиБ", "KiB"))
        } else {
            format!("{bytes} {}", self.choose("Б", "B"))
        }
    }
    pub fn country_name(self, code: &str) -> &str {
        match self {
            Self::Ru => crate::countries::name(code),
            Self::En => ENGLISH_COUNTRIES
                .binary_search_by_key(&code, |(iso, _)| *iso)
                .map(|i| ENGLISH_COUNTRIES[i].1)
                .unwrap_or(code),
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct LocalizedText {
    ru: String,
    en: String,
}
impl LocalizedText {
    pub fn new(ru: impl Into<String>, en: impl Into<String>) -> Self {
        Self {
            ru: ru.into(),
            en: en.into(),
        }
    }
    pub fn get(&self, language: Language) -> &str {
        language.choose(&self.ru, &self.en)
    }
}
// Legacy consumers and assertions remain Russian; UI consumers select explicitly.
impl Deref for LocalizedText {
    type Target = str;
    fn deref(&self) -> &str {
        &self.ru
    }
}
impl fmt::Display for LocalizedText {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.ru)
    }
}
impl From<String> for LocalizedText {
    fn from(value: String) -> Self {
        Self::new(value.clone(), value)
    }
}
impl From<&str> for LocalizedText {
    fn from(value: &str) -> Self {
        Self::new(value, value)
    }
}
impl PartialEq<&str> for LocalizedText {
    fn eq(&self, other: &&str) -> bool {
        self.ru == *other
    }
}

// Generated once from the installed RegionInfo.EnglishName table; no runtime .NET dependency.
const ENGLISH_COUNTRIES: &[(&str, &str)] = &[
    ("AD", "Andorra"),
    ("AE", "United Arab Emirates"),
    ("AF", "Afghanistan"),
    ("AG", "Antigua & Barbuda"),
    ("AI", "Anguilla"),
    ("AL", "Albania"),
    ("AM", "Armenia"),
    ("AO", "Angola"),
    ("AQ", "Antarctica"),
    ("AR", "Argentina"),
    ("AS", "American Samoa"),
    ("AT", "Austria"),
    ("AU", "Australia"),
    ("AW", "Aruba"),
    ("AX", "Åland Islands"),
    ("AZ", "Azerbaijan"),
    ("BA", "Bosnia & Herzegovina"),
    ("BB", "Barbados"),
    ("BD", "Bangladesh"),
    ("BE", "Belgium"),
    ("BF", "Burkina Faso"),
    ("BG", "Bulgaria"),
    ("BH", "Bahrain"),
    ("BI", "Burundi"),
    ("BJ", "Benin"),
    ("BL", "St. Barthélemy"),
    ("BM", "Bermuda"),
    ("BN", "Brunei"),
    ("BO", "Bolivia"),
    ("BQ", "Bonaire, Sint Eustatius and Saba"),
    ("BR", "Brazil"),
    ("BS", "Bahamas"),
    ("BT", "Bhutan"),
    ("BV", "Bouvet Island"),
    ("BW", "Botswana"),
    ("BY", "Belarus"),
    ("BZ", "Belize"),
    ("CA", "Canada"),
    ("CC", "Cocos (Keeling) Islands"),
    ("CD", "Congo (DRC)"),
    ("CF", "Central African Republic"),
    ("CG", "Congo"),
    ("CH", "Switzerland"),
    ("CI", "Côte d’Ivoire"),
    ("CK", "Cook Islands"),
    ("CL", "Chile"),
    ("CM", "Cameroon"),
    ("CN", "China"),
    ("CO", "Colombia"),
    ("CR", "Costa Rica"),
    ("CS", "Serbia"),
    ("CU", "Cuba"),
    ("CV", "Cabo Verde"),
    ("CW", "Curaçao"),
    ("CX", "Christmas Island"),
    ("CY", "Cyprus"),
    ("CZ", "Czechia"),
    ("DE", "Germany"),
    ("DJ", "Djibouti"),
    ("DK", "Denmark"),
    ("DM", "Dominica"),
    ("DO", "Dominican Republic"),
    ("DZ", "Algeria"),
    ("EC", "Ecuador"),
    ("EE", "Estonia"),
    ("EG", "Egypt"),
    ("ER", "Eritrea"),
    ("ES", "Spain"),
    ("ET", "Ethiopia"),
    ("FI", "Finland"),
    ("FJ", "Fiji"),
    ("FK", "Falkland Islands"),
    ("FM", "Micronesia"),
    ("FO", "Faroe Islands"),
    ("FR", "France"),
    ("GA", "Gabon"),
    ("GB", "United Kingdom"),
    ("GD", "Grenada"),
    ("GE", "Georgia"),
    ("GF", "French Guiana"),
    ("GG", "Guernsey"),
    ("GH", "Ghana"),
    ("GI", "Gibraltar"),
    ("GL", "Greenland"),
    ("GM", "Gambia"),
    ("GN", "Guinea"),
    ("GP", "Guadeloupe"),
    ("GQ", "Equatorial Guinea"),
    ("GR", "Greece"),
    ("GS", "South Georgia & South Sandwich Islands"),
    ("GT", "Guatemala"),
    ("GU", "Guam"),
    ("GW", "Guinea-Bissau"),
    ("GY", "Guyana"),
    ("HK", "Hong Kong SAR"),
    ("HM", "Heard & McDonald Islands"),
    ("HN", "Honduras"),
    ("HR", "Croatia"),
    ("HT", "Haiti"),
    ("HU", "Hungary"),
    ("ID", "Indonesia"),
    ("IE", "Ireland"),
    ("IL", "Israel"),
    ("IM", "Isle of Man"),
    ("IN", "India"),
    ("IO", "British Indian Ocean Territory"),
    ("IQ", "Iraq"),
    ("IR", "Iran"),
    ("IS", "Iceland"),
    ("IT", "Italy"),
    ("IV", "Invariant Country"),
    ("JE", "Jersey"),
    ("JM", "Jamaica"),
    ("JO", "Jordan"),
    ("JP", "Japan"),
    ("KE", "Kenya"),
    ("KG", "Kyrgyzstan"),
    ("KH", "Cambodia"),
    ("KI", "Kiribati"),
    ("KM", "Comoros"),
    ("KN", "St. Kitts & Nevis"),
    ("KP", "North Korea"),
    ("KR", "Korea"),
    ("KW", "Kuwait"),
    ("KY", "Cayman Islands"),
    ("KZ", "Kazakhstan"),
    ("LA", "Laos"),
    ("LB", "Lebanon"),
    ("LC", "St. Lucia"),
    ("LI", "Liechtenstein"),
    ("LK", "Sri Lanka"),
    ("LR", "Liberia"),
    ("LS", "Lesotho"),
    ("LT", "Lithuania"),
    ("LU", "Luxembourg"),
    ("LV", "Latvia"),
    ("LY", "Libya"),
    ("MA", "Morocco"),
    ("MC", "Monaco"),
    ("MD", "Moldova"),
    ("ME", "Montenegro"),
    ("MF", "St. Martin"),
    ("MG", "Madagascar"),
    ("MH", "Marshall Islands"),
    ("MK", "North Macedonia"),
    ("ML", "Mali"),
    ("MM", "Myanmar"),
    ("MN", "Mongolia"),
    ("MO", "Macao SAR"),
    ("MP", "Northern Mariana Islands"),
    ("MQ", "Martinique"),
    ("MR", "Mauritania"),
    ("MS", "Montserrat"),
    ("MT", "Malta"),
    ("MU", "Mauritius"),
    ("MV", "Maldives"),
    ("MW", "Malawi"),
    ("MX", "Mexico"),
    ("MY", "Malaysia"),
    ("MZ", "Mozambique"),
    ("NA", "Namibia"),
    ("NC", "New Caledonia"),
    ("NE", "Niger"),
    ("NF", "Norfolk Island"),
    ("NG", "Nigeria"),
    ("NI", "Nicaragua"),
    ("NL", "Netherlands"),
    ("NO", "Norway"),
    ("NP", "Nepal"),
    ("NR", "Nauru"),
    ("NU", "Niue"),
    ("NZ", "New Zealand"),
    ("OM", "Oman"),
    ("PA", "Panama"),
    ("PE", "Peru"),
    ("PF", "French Polynesia"),
    ("PG", "Papua New Guinea"),
    ("PH", "Philippines"),
    ("PK", "Pakistan"),
    ("PL", "Poland"),
    ("PM", "St. Pierre & Miquelon"),
    ("PN", "Pitcairn Islands"),
    ("PR", "Puerto Rico"),
    ("PS", "Palestinian Authority"),
    ("PT", "Portugal"),
    ("PW", "Palau"),
    ("PY", "Paraguay"),
    ("QA", "Qatar"),
    ("RE", "Réunion"),
    ("RO", "Romania"),
    ("RS", "Serbia"),
    ("RU", "Russia"),
    ("RW", "Rwanda"),
    ("SA", "Saudi Arabia"),
    ("SB", "Solomon Islands"),
    ("SC", "Seychelles"),
    ("SD", "Sudan"),
    ("SE", "Sweden"),
    ("SG", "Singapore"),
    ("SH", "St Helena, Ascension, Tristan da Cunha"),
    ("SI", "Slovenia"),
    ("SJ", "Svalbard & Jan Mayen"),
    ("SK", "Slovakia"),
    ("SL", "Sierra Leone"),
    ("SM", "San Marino"),
    ("SN", "Senegal"),
    ("SO", "Somalia"),
    ("SR", "Suriname"),
    ("SS", "South Sudan"),
    ("ST", "São Tomé & Príncipe"),
    ("SV", "El Salvador"),
    ("SX", "Sint Maarten"),
    ("SY", "Syria"),
    ("SZ", "Eswatini"),
    ("TC", "Turks & Caicos Islands"),
    ("TD", "Chad"),
    ("TF", "French Southern Territories"),
    ("TG", "Togo"),
    ("TH", "Thailand"),
    ("TJ", "Tajikistan"),
    ("TK", "Tokelau"),
    ("TL", "Timor-Leste"),
    ("TM", "Turkmenistan"),
    ("TN", "Tunisia"),
    ("TO", "Tonga"),
    ("TR", "Türkiye"),
    ("TT", "Trinidad & Tobago"),
    ("TV", "Tuvalu"),
    ("TW", "Taiwan"),
    ("TZ", "Tanzania"),
    ("UA", "Ukraine"),
    ("UG", "Uganda"),
    ("UM", "U.S. Outlying Islands"),
    ("US", "United States"),
    ("UY", "Uruguay"),
    ("UZ", "Uzbekistan"),
    ("VA", "Vatican City"),
    ("VC", "St. Vincent & Grenadines"),
    ("VE", "Venezuela"),
    ("VG", "British Virgin Islands"),
    ("VI", "U.S. Virgin Islands"),
    ("VN", "Vietnam"),
    ("VU", "Vanuatu"),
    ("WF", "Wallis & Futuna"),
    ("WS", "Samoa"),
    ("XK", "Kosovo"),
    ("YE", "Yemen"),
    ("YT", "Mayotte"),
    ("ZA", "South Africa"),
    ("ZM", "Zambia"),
    ("ZW", "Zimbabwe"),
];

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn language_uses_ui_language_family_and_explicit_choice_overrides_system() {
        assert_eq!(Language::resolve("ru"), Language::Ru);
        assert_eq!(Language::resolve("en"), Language::En);
        assert_eq!(Language::resolve("system"), Language::system());
        assert_eq!(Language::from_ui_language(0x0419), Language::Ru);
        assert_eq!(Language::from_ui_language(0x0819), Language::Ru);
        assert_eq!(Language::from_ui_language(0x0409), Language::En);
        assert_eq!(Language::from_ui_language(0x041f), Language::En);
    }
    #[test]
    fn dynamic_text_switches_without_rebuilding_or_translating_interpolated_values() {
        let value = LocalizedText::new("Проверка 17: 20 мс", "Check 17: 20 ms");
        assert_eq!(value.get(Language::Ru), "Проверка 17: 20 мс");
        assert_eq!(value.get(Language::En), "Check 17: 20 ms");
        assert_eq!(value.to_string(), "Проверка 17: 20 мс");
        assert!(value.contains("17"));
        assert_eq!(LocalizedText::default().get(Language::En), "");
    }
    #[test]
    fn units_and_country_tables_cover_both_languages_and_preserve_unknown_codes() {
        for (value, ru, en) in [
            (0, "0 с", "0 s"),
            (61_000, "1 мин", "1 min"),
            (3_661_000, "1 ч 1 мин", "1 h 1 min"),
            (90_000_000, "1 д 1 ч", "1 d 1 h"),
        ] {
            assert_eq!(Language::Ru.duration(value), ru);
            assert_eq!(Language::En.duration(value), en);
        }
        for (value, ru, en) in [
            (17, "17 Б", "17 B"),
            (1024, "1.0 КиБ", "1.0 KiB"),
            (1048576, "1.0 МиБ", "1.0 MiB"),
            (1073741824, "1.00 ГиБ", "1.00 GiB"),
        ] {
            assert_eq!(Language::Ru.bytes(value), ru);
            assert_eq!(Language::En.bytes(value), en);
        }
        assert_eq!(Language::Ru.duration(-1), "0 с");
        assert_eq!(Language::Ru.country_name("DE"), "Германия");
        for (code, name) in [
            ("AQ", "Антарктида"),
            ("AS", "Американское Самоа"),
            ("YT", "Майотта"),
            ("ZW", "Зимбабве"),
            ("AE", "ОАЭ"),
            ("US", "США"),
            ("ZA", "ЮАР"),
        ] {
            assert_eq!(Language::Ru.country_name(code), name);
        }
        assert_eq!(Language::En.country_name("DE"), "Germany");
        assert_eq!(Language::En.country_name("XK"), "Kosovo");
        assert_eq!(Language::En.country_name("???"), "???");
        assert_eq!(ENGLISH_COUNTRIES.len(), crate::countries::COUNTRIES.len());
        for ((expected, _), (actual, name)) in
            crate::countries::COUNTRIES.iter().zip(ENGLISH_COUNTRIES)
        {
            assert_eq!(expected, actual);
            assert!(!name.is_empty());
        }
    }
}
