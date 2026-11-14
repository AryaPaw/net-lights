use super::*;
use netlights_core::geo::{GeoDiagnostic, GeoIssue, GeoSourceDiagnostic};

#[derive(Default)]
struct Last {
    signature: Option<(Option<GeoIssue>, Option<u16>, bool, bool)>,
    written: Option<Instant>,
}
impl Last {
    fn event(
        &mut self,
        prefix: &str,
        value: &GeoSourceDiagnostic,
        retry: u64,
        now: Instant,
    ) -> Option<String> {
        let signature = (value.issue, value.http_status, value.cached, value.deferred);
        let changed = self.signature != Some(signature);
        let repeat = value.issue.is_some()
            && self
                .written
                .is_none_or(|at| now.saturating_duration_since(at) >= Duration::from_secs(60));
        if !changed && !repeat {
            return None;
        }
        self.signature = Some(signature);
        self.written = Some(now);
        Some(format!(
            "{prefix}-{}-{}-http-{}-ms-{}-next-{}",
            if value.deferred {
                "wait"
            } else if value.cached {
                "cache"
            } else {
                "request"
            },
            value.issue.map(GeoIssue::code).unwrap_or("ok"),
            value.http_status.unwrap_or(0),
            value.elapsed_ms.min(999999),
            retry.min(86400)
        ))
    }
}
#[derive(Default)]
pub(super) struct NetworkLog {
    primary: Last,
    fallback: Last,
    geo_observed: Option<chrono::DateTime<Utc>>,
    mismatch: bool,
    probes: HashMap<(Group, usize), Last>,
}
impl NetworkLog {
    pub fn geo(&mut self, report: &GeoDiagnostic, now: Instant) -> Vec<String> {
        if self.geo_observed == Some(report.observed_utc) {
            return Vec::new();
        }
        self.geo_observed = Some(report.observed_utc);
        let mut events = Vec::new();
        let retry = (report.next_attempt_utc - report.observed_utc)
            .num_seconds()
            .max(0) as u64;
        if let Some(code) = self
            .primary
            .event("geo-primary", &report.primary, retry, now)
        {
            events.push(code);
            if let Some(until) = report.primary.retry_until {
                events.push(format!(
                    "geo-primary-hold-{}",
                    (until - report.observed_utc).num_seconds().clamp(0, 86400)
                ));
            }
        }
        if let Some(fallback) = &report.fallback
            && let Some(code) = self.fallback.event("geo-fallback", fallback, retry, now)
        {
            events.push(code);
            if let Some(until) = fallback.retry_until {
                events.push(format!(
                    "geo-fallback-hold-{}",
                    (until - report.observed_utc).num_seconds().clamp(0, 86400)
                ));
            }
        }
        if self.mismatch != report.ip_mismatch {
            self.mismatch = report.ip_mismatch;
            events.push(
                if self.mismatch {
                    "geo-ip-mismatch"
                } else {
                    "geo-ip-consistent"
                }
                .into(),
            );
        }
        events
    }
    pub fn probe(
        &mut self,
        group: Group,
        index: usize,
        result: &ProbeResult,
        now: Instant,
    ) -> Option<String> {
        let issue = result
            .failure
            .map(GeoIssue::from_transport)
            .or_else(|| (result.status == Some(429)).then_some(GeoIssue::RateLimit));
        let value = GeoSourceDiagnostic {
            issue,
            http_status: result.status,
            elapsed_ms: result.elapsed_ms,
            ..Default::default()
        };
        let prefix = format!(
            "probe-{}-{index}",
            if group == Group::Ru {
                "provider"
            } else {
                "world"
            }
        );
        self.probes.entry((group, index)).or_default().event(
            &prefix,
            &value,
            result.retry_after_ms / 1000,
            now,
        )
    }
}

