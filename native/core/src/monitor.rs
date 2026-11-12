use crate::model::*;
use chrono::{DateTime, Duration, Utc};
use std::collections::{HashMap, HashSet, VecDeque};

#[derive(Clone, Debug)]
pub struct ProbeWork {
    pub endpoint: Endpoint,
    pub epoch: u64,
    pub attempt: u64,
    pub started: u64,
    pub deadline_ms: u64,
    pub manual: bool,
}
#[derive(Clone)]
struct Sample {
    epoch: u64,
    attempt: u64,
    started: u64,
    completed: u64,
    utc: DateTime<Utc>,
    result: ProbeResult,
}
struct Slot {
    endpoint: Endpoint,
    last_started: Option<u64>,
    next_allowed: u64,
    physical: Option<u64>,
    sample: Option<Sample>,
    stats: EndpointStats,
    episode_issued: bool,
    episode_covered: bool,
}
struct Episode {
    start: u64,
    deadline: u64,
    issued: usize,
    successes: HashSet<String>,
}
struct GroupRuntime {
    group: Group,
    slots: Vec<Slot>,
    cursor: usize,
    next_slot: u64,
    saw_online: bool,
    episode: Option<Episode>,
    confirmation_ended: Option<u64>,
    failures: VecDeque<String>,
}

