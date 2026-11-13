use std::collections::VecDeque;

#[derive(Clone, Copy)]
pub struct Spec {
    hwnd: usize,
    pid: u32,
    roi: [i32; 4],
}
pub fn parse(arguments: &[String]) -> Option<Spec> {
    if arguments.len() != 7 || arguments[0] != "--capture-roi" {
        return None;
    }
    let spec = Spec {
        hwnd: arguments[1].parse().ok()?,
        pid: arguments[2].parse().ok()?,
        roi: [
            arguments[3].parse().ok()?,
            arguments[4].parse().ok()?,
            arguments[5].parse().ok()?,
            arguments[6].parse().ok()?,
        ],
    };
    (spec.hwnd > 0 && spec.pid > 0 && dimensions(spec.roi).is_some()).then_some(spec)
}
fn dimensions(rect: [i32; 4]) -> Option<(u32, u32)> {
    let width = u32::try_from(i64::from(rect[2]) - i64::from(rect[0])).ok()?;
    let height = u32::try_from(i64::from(rect[3]) - i64::from(rect[1])).ok()?;
    (width > 0 && height > 0 && u64::from(width) * u64::from(height) <= 65536)
        .then_some((width, height))
}
fn capture_bounds(raw: [i32; 4], extended: [i32; 4], size: (i32, i32)) -> Option<[i32; 4]> {
    let matches = |rect: [i32; 4]| {
        size.0 > 0
            && size.1 > 0
            && i64::from(rect[2]) - i64::from(rect[0]) == i64::from(size.0)
            && i64::from(rect[3]) - i64::from(rect[1]) == i64::from(size.1)
    };
    match (matches(raw), matches(extended)) {
        (true, false) => Some(raw),
        (false, true) => Some(extended),
        (true, true) if raw == extended => Some(raw),
        _ => None,
    }
}
fn local_roi(
    roi: [i32; 4],
    client: [i32; 4],
    bounds: [i32; 4],
    size: (i32, i32),
) -> Option<[u32; 4]> {
    if dimensions(roi).is_none()
        || i64::from(size.0) != i64::from(bounds[2]) - i64::from(bounds[0])
        || i64::from(size.1) != i64::from(bounds[3]) - i64::from(bounds[1])
        || size.0 <= 0
        || size.1 <= 0
        || roi[0] < client[0]
        || roi[1] < client[1]
        || roi[2] > client[2]
        || roi[3] > client[3]
    {
        return None;
    }
    let values = [
        i64::from(roi[0]) - i64::from(bounds[0]),
        i64::from(roi[1]) - i64::from(bounds[1]),
        i64::from(roi[2]) - i64::from(bounds[0]),
        i64::from(roi[3]) - i64::from(bounds[1]),
    ];
    let result = values.map(u32::try_from);
    let result = [
        result[0].ok()?,
        result[1].ok()?,
        result[2].ok()?,
        result[3].ok()?,
    ];
    (result[2] <= size.0 as u32 && result[3] <= size.1 as u32).then_some(result)
}
struct Frame {
    qpc: i64,
    pixels: Vec<u8>,
}
#[derive(Default)]
struct Queue {
    frames: VecDeque<Frame>,
    bytes: usize,
    last_qpc: i64,
}
impl Queue {
    fn push(&mut self, frame: Frame) -> Result<(), Failure> {
        if frame.qpc <= self.last_qpc {
            return Err(Failure("timestamp_order", 0));
        }
        if self.frames.len() >= 64 || frame.pixels.len() > 16 * 1024 * 1024 - self.bytes {
            return Err(Failure("queue_overflow", 0));
        }
        self.last_qpc = frame.qpc;
        self.bytes += frame.pixels.len();
        self.frames.push_back(frame);
        Ok(())
    }
    fn pop(&mut self) -> Option<Frame> {
        let frame = self.frames.pop_front()?;
        self.bytes -= frame.pixels.len();
        Some(frame)
    }
}
#[derive(Clone, Copy)]
struct Failure(&'static str, i32);
fn captured_qpc(
    duration: i64,
    frequency: i64,
    received_qpc: i64,
    previous: i64,
) -> Result<i64, Failure> {
    if duration <= 0 || frequency <= 0 || received_qpc <= 0 {
        return Err(Failure("capture_timestamp", 0));
    }
    // SystemRelativeTime is compositor QPC time expressed in 100 ns units.
    let value = i128::from(duration)
        .checked_mul(i128::from(frequency))
        .and_then(|ticks| i64::try_from(ticks / 10_000_000).ok())
        .ok_or(Failure("capture_timestamp", 0))?;
    if value <= 0 || value <= previous || value > received_qpc {
        return Err(Failure("capture_timestamp", 0));
    }
    Ok(value)
}
pub fn error(code: &'static str, hresult: i32) {
    println!("{{\"kind\":\"error\",\"code\":\"{code}\",\"hresult\":{hresult}}}");
}
#[cfg(not(windows))]
pub fn run(_: Spec) -> u8 {
    error("unsupported_platform", 0);
    3
}
#[cfg(windows)]
pub use win::run;

#[cfg(windows)]
#[path = "capture_pipe.rs"]
mod pipe;
#[cfg(windows)]
#[path = "capture_windows.rs"]
mod win;

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn capture_shape_selects_only_an_unambiguous_os_rectangle() {
        let raw = [90, 80, 1110, 900];
        let extended = [98, 88, 1102, 892];
        assert_eq!(capture_bounds(raw, extended, (1020, 820)), Some(raw));
        assert_eq!(capture_bounds(raw, extended, (1004, 804)), Some(extended));
        assert_eq!(capture_bounds(raw, raw, (1020, 820)), Some(raw));
        assert!(capture_bounds(raw, [91, 80, 1111, 900], (1020, 820)).is_none());
        assert!(capture_bounds(raw, extended, (1000, 800)).is_none());
        assert!(capture_bounds(raw, extended, (0, 0)).is_none());
        let client = [100, 100, 1100, 890];
        assert_eq!(
            local_roi([100, 100, 110, 110], client, raw, (1020, 820)),
            Some([10, 20, 20, 30])
        );
        assert_eq!(
            local_roi([100, 100, 110, 110], client, extended, (1004, 804)),
            Some([2, 12, 12, 22])
        );
        assert!(
            local_roi(
                [90, 90, 100, 100],
                [80, 80, 1110, 900],
                extended,
                (1004, 804)
            )
            .is_none()
        );
        assert!(local_roi([99, 100, 110, 110], client, raw, (1020, 820)).is_none());
    }
    #[test]
    fn compositor_clock_conversion_preserves_qpc_units_and_rejects_invalid_times() {
        assert_eq!(
            captured_qpc(10_000_000, 3_579_545, 3_579_600, 0).ok(),
            Some(3_579_545)
        );
        assert_eq!(
            captured_qpc(10_000_001, 24_000_000, 24_000_003, 0).ok(),
            Some(24_000_002)
        );
        assert_eq!(captured_qpc(12_345_678, 1000, 1235, 1233).ok(), Some(1234));
        for (duration, frequency, received, previous) in [
            (0, 1000, 2000, 0),
            (-1, 1000, 2000, 0),
            (1, 0, 2000, 0),
            (1, 1000, 0, 0),
            (1, 1000, 2000, 0),
            (12_345_678, 1000, 1233, 0),
            (12_345_678, 1000, 1235, 1234),
            (12_345_678, 1000, 1235, 1235),
            (i64::MAX, i64::MAX, i64::MAX, 0),
        ] {
            assert!(captured_qpc(duration, frequency, received, previous).is_err());
        }
    }
    #[test]
    fn capture_arguments_cannot_bypass_the_hosted_identity_guard() {
        let args = ["--capture-roi", "12", "34", "0", "0", "100", "10"].map(String::from);
        let (_, code) = crate::run_guarded(
            &args,
            [None, None, None],
            || panic!("Untrusted capture must not inspect the token"),
            |_| panic!("Untrusted capture must not reach any window or GPU API"),
        );
        assert_eq!(code, 3);
        let (_, code) = crate::run_guarded(
            &args,
            [Some("true"), Some("github-hosted"), Some("true")],
            || Ok("developer".into()),
            |_| panic!("A claimed CI environment can't replace the token"),
        );
        assert_eq!(code, 3);
    }
    #[test]
    fn arguments_geometry_and_translation_fail_closed() {
        let valid: Vec<String> = ["--capture-roi", "12", "34", "-20", "30", "80", "40"]
            .map(String::from)
            .into();
        assert!(parse(&valid).is_some());
        for index in 1..7 {
            let mut bad = valid.clone();
            bad[index] = "invalid".into();
            assert!(parse(&bad).is_none());
        }
        assert!(parse(&valid[..6]).is_none());
        assert_eq!(
            local_roi(
                [-20, 30, 80, 40],
                [-30, 20, 90, 80],
                [-40, 10, 100, 100],
                (140, 90)
            ),
            Some([20, 20, 120, 30])
        );
        assert!(
            local_roi(
                [-20, 30, 80, 40],
                [-10, 20, 90, 80],
                [-40, 10, 100, 100],
                (140, 90)
            )
            .is_none()
        );
        assert!(
            local_roi(
                [-20, 30, 80, 40],
                [-30, 20, 90, 80],
                [-40, 10, 100, 100],
                (141, 90)
            )
            .is_none()
        );
        assert!(dimensions([i32::MIN, 0, i32::MAX, 2]).is_none());
        assert!(dimensions([0, 0, 257, 256]).is_none());
    }
    #[test]
    fn queue_preserves_order_and_rejects_limits_without_dropping() {
        let mut queue = Queue::default();
        for qpc in 1..=64 {
            queue
                .push(Frame {
                    qpc,
                    pixels: vec![0; 262144],
                })
                .unwrap_or_else(|_| panic!("within cap"));
        }
        assert!(
            queue
                .push(Frame {
                    qpc: 65,
                    pixels: vec![0]
                })
                .is_err()
        );
        assert_eq!(queue.frames.len(), 64);
        for qpc in 1..=64 {
            assert_eq!(queue.pop().unwrap().qpc, qpc);
        }
        assert_eq!(queue.bytes, 0);
        assert!(
            queue
                .push(Frame {
                    qpc: 64,
                    pixels: vec![]
                })
                .is_err()
        );
        assert!(
            queue
                .push(Frame {
                    qpc: 65,
                    pixels: vec![0; 16 * 1024 * 1024 + 1]
                })
                .is_err()
        );
    }
}
