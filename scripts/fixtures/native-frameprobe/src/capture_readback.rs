use super::Failure;
use std::{
    collections::VecDeque,
    time::{Duration, Instant},
};

// Sixteen maximum-sized ROIs contain 4 MiB of pixel payload; driver allocation
// and row-pitch alignment can add overhead to the staging surfaces.
pub(super) const CAPACITY: usize = 16;
const DEADLINE: Duration = Duration::from_millis(100);

pub(super) struct Queue<T> {
    items: VecDeque<(Instant, T)>,
}
impl<T> Queue<T> {
    pub(super) fn new() -> Self {
        Self {
            items: VecDeque::new(),
        }
    }
    pub(super) fn is_empty(&self) -> bool {
        self.items.is_empty()
    }
    pub(super) fn diagnostic_front(&self, now: Instant) -> Option<(&T, Duration, usize)> {
        self.items
            .front()
            .map(|(at, value)| (value, now.saturating_duration_since(*at), self.items.len()))
    }
    pub(super) fn push(&mut self, submitted: Instant, value: T) -> Result<(), Failure> {
        if self.items.len() == CAPACITY {
            return Err(Failure("readback_queue_overflow", 0));
        }
        self.items.push_back((submitted, value));
        Ok(())
    }
    // One nonblocking attempt. A busy head retains both ownership and its
    // original deadline, while the caller continues acquiring desktop frames.
    pub(super) fn poll<R>(
        &mut self,
        now: Instant,
        read: impl FnOnce(&T) -> Result<Option<R>, Failure>,
    ) -> Result<Option<(T, R)>, Failure> {
        let Some((submitted, value)) = self.items.front() else {
            return Ok(None);
        };
        let age = now
            .checked_duration_since(*submitted)
            .ok_or(Failure("readback_clock", 0))?;
        if age >= DEADLINE {
            return Err(Failure("staging_map_timeout", 0));
        }
        let Some(result) = read(value)? else {
            return Ok(None);
        };
        let (_, value) = self.items.pop_front().unwrap();
        Ok(Some((value, result)))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn busy_front_allows_every_acquisition_and_drains_in_presentation_order() {
        let start = Instant::now();
        let mut queue = Queue::new();
        let mut observed = Vec::new();
        for ms in (0..=192).step_by(16) {
            let now = start + Duration::from_millis(ms);
            while let Some(((_, id), pixels)) = queue
                .poll(now, |&(ready, id)| Ok((now >= ready).then_some(id)))
                .unwrap()
            {
                assert_eq!(id, pixels);
                observed.push(id);
            }
            if ms <= 112 {
                queue
                    .push(now, (now + Duration::from_millis(71), ms / 16))
                    .unwrap();
            }
        }
        assert!(queue.is_empty());
        assert_eq!(observed, (0..8).collect::<Vec<_>>());
        // A synchronous 71ms wait would fail to acquire consecutive 16ms frames.
        let mut available = 0;
        let missed = (0..8)
            .filter(|i| {
                let due = i * 16;
                if due < available {
                    true
                } else {
                    available = due + 71;
                    false
                }
            })
            .count();
        assert!(missed > 0);
    }
    #[test]
    fn ready_second_frame_never_overtakes_busy_first() {
        let now = Instant::now();
        let mut queue = Queue::new();
        queue.push(now, 1).unwrap();
        queue.push(now, 2).unwrap();
        assert_eq!(queue.poll::<usize>(now, |_| Ok(None)).unwrap(), None);
        assert_eq!(queue.poll(now, |id| Ok(Some(*id))).unwrap(), Some((1, 1)));
        assert_eq!(queue.poll(now, |id| Ok(Some(*id))).unwrap(), Some((2, 2)));
    }
    #[test]
    fn busy_retries_cannot_extend_deadline_or_hide_errors() {
        let start = Instant::now();
        let mut queue = Queue::new();
        queue.push(start, 1).unwrap();
        assert_eq!(
            queue
                .poll::<()>(start + Duration::from_millis(99), |_| Ok(None))
                .unwrap(),
            None
        );
        assert_eq!(
            queue
                .poll::<()>(start + DEADLINE, |_| panic!(
                    "Expired readback mustn't be retried"
                ))
                .unwrap_err(),
            Failure("staging_map_timeout", 0)
        );
        assert_eq!(
            queue
                .poll::<()>(start, |_| Err(Failure("read_failed", 7)))
                .unwrap_err(),
            Failure("read_failed", 7)
        );
        assert!(!queue.is_empty());
    }
    #[test]
    fn full_queue_fails_without_overwriting_and_completion_returns_ownership() {
        let now = Instant::now();
        let mut queue = Queue::new();
        for i in 0..CAPACITY {
            queue.push(now, i).unwrap();
        }
        assert_eq!(
            queue.push(now, CAPACITY).unwrap_err(),
            Failure("readback_queue_overflow", 0)
        );
        let (slot, ()) = queue.poll(now, |_| Ok(Some(()))).unwrap().unwrap();
        assert_eq!(slot, 0);
        queue.push(now, CAPACITY).unwrap();
        let mut ids = Vec::new();
        while let Some((id, ())) = queue.poll(now, |_| Ok(Some(()))).unwrap() {
            ids.push(id);
        }
        assert_eq!(ids, (1..=CAPACITY).collect::<Vec<_>>());
    }
}
