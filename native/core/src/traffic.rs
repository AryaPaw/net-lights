use serde::Serialize;
use std::{
    collections::{BTreeMap, VecDeque},
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
    },
    time::Instant,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum Category {
    Provider,
    World,
    Country,
    Fallback,
    V2Fly,
    IPLocate,
    Updates,
}
impl Category {
    pub fn label(self) -> &'static str {
        match self {
            Self::Provider => "Проверки провайдера",
            Self::World => "Проверки мира",
            Self::Country => "Страна: country.is",
            Self::Fallback => "Страна: ipwho.is",
            Self::V2Fly => "База V2Fly",
            Self::IPLocate => "База IPLocate",
            Self::Updates => "Обновления",
        }
    }
    pub fn regular(self) -> bool {
        matches!(
            self,
            Self::Provider | Self::World | Self::Country | Self::Fallback
        )
    }
}
#[derive(Default)]
pub struct Counter {
    pub sent: AtomicU64,
    pub received: AtomicU64,
}
#[derive(Clone, Debug, Serialize)]
pub struct TrafficRow {
    pub category: String,
    pub host: String,
    pub sent_bytes: u64,
    pub received_bytes: u64,
}
pub struct Traffic {
    counters: Mutex<BTreeMap<(Category, String), Arc<Counter>>>,
    overflow: Arc<Counter>,
    pub started: Instant,
}
impl Default for Traffic {
    fn default() -> Self {
        Self {
            counters: Mutex::new(BTreeMap::new()),
            overflow: Arc::default(),
            started: Instant::now(),
        }
    }
}
impl Traffic {
    pub fn counter(&self, category: Category, host: &str) -> Arc<Counter> {
        let mut counters = self.counters.lock().unwrap_or_else(|e| e.into_inner());
        let key = (category, host.to_ascii_lowercase());
        if let Some(c) = counters.get(&key) {
            return c.clone();
        }
        if counters.len() >= 64 {
            return self.overflow.clone();
        }
        let counter = Arc::new(Counter::default());
        counters.insert(key, counter.clone());
        counter
    }
    pub fn snapshot(&self) -> Vec<TrafficRow> {
        let counters = self.counters.lock().unwrap_or_else(|e| e.into_inner());
        let mut rows: Vec<_> = counters
            .iter()
            .map(|((c, host), counter)| TrafficRow {
                category: c.label().into(),
                host: host.clone(),
                sent_bytes: counter.sent.load(Ordering::Relaxed),
                received_bytes: counter.received.load(Ordering::Relaxed),
            })
            .collect();
        let sent = self.overflow.sent.load(Ordering::Relaxed);
        let received = self.overflow.received.load(Ordering::Relaxed);
        if sent + received > 0 {
            rows.push(TrafficRow {
                category: "Прочее".into(),
                host: String::new(),
                sent_bytes: sent,
                received_bytes: received,
            });
        }
        rows.sort_by_key(|r| std::cmp::Reverse(r.sent_bytes.saturating_add(r.received_bytes)));
        rows
    }
    pub fn regular_total(&self) -> u64 {
        self.counters
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .iter()
            .filter(|((c, _), _)| c.regular())
            .fold(0u64, |sum, (_, c)| {
                sum.saturating_add(c.sent.load(Ordering::Relaxed))
                    .saturating_add(c.received.load(Ordering::Relaxed))
            })
    }
}

