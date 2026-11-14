// Backported from Argus a8c1a705481cb729758041db647a7955c10efe5e.
// Original source: vendor/gpui-pre/src/elements/div.rs, Apache-2.0.
use super::{Pixels, Point, point, px};
use std::{cell::RefCell, collections::HashMap, time::Instant};

/// Chromium's pixels per wheel line on Windows (`kScrollbarPixelsPerLine`):
/// 100 px per notch at the default three lines.
pub const PIXELS_PER_LINE: f32 = 100. / 3.;

struct Glide {
    from: Point<Pixels>,
    to: Point<Pixels>,
    start: Instant,
    /// Seconds; `None` until the first frame, which clamps the target to
    /// the scrollable range and then times the segment.
    duration: Option<f32>,
    /// The timing function's initial slope, carrying a retargeted glide's velocity.
    slope: f32,
    /// The velocity (px/s along the larger axis) when the segment began.
    velocity: f32,
    /// The offset the glide last wrote; anything else means someone else scrolled.
    written: Point<Pixels>,
}

thread_local! {
    static GLIDES: RefCell<HashMap<usize, Glide>> = RefCell::new(HashMap::new());
}

/// The segment duration in seconds for a distance (cc's kInverseDelta).
fn segment(distance: f32) -> f32 {
    const RAMP_START: f32 = 120.;
    const RAMP_END: f32 = 480.;
    const MIN_FRAMES: f32 = 6.;
    const MAX_FRAMES: f32 = 12.;
    let slope = (MIN_FRAMES - MAX_FRAMES) / (RAMP_END - RAMP_START);
    let frames = (MAX_FRAMES + (distance.abs() - RAMP_START) * slope).clamp(MIN_FRAMES, MAX_FRAMES);
    frames / 60.
}

/// The larger component of a delta, keeping its sign (cc's MaximumDimension).
fn major(delta: Point<Pixels>) -> f32 {
    let (x, y) = (f32::from(delta.x), f32::from(delta.y));
    if x.abs() > y.abs() { x } else { y }
}

/// cubic-bezier(0.42, 0.42 * slope, 0.58, 1): progress at `x`, and its slope.
fn ease(slope: f32, x: f32) -> (f32, f32) {
    let (x1, y1, x2, y2) = (0.42f32, 0.42 * slope, 0.58f32, 1f32);
    let (cx, cy) = (3. * x1, 3. * y1);
    let (bx, by) = (3. * (x2 - x1) - cx, 3. * (y2 - y1) - cy);
    let (ax, ay) = (1. - cx - bx, 1. - cy - by);
    let curve_x = |t: f32| ((ax * t + bx) * t + cx) * t;
    let speed_x = |t: f32| (3. * ax * t + 2. * bx) * t + cx;
    let mut t = x;
    for _ in 0..8 {
        let error = curve_x(t) - x;
        let speed = speed_x(t);
        if error.abs() < 1e-6 || speed.abs() < 1e-6 {
            break;
        }
        t = (t - error / speed).clamp(0., 1.);
    }
    if (curve_x(t) - x).abs() > 1e-4 {
        let (mut low, mut high) = (0f32, 1f32);
        for _ in 0..32 {
            t = (low + high) / 2.;
            if curve_x(t) < x { low = t } else { high = t }
        }
    }
    let y = ((ay * t + by) * t + cy) * t;
    let dy = (3. * ay * t + 2. * by) * t + cy;
    let dx = speed_x(t);
    (y, if dx.abs() < 1e-6 { 0. } else { dy / dx })
}

impl Glide {
    fn progress(&self, now: Instant) -> f32 {
        match self.duration {
            Some(duration) if duration > 0. => {
                (now.saturating_duration_since(self.start).as_secs_f32() / duration).min(1.)
            }
            _ => 0.,
        }
    }

    fn value(&self, now: Instant) -> Point<Pixels> {
        let (y, _) = ease(self.slope, self.progress(now));
        self.from + (self.to - self.from) * y
    }

    /// Velocity in px/s along the larger axis of the segment.
    fn velocity(&self, now: Instant) -> f32 {
        match self.duration {
            Some(duration) if duration > 0. => {
                let x = self.progress(now);
                if x >= 1. {
                    return 0.;
                }
                ease(self.slope, x).1 * major(self.to - self.from) / duration
            }
            _ => self.velocity,
        }
    }
}