/// A single sequential owner drives all admission, evidence and state transitions.
/// Time is injected in monotonic milliseconds so tests don't depend on wall-clock sleeps.
pub struct Monitor {
    groups: [GroupRuntime; 2],
    epoch: u64,
    next_attempt: u64,
    pending: HashMap<u64, ProbeWork>,
    starts: VecDeque<u64>,
    interval_ms: u64,
    pub paused: bool,
    pub network_unavailable: bool,
    pub monitor_error: Option<String>,
    capacity: bool,
    probe_deadline_ms: u64,
}
impl Monitor {
    pub fn new(endpoints: Vec<Endpoint>, seconds: u64, now: u64) -> anyhow::Result<Self> {
        validate_endpoints(&endpoints)?;
        let interval_ms = normalized_interval(seconds);
        let groups = Group::ALL.map(|group| GroupRuntime {
            group,
            slots: endpoints
                .iter()
                .filter(|e| e.group == group)
                .cloned()
                .map(|endpoint| Slot {
                    endpoint,
                    last_started: None,
                    next_allowed: 0,
                    physical: None,
                    sample: None,
                    stats: EndpointStats::default(),
                    episode_issued: false,
                    episode_covered: false,
                })
                .collect(),
            cursor: 0,
            next_slot: now
                + if group == Group::World {
                    interval_ms / 2
                } else {
                    0
                },
            saw_online: false,
            episode: Some(Episode {
                start: now,
                deadline: now + 12_000,
                issued: 0,
                successes: HashSet::new(),
            }),
            confirmation_ended: None,
            failures: VecDeque::new(),
        });
        Ok(Self {
            groups,
            epoch: 1,
            next_attempt: 0,
            pending: HashMap::new(),
            starts: VecDeque::new(),
            interval_ms,
            paused: false,
            network_unavailable: false,
            monitor_error: None,
            capacity: false,
            probe_deadline_ms: 2000,
        })
    }
    pub fn epoch(&self) -> u64 {
        self.epoch
    }
    pub fn physical_count(&self) -> usize {
        self.pending.len()
    }
    pub fn set_probe_deadline(&mut self, milliseconds: u64) -> anyhow::Result<()> {
        anyhow::ensure!(
            milliseconds > 0 && milliseconds <= 60_000,
            "Invalid probe deadline"
        );
        self.probe_deadline_ms = milliseconds;
        Ok(())
    }
    pub fn set_interval(&mut self, seconds: u64, now: u64) {
        let next = normalized_interval(seconds);
        if next == self.interval_ms {
            return;
        }
        self.interval_ms = next;
        for g in &mut self.groups {
            g.next_slot = now
                + if g.group == Group::World {
                    self.interval_ms / 2
                } else {
                    self.interval_ms
                };
        }
    }
    pub fn new_epoch(&mut self, now: u64) {
        self.epoch = self.epoch.saturating_add(1);
        self.network_unavailable = false;
        self.monitor_error = None;
        self.capacity = false;
        for group in &mut self.groups {
            group.saw_online = false;
            group.failures.clear();
            for slot in &mut group.slots {
                slot.sample = None;
            }
            Self::begin_episode(group, now);
        }
        // Pending work still owns physical permits until its transport acknowledges completion.
    }
    pub fn set_paused(&mut self, paused: bool, now: u64) {
        let _ = now;
        self.paused = paused;
    }
    pub fn request_check(&mut self, now: u64) {
        if self.paused || self.network_unavailable {
            return;
        }
        for g in &mut self.groups {
            if g.episode.is_none()
                && g.confirmation_ended
                    .is_none_or(|t| now.saturating_sub(t) >= 15_000)
            {
                Self::begin_episode(g, now);
            }
        }
    }
    pub fn tick(&mut self, now: u64) -> Vec<ProbeWork> {
        self.trim_starts(now);
        if self.paused || self.network_unavailable {
            return Vec::new();
        }
        self.capacity = false;
        let mut work = Vec::new();
        for gi in 0..2 {
            if self.groups[gi]
                .episode
                .as_ref()
                .is_some_and(|ep| now >= ep.deadline)
            {
                Self::end_episode(&mut self.groups[gi], now);
            }
            if now >= self.groups[gi].next_slot {
                let group = &mut self.groups[gi];
                group.next_slot += self.interval_ms;
                if group.next_slot <= now {
                    group.next_slot = now + self.interval_ms;
                }
                if group.episode.is_none() {
                    let si = group.cursor;
                    group.cursor = (si + 1) % group.slots.len();
                    if let Some(item) = self.launch(gi, si, now, false, self.probe_deadline_ms) {
                        work.push(item);
                    }
                }
            }
            loop {
                let group = &self.groups[gi];
                let Some(ep) = &group.episode else { break };
                if ep.issued >= 7 {
                    break;
                }
                let next = group
                    .slots
                    .iter()
                    .enumerate()
                    .filter(|(_, s)| {
                        !s.episode_issued
                            && !s.episode_covered
                            && s.physical.is_none()
                            && now >= s.next_allowed
                            && s.last_started.is_none_or(|t| now.saturating_sub(t) >= 8000)
                    })
                    .min_by_key(|(_, s)| s.last_started)
                    .map(|(si, _)| si);
                let Some(si) = next else { break };
                let Some(item) = self.launch(gi, si, now, false, self.probe_deadline_ms) else {
                    break;
                };
                work.push(item);
            }
        }
        work
    }
    pub fn reserve_manual(&mut self, id: &str, now: u64) -> anyhow::Result<ProbeWork> {
        anyhow::ensure!(
            !self.paused && !self.network_unavailable,
            "Проверки приостановлены или сеть недоступна."
        );
        for gi in 0..2 {
            if let Some(si) = self.groups[gi]
                .slots
                .iter()
                .position(|s| s.endpoint.id == id)
            {
                return self.launch(gi, si, now, true, 15_000)
                    .ok_or_else(|| anyhow::anyhow!("Адрес уже проверяется, действует пауза сервера или достигнут лимит запросов."));
            }
        }
        anyhow::bail!("Адрес не найден.")
    }
    fn trim_starts(&mut self, now: u64) {
        while self
            .starts
            .front()
            .is_some_and(|t| now.saturating_sub(*t) >= 60_000)
        {
            self.starts.pop_front();
        }
    }
    fn launch(
        &mut self,
        gi: usize,
        si: usize,
        now: u64,
        manual: bool,
        deadline_ms: u64,
    ) -> Option<ProbeWork> {
        self.trim_starts(now);
        let group = &mut self.groups[gi];
        let slot = &group.slots[si];
        if self.pending.len() >= 4 {
            self.capacity = true;
            return None;
        }
        if group.slots.iter().filter(|s| s.physical.is_some()).count() >= 2
            || slot.physical.is_some()
            || now < slot.next_allowed
            || slot
                .last_started
                .is_some_and(|t| now.saturating_sub(t) < 8000)
            || self.starts.len() >= 120
            || self
                .starts
                .iter()
                .filter(|t| now.saturating_sub(**t) < 1000)
                .count()
                >= 4
        {
            return None;
        }
        self.next_attempt += 1;
        let slot = &mut group.slots[si];
        slot.last_started = Some(now);
        slot.physical = Some(self.next_attempt);
        if !manual && let Some(ep) = &mut group.episode {
            ep.issued += 1;
            slot.episode_issued = true;
        }
        let work = ProbeWork {
            endpoint: slot.endpoint.clone(),
            epoch: self.epoch,
            attempt: self.next_attempt,
            started: now,
            deadline_ms,
            manual,
        };
        self.starts.push_back(now);
        self.pending.insert(work.attempt, work.clone());
        Some(work)
    }
    /// Call only after the transport future is finished/dropped and its stream released.
    pub fn complete(
        &mut self,
        work: &ProbeWork,
        result: ProbeResult,
        now: u64,
        utc: DateTime<Utc>,
    ) {
        let Some(pending) = self.pending.remove(&work.attempt) else {
            return;
        };
        let gi = pending.endpoint.group.index();
        let Some(si) = self.groups[gi]
            .slots
            .iter()
            .position(|s| s.endpoint.id == pending.endpoint.id)
        else {
            return;
        };
        let group = &mut self.groups[gi];
        let slot = &mut group.slots[si];
        let had_episode = group.episode.is_some();
        if slot.physical != Some(work.attempt) {
            return;
        }
        slot.physical = None;
        if pending.epoch != self.epoch || result.outcome == Outcome::Cancelled {
            return;
        }
        slot.next_allowed = slot
            .next_allowed
            .max(now.saturating_add(result.retry_after_ms))
            .max(work.started.saturating_add(8000));
        if work.manual {
            return;
        }
        if slot
            .sample
            .as_ref()
            .is_some_and(|s| s.attempt >= work.attempt)
        {
            return;
        }
        slot.stats.samples += 1;
        match result.outcome {
            Outcome::Reachable => slot.stats.reachable += 1,
            Outcome::Unreachable => slot.stats.unreachable += 1,
            Outcome::Indeterminate => slot.stats.indeterminate += 1,
            Outcome::Cancelled => {}
        }
        if matches!(result.status, Some(403 | 429)) {
            slot.stats.restricted += 1;
        }
        if matches!(result.failure, Some("deadline" | "timeout")) {
            slot.stats.timeouts += 1;
        }
        if result.failure == Some("dns") {
            slot.stats.dns_failures += 1;
        }
        slot.stats.last_http = result.status.or(slot.stats.last_http);
        if let Some(failure) = result.failure {
            slot.stats.last_failure = Some(failure.into());
        }
        let outcome = result.outcome;
        slot.sample = Some(Sample {
            epoch: self.epoch,
            attempt: work.attempt,
            started: work.started,
            completed: now,
            utc,
            result,
        });
        if let Some(ep) = &mut group.episode
            && work.started >= ep.start
        {
            slot.episode_covered = true;
            if outcome == Outcome::Reachable {
                ep.successes.insert(slot.endpoint.infrastructure_id.clone());
            }
        }
        if outcome == Outcome::Reachable {
            group.failures.clear();
        }
        if group
            .episode
            .as_ref()
            .is_some_and(|ep| ep.successes.len() >= 2)
            || group.episode.is_some()
                && group
                    .slots
                    .iter()
                    .all(|s| s.episode_covered && s.physical.is_none())
        {
            Self::end_episode(group, now);
        }
        if outcome == Outcome::Unreachable {
            let infra = group.slots[si].endpoint.infrastructure_id.clone();
            group.failures.push_back(infra);
            while group.failures.len() > 32 {
                group.failures.pop_front();
            }
            let current = group_view(group, self.epoch, now, utc, self.interval_ms, false, None);
            let leaving_online = current.availability == Availability::Online;
            let needed = if leaving_online { 2 } else { 1 };
            if !had_episode
                && group.episode.is_none()
                && current.availability != Availability::Offline
                && (leaving_online
                    || group
                        .confirmation_ended
                        .is_none_or(|t| now.saturating_sub(t) >= 15_000))
                && group.failures.iter().collect::<HashSet<_>>().len() >= needed
            {
                Self::begin_episode(group, now);
            }
        }
        if group_view(group, self.epoch, now, utc, self.interval_ms, false, None).availability
            == Availability::Online
        {
            group.saw_online = true;
        }
    }
    fn begin_episode(group: &mut GroupRuntime, now: u64) {
        let mut ep = Episode {
            start: now,
            deadline: now + 12_000,
            issued: 0,
            successes: HashSet::new(),
        };
        for slot in &mut group.slots {
            slot.episode_issued = false;
            slot.episode_covered = false;
            if slot.physical.is_some() && slot.last_started.is_some_and(|t| t >= now) {
                slot.episode_covered = true;
            }
            if let Some(s) = &slot.sample
                && s.started >= now
                && s.result.outcome != Outcome::Cancelled
            {
                slot.episode_covered = true;
                if s.result.outcome == Outcome::Reachable {
                    ep.successes.insert(slot.endpoint.infrastructure_id.clone());
                }
            }
        }
        group.episode = Some(ep);
    }
    fn end_episode(group: &mut GroupRuntime, now: u64) {
        group.episode = None;
        group.confirmation_ended = Some(now);
        for slot in &mut group.slots {
            slot.episode_issued = false;
            slot.episode_covered = false;
        }
    }
    pub fn snapshot(&mut self, now: u64, utc: DateTime<Utc>) -> MonitorSnapshot {
        let groups = std::array::from_fn(|gi| {
            let g = &mut self.groups[gi];
            let view = group_view(
                g,
                self.epoch,
                now,
                utc,
                self.interval_ms,
                self.network_unavailable,
                self.monitor_error.as_deref(),
            );
            if view.availability == Availability::Online {
                g.saw_online = true;
            }
            view
        });
        MonitorSnapshot {
            epoch: self.epoch,
            generated_utc: utc,
            groups,
            paused: self.paused,
            network_unavailable: self.network_unavailable,
            monitor_error: self.monitor_error.clone(),
            capacity_exhausted: self.capacity,
        }
    }
}
fn normalized_interval(seconds: u64) -> u64 {
    if AVAILABILITY_INTERVALS.contains(&seconds) {
        seconds * 1000
    } else {
        5000
    }
}
fn group_view(
    group: &GroupRuntime,
    epoch: u64,
    now: u64,
    utc: DateTime<Utc>,
    interval_ms: u64,
    unavailable: bool,
    error: Option<&str>,
) -> GroupView {
    let ttl = 20_000.max(interval_ms * (group.slots.len() as u64 + 3));
    let mut reachable = HashSet::new();
    let mut unreachable = HashSet::new();
    let mut failures = 0;
    let endpoints = group
        .slots
        .iter()
        .map(|slot| {
            let fresh = slot.sample.as_ref().is_some_and(|s| {
                s.epoch == epoch
                    && now.saturating_sub(s.completed) <= ttl
                    && !(s.result.outcome == Outcome::Reachable
                        && group
                            .episode
                            .as_ref()
                            .is_some_and(|ep| s.started < ep.start))
            });
            let sample = slot.sample.as_ref().filter(|_| fresh);
            match sample.map(|s| s.result.outcome) {
                Some(Outcome::Reachable) => {
                    reachable.insert(&slot.endpoint.infrastructure_id);
                }
                Some(Outcome::Unreachable) => {
                    failures += 1;
                    unreachable.insert(&slot.endpoint.infrastructure_id);
                }
                _ => {}
            }
            EndpointView {
                endpoint: slot.endpoint.clone(),
                outcome: sample.map(|s| s.result.outcome),
                http_status: sample.and_then(|s| s.result.status),
                failure: sample.and_then(|s| s.result.failure.map(str::to_owned)),
                elapsed_ms: sample.map(|s| s.result.elapsed_ms),
                completed_utc: slot.sample.as_ref().map(|s| s.utc),
                fresh,
                in_flight: slot.physical.is_some(),
                stats: slot.stats.clone(),
                pause_until_utc: slot
                    .sample
                    .as_ref()
                    .filter(|s| {
                        matches!(s.result.status, Some(403 | 429 | 503)) && slot.next_allowed > now
                    })
                    .map(|_| {
                        utc + Duration::milliseconds(
                            (slot.next_allowed - now).min(i64::MAX as u64) as i64
                        )
                    }),
            }
        })
        .collect();
    let (availability, reason) = if error.is_some() {
        (Availability::Unknown, "Локальная ошибка монитора.")
    } else if unavailable {
        (Availability::Offline, "Нет сетевого подключения.")
    } else if reachable.len() >= 2 {
        (
            Availability::Online,
            "Не менее двух независимых HTTPS-подтверждений.",
        )
    } else if reachable.len() == 1 {
        (
            Availability::Limited,
            "Одно независимое HTTPS-подтверждение.",
        )
    } else if unreachable.len() >= 3
        && failures >= 3
        && (group.saw_online || failures == group.slots.len())
    {
        (
            Availability::Offline,
            "Контрольные HTTPS-направления недоступны.",
        )
    } else {
        (Availability::Unknown, "Недостаточно свежих доказательств.")
    };
    GroupView {
        group: group.group,
        availability,
        reason: reason.into(),
        confirmation: group.episode.is_some(),
        endpoints,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn result(outcome: Outcome) -> ProbeResult {
        ProbeResult {
            outcome,
            status: if outcome == Outcome::Reachable {
                Some(204)
            } else {
                None
            },
            failure: None,
            elapsed_ms: 10,
            retry_after_ms: 0,
        }
    }
    #[test]
    fn physical_limits_survive_epoch_until_acknowledged() {
        let mut m = Monitor::new(builtins(), 5, 0).unwrap();
        let w = m.tick(0);
        assert_eq!(w.len(), 4);
        m.new_epoch(1);
        assert!(m.tick(1).is_empty());
        m.complete(&w[0], result(Outcome::Reachable), 2, Utc::now());
        assert_eq!(m.physical_count(), 3);
        assert!(
            m.snapshot(2, Utc::now())
                .groups
                .iter()
                .all(|g| g.availability == Availability::Unknown)
        );
    }
    #[test]
    fn two_independent_successes_finish_confirmation() {
        let mut m = Monitor::new(builtins(), 5, 0).unwrap();
        let w = m.tick(0);
        for item in w.iter().filter(|w| w.endpoint.group == Group::Ru) {
            m.complete(item, result(Outcome::Reachable), 10, Utc::now());
        }
        let s = m.snapshot(10, Utc::now());
        assert_eq!(s.groups[0].availability, Availability::Online);
        assert!(!s.groups[0].confirmation);
    }
    #[test]
    fn manual_does_not_vote_and_duplicate_completion_is_ignored() {
        let mut m = Monitor::new(builtins(), 5, 0).unwrap();
        let w = m.reserve_manual("ru-yandex", 0).unwrap();
        m.complete(&w, result(Outcome::Reachable), 10, Utc::now());
        m.complete(&w, result(Outcome::Reachable), 11, Utc::now());
        let s = m.snapshot(11, Utc::now());
        assert_eq!(s.groups[0].availability, Availability::Unknown);
        assert_eq!(s.groups[0].endpoints[0].stats.samples, 0);
    }
    #[test]
    fn long_intervals_extend_freshness_without_wall_clock_dependency() {
        let mut m = Monitor::new(builtins(), 300, 0).unwrap();
        let w = m.tick(0);
        m.complete(&w[0], result(Outcome::Reachable), 10, Utc::now());
        assert!(m.snapshot(29_000, Utc::now() - Duration::days(1)).groups[0].endpoints[0].fresh);
    }
    #[test]
    fn same_interval_never_postpones_due_slots() {
        let mut m = Monitor::new(builtins(), 5, 0).unwrap();
        for group in &mut m.groups {
            group.episode = None;
            group.next_slot = 5000;
        }
        for now in 0..=5 {
            m.set_interval(5, now * 1000);
        }
        assert_eq!(m.tick(5000).len(), 2);
    }
    #[test]
    fn startup_failures_need_complete_pool_but_known_online_can_fail_with_three_infrastructures() {
        let mut m = Monitor::new(builtins(), 5, 0).unwrap();
        let utc = Utc::now();
        for group in &mut m.groups {
            group.episode = None;
        }
        for id in ["ru-yandex", "ru-mail", "ru-rostelecom"] {
            let index = m.groups[0]
                .slots
                .iter()
                .position(|s| s.endpoint.id == id)
                .unwrap();
            let work = m.launch(0, index, 0, false, 2000).unwrap();
            m.complete(&work, result(Outcome::Unreachable), 1, utc);
        }
        assert_eq!(
            m.snapshot(1, utc).groups[0].availability,
            Availability::Unknown
        );
        m.groups[0].saw_online = true;
        assert_eq!(
            m.snapshot(1, utc).groups[0].availability,
            Availability::Offline
        );
    }
    #[test]
    fn admission_holds_per_endpoint_and_group_until_completion() {
        let mut m = Monitor::new(builtins(), 5, 0).unwrap();
        let a = m.reserve_manual("ru-yandex", 0).unwrap();
        let b = m.reserve_manual("ru-mail", 0).unwrap();
        assert!(m.reserve_manual("ru-yandex", 0).is_err());
        assert!(m.reserve_manual("ru-rostelecom", 0).is_err());
        m.complete(&a, result(Outcome::Cancelled), 1, Utc::now());
        assert!(m.reserve_manual("ru-yandex", 2).is_err());
        assert!(m.reserve_manual("ru-rostelecom", 2).is_ok());
        m.complete(&b, result(Outcome::Cancelled), 2, Utc::now());
    }
    #[test]
    fn cancellation_and_stale_epoch_never_change_evidence_or_counters() {
        let mut m = Monitor::new(builtins(), 5, 0).unwrap();
        let work = m.tick(0);
        m.new_epoch(1);
        for w in &work {
            m.complete(w, result(Outcome::Reachable), 2, Utc::now());
        }
        let snapshot = m.snapshot(2, Utc::now());
        assert_eq!(m.physical_count(), 0);
        assert!(
            snapshot
                .groups
                .iter()
                .flat_map(|g| &g.endpoints)
                .all(|e| e.stats.samples == 0 && !e.fresh)
        );
    }
    #[test]
    fn pause_stops_admission_and_unpause_keeps_epoch() {
        let mut m = Monitor::new(builtins(), 5, 0).unwrap();
        let epoch = m.epoch();
        m.set_paused(true, 0);
        assert!(m.tick(1000).is_empty());
        assert!(m.reserve_manual("ru-yandex", 1000).is_err());
        m.set_paused(false, 1001);
        assert_eq!(m.epoch(), epoch);
        assert!(!m.tick(1001).is_empty());
    }
    #[test]
    fn every_interval_obeys_global_rate_and_physical_limits_under_repeated_check_requests() {
        for interval in AVAILABILITY_INTERVALS {
            let mut m = Monitor::new(builtins(), interval, 0).unwrap();
            let mut starts = Vec::new();
            for now in (0..180_000).step_by(100) {
                m.request_check(now);
                let work = m.tick(now);
                assert!(m.physical_count() <= 4);
                assert!(
                    Group::ALL.iter().all(|g| work
                        .iter()
                        .filter(|w| w.endpoint.group == *g)
                        .count()
                        <= 2)
                );
                for w in work {
                    starts.push(now);
                    m.complete(&w, result(Outcome::Reachable), now + 1, Utc::now());
                }
                assert!(
                    starts
                        .iter()
                        .filter(|t| now.saturating_sub(**t) < 1000)
                        .count()
                        <= 4
                );
                assert!(
                    starts
                        .iter()
                        .filter(|t| now.saturating_sub(**t) < 60_000)
                        .count()
                        <= 120
                );
            }
        }
    }
}
