use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};
use url::Url;

pub const PERIODS: [u32; 8] = [1, 3, 6, 12, 24, 72, 120, 168];
pub const AVAILABILITY_INTERVALS: [u64; 10] = [2, 3, 4, 5, 10, 15, 30, 60, 120, 300];
pub const COUNTRY_INTERVALS: [u64; 4] = [10, 15, 30, 60];

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum Group {
    #[default]
    Ru,
    World,
}
impl Group {
    pub const ALL: [Self; 2] = [Self::Ru, Self::World];
    pub fn index(self) -> usize {
        if self == Self::Ru { 0 } else { 1 }
    }
    pub fn label(self) -> &'static str {
        if self == Self::Ru {
            "Провайдер"
        } else {
            "Мир"
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum Availability {
    Online,
    Limited,
    Offline,
    #[default]
    Unknown,
}
impl Availability {
    pub fn label(self) -> &'static str {
        match self {
            Self::Online => "Доступно",
            Self::Limited => "Ограничено",
            Self::Offline => "Нет ответа",
            Self::Unknown => "Нет свежих данных",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum Outcome {
    Reachable,
    Unreachable,
    Indeterminate,
    Cancelled,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Endpoint {
    pub id: String,
    #[serde(deserialize_with = "endpoint_group")]
    pub group: Group,
    pub uri: Url,
    pub infrastructure_id: String,
}
fn endpoint_group<'de, D: serde::Deserializer<'de>>(d: D) -> Result<Group, D::Error> {
    let name = String::deserialize(d)?;
    match name.to_lowercase().as_str() {
        "ru" | "рф" => Ok(Group::Ru),
        "world" => Ok(Group::World),
        _ => Err(serde::de::Error::custom("Unsupported endpoint group")),
    }
}

pub fn builtins() -> Vec<Endpoint> {
    [
        (
            "ru-yandex",
            Group::Ru,
            "https://yandex.ru/robots.txt",
            "yandex",
        ),
        ("ru-mail", Group::Ru, "https://www.mail.ru/robots.txt", "vk"),
        (
            "ru-rostelecom",
            Group::Ru,
            "https://www.rt.ru/robots.txt",
            "rostelecom",
        ),
        (
            "ru-selectel",
            Group::Ru,
            "https://selectel.ru/robots.txt",
            "selectel",
        ),
        (
            "ru-beeline",
            Group::Ru,
            "https://www.beeline.ru/robots.txt",
            "vimpelcom",
        ),
        (
            "ru-megafon",
            Group::Ru,
            "https://moscow.megafon.ru/robots.txt",
            "megafon",
        ),
        (
            "ru-timeweb",
            Group::Ru,
            "https://timeweb.com/robots.txt",
            "hll",
        ),
        (
            "world-cloudflare",
            Group::World,
            "https://cp.cloudflare.com/generate_204",
            "cloudflare",
        ),
        (
            "world-github",
            Group::World,
            "https://github.com/robots.txt",
            "github",
        ),
        (
            "world-wikimedia",
            Group::World,
            "https://en.wikipedia.org/robots.txt",
            "wikimedia",
        ),
        (
            "world-google",
            Group::World,
            "https://go.dev/robots.txt",
            "google",
        ),
        (
            "world-duckduckgo",
            Group::World,
            "https://duckduckgo.com/robots.txt",
            "duckduckgo",
        ),
        (
            "world-apache",
            Group::World,
            "https://www.apache.org/robots.txt",
            "fastly",
        ),
        (
            "world-arch",
            Group::World,
            "https://archlinux.org/",
            "haproxy",
        ),
    ]
    .into_iter()
    .map(|(id, group, uri, infra)| Endpoint {
        id: id.into(),
        group,
        uri: Url::parse(uri).expect("Built-in URL"),
        infrastructure_id: infra.into(),
    })
    .collect()
}

pub fn validate_endpoints(endpoints: &[Endpoint]) -> anyhow::Result<()> {
    anyhow::ensure!(
        endpoints.len() == 14,
        "Нужно по семь адресов в каждой группе."
    );
    let mut ids = HashSet::new();
    let mut urls = HashSet::new();
    for e in endpoints {
        anyhow::ensure!(
            !e.id.trim().is_empty() && ids.insert(e.id.clone()),
            "Повторяющийся или пустой идентификатор."
        );
        anyhow::ensure!(
            !e.infrastructure_id.trim().is_empty(),
            "Не задана инфраструктура адреса."
        );
        anyhow::ensure!(
            e.uri.scheme() == "https"
                && e.uri.host_str().is_some()
                && e.uri.username().is_empty()
                && e.uri.password().is_none(),
            "Нужен HTTPS без учётных данных."
        );
        let query = e.uri.query().unwrap_or_default().to_ascii_lowercase();
        anyhow::ensure!(
            !["token=", "key=", "secret=", "password="]
                .iter()
                .any(|s| query.contains(s)),
            "Адрес содержит секрет в параметрах."
        );
        let key = format!(
            "{}:{}{}",
            e.uri.host_str().unwrap_or_default(),
            e.uri.port_or_known_default().unwrap_or(443),
            e.uri.path().trim_end_matches('/')
        )
        .to_ascii_lowercase();
        anyhow::ensure!(urls.insert(key), "Повторяющийся адрес.");
    }
    for group in Group::ALL {
        let members: Vec<_> = endpoints.iter().filter(|e| e.group == group).collect();
        anyhow::ensure!(
            members.len() == 7
                && members
                    .iter()
                    .map(|e| &e.infrastructure_id)
                    .collect::<HashSet<_>>()
                    .len()
                    >= 3,
            "Нужно семь адресов и три независимые инфраструктуры в группе."
        );
    }
    Ok(())
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Settings {
    pub auto_start: bool,
    pub auto_update_enabled: bool,
    pub availability_polling_seconds: i64,
    pub country_polling_seconds: i64,
    pub emphasize_short_statuses: bool,
    pub show_location_debug_sources: bool,
    pub geo_country_detection_enabled: bool,
    pub geo_country_icon_enabled: bool,
    pub geo_country_letter_size: i32,
    pub location_chart_window_hours: i64,
    #[serde(default)]
    pub settings_version: i32,
    pub window_x: i32,
    pub window_y: i32,
    pub window_width: i32,
    pub window_height: i32,
    pub theme: String,
    pub language: String,
    #[serde(flatten)]
    pub extra: BTreeMap<String, serde_json::Value>,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            auto_start: true,
            auto_update_enabled: true,
            availability_polling_seconds: 5,
            country_polling_seconds: 10,
            emphasize_short_statuses: false,
            show_location_debug_sources: false,
            geo_country_detection_enabled: false,
            geo_country_icon_enabled: false,
            geo_country_letter_size: 1,
            location_chart_window_hours: 12,
            settings_version: 8,
            window_x: i32::MIN,
            window_y: i32::MIN,
            window_width: 1450,
            window_height: 1300,
            theme: "system".into(),
            language: "system".into(),
            extra: BTreeMap::new(),
        }
    }
}
impl Settings {
    pub fn normalize(&mut self) {
        if self.settings_version < 1 {
            self.auto_start = true;
        }
        if self.settings_version < 2 {
            self.geo_country_icon_enabled = false;
        }
        if self.settings_version < 3 {
            self.geo_country_detection_enabled = self.geo_country_icon_enabled;
        }
        if self.settings_version < 7
            && matches!(
                (self.window_width, self.window_height),
                (900, 800) | (1040, 920) | (1360, 1177) | (1400, 1200) | (1421, 1273)
            )
        {
            self.window_width = 1450;
            self.window_height = 1300;
        }
        if self.settings_version < 4 && (self.window_x, self.window_y) == (80, 80) {
            self.window_x = i32::MIN;
            self.window_y = i32::MIN;
        }
        if self.availability_polling_seconds < 0
            || !AVAILABILITY_INTERVALS.contains(&(self.availability_polling_seconds as u64))
        {
            self.availability_polling_seconds = 5;
        }
        if self.country_polling_seconds < 0
            || !COUNTRY_INTERVALS.contains(&(self.country_polling_seconds as u64))
        {
            self.country_polling_seconds = 10;
        }
        if !PERIODS
            .iter()
            .any(|&hours| i64::from(hours) == self.location_chart_window_hours)
        {
            self.location_chart_window_hours = 12;
        }
        if !(0..=2).contains(&self.geo_country_letter_size) {
            self.geo_country_letter_size = 1;
        }
        if !matches!(self.theme.as_str(), "system" | "light" | "dark") {
            self.theme = "system".into();
        }
        if !matches!(self.language.as_str(), "system" | "ru" | "en") {
            self.language = "system".into();
        }
        self.window_width = self.window_width.clamp(640, 7680);
        self.window_height = self.window_height.clamp(480, 4320);
        if !self.geo_country_detection_enabled {
            self.geo_country_icon_enabled = false;
        }
        if self.settings_version < 8 {
            self.show_location_debug_sources = false;
        }
        self.settings_version = self.settings_version.max(8);
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct EndpointStats {
    pub samples: u64,
    pub reachable: u64,
    pub unreachable: u64,
    pub indeterminate: u64,
    pub restricted: u64,
    pub timeouts: u64,
    pub dns_failures: u64,
    pub last_http: Option<u16>,
    pub last_failure: Option<String>,
}
#[derive(Clone, Debug)]
pub struct ProbeResult {
    pub outcome: Outcome,
    pub status: Option<u16>,
    pub failure: Option<&'static str>,
    pub elapsed_ms: u64,
    pub retry_after_ms: u64,
}
#[derive(Clone, Debug)]
pub struct EndpointView {
    pub endpoint: Endpoint,
    pub outcome: Option<Outcome>,
    pub http_status: Option<u16>,
    pub failure: Option<String>,
    pub elapsed_ms: Option<u64>,
    pub completed_utc: Option<DateTime<Utc>>,
    pub fresh: bool,
    pub in_flight: bool,
    pub pause_until_utc: Option<DateTime<Utc>>,
    pub stats: EndpointStats,
}
#[derive(Clone, Debug)]
pub struct GroupView {
    pub group: Group,
    pub availability: Availability,
    pub reason: String,
    pub confirmation: bool,
    pub endpoints: Vec<EndpointView>,
}
#[derive(Clone, Debug)]
pub struct MonitorSnapshot {
    pub epoch: u64,
    pub generated_utc: DateTime<Utc>,
    pub groups: [GroupView; 2],
    pub paused: bool,
    pub network_unavailable: bool,
    pub monitor_error: Option<String>,
    pub capacity_exhausted: bool,
}

#[cfg(test)]
mod tests {
    #[test]
    fn language_defaults_to_system_and_roundtrips_each_supported_choice() {
        let mut settings: super::Settings = serde_json::from_str("{}").unwrap();
        assert_eq!(settings.language, "system");
        for choice in ["system", "ru", "en"] {
            settings.language = choice.into();
            settings.normalize();
            let encoded = serde_json::to_vec(&settings).unwrap();
            let decoded: super::Settings = serde_json::from_slice(&encoded).unwrap();
            assert_eq!(decoded.language, choice);
            assert_eq!(decoded.settings_version, settings.settings_version);
        }
        settings.language = "unsupported".into();
        settings.normalize();
        assert_eq!(settings.language, "system");
    }
    use super::*;
    #[test]
    fn builtin_pool_is_valid() {
        validate_endpoints(&builtins()).unwrap();
    }
    #[test]
    fn secrets_and_same_infrastructure_do_not_become_independent() {
        let mut e = builtins();
        e[0].uri.set_query(Some("token=secret"));
        assert!(validate_endpoints(&e).is_err());
        e = builtins();
        for item in e.iter_mut().filter(|e| e.group == Group::Ru) {
            item.infrastructure_id = "same".into();
        }
        assert!(validate_endpoints(&e).is_err());
    }
    #[test]
    fn old_settings_keep_geometry_and_unknown_fields() {
        let mut settings: Settings = serde_json::from_str(
            r#"{"settingsVersion":8,"windowWidth":1234,"windowHeight":987,"futureValue":42}"#,
        )
        .unwrap();
        settings.normalize();
        assert_eq!(settings.window_width, 1234);
        assert_eq!(serde_json::to_value(settings).unwrap()["futureValue"], 42);
    }
    #[test]
    fn out_of_range_periods_cannot_wrap_into_a_supported_window() {
        for invalid in [i64::MIN, -1, 0, 4_294_967_308, i64::MAX] {
            let mut settings = Settings {
                location_chart_window_hours: invalid,
                ..Default::default()
            };
            settings.normalize();
            assert_eq!(
                settings.location_chart_window_hours, 12,
                "Invalid period: {invalid}"
            );
        }
        for hours in PERIODS {
            let mut settings = Settings {
                location_chart_window_hours: i64::from(hours),
                ..Default::default()
            };
            settings.normalize();
            assert_eq!(settings.location_chart_window_hours, i64::from(hours));
        }
    }
    #[test]
    fn legacy_geometry_migrates_once_and_current_custom_size_has_priority() {
        for (width, height) in [
            (900, 800),
            (1040, 920),
            (1360, 1177),
            (1400, 1200),
            (1421, 1273),
        ] {
            let mut settings = Settings {
                settings_version: 0,
                window_width: width,
                window_height: height,
                window_x: 80,
                window_y: 80,
                auto_start: false,
                emphasize_short_statuses: false,
                show_location_debug_sources: true,
                ..Default::default()
            };
            settings.normalize();
            assert_eq!(
                (settings.window_width, settings.window_height),
                (1450, 1300)
            );
            assert_eq!((settings.window_x, settings.window_y), (i32::MIN, i32::MIN));
            assert!(settings.auto_start);
            assert!(!settings.emphasize_short_statuses);
            assert!(!settings.show_location_debug_sources);
            let migrated = settings.clone();
            settings.normalize();
            assert_eq!(settings, migrated, "Migration must be idempotent");

            settings.window_width = width;
            settings.window_height = height;
            settings.normalize();
            assert_eq!(
                (settings.window_width, settings.window_height),
                (width, height),
                "Current user geometry mustn't be reset"
            );
        }
    }
    #[test]
    fn actual_event_widths_are_default_and_explicit_short_event_choices_survive_loading() {
        assert!(!Settings::default().emphasize_short_statuses);
        for version in [0, 4, 8] {
            let mut missing: Settings = serde_json::from_value(serde_json::json!({
                "settingsVersion": version
            }))
            .unwrap();
            missing.normalize();
            assert!(!missing.emphasize_short_statuses);
            for choice in [false, true] {
                let mut saved: Settings = serde_json::from_value(serde_json::json!({
                    "settingsVersion": version,
                    "emphasizeShortStatuses": choice
                }))
                .unwrap();
                saved.normalize();
                assert_eq!(saved.emphasize_short_statuses, choice);
            }
        }
    }
    #[test]
    fn corrupt_signed_selectors_fall_back_without_discarding_custom_geometry() {
        let mut settings = Settings {
            availability_polling_seconds: -1,
            country_polling_seconds: i64::MIN,
            geo_country_letter_size: 99,
            theme: "unsupported".into(),
            window_width: 1234,
            window_height: 987,
            ..Default::default()
        };
        settings.normalize();
        assert_eq!(settings.availability_polling_seconds, 5);
        assert_eq!(settings.country_polling_seconds, 10);
        assert_eq!(settings.geo_country_letter_size, 1);
        assert_eq!(settings.theme, "system");
        assert_eq!((settings.window_width, settings.window_height), (1234, 987));
    }
    #[test]
    fn legacy_custom_pool_group_aliases_preserve_the_custom_address() {
        for alias in ["ru", "rU", "РФ"] {
            let mut value = serde_json::to_value(&builtins()[0]).unwrap();
            value["group"] = alias.into();
            value["uri"] = "https://custom.example/check".into();
            let endpoint: Endpoint = serde_json::from_value(value).unwrap();
            assert_eq!(endpoint.group, Group::Ru);
            assert_eq!(endpoint.uri.host_str(), Some("custom.example"));
        }
    }
}