/// The first ten minutes establish the measured normal rate. Downloads don't
/// contribute to the baseline or warning. No settings or poll intervals are changed.
#[derive(Default)]
pub struct TrafficGuard {
    samples: VecDeque<(u64, u64)>,
    baseline_started: Option<u64>,
    baseline_total: u64,
    calibrated_limit: Option<u64>,
    active_limit: Option<u64>,
    baseline: Option<u64>,
    pub warning: bool,
}
impl TrafficGuard {
    pub fn threshold(&self) -> Option<u64> {
        self.active_limit
    }
    pub fn baseline_bytes_per_hour(&self) -> Option<u64> {
        self.baseline
    }
    pub fn observe(&mut self, now_seconds: u64, total: u64, override_limit: Option<u64>) {
        let start = *self.baseline_started.get_or_insert(now_seconds);
        if self.samples.is_empty() {
            self.baseline_total = total;
        }
        if self
            .samples
            .back()
            .is_none_or(|(last, _)| now_seconds.saturating_sub(*last) >= 10)
        {
            self.samples.push_back((now_seconds, total));
        }
        while self.samples.len() > 362
            || self
                .samples
                .get(1)
                .is_some_and(|(time, _)| now_seconds.saturating_sub(*time) >= 3600)
        {
            self.samples.pop_front();
        }
        if self.calibrated_limit.is_none() && now_seconds.saturating_sub(start) >= 600 {
            let baseline = total
                .saturating_sub(self.baseline_total)
                .saturating_mul(3600)
                / now_seconds.saturating_sub(start).max(1);
            self.baseline = Some(baseline);
            // A runaway during calibration must not train itself into a normal baseline.
            self.calibrated_limit = Some(
                baseline
                    .saturating_mul(10)
                    .clamp(64 * 1024 * 1024, 1024 * 1024 * 1024),
            );
        }
        self.active_limit = override_limit.or(self.calibrated_limit);
        let Some(limit) = self.active_limit else {
            self.warning = false;
            return;
        };
        let spent = total.saturating_sub(self.samples.front().map_or(total, |(_, t)| *t));
        self.warning = spent >= limit;
    }
}
pub fn bytes_label(bytes: u64) -> String {
    crate::locale::Language::Ru.bytes(bytes)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn displayed_limit_matches_the_warning_and_removing_an_override_clears_an_uncalibrated_warning()
    {
        let mut guard = TrafficGuard::default();
        let chosen = 64 * 1024 * 1024;
        guard.observe(0, 0, Some(chosen));
        assert_eq!(guard.threshold(), Some(chosen));
        guard.observe(10, chosen, Some(chosen));
        assert!(guard.warning);
        guard.observe(20, chosen, None);
        assert_eq!(guard.threshold(), None);
        assert!(!guard.warning);
        guard.observe(600, chosen, None);
        let calibrated = guard.threshold().unwrap();
        assert!(calibrated > chosen);
        guard.observe(610, chosen, Some(chosen * 2));
        assert_eq!(guard.threshold(), Some(chosen * 2));
        assert!(!guard.warning);
        guard.observe(620, chosen, None);
        assert_eq!(guard.threshold(), Some(calibrated));
    }
    #[test]
    fn sources_are_bounded_and_downloads_dont_trigger_guard() {
        let traffic = Traffic::default();
        traffic
            .counter(Category::V2Fly, "example.org")
            .received
            .store(1 << 30, Ordering::Relaxed);
        assert_eq!(traffic.regular_total(), 0);
        for i in 0..100 {
            traffic.counter(Category::World, &format!("host{i}"));
        }
        assert!(traffic.snapshot().len() <= 65);
    }
    #[test]
    fn warnings_wait_for_measurement_then_follow_the_rolling_window() {
        let mut guard = TrafficGuard::default();
        guard.observe(0, 0, None);
        guard.observe(599, 1000, None);
        assert_eq!(guard.threshold(), None);
        guard.observe(600, 1000, None);
        assert_eq!(guard.threshold(), Some(64 * 1024 * 1024));
        guard.observe(610, 1024 * 1024 * 1024, None);
        assert!(guard.warning);
        guard.observe(4210, 1024 * 1024 * 1024, None);
        assert!(!guard.warning);
    }
    #[test]
    fn runaway_during_calibration_cannot_raise_its_own_safety_ceiling() {
        let mut guard = TrafficGuard::default();
        guard.observe(0, 0, None);
        guard.observe(600, 1024 * 1024 * 1024, None);
        assert!(guard.warning);
        assert_eq!(guard.threshold(), Some(1024 * 1024 * 1024));
    }
}
