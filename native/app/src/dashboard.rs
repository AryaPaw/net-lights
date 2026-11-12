//! Read-only presentation of persisted observations; no network or storage owner.
use chrono::{DateTime, Duration, Utc};
use netlights_core::{history::*, model::*};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TimeRange {
    pub start: DateTime<Utc>,
    pub end: DateTime<Utc>,
}
impl TimeRange {
    pub fn new(start: DateTime<Utc>, end: DateTime<Utc>) -> Option<Self> {
        (end > start).then_some(Self { start, end })
    }
    pub fn millis(self) -> i64 {
        (self.end - self.start).num_milliseconds()
    }
    pub fn zoom(self, a: f32, b: f32) -> Option<Self> {
        if !a.is_finite() || !b.is_finite() {
            return None;
        }
        let lo = a.min(b).clamp(0., 1.);
        let hi = a.max(b).clamp(0., 1.);
        if hi - lo < 0.01 {
            return None;
        }
        Self::new(
            self.start + Duration::milliseconds((self.millis() as f64 * lo as f64) as i64),
            self.start + Duration::milliseconds((self.millis() as f64 * hi as f64) as i64),
        )
    }
}

#[derive(Clone, Debug, Default)]
pub struct Stats {
    pub recorded: bool,
    pub online_ms: i64,
    pub observed_ms: i64,
    pub offline_ms: i64,
    pub offline_duration: Duration,
    pub limited_ms: i64,
    pub outages: usize,
}
#[derive(Clone, Copy, Debug)]
pub struct RecordedInterval {
    pub source: usize,
    pub start: DateTime<Utc>,
    pub end: DateTime<Utc>,
}
pub fn priority(s: &AvailabilitySpan) -> u8 {
    if !s.paused && s.state == Availability::Offline {
        3
    } else if !s.paused && s.state == Availability::Limited {
        2
    } else {
        1
    }
}
pub fn intervals(
    history: &AvailabilityHistory,
    group: Group,
    range: TimeRange,
) -> Vec<RecordedInterval> {
    use std::collections::BTreeSet;
    let mut boundaries = Vec::new();
    let mut points = Vec::new();
    for (i, s) in history
        .spans
        .iter()
        .enumerate()
        .filter(|(_, s)| s.group == group)
    {
        let a = s.started_utc.max(range.start);
        let b = s.ended_utc.unwrap_or(range.end).min(range.end);
        if a < b {
            boundaries.push((a, true, i));
            boundaries.push((b, false, i));
        } else if a == b && s.started_utc == a && s.ended_utc == Some(a) {
            points.push(RecordedInterval {
                source: i,
                start: a,
                end: b,
            });
        }
    }
    boundaries.sort_unstable();
    let mut active = BTreeSet::new();
    let mut previous = range.start;
    let mut result: Vec<RecordedInterval> = Vec::new();
    for (at, starting, i) in boundaries {
        if at > previous
            && let Some(&(_, _, source)) = active.last()
        {
            if let Some(last) = result
                .last_mut()
                .filter(|s| s.source == source && s.end == previous)
            {
                last.end = at;
            } else {
                result.push(RecordedInterval {
                    source,
                    start: previous,
                    end: at,
                });
            }
        }
        let s = &history.spans[i];
        let key = (priority(s), s.started_utc, i);
        if starting {
            active.insert(key);
        } else {
            active.remove(&key);
        }
        previous = at;
    }
    result.extend(points);
    result.sort_by_key(|s| (s.start, priority(&history.spans[s.source]), s.source));
    result
}
pub fn stats(history: &AvailabilityHistory, group: Group, range: TimeRange) -> Stats {
    let mut result = Stats::default();
    let mut elapsed = [Duration::zero(); 3];
    let mut last_offline_end = None;
    for interval in intervals(history, group, range) {
        let s = &history.spans[interval.source];
        let a = interval.start;
        let b = interval.end;
        if a >= b {
            continue;
        }
        result.recorded = true;
        let duration = b - a;
        if !s.paused && s.state != Availability::Unknown {
            match s.state {
                Availability::Online => elapsed[0] += duration,
                Availability::Offline => {
                    elapsed[2] += duration;
                    if last_offline_end != Some(a) {
                        result.outages += 1;
                    }
                    last_offline_end = Some(b);
                }
                Availability::Limited => elapsed[1] += duration,
                Availability::Unknown => (),
            }
        }
    }
    result.online_ms = elapsed[0].num_milliseconds();
    result.limited_ms = elapsed[1].num_milliseconds();
    result.offline_duration = elapsed[2];
    result.offline_ms = elapsed[2].num_milliseconds();
    result.observed_ms = (elapsed[0] + elapsed[1] + elapsed[2]).num_milliseconds();
    result
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum EventKind {
    Availability {
        group: Group,
        state: Availability,
        paused: bool,
    },
    Country {
        previous: String,
        current: String,
        previous_end: DateTime<Utc>,
    },
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HistoryEvent {
    /// Recorded boundary, not an asserted time of the physical network change.
    pub start: DateTime<Utc>,
    pub end: DateTime<Utc>,
    pub ongoing: bool,
    pub kind: EventKind,
}
impl HistoryEvent {
    pub fn color(&self) -> u32 {
        match &self.kind {
            EventKind::Availability { state, paused, .. } => {
                crate::charts::status_color(*state, *paused)
            }
            EventKind::Country { current, .. } => crate::charts::country_color(current),
        }
    }
}
pub fn country_changes(history: &LocationHistory, range: TimeRange) -> Vec<HistoryEvent> {
    // The loader and observation owner maintain chronological stays. A resumed
    // observation of the same country is a gap, not a country transition.
    history
        .stays
        .windows(2)
        .filter_map(|pair| {
            let (a, b) = (&pair[0], &pair[1]);
            if a.iso == b.iso || b.started_utc <= range.start || b.started_utc > range.end {
                return None;
            }
            Some(HistoryEvent {
                start: b.started_utc,
                end: b.started_utc,
                ongoing: false,
                kind: EventKind::Country {
                    previous: a.iso.clone(),
                    current: b.iso.clone(),
                    previous_end: history
                        .observations
                        .partition_point(|o| o.observed_utc < b.started_utc)
                        .checked_sub(1)
                        .and_then(|i| history.observations.get(i))
                        .filter(|o| {
                            o.confirmed
                                && o.country_code.as_deref() == Some(a.iso.as_str())
                                && o.until() <= b.started_utc
                        })
                        .map(|o| o.until())
                        .unwrap_or_else(|| {
                            a.ended_utc
                                .filter(|end| *end < b.started_utc)
                                .unwrap_or(a.started_utc)
                        }),
                },
            })
        })
        .collect()
}
pub fn recent_events(
    history: &AvailabilityHistory,
    changes: &[HistoryEvent],
    range: TimeRange,
) -> Vec<HistoryEvent> {
    let candidates = history
        .spans
        .iter()
        .filter_map(|s| {
            if s.state == Availability::Online && !s.paused {
                return None;
            }
            let end = s.ended_utc.unwrap_or(range.end);
            if s.started_utc > range.end || end <= range.start || end < s.started_utc {
                return None;
            }
            Some(HistoryEvent {
                start: s.started_utc,
                end,
                ongoing: s.ended_utc.is_none(),
                kind: EventKind::Availability {
                    group: s.group,
                    state: s.state,
                    paused: s.paused,
                },
            })
        })
        .chain(changes.iter().rev().take(8).cloned());
    let mut events: Vec<HistoryEvent> = Vec::with_capacity(8);
    for event in candidates {
        let i = events.partition_point(|old| {
            old.start > event.start
                || (old.start == event.start && event_order(old) <= event_order(&event))
        });
        if i < 8 {
            events.insert(i, event);
            events.truncate(8);
        }
    }
    events
}
fn event_order(event: &HistoryEvent) -> usize {
    match event.kind {
        EventKind::Availability { group, .. } => group.index(),
        EventKind::Country { .. } => 2,
    }
}
pub fn country_at(history: &LocationHistory, at: DateTime<Utc>) -> Option<&str> {
    let i = history
        .stays
        .partition_point(|s| s.started_utc <= at)
        .checked_sub(1)?;
    let s = &history.stays[i];
    let end = s
        .ended_utc
        .unwrap_or(history.last_observed_utc.unwrap_or(s.started_utc));
    (at <= end).then_some(s.iso.as_str())
}
pub fn select_availability(
    history: &AvailabilityHistory,
    group: Group,
    at: DateTime<Utc>,
    now: DateTime<Utc>,
) -> Option<HistoryEvent> {
    let s = history
        .spans
        .iter()
        .enumerate()
        .filter(|(_, s)| {
            let end = s.ended_utc.unwrap_or(now);
            s.group == group
                && s.started_utc <= at
                && (at < end || (at == end && (s.ended_utc.is_none() || s.started_utc == end)))
        })
        .max_by_key(|(i, s)| (priority(s), s.started_utc, *i))
        .map(|(_, s)| s)?;
    Some(HistoryEvent {
        start: s.started_utc,
        end: s.ended_utc.unwrap_or(now),
        ongoing: s.ended_utc.is_none(),
        kind: EventKind::Availability {
            group,
            state: s.state,
            paused: s.paused,
        },
    })
}
pub fn overall(snapshot: &MonitorSnapshot) -> (Availability, bool) {
    if snapshot.monitor_error.is_some() {
        return (Availability::Unknown, false);
    }
    if snapshot.paused {
        return (Availability::Unknown, true);
    }
    if snapshot.network_unavailable {
        return (Availability::Offline, false);
    }
    let states = snapshot.groups.each_ref().map(|g| g.availability);
    for state in [
        Availability::Offline,
        Availability::Unknown,
        Availability::Limited,
    ] {
        if states.contains(&state) {
            return (state, false);
        }
    }
    (Availability::Online, false)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn range(hours: u32) -> TimeRange {
        let end: DateTime<Utc> = "2026-11-12T12:00:00Z".parse().unwrap();
        TimeRange {
            start: end - Duration::hours(hours as i64),
            end,
        }
    }
    #[test]
    fn submillisecond_spans_accumulate_before_duration_rounding() {
        let r = range(1);
        let a = r.start;
        let history = AvailabilityHistory {
            spans: vec![
                AvailabilitySpan {
                    group: Group::Ru,
                    state: Availability::Offline,
                    paused: false,
                    started_utc: a,
                    ended_utc: Some(a + Duration::microseconds(500)),
                },
                AvailabilitySpan {
                    group: Group::Ru,
                    state: Availability::Offline,
                    paused: false,
                    started_utc: a + Duration::microseconds(500),
                    ended_utc: Some(a + Duration::microseconds(1200)),
                },
            ],
            ..Default::default()
        };
        let actual = stats(&history, Group::Ru, r);
        assert_eq!(actual.offline_ms, 1);
        assert_eq!(actual.observed_ms, 1);
        assert_eq!(actual.offline_duration, Duration::microseconds(1200));
        assert_eq!(actual.outages, 1);
    }
    #[test]
    fn coverage_excludes_sleep_unknown_pause_and_clips_all_eight_periods() {
        for hours in PERIODS {
            let r = range(hours);
            let mut h = AvailabilityHistory::default();
            assert!(!stats(&h, Group::Ru, r).recorded);
            let states = [
                (Availability::Online, false),
                (Availability::Limited, false),
                (Availability::Offline, false),
                (Availability::Online, true),
                (Availability::Unknown, false),
            ];
            for (i, (state, paused)) in states.into_iter().enumerate() {
                let start = r.start + Duration::minutes(i as i64 * 5);
                h.spans.push(AvailabilitySpan {
                    group: Group::Ru,
                    state,
                    paused,
                    started_utc: start,
                    ended_utc: Some(start + Duration::minutes(5)),
                });
            }
            let s = stats(&h, Group::Ru, r);
            assert_eq!(
                (
                    s.online_ms,
                    s.observed_ms,
                    s.offline_ms,
                    s.limited_ms,
                    s.outages
                ),
                (300_000, 900_000, 300_000, 300_000, 1)
            );
            assert_eq!(stats(&h, Group::World, r).observed_ms, 0);
            assert_eq!(recent_events(&h, &[], r).len(), 4);
        }
    }
    #[test]
    fn missing_country_stays_are_not_carried_across_a_gap_or_into_the_future() {
        let r = range(1);
        let a = r.start;
        let h = LocationHistory {
            stays: vec![
                LocationStay {
                    iso: "DE".into(),
                    started_utc: a,
                    ended_utc: Some(a + Duration::minutes(5)),
                },
                LocationStay {
                    iso: "DE".into(),
                    started_utc: a + Duration::minutes(10),
                    ended_utc: Some(a + Duration::minutes(15)),
                },
                LocationStay {
                    iso: "JP".into(),
                    started_utc: a + Duration::minutes(20),
                    ended_utc: None,
                },
            ],
            last_observed_utc: Some(a + Duration::minutes(25)),
            ..Default::default()
        };
        assert_eq!(country_at(&h, a + Duration::minutes(7)), None);
        assert_eq!(country_at(&h, a + Duration::minutes(21)), Some("JP"));
        assert_eq!(country_at(&h, a + Duration::minutes(26)), None);
        let changes = country_changes(&h, r);
        assert_eq!(changes.len(), 1);
        assert_eq!(changes[0].start, a + Duration::minutes(20));
        assert!(
            matches!(&changes[0].kind,EventKind::Country {previous_end,..} if *previous_end==a+Duration::minutes(15))
        );
    }
    #[test]
    fn zoom_is_bounded_reversible_and_rejects_accidental_clicks() {
        let r = range(1);
        assert_eq!(r.zoom(0.75, 0.25), r.zoom(0.25, 0.75));
        assert_eq!(r.zoom(-1., 2.), Some(r));
        assert_eq!(r.zoom(f32::NAN, 1.), None);
        assert_eq!(r.zoom(0.5, 0.501), None);
        assert_eq!(r.zoom(0.25, 0.75).unwrap().millis(), 1_800_000);
    }
    #[test]
    fn zero_length_and_boundary_spans_do_not_invent_coverage_or_outages() {
        let r = range(1);
        let h = AvailabilityHistory {
            spans: vec![
                AvailabilitySpan {
                    group: Group::Ru,
                    state: Availability::Offline,
                    paused: false,
                    started_utc: r.start - Duration::minutes(1),
                    ended_utc: Some(r.start),
                },
                AvailabilitySpan {
                    group: Group::Ru,
                    state: Availability::Offline,
                    paused: false,
                    started_utc: r.end,
                    ended_utc: Some(r.end),
                },
            ],
            ..Default::default()
        };
        let s = stats(&h, Group::Ru, r);
        assert!(!s.recorded);
        assert_eq!(s.outages, 0);
    }
    #[test]
    fn separate_failures_with_the_same_status_do_not_reopen_the_first_selection() {
        let r = range(1);
        let at = r.end - Duration::seconds(10);
        let h = AvailabilityHistory {
            spans: vec![
                AvailabilitySpan {
                    group: Group::World,
                    state: Availability::Offline,
                    paused: false,
                    started_utc: at,
                    ended_utc: Some(at + Duration::seconds(2)),
                },
                AvailabilitySpan {
                    group: Group::World,
                    state: Availability::Offline,
                    paused: false,
                    started_utc: at + Duration::seconds(8),
                    ended_utc: None,
                },
            ],
            ..Default::default()
        };
        let first =
            select_availability(&h, Group::World, at + Duration::seconds(1), r.end).unwrap();
        assert!(!first.ongoing);
        assert_eq!(first.end, at + Duration::seconds(2));
        let current = select_availability(&h, Group::World, r.end, r.end).unwrap();
        assert!(current.ongoing);
        assert_eq!(select_availability(&h, Group::Ru, at, r.end), None);
    }
    #[test]
    fn overall_never_hides_monitor_errors_or_an_unknown_direction_behind_success() {
        let mut s = MonitorSnapshot {
            epoch: 0,
            generated_utc: range(1).end,
            paused: false,
            network_unavailable: false,
            monitor_error: None,
            capacity_exhausted: false,
            groups: Group::ALL.map(|group| GroupView {
                group,
                availability: Availability::Online,
                reason: String::new(),
                confirmation: true,
                endpoints: vec![],
            }),
        };
        assert_eq!(overall(&s), (Availability::Online, false));
        s.groups[1].availability = Availability::Unknown;
        assert_eq!(overall(&s), (Availability::Unknown, false));
        s.paused = true;
        assert_eq!(overall(&s), (Availability::Unknown, true));
        s.monitor_error = Some("stopped".into());
        assert_eq!(overall(&s), (Availability::Unknown, false));
        s.monitor_error = None;
        s.paused = false;
        s.network_unavailable = true;
        assert_eq!(overall(&s), (Availability::Offline, false));
    }
    #[test]
    fn selected_event_preserves_full_recorded_interval_outside_a_zoom_window() {
        let r = range(3);
        let h = AvailabilityHistory {
            spans: vec![AvailabilitySpan {
                group: Group::Ru,
                state: Availability::Limited,
                paused: false,
                started_utc: r.start,
                ended_utc: Some(r.end),
            }],
            ..Default::default()
        };
        let zoom = r.zoom(0.4, 0.6).unwrap();
        let event = select_availability(&h, Group::Ru, zoom.start, r.end).unwrap();
        assert_eq!(event.start, r.start);
        assert_eq!(event.end, r.end);
        assert_eq!(stats(&h, Group::Ru, zoom).limited_ms, zoom.millis());
    }
    #[test]
    fn shuffled_and_overlapping_records_share_one_bounded_coverage_with_the_chart() {
        let r = range(1);
        let a = r.start;
        let span = |state, start, end| AvailabilitySpan {
            group: Group::Ru,
            state,
            paused: false,
            started_utc: a + Duration::minutes(start),
            ended_utc: Some(a + Duration::minutes(end)),
        };
        let h = AvailabilityHistory {
            spans: vec![
                span(Availability::Online, 30, 60),
                span(Availability::Offline, 10, 40),
                span(Availability::Limited, 0, 20),
            ],
            ..Default::default()
        };
        let parts = intervals(&h, Group::Ru, r);
        assert!(parts.windows(2).all(|p| p[0].end <= p[1].start));
        let s = stats(&h, Group::Ru, r);
        assert_eq!(s.observed_ms, r.millis());
        assert_eq!(s.offline_ms, 1_800_000);
        assert_eq!(s.online_ms, 1_200_000);
        assert_eq!(s.limited_ms, 600_000);
        assert_eq!(s.outages, 1);
        assert!(matches!(
            select_availability(&h, Group::Ru, a + Duration::minutes(35), r.end)
                .unwrap()
                .kind,
            EventKind::Availability {
                state: Availability::Offline,
                ..
            }
        ));
        let plotted = crate::charts::availability(&h, Group::Ru, r.start, r.end, true);
        assert_eq!(plotted.start, r.start);
        assert_eq!(plotted.end, r.end);
    }
    #[test]
    fn recent_events_are_bounded_and_chronological_even_when_records_are_shuffled() {
        let r = range(1);
        let h = AvailabilityHistory {
            spans: (0..100)
                .rev()
                .map(|i| AvailabilitySpan {
                    group: Group::World,
                    state: Availability::Offline,
                    paused: false,
                    started_utc: r.start + Duration::seconds(i * 20),
                    ended_utc: Some(r.start + Duration::seconds(i * 20 + 1)),
                })
                .collect(),
            ..Default::default()
        };
        let events = recent_events(&h, &[], r);
        assert_eq!(events.len(), 8);
        assert!(events.windows(2).all(|p| p[0].start > p[1].start));
        assert_eq!(events[0].start, r.start + Duration::seconds(1980));
    }
}
