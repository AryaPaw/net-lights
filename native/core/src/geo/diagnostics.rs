use crate::locale::Language;
use chrono::{DateTime, Utc};
use serde::Serialize;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum GeoIssue {
    Deadline,
    Dns,
    Transport,
    Tls,
    Certificate,
    LocalResource,
    Http,
    RateLimit,
    InvalidResponse,
    Quota,
    QuotaStorage,
}
impl GeoIssue {
    pub fn code(self) -> &'static str {
        match self {
            Self::Deadline => "deadline",
            Self::Dns => "dns",
            Self::Transport => "transport",
            Self::Tls => "tls",
            Self::Certificate => "certificate",
            Self::LocalResource => "local-resource",
            Self::Http => "http",
            Self::RateLimit => "rate-limit",
            Self::InvalidResponse => "invalid-response",
            Self::Quota => "quota",
            Self::QuotaStorage => "quota-storage",
        }
    }
    pub fn from_transport(code: &str) -> Self {
        match code {
            "deadline" | "timeout" => Self::Deadline,
            "dns" => Self::Dns,
            "transport" => Self::Transport,
            "tls-protocol" | "tls-indeterminate" => Self::Tls,
            "certificate-trust" => Self::Certificate,
            "invalid-http" | "invalid-target" | "response-size" => Self::InvalidResponse,
            _ => Self::LocalResource,
        }
    }
    pub fn text(self, lang: Language) -> &'static str {
        let (ru, en) = match self {
            Self::Deadline => (
                "ответ не получен за отведённое время",
                "the request timed out",
            ),
            Self::Dns => (
                "не удалось определить адрес сервиса через DNS",
                "the service address couldn't be resolved through DNS",
            ),
            Self::Transport => (
                "не удалось установить или сохранить соединение",
                "the connection couldn't be established or was interrupted",
            ),
            Self::Tls => (
                "не удалось установить защищённое соединение TLS",
                "the secure TLS connection failed",
            ),
            Self::Certificate => (
                "сертификат сервиса не прошёл проверку",
                "the service certificate couldn't be verified",
            ),
            Self::LocalResource => (
                "локальная ошибка сетевого запроса",
                "a local network request error occurred",
            ),
            Self::Http => (
                "сервис вернул HTTP-ошибку",
                "the service returned an HTTP error",
            ),
            Self::RateLimit => (
                "сервис ограничил запросы; ожидаем разрешённого повтора",
                "the service rate limit is active; waiting until retry is allowed",
            ),
            Self::InvalidResponse => (
                "сервис вернул некорректный или неполный ответ",
                "the service returned an invalid or incomplete response",
            ),
            Self::Quota => (
                "исчерпан суточный бюджет резервных запросов",
                "the daily fallback request budget is exhausted",
            ),
            Self::QuotaStorage => (
                "не удалось проверить или сохранить счётчик резервных запросов",
                "the fallback request counter couldn't be verified or saved",
            ),
        };
        lang.choose(ru, en)
    }
}

/// Only typed, public diagnostics. IPs, URLs, response bodies and error strings
/// must never enter this representation, which can be logged and exported.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize)]
pub struct GeoSourceDiagnostic {
    pub issue: Option<GeoIssue>,
    pub http_status: Option<u16>,
    pub elapsed_ms: u64,
    pub retry_until: Option<DateTime<Utc>>,
    pub cached: bool,
    pub deferred: bool,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct GeoDiagnostic {
    pub observed_utc: DateTime<Utc>,
    pub primary: GeoSourceDiagnostic,
    pub fallback: Option<GeoSourceDiagnostic>,
    pub ip_mismatch: bool,
    pub confirmed: bool,
    pub next_attempt_utc: DateTime<Utc>,
}