/// Where a running glide is heading, or `current` when none is.
pub fn target(key: usize, current: Point<Pixels>) -> Point<Pixels> {
    GLIDES.with_borrow(|glides| match glides.get(&key) {
        Some(glide) if glide.written == current => glide.to,
        _ => current,
    })
}

/// Glides from `current` to `to`, continuing a running glide's motion.
pub fn glide_to(key: usize, current: Point<Pixels>, to: Point<Pixels>) {
    glide_to_at(key, current, to, Instant::now());
}

/// Starts or retargets a motion at an explicit clock instant.
pub fn glide_to_at(key: usize, current: Point<Pixels>, to: Point<Pixels>, now: Instant) {
    GLIDES.with_borrow_mut(|glides| {
        let (from, velocity) = match glides.get(&key) {
            Some(glide) if glide.written == current => (glide.value(now), glide.velocity(now)),
            _ => (current, 0.),
        };
        glides.insert(
            key,
            Glide {
                from,
                to,
                start: now,
                duration: None,
                slope: 0.,
                velocity,
                written: current,
            },
        );
    });
}

thread_local! {
    static CLAIMED: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
}

/// A wheel event starts its dispatch: no scroller has taken it yet.
pub fn new_event() {
    CLAIMED.set(false);
}

/// A scroller moved for the event being dispatched; outer scrollers leave it.
pub fn claim() {
    CLAIMED.set(true);
}

/// Whether a scroller already moved for the event being dispatched.
pub fn claimed() -> bool {
    CLAIMED.get()
}

/// Ends a running glide where it is.
pub fn cancel(key: usize) {
    let _ = GLIDES.try_with(|glides| {
        if let Ok(mut glides) = glides.try_borrow_mut() {
            glides.remove(&key);
        }
    });
}

/// Whether a glide is running.
pub fn running(key: usize) -> bool {
    GLIDES.with_borrow(|glides| glides.contains_key(&key))
}

