use crate::model::*;
use chrono::{DateTime, Duration, Utc};
use serde::{Deserialize, Serialize};

pub const LOCATION_STAY_LIMIT: usize = 100_000;
pub const LOCATION_OBSERVATION_LIMIT: usize = 70_000;

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AvailabilitySpan {
    pub group: Group,
    pub state: Availability,
    pub paused: bool,
    pub started_utc: DateTime<Utc>,
    pub ended_utc: Option<DateTime<Utc>>,
}
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AvailabilityHistory {
    #[serde(deserialize_with = "bounded_spans")]
    pub spans: Vec<AvailabilitySpan>,
    pub last_observed_utc: Option<DateTime<Utc>>,
    pub capacity_truncated_through_utc: Option<DateTime<Utc>>,
    #[serde(skip)]
    pub revision: u64,
    // Chart topology changes immediately; rolling retention bounds don't need
    // to invalidate geometry ahead of the separate periodic axis refresh.
    #[serde(skip)]
    pub structure_revision: u64,
}
impl AvailabilityHistory {
    pub fn observe(&mut self, snapshot: &MonitorSnapshot) {
        let now = snapshot.generated_utc;
        self.seal_stale(now);
        for group in &snapshot.groups {
            let current = self
                .spans
                .iter()
                .rposition(|s| s.group == group.group && s.ended_utc.is_none());
            if let Some(i) = current {
                if self.spans[i].state == group.availability
                    && self.spans[i].paused == snapshot.paused
                {
                    continue;
                }
                self.spans[i].ended_utc = Some(now.max(self.spans[i].started_utc));
            }
            self.spans.push(AvailabilitySpan {
                group: group.group,
                state: group.availability,
                paused: snapshot.paused,
                started_utc: now,
                ended_utc: None,
            });
            self.revision += 1;
            self.structure_revision += 1;
        }
        self.last_observed_utc = Some(now);
        self.prune(now);
    }
    pub fn close(&mut self, now: DateTime<Utc>) {
        let end = self
            .last_observed_utc
            .filter(|t| now.signed_duration_since(*t) > Duration::minutes(2))
            .unwrap_or(now);
        for span in &mut self.spans {
            if span.ended_utc.is_none() {
                span.ended_utc = Some(end.max(span.started_utc));
                self.revision += 1;
                self.structure_revision += 1;
            }
        }
    }
    pub fn seal_stale(&mut self, now: DateTime<Utc>) {
        if self
            .last_observed_utc
            .is_some_and(|t| now - t > Duration::minutes(2))
        {
            self.close(now);
        }
    }
    pub fn prune(&mut self, now: DateTime<Utc>) {
        let previous_len = self.spans.len();
        let previous_truncation = self.capacity_truncated_through_utc;
        let mut changed = false;
        let cutoff = now - Duration::days(7);
        self.spans.retain(|s| {
            s.ended_utc.unwrap_or(now) > cutoff
                && s.ended_utc.is_none_or(|end| end >= s.started_utc)
        });
        for span in &mut self.spans {
            changed |= span.started_utc < cutoff;
            span.started_utc = span.started_utc.max(cutoff);
        }
        while self.spans.len() > 10_000 {
            let index = self
                .spans
                .iter()
                .position(|s| s.ended_utc.is_some())
                .unwrap_or(0);
            let removed = self.spans.remove(index);
            let through = removed.ended_utc.unwrap_or(removed.started_utc);
            self.capacity_truncated_through_utc = Some(
                self.capacity_truncated_through_utc
                    .unwrap_or(through)
                    .max(through),
            );
        }
        if self
            .capacity_truncated_through_utc
            .is_some_and(|t| t <= cutoff)
        {
            self.capacity_truncated_through_utc = None;
        }
        let structure_changed = previous_len != self.spans.len()
            || previous_truncation != self.capacity_truncated_through_utc;
        if changed || structure_changed {
            self.revision += 1;
        }
        if structure_changed {
            self.structure_revision += 1;
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocationStay {
    pub iso: String,
    pub started_utc: DateTime<Utc>,
    pub ended_utc: Option<DateTime<Utc>>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocationObservation {
    pub observed_utc: DateTime<Utc>,
    pub public_ip_hash: Option<String>,
    pub country_code: Option<String>,
    pub local_country: Option<String>,
    pub online_country: Option<String>,
    pub third_country: Option<String>,
    pub database_version: Option<String>,
    #[serde(default)]
    pub public_ip_changed: bool,
    #[serde(default)]
    pub country_changed: bool,
    #[serde(default)]
    pub classification_changed: bool,
    #[serde(default)]
    pub confirmed: bool,
    pub public_ip: Option<String>,
    pub observed_until_utc: Option<DateTime<Utc>>,
    pub iplocate_country: Option<String>,
    pub iplocate_database_version: Option<String>,
    pub resolution_source: Option<String>,
}
impl LocationObservation {
    pub fn until(&self) -> DateTime<Utc> {
        self.observed_until_utc.unwrap_or(self.observed_utc)
    }
    fn same_sources(&self, other: &Self) -> bool {
        self.public_ip_hash == other.public_ip_hash
            && self.public_ip == other.public_ip
            && self.country_code == other.country_code
            && self.local_country == other.local_country
            && self.online_country == other.online_country
            && self.third_country == other.third_country
            && self.database_version == other.database_version
            && self.iplocate_country == other.iplocate_country
            && self.iplocate_database_version == other.iplocate_database_version
            && self.resolution_source == other.resolution_source
            && self.confirmed == other.confirmed
    }
    pub fn valid(&self) -> bool {
        self.until() >= self.observed_utc
            && [
                &self.country_code,
                &self.local_country,
                &self.online_country,
                &self.third_country,
                &self.iplocate_country,
            ]
            .iter()
            .all(|s| s.as_ref().is_none_or(|s| valid_iso(s)))
            && self
                .public_ip
                .as_ref()
                .is_none_or(|ip| ip.parse::<std::net::IpAddr>().is_ok())
            && self
                .public_ip_hash
                .as_ref()
                .is_none_or(|s| s.len() == 64 && s.bytes().all(|c| c.is_ascii_hexdigit()))
            && [&self.database_version, &self.iplocate_database_version]
                .iter()
                .all(|s| s.as_ref().is_none_or(|s| s.len() <= 64))
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocationHistory {
    #[serde(default = "history_version")]
    pub version: u32,
    #[serde(deserialize_with = "bounded_stays")]
    pub stays: Vec<LocationStay>,
    #[serde(default, deserialize_with = "bounded_observations")]
    pub observations: Vec<LocationObservation>,
    pub last_observed_utc: Option<DateTime<Utc>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub capacity_truncated_through_utc: Option<DateTime<Utc>>,
    #[serde(skip)]
    pub revision: u64,
    #[serde(skip)]
    pub observation_boundary: bool,
}
fn history_version() -> u32 {
    3
}
fn bounded_sequence<'de, D, T, const MAX: usize>(deserializer: D) -> Result<Vec<T>, D::Error>
where
    D: serde::Deserializer<'de>,
    T: Deserialize<'de>,
{
    struct Sequence<T, const MAX: usize>(std::marker::PhantomData<T>);
    impl<'de, T: Deserialize<'de>, const MAX: usize> serde::de::Visitor<'de> for Sequence<T, MAX> {
        type Value = Vec<T>;
        fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            write!(f, "at most {MAX} history records")
        }
        fn visit_seq<A: serde::de::SeqAccess<'de>>(
            self,
            mut sequence: A,
        ) -> Result<Self::Value, A::Error> {
            let mut rows = Vec::with_capacity(sequence.size_hint().unwrap_or(0).min(MAX).min(1024));
            loop {
                if rows.len() == MAX {
                    if sequence.next_element::<serde::de::IgnoredAny>()?.is_some() {
                        return Err(serde::de::Error::custom("History record limit exceeded"));
                    }
                    break;
                }
                match sequence.next_element()? {
                    Some(row) => rows.push(row),
                    None => break,
                }
            }
            Ok(rows)
        }
    }
    deserializer.deserialize_seq(Sequence::<T, MAX>(std::marker::PhantomData))
}
fn bounded_spans<'de, D: serde::Deserializer<'de>>(
    d: D,
) -> Result<Vec<AvailabilitySpan>, D::Error> {
    bounded_sequence::<D, _, 10002>(d)
}
fn bounded_stays<'de, D: serde::Deserializer<'de>>(d: D) -> Result<Vec<LocationStay>, D::Error> {
    bounded_sequence::<D, _, LOCATION_STAY_LIMIT>(d)
}
fn bounded_observations<'de, D: serde::Deserializer<'de>>(
    d: D,
) -> Result<Vec<LocationObservation>, D::Error> {
    bounded_sequence::<D, _, LOCATION_OBSERVATION_LIMIT>(d)
}
impl Default for LocationHistory {
    fn default() -> Self {
        Self {
            version: 3,
            stays: Vec::new(),
            observations: Vec::new(),
            last_observed_utc: None,
            capacity_truncated_through_utc: None,
            revision: 0,
            observation_boundary: false,
        }
    }
}
impl LocationHistory {
    pub fn close(&mut self, at: DateTime<Utc>) {
        self.observation_boundary = true;
        self.close_stay(at);
    }
    fn close_stay(&mut self, at: DateTime<Utc>) {
        if let Some(last) = self.stays.last_mut()
            && last.ended_utc.is_none()
        {
            last.ended_utc = Some(at.max(last.started_utc));
            self.revision += 1;
        }
    }
    pub fn seal_stale(&mut self, now: DateTime<Utc>) {
        if let Some(last) = self.last_observed_utc
            && now - last > Duration::minutes(2)
        {
            self.close_stay(last);
        }
    }
    pub fn observe(&mut self, observation: LocationObservation) {
        if !observation.valid() {
            return;
        }
        let now = observation.observed_utc;
        self.seal_stale(now);
        if observation.confirmed {
            if let Some(iso) = &observation.country_code {
                let current = self.stays.last().filter(|s| s.ended_utc.is_none());
                if current.is_none_or(|s| s.iso != *iso) {
                    self.close_stay(now);
                    self.stays.push(LocationStay {
                        iso: iso.clone(),
                        started_utc: now,
                        ended_utc: None,
                    });
                }
                self.last_observed_utc = Some(now);
            }
        } else {
            self.close_stay(self.last_observed_utc.unwrap_or(now));
        }
        let until = observation.until();
        if let Some(last) = self.observations.last_mut()
            && !self.observation_boundary
            && now >= last.until()
            && now - last.until() <= Duration::minutes(2)
            && last.same_sources(&observation)
        {
            last.observed_until_utc = Some(until.max(last.until()));
        } else {
            self.observations.push(observation);
        }
        self.observation_boundary = false;
        self.revision += 1;
        self.prune(now);
    }
    pub fn prune(&mut self, now: DateTime<Utc>) {
        let previous_counts = (self.observations.len(), self.stays.len());
        let previous_truncation = self.capacity_truncated_through_utc;
        let mut changed = false;
        let cutoff = now - Duration::days(7);
        self.observations
            .retain(|o| o.until() >= cutoff && o.valid());
        let excess = self
            .observations
            .len()
            .saturating_sub(LOCATION_OBSERVATION_LIMIT);
        if excess > 0 {
            let through = self.observations.drain(..excess).map(|o| o.until()).max();
            self.record_capacity_truncation(through);
        }
        let anchor = self
            .stays
            .iter()
            .rposition(|s| s.ended_utc.is_some_and(|e| e <= cutoff));
        let mut i = 0;
        self.stays.retain_mut(|s| {
            let index = i;
            i += 1;
            if !valid_iso(&s.iso) || s.ended_utc.is_some_and(|e| e < s.started_utc) {
                return false;
            }
            let is_anchor = Some(index) == anchor;
            if !is_anchor && s.ended_utc.unwrap_or(now) <= cutoff {
                return false;
            }
            if !is_anchor {
                changed |= s.started_utc < cutoff;
                s.started_utc = s.started_utc.max(cutoff);
            }
            true
        });
        let excess = self.stays.len().saturating_sub(LOCATION_STAY_LIMIT);
        if excess > 0 {
            // Loading sorts stays chronologically; observe appends the newest stay.
            // Keep the current country and discard only the oldest retained rows.
            let through = self
                .stays
                .drain(..excess)
                .map(|s| s.ended_utc.unwrap_or(s.started_utc))
                .max();
            self.record_capacity_truncation(through);
        }
        if self
            .capacity_truncated_through_utc
            .is_some_and(|t| t <= cutoff)
        {
            self.capacity_truncated_through_utc = None;
        }
        if changed
            || previous_counts != (self.observations.len(), self.stays.len())
            || previous_truncation != self.capacity_truncated_through_utc
        {
            self.revision += 1;
        }
    }
    fn record_capacity_truncation(&mut self, through: Option<DateTime<Utc>>) {
        if let Some(through) = through {
            self.capacity_truncated_through_utc = Some(
                self.capacity_truncated_through_utc
                    .unwrap_or(through)
                    .max(through),
            );
        }
    }
}

pub fn valid_iso(code: &str) -> bool {
    code.len() == 2 && crate::countries::valid(code)
}
pub fn duration_label(milliseconds: i64) -> String {
    crate::locale::Language::Ru.duration(milliseconds)
}

#[derive(Clone, Debug)]
pub struct FrequencyBucket {
    pub start: DateTime<Utc>,
    pub end: DateTime<Utc>,
    pub changes: usize,
    pub observed: bool,
}
pub fn frequency(
    history: &LocationHistory,
    start: DateTime<Utc>,
    end: DateTime<Utc>,
    count: usize,
) -> Vec<FrequencyBucket> {
    if start >= end || count == 0 {
        return Vec::new();
    }
    let total = (end - start).num_nanoseconds().unwrap_or(i64::MAX);
    let mut result: Vec<_> = (0..count)
        .map(|i| {
            let a =
                start + Duration::nanoseconds((total as u128 * i as u128 / count as u128) as i64);
            let b = start
                + Duration::nanoseconds((total as u128 * (i + 1) as u128 / count as u128) as i64);
            FrequencyBucket {
                start: a,
                end: b,
                changes: 0,
                observed: false,
            }
        })
        .collect();
    let mut ordered: Vec<_> = history
        .stays
        .iter()
        .filter(|s| valid_iso(&s.iso) && s.started_utc <= end)
        .collect();
    ordered.sort_by_key(|s| s.started_utc);
    let mut coverage = vec![0i32; count + 1];
    for stay in &ordered {
        let a = stay.started_utc.max(start);
        let b = stay.ended_utc.unwrap_or(end).min(end);
        if a >= b {
            continue;
        }
        let left = (a - start).num_nanoseconds().unwrap_or(0).max(0) as u128;
        let right = (b - start).num_nanoseconds().unwrap_or(total).max(0) as u128;
        let first = (left * count as u128 / total.max(1) as u128).min(count as u128 - 1) as usize;
        let after = (right * count as u128)
            .div_ceil(total.max(1) as u128)
            .min(count as u128) as usize;
        coverage[first] += 1;
        coverage[after.max(first + 1)] -= 1;
    }
    let mut active = 0;
    for (i, bucket) in result.iter_mut().enumerate() {
        active += coverage[i];
        bucket.observed = active > 0;
    }
    for pair in ordered.windows(2) {
        let previous = &pair[0];
        let current = &pair[1];
        let time = current.started_utc;
        // A gap followed by the same country isn't a country transition.
        if previous.iso == current.iso || time <= start || time > end {
            continue;
        }
        let offset = (time - start).num_nanoseconds().unwrap_or(total).max(0) as u128;
        let index = ((offset.saturating_mul(count as u128).saturating_sub(1))
            / total.max(1) as u128) as usize;
        result[index.min(count - 1)].changes += 1;
        result[index.min(count - 1)].observed = true;
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    fn availability_snapshot(
        at: DateTime<Utc>,
        provider: Availability,
        paused: bool,
    ) -> MonitorSnapshot {
        MonitorSnapshot {
            epoch: 0,
            generated_utc: at,
            groups: Group::ALL.map(|group| GroupView {
                group,
                availability: if group == Group::Ru {
                    provider
                } else {
                    Availability::Online
                },
                reason: "fixture".into(),
                confirmation: false,
                endpoints: Vec::new(),
            }),
            paused,
            network_unavailable: false,
            monitor_error: None,
            capacity_exhausted: false,
        }
    }

    #[test]
    fn availability_rolling_retention_keeps_chart_structure_until_a_span_expires() {
        let now: DateTime<Utc> = "2026-11-12T12:00:00Z".parse().unwrap();
        let original_start = now - Duration::days(7);
        let mut history = AvailabilityHistory {
            spans: Group::ALL
                .map(|group| AvailabilitySpan {
                    group,
                    state: Availability::Online,
                    paused: false,
                    started_utc: original_start,
                    ended_utc: Some(original_start + Duration::hours(1)),
                })
                .into(),
            last_observed_utc: Some(now),
            ..Default::default()
        };
        history.observe(&availability_snapshot(now, Availability::Online, false));
        let structure = history.structure_revision;
        for seconds in 1..=3 {
            let at = now + Duration::seconds(seconds);
            let persisted = history.revision;
            history.observe(&availability_snapshot(at, Availability::Online, false));
            assert_eq!(history.spans[0].started_utc, at - Duration::days(7));
            assert_eq!(history.last_observed_utc, Some(at));
            assert!(
                history.revision > persisted,
                "Retention still has to be saved"
            );
            assert_eq!(
                history.structure_revision, structure,
                "Rolling the seven-day boundary mustn't clear a stationary chart pointer"
            );
        }
        history.prune(now + Duration::hours(1));
        assert_eq!(history.spans.len(), 2);
        assert!(history.structure_revision > structure);
        assert_eq!(history.last_observed_utc, Some(now + Duration::seconds(3)));
    }

    #[test]
    fn availability_structure_reports_real_status_pause_and_close_changes_immediately() {
        let now: DateTime<Utc> = "2026-11-12T12:00:00Z".parse().unwrap();
        let mut history = AvailabilityHistory::default();
        history.observe(&availability_snapshot(now, Availability::Online, false));
        let initial = history.structure_revision;
        assert!(initial > 0);
        history.observe(&availability_snapshot(
            now + Duration::seconds(1),
            Availability::Offline,
            false,
        ));
        assert!(history.structure_revision > initial);
        assert!(history.spans.iter().any(|s| {
            s.group == Group::Ru
                && s.state == Availability::Offline
                && s.started_utc == now + Duration::seconds(1)
                && s.ended_utc.is_none()
        }));
        let changed = history.structure_revision;
        history.observe(&availability_snapshot(
            now + Duration::seconds(2),
            Availability::Offline,
            true,
        ));
        assert!(history.structure_revision > changed);
        assert!(
            history
                .spans
                .iter()
                .filter(|s| s.ended_utc.is_none())
                .all(|s| s.paused)
        );
        let paused = history.structure_revision;
        history.close(now + Duration::seconds(3));
        assert!(history.structure_revision > paused);
        assert!(history.spans.iter().all(|s| s.ended_utc.is_some()));
        let closed = history.structure_revision;
        history.close(now + Duration::seconds(4));
        assert_eq!(history.structure_revision, closed);
    }

    #[test]
    fn availability_structure_is_transient_and_capacity_notice_expiry_is_visible() {
        let now: DateTime<Utc> = "2026-11-12T12:00:00Z".parse().unwrap();
        let mut history = AvailabilityHistory {
            capacity_truncated_through_utc: Some(now - Duration::days(8)),
            ..Default::default()
        };
        history.prune(now);
        assert_eq!(history.structure_revision, 1);
        assert_eq!(history.revision, 1);
        assert!(history.capacity_truncated_through_utc.is_none());
        history.observe(&availability_snapshot(now, Availability::Online, false));
        let mut saved = serde_json::to_value(&history).unwrap();
        assert!(saved.get("revision").is_none());
        assert!(saved.get("structureRevision").is_none());
        saved["structureRevision"] = 999.into();
        let restored: AvailabilityHistory = serde_json::from_value(saved).unwrap();
        assert_eq!(restored.structure_revision, 0);
        assert_eq!(restored.revision, 0);
        assert_eq!(restored.spans, history.spans);
        assert_eq!(restored.last_observed_utc, history.last_observed_utc);
    }
    fn confirmed_observation(at: DateTime<Utc>, ip: &str) -> LocationObservation {
        serde_json::from_value(serde_json::json!({
            "observedUtc":at,"countryCode":"DE","localCountry":"DE",
            "onlineCountry":"DE","iplocateCountry":"DE","confirmed":true,
            "publicIp":ip,"databaseVersion":"v2fly-v1",
            "iplocateDatabaseVersion":"iplocate-v1","resolutionSource":"country.is"
        }))
        .unwrap()
    }
    #[test]
    fn short_session_break_preserves_a_gap_in_country_and_source_histories() {
        let start = Utc::now();
        let resume = start + Duration::seconds(15);
        let mut history = LocationHistory::default();
        history.observe(confirmed_observation(start, "192.0.2.1"));
        history.close(start);
        let mut invalid = confirmed_observation(start + Duration::seconds(5), "192.0.2.1");
        invalid.country_code = Some("INVALID".into());
        history.observe(invalid);
        history.observe(confirmed_observation(resume, "192.0.2.1"));
        assert_eq!(history.stays.len(), 2);
        assert_eq!(history.stays[0].ended_utc, Some(start));
        assert_eq!(history.stays[1].started_utc, resume);
        assert_eq!(history.observations.len(), 2);
        assert_eq!(history.observations[0].until(), start);
        assert_eq!(history.observations[1].observed_utc, resume);
        assert!(
            !serde_json::to_string(&history)
                .unwrap()
                .contains("observationBoundary")
        );
    }
    fn estimate(at: DateTime<Utc>) -> LocationObservation {
        let mut row = confirmed_observation(at, "192.0.2.1");
        row.confirmed = false;
        row.country_code = None;
        row.online_country = None;
        row.resolution_source = Some("local-estimate".into());
        row
    }
    #[test]
    fn explicit_break_also_separates_estimates_without_any_confirmed_stay() {
        let start = Utc::now();
        let mut history = LocationHistory::default();
        history.observe(estimate(start));
        history.close(start);
        history.observe(estimate(start + Duration::seconds(10)));
        assert!(history.stays.is_empty());
        assert_eq!(history.observations.len(), 2);
        assert_eq!(history.observations[0].until(), start);
    }
    #[test]
    fn continuous_estimates_compact_after_the_confirmed_country_is_stale() {
        let start = Utc::now();
        let mut history = LocationHistory::default();
        history.observe(confirmed_observation(start, "192.0.2.1"));
        for seconds in (10..=180).step_by(10) {
            history.observe(estimate(start + Duration::seconds(seconds)));
        }
        assert_eq!(history.observations.len(), 2);
        assert_eq!(
            history.observations[1].until(),
            start + Duration::seconds(180)
        );
        assert_eq!(history.stays[0].ended_utc, Some(start));
    }
    #[test]
    fn repeated_checks_compact_but_ip_and_database_changes_preserve_original_evidence() {
        let start = Utc::now();
        let mut history = LocationHistory::default();
        history.observe(confirmed_observation(start, "192.0.2.1"));
        history.observe(confirmed_observation(
            start + Duration::seconds(10),
            "192.0.2.1",
        ));
        assert_eq!(history.stays.len(), 1);
        assert_eq!(history.observations.len(), 1);
        assert_eq!(history.observations[0].observed_utc, start);
        assert_eq!(
            history.observations[0].until(),
            start + Duration::seconds(10)
        );

        history.observe(confirmed_observation(
            start + Duration::seconds(20),
            "192.0.2.2",
        ));
        let mut reclassified = confirmed_observation(start + Duration::seconds(30), "192.0.2.2");
        reclassified.database_version = Some("v2fly-v2".into());
        reclassified.local_country = Some("FR".into());
        history.observe(reclassified);
        assert_eq!(
            history.stays.len(),
            1,
            "An IP or database change isn't a country change"
        );
        assert_eq!(history.stays[0].started_utc, start);
        assert!(history.stays[0].ended_utc.is_none());
        assert_eq!(
            history.last_observed_utc,
            Some(start + Duration::seconds(30))
        );
        assert_eq!(history.observations.len(), 3);
        assert_eq!(
            history.observations[0].public_ip.as_deref(),
            Some("192.0.2.1")
        );
        assert_eq!(
            history.observations[0].until(),
            start + Duration::seconds(10)
        );
        assert_eq!(
            history.observations[1].database_version.as_deref(),
            Some("v2fly-v1")
        );
        assert_eq!(history.observations[1].local_country.as_deref(), Some("DE"));
        assert_eq!(
            history.observations[2].database_version.as_deref(),
            Some("v2fly-v2")
        );
        assert_eq!(history.observations[2].local_country.as_deref(), Some("FR"));

        let restored: LocationHistory =
            serde_json::from_slice(&serde_json::to_vec(&history).unwrap()).unwrap();
        assert_eq!(restored.stays, history.stays);
        assert_eq!(restored.observations, history.observations);
    }
    #[test]
    fn failed_check_and_sleep_never_fill_the_gap_with_the_previous_country() {
        let start = Utc::now();
        let mut history = LocationHistory::default();
        history.observe(confirmed_observation(start, "192.0.2.1"));
        history.observe(confirmed_observation(
            start + Duration::seconds(10),
            "192.0.2.1",
        ));
        let mut failed = confirmed_observation(start + Duration::seconds(20), "192.0.2.1");
        failed.confirmed = false;
        failed.country_code = None;
        failed.public_ip = None;
        failed.online_country = None;
        failed.resolution_source = Some("local-estimate".into());
        history.observe(failed);
        assert_eq!(
            history.last_observed_utc,
            Some(start + Duration::seconds(10))
        );
        assert_eq!(
            history.stays[0].ended_utc,
            Some(start + Duration::seconds(10))
        );

        let resumed = start + Duration::minutes(5);
        history.observe(confirmed_observation(resumed, "192.0.2.1"));
        assert_eq!(history.stays.len(), 2);
        assert_eq!(history.stays[1].started_utc, resumed);
        assert_eq!(
            history.observations[0].until(),
            start + Duration::seconds(10)
        );
        assert_eq!(
            history.observations.len(),
            3,
            "Unknown time can't be compacted into verified checks"
        );
        let buckets = frequency(&history, start, resumed + Duration::seconds(1), 120);
        assert_eq!(buckets.iter().map(|b| b.changes).sum::<usize>(), 0);
        assert!(buckets.iter().any(|b| !b.observed));

        history.seal_stale(resumed + Duration::minutes(3));
        assert_eq!(history.stays[1].ended_utc, Some(resumed));
        let before = history.observations.clone();
        let mut invalid = confirmed_observation(resumed + Duration::minutes(4), "invalid-ip");
        invalid.country_code = Some("ZZ".into());
        history.observe(invalid);
        assert_eq!(
            history.observations, before,
            "Invalid evidence mustn't enter history"
        );
    }
    #[test]
    fn shutdown_gap_is_not_painted_online() {
        let now = Utc::now();
        let mut h = AvailabilityHistory {
            last_observed_utc: Some(now - Duration::hours(8)),
            spans: vec![AvailabilitySpan {
                group: Group::Ru,
                state: Availability::Online,
                paused: false,
                started_utc: now - Duration::hours(9),
                ended_utc: None,
            }],
            ..Default::default()
        };
        h.seal_stale(now);
        assert_eq!(h.spans[0].ended_utc, h.last_observed_utc);
    }
    #[test]
    fn capacity_preserves_stable_open_group() {
        let now = Utc::now();
        let mut h = AvailabilityHistory::default();
        h.spans.push(AvailabilitySpan {
            group: Group::Ru,
            state: Availability::Online,
            paused: false,
            started_utc: now - Duration::hours(1),
            ended_utc: None,
        });
        for i in 0..10_005 {
            h.spans.push(AvailabilitySpan {
                group: Group::World,
                state: Availability::Limited,
                paused: false,
                started_utc: now - Duration::seconds(20_000 - i),
                ended_utc: Some(now - Duration::seconds(19_999 - i)),
            });
        }
        h.prune(now);
        assert_eq!(h.spans.len(), 10_000);
        assert!(h.structure_revision > 0);
        assert_eq!(h.spans[0].group, Group::Ru);
        assert!(h.spans[0].ended_utc.is_none());
        assert!(h.capacity_truncated_through_utc.is_some());
    }
    #[test]
    fn location_observation_capacity_reports_the_removed_boundary_and_expires_after_retention() {
        let now = Utc::now();
        let oldest = now - Duration::seconds(LOCATION_OBSERVATION_LIMIT as i64);
        let mut history = LocationHistory {
            observations: (0..=LOCATION_OBSERVATION_LIMIT)
                .map(|i| confirmed_observation(oldest + Duration::seconds(i as i64), "192.0.2.1"))
                .collect(),
            ..Default::default()
        };
        history.prune(now);
        assert_eq!(history.observations.len(), LOCATION_OBSERVATION_LIMIT);
        assert_eq!(history.capacity_truncated_through_utc, Some(oldest));
        assert_eq!(history.observations.last().unwrap().observed_utc, now);
        let serialized = serde_json::to_vec(&history).unwrap();
        let mut restored: LocationHistory = serde_json::from_slice(&serialized).unwrap();
        assert_eq!(restored.capacity_truncated_through_utc, Some(oldest));
        restored.prune(now + Duration::days(7));
        assert!(restored.capacity_truncated_through_utc.is_none());
    }
    #[test]
    fn retention_changes_publish_a_new_revision_without_inventing_observations() {
        let now = Utc::now();
        let recorded = now - Duration::days(8);
        let mut locations = LocationHistory::default();
        locations.observe(confirmed_observation(recorded, "192.0.2.1"));
        locations.close(recorded);
        let revision = locations.revision;
        locations.prune(now);
        assert!(locations.observations.is_empty());
        assert_eq!(locations.last_observed_utc, Some(recorded));
        assert!(locations.revision > revision);
        let unchanged = locations.revision;
        locations.prune(now);
        assert_eq!(locations.revision, unchanged);
        let mut availability = AvailabilityHistory {
            spans: vec![AvailabilitySpan {
                group: Group::Ru,
                state: Availability::Online,
                paused: false,
                started_utc: recorded,
                ended_utc: Some(recorded + Duration::hours(1)),
            }],
            last_observed_utc: Some(recorded),
            ..Default::default()
        };
        availability.prune(now);
        assert!(availability.spans.is_empty());
        assert_eq!(availability.revision, 1);
        availability.prune(now);
        assert_eq!(availability.revision, 1);
    }
    #[test]
    fn frequency_boundaries_and_gaps_are_honest_for_every_period() {
        let end = Utc::now();
        for hours in PERIODS {
            let start = end - Duration::hours(hours as i64);
            let h = LocationHistory {
                stays: vec![
                    LocationStay {
                        iso: "DE".into(),
                        started_utc: start - Duration::minutes(10),
                        ended_utc: Some(start),
                    },
                    LocationStay {
                        iso: "PL".into(),
                        started_utc: start,
                        ended_utc: Some(start + Duration::minutes(5)),
                    },
                    LocationStay {
                        iso: "PL".into(),
                        started_utc: start + Duration::minutes(10),
                        ended_utc: Some(end - Duration::minutes(2)),
                    },
                    LocationStay {
                        iso: "RU".into(),
                        started_utc: end,
                        ended_utc: Some(end),
                    },
                ],
                ..Default::default()
            };
            assert_eq!(
                frequency(&h, start, end, 120)
                    .iter()
                    .map(|b| b.changes)
                    .sum::<usize>(),
                1
            );
        }
    }
    #[test]
    fn frequency_orders_persisted_rows_and_counts_point_events() {
        let start = Utc::now();
        let end = start + Duration::hours(1);
        let h = LocationHistory {
            stays: vec![
                LocationStay {
                    iso: "DE".into(),
                    started_utc: start + Duration::minutes(30),
                    ended_utc: Some(end),
                },
                LocationStay {
                    iso: "DE".into(),
                    started_utc: start,
                    ended_utc: Some(start + Duration::minutes(10)),
                },
                LocationStay {
                    iso: "FR".into(),
                    started_utc: start + Duration::minutes(10),
                    ended_utc: Some(start + Duration::minutes(10)),
                },
            ],
            ..Default::default()
        };
        let buckets = frequency(&h, start, end, 120);
        assert_eq!(buckets.iter().map(|b| b.changes).sum::<usize>(), 2);
        assert!(buckets.iter().filter(|b| b.changes > 0).all(|b| b.observed));
    }
    #[test]
    fn seven_day_retention_preserves_only_the_needed_transition_anchor() {
        let now = Utc::now();
        let mut h = LocationHistory {
            stays: vec![
                LocationStay {
                    iso: "RU".into(),
                    started_utc: now - Duration::days(10),
                    ended_utc: Some(now - Duration::days(9)),
                },
                LocationStay {
                    iso: "DE".into(),
                    started_utc: now - Duration::days(9),
                    ended_utc: Some(now - Duration::days(8)),
                },
                LocationStay {
                    iso: "PL".into(),
                    started_utc: now - Duration::days(8),
                    ended_utc: None,
                },
            ],
            ..Default::default()
        };
        h.prune(now);
        assert_eq!(h.stays.len(), 2);
        assert_eq!(h.stays[0].iso, "DE");
        assert_eq!(h.stays[1].started_utc, now - Duration::days(7));
    }
    #[test]
    fn declared_record_caps_are_enforced_while_deserializing() {
        let now = Utc::now();
        let row = AvailabilitySpan {
            group: Group::Ru,
            state: Availability::Online,
            paused: false,
            started_utc: now,
            ended_utc: Some(now),
        };
        let bytes = serde_json::to_vec(&serde_json::json!({"spans":vec![row;10003]})).unwrap();
        assert!(serde_json::from_slice::<AvailabilityHistory>(&bytes).is_err());
    }
}