pub(crate) fn geo_notice(state: &State, lang: Language) -> Option<String> {
    if !state.settings.geo_country_detection_enabled {
        return None;
    }
    if state.monitor.network_unavailable {
        return Some(
            lang.choose(
                "Нет сетевого подключения. Определение страны возобновится после его появления.",
                "No network connection. Country checks will resume when it returns.",
            )
            .into(),
        );
    }
    let report = state.geo_diagnostic.as_ref()?;
    if report.primary.issue.is_none() && !report.ip_mismatch && report.confirmed {
        return None;
    }
    let mut parts = Vec::new();
    for (name, source) in [
        ("country.is", Some(&report.primary)),
        ("ipwho.is", report.fallback.as_ref()),
    ] {
        if let Some(source) = source
            && let Some(issue) = source.issue
        {
            let http = source
                .http_status
                .map(|s| format!(" (HTTP {s})"))
                .unwrap_or_default();
            parts.push(format!("{name}: {}{http}.", issue.text(lang)));
            if let Some(until) = source.retry_until {
                let until = until.with_timezone(&chrono::Local).format("%d.%m %H:%M:%S");
                parts.push(match lang {
                    Language::Ru => format!("Повтор {name} разрешён после {until}."),
                    Language::En => format!("{name} allows retry after {until}."),
                });
            }
        }
    }
    if report.ip_mismatch {
        parts.push(
            lang.choose(
                "Источники вернули разные публичные IP. Страна не подтверждена.",
                "Sources returned different public IPs. The country isn't confirmed.",
            )
            .into(),
        );
    } else if report.confirmed && report.fallback.as_ref().is_some_and(|s| s.cached) {
        parts.push(
            lang.choose(
                "Использован сохранённый результат резервного сервиса для проверенного IP.",
                "A cached fallback result was used for the verified IP.",
            )
            .into(),
        );
    } else if report.confirmed {
        parts.push(
            lang.choose(
                "Страна получена от резервного сервиса.",
                "The fallback service supplied the country.",
            )
            .into(),
        );
    } else {
        parts.push(
            lang.choose(
                "Свежий онлайн-результат не получен.",
                "No recent online result is available.",
            )
            .into(),
        );
    }
    let checked = report
        .observed_utc
        .with_timezone(&chrono::Local)
        .format("%d.%m %H:%M:%S");
    let next = report
        .next_attempt_utc
        .with_timezone(&chrono::Local)
        .format("%d.%m %H:%M:%S");
    parts.push(match lang {
        Language::Ru => format!("Состояние на {checked}. Повтор запланирован на {next}."),
        Language::En => format!("Status at {checked}. Next attempt scheduled for {next}."),
    });
    Some(parts.join(" "))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn logging_records_changes_repeats_at_most_once_a_minute_and_never_invents_attempts() {
        let at = Utc::now();
        let clock = Instant::now();
        let mut report = GeoDiagnostic {
            observed_utc: at,
            primary: GeoSourceDiagnostic {
                issue: Some(GeoIssue::Deadline),
                elapsed_ms: 5000,
                ..Default::default()
            },
            fallback: None,
            ip_mismatch: false,
            confirmed: false,
            next_attempt_utc: at + chrono::Duration::seconds(10),
        };
        let mut log = NetworkLog::default();
        let first = log.geo(&report, clock);
        assert_eq!(first.len(), 1);
        assert!(first[0].contains("primary-request-deadline"));
        assert!(
            log.geo(&report, clock + Duration::from_secs(120))
                .is_empty()
        );
        report.observed_utc = at + chrono::Duration::seconds(10);
        assert!(log.geo(&report, clock + Duration::from_secs(10)).is_empty());
        report.observed_utc = at + chrono::Duration::seconds(60);
        assert_eq!(log.geo(&report, clock + Duration::from_secs(60)).len(), 1);
        report.observed_utc = at + chrono::Duration::seconds(61);
        report.primary.issue = None;
        report.primary.http_status = Some(200);
        report.confirmed = true;
        let recovery = log.geo(&report, clock + Duration::from_secs(61));
        assert_eq!(recovery.len(), 1);
        assert!(recovery[0].contains("request-ok-http-200"));
    }
    #[test]
    fn unknown_probe_errors_never_leak_strings_and_waits_are_explicit_and_bounded() {
        let mut log = NetworkLog::default();
        let code = log
            .probe(
                Group::World,
                3,
                &ProbeResult {
                    outcome: Outcome::Indeterminate,
                    status: None,
                    failure: Some("token=private-fixture"),
                    elapsed_ms: u64::MAX,
                    retry_after_ms: u64::MAX,
                },
                Instant::now(),
            )
            .unwrap();
        assert!(code.contains("local-resource"));
        assert!(!code.contains("private-fixture"));
        let at = Utc::now();
        let report = GeoDiagnostic {
            observed_utc: at,
            primary: GeoSourceDiagnostic {
                issue: Some(GeoIssue::RateLimit),
                http_status: Some(429),
                deferred: true,
                retry_until: Some(at + chrono::Duration::seconds(600)),
                ..Default::default()
            },
            fallback: None,
            ip_mismatch: false,
            confirmed: false,
            next_attempt_utc: at + chrono::Duration::seconds(10),
        };
        let mut codes = log.geo(&report, Instant::now());
        assert!(
            codes
                .iter()
                .any(|code| code.contains("primary-wait-rate-limit"))
        );
        assert!(codes.iter().any(|code| code == "geo-primary-hold-600"));
        codes.push(code);
        for code in codes {
            assert!(code.len() <= 80);
            assert!(code.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-'));
        }
    }
}