/// Moves the offset along a running glide, keeping the target inside the
/// scrollable range, and asks for the next frame until the glide ends.
pub fn step(
    key: usize,
    offset: &mut Point<Pixels>,
    max: Point<Pixels>,
    now: Instant,
    reduced_motion: bool,
) -> bool {
    let mut needs_frame = false;
    GLIDES.with_borrow_mut(|glides| {
        let Some(glide) = glides.get_mut(&key) else {
            return;
        };
        if *offset != glide.written {
            glides.remove(&key);
            return;
        }
        let clamp = |p: Point<Pixels>| point(p.x.clamp(-max.x, px(0.)), p.y.clamp(-max.y, px(0.)));
        let to = clamp(glide.to);
        if reduced_motion {
            *offset = to;
            glides.remove(&key);
            return;
        }
        if glide.duration.is_none() || to != glide.to {
            // Time the segment from where it began, toward the reachable target.
            if glide.duration.is_some() {
                glide.velocity = glide.velocity(now);
                glide.from = glide.value(now);
                glide.start = now;
            }
            glide.to = to;
            let distance = major(to - glide.from);
            if distance.abs() < 0.01 {
                *offset = to;
                glides.remove(&key);
                return;
            }
            let duration = segment(distance);
            glide.duration = Some(duration);
            glide.slope = (glide.velocity * duration / distance).clamp(-1000., 1000.);
        }
        *offset = clamp(glide.value(now));
        glide.written = *offset;
        if glide.progress(now) >= 1. {
            *offset = to;
            glide.written = to;
            glides.remove(&key);
        } else {
            needs_frame = true;
        }
    });
    needs_frame
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn durations_follow_chromium_inverse_delta() {
        assert!((segment(100.) - 0.2).abs() < 1e-6);
        assert!((segment(-120.) - 0.2).abs() < 1e-6);
        assert!((segment(300.) - 0.15).abs() < 1e-6);
        assert!((segment(900.) - 0.1).abs() < 1e-6);
    }

    #[test]
    fn ease_in_out_matches_css_and_carries_slope() {
        let (half, slope) = ease(0., 0.5);
        assert!((half - 0.5).abs() < 1e-4);
        assert!(slope > 1.5);
        assert!(ease(0., 0.).1.abs() < 1e-4);
        assert!((ease(2., 0.).1 - 2.).abs() < 1e-3);
        assert!((ease(0., 1.).0 - 1.).abs() < 1e-5);
    }

    #[test]
    fn inertia_reaches_the_target_and_stops_requesting_frames() {
        let now = Instant::now();
        let key = 1001;
        let mut p = point(px(0.), px(0.));
        let max = point(px(0.), px(500.));
        glide_to_at(key, p, point(px(0.), px(-100.)), now);
        assert!(step(key, &mut p, max, now, false));
        assert_eq!(p.y, px(0.));
        assert!(step(
            key,
            &mut p,
            max,
            now + std::time::Duration::from_millis(100),
            false
        ));
        assert!((f32::from(p.y) + 50.).abs() < 0.1);
        assert!(!step(
            key,
            &mut p,
            max,
            now + std::time::Duration::from_millis(200),
            false
        ));
        assert_eq!(p.y, px(-100.));
        assert!(!running(key));
        assert!(!step(
            key,
            &mut p,
            max,
            now + std::time::Duration::from_secs(1),
            false
        ));
    }

    #[test]
    fn consecutive_notches_extend_the_target_without_resetting_the_position() {
        let now = Instant::now();
        let key = 1002;
        let mut p = point(px(0.), px(0.));
        let max = point(px(0.), px(500.));
        glide_to_at(key, p, point(px(0.), px(-100.)), now);
        step(key, &mut p, max, now, false);
        let later = now + std::time::Duration::from_millis(80);
        step(key, &mut p, max, later, false);
        let before = p;
        glide_to_at(key, p, target(key, p) + point(px(0.), px(-100.)), later);
        step(key, &mut p, max, later, false);
        assert!((f32::from(p.y - before.y)).abs() < 0.01);
        assert_eq!(target(key, p).y, px(-200.));
        step(
            key,
            &mut p,
            max,
            later + std::time::Duration::from_millis(1),
            false,
        );
        assert!(p.y < before.y);
        assert!(!step(
            key,
            &mut p,
            max,
            later + std::time::Duration::from_secs(1),
            false
        ));
        assert_eq!(p.y, px(-200.));
    }

    #[test]
    fn reversing_direction_and_resizing_keep_offsets_inside_the_real_range() {
        let now = Instant::now();
        let key = 1003;
        let mut p = point(px(0.), px(-100.));
        let max = point(px(0.), px(500.));
        glide_to_at(key, p, point(px(0.), px(-400.)), now);
        step(key, &mut p, max, now, false);
        let later = now + std::time::Duration::from_millis(50);
        step(key, &mut p, max, later, false);
        glide_to_at(key, p, point(px(0.), px(0.)), later);
        for tick in 0..=40 {
            step(
                key,
                &mut p,
                max,
                later + std::time::Duration::from_millis(tick * 10),
                false,
            );
            assert!(p.y >= -max.y && p.y <= px(0.));
        }
        assert_eq!(p.y, px(0.));
        glide_to_at(key, p, point(px(0.), px(-1000.)), now);
        step(key, &mut p, point(px(0.), px(60.)), now, false);
        step(
            key,
            &mut p,
            point(px(0.), px(60.)),
            now + std::time::Duration::from_secs(1),
            false,
        );
        assert_eq!(p.y, px(-60.));
        assert!(!running(key));
    }

    #[test]
    fn explicit_cancellation_and_reduced_motion_cannot_resurrect_a_glide() {
        let now = Instant::now();
        let key = 1004;
        let mut p = point(px(0.), px(0.));
        let max = point(px(0.), px(500.));
        glide_to_at(key, p, point(px(0.), px(-100.)), now);
        cancel(key);
        assert!(!step(key, &mut p, max, now, false));
        assert_eq!(p.y, px(0.));
        glide_to_at(key, p, point(px(0.), px(-100.)), now);
        assert!(!step(key, &mut p, max, now, true));
        assert_eq!(p.y, px(-100.));
        assert!(!running(key));
        glide_to_at(key, p, point(px(0.), px(-200.)), now);
        p.y = px(-30.);
        assert!(!step(key, &mut p, max, now, false));
        assert_eq!(p.y, px(-30.));
        assert!(!running(key));
    }

    #[test]
    fn one_nested_scroller_claims_each_wheel_packet() {
        new_event();
        assert!(!claimed());
        claim();
        assert!(claimed());
        new_event();
        assert!(!claimed());
    }

    #[test]
    fn default_windows_notch_is_one_hundred_pixels_and_live_schedule_is_cancellable() {
        assert!((PIXELS_PER_LINE * 3. - 100.).abs() < 0.001);
        let key = 1005;
        let p = point(px(0.), px(0.));
        glide_to(key, p, point(px(0.), px(-100.)));
        assert_eq!(target(key, p).y, px(-100.));
        cancel(key);
        assert!(!running(key));
    }
}
