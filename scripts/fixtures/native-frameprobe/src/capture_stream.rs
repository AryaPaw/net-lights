use super::*;
use std::sync::mpsc::{RecvTimeoutError, TrySendError, sync_channel};

pub(super) enum Event {
    Ready(i64),
    Pointer(i64),
    Frame {
        baseline: Option<u32>,
        qpc: i64,
        width: u32,
        height: u32,
        pixels: Vec<u8>,
    },
    End(i64),
}

struct StopOnDrop(Arc<Signals>);
impl Drop for StopOnDrop {
    fn drop(&mut self) {
        self.0.stop.store(true, Ordering::Release);
    }
}

pub(super) fn run(
    signals: Arc<Signals>,
    produce: impl FnOnce(&mut dyn FnMut(Event) -> Result<()>) -> Result<()> + Send,
    mut consume: impl FnMut(Event) -> Result<()>,
) -> Result<()> {
    // Each native ROI is at most 256 KiB. The queue holds at most 16 MiB,
    // with one additional frame owned by each endpoint. Never overwrite a frame.
    let (sender, receiver) = sync_channel(64);
    std::thread::scope(|scope| {
        let _stop = StopOnDrop(signals.clone());
        let shared = signals.clone();
        let producer = scope.spawn(move || {
            let result = produce(&mut |event| {
                sender.try_send(event).map_err(|error| match error {
                    TrySendError::Full(_) => Failure("queue_overflow", 0),
                    TrySendError::Disconnected(_) => Failure("capture_receiver_closed", 0),
                })
            });
            if let Err(failure) = result {
                shared.fail(failure);
            }
            result
        });
        loop {
            if signals
                .failure
                .lock()
                .map_err(|_| Failure("signals_lock", 0))?
                .is_some()
            {
                break;
            }
            match receiver.recv_timeout(Duration::from_millis(50)) {
                Ok(event) => {
                    if let Err(failure) = consume(event) {
                        signals.fail(failure);
                        break;
                    }
                }
                Err(RecvTimeoutError::Disconnected) => break,
                Err(RecvTimeoutError::Timeout) => (),
            }
        }
        let produced = producer.join().unwrap_or_else(|_| {
            let failure = Failure("capture_worker_panic", 0);
            signals.fail(failure);
            Err(failure)
        });
        if let Some(failure) = *signals
            .failure
            .lock()
            .map_err(|_| Failure("signals_lock", 0))?
        {
            return Err(failure);
        }
        produced
    })
}

pub(super) fn write_event(output: &mut impl Write, event: Event) -> Result<()> {
    let written = match event {
        Event::Ready(frequency) => writeln!(
            output,
            "{{\"kind\":\"ready\",\"frequency\":{frequency},\"source\":\"DXGI Desktop Duplication\"}}"
        ),
        Event::Pointer(qpc) => writeln!(output, "{{\"kind\":\"pointer\",\"qpc\":{qpc}}}"),
        Event::End(qpc) => writeln!(output, "{{\"kind\":\"end\",\"qpc\":{qpc}}}"),
        Event::Frame {
            baseline,
            qpc,
            width,
            height,
            pixels,
        } => {
            let area = u64::from(width) * u64::from(height);
            if area == 0 || area > 65536 || pixels.len() as u64 != area * 4 {
                return Err(Failure("pixel_layout", 0));
            }
            let (kind, initial) = match baseline {
                Some(0) => return Err(Failure("baseline_count", 0)),
                Some(count) => ("baseline", format!(",\"accumulated_frames\":{count}")),
                None => ("frame", String::new()),
            };
            let mut hex = String::with_capacity(pixels.len() * 2);
            const DIGITS: &[u8; 16] = b"0123456789abcdef";
            for value in pixels {
                hex.push(DIGITS[(value >> 4) as usize] as char);
                hex.push(DIGITS[(value & 15) as usize] as char);
            }
            writeln!(
                output,
                "{{\"kind\":\"{kind}\",\"qpc\":{qpc},\"width\":{width},\"height\":{height},\"pixels\":\"{hex}\"{initial}}}"
            )
        }
    };
    written.map_err(|_| Failure("parent_output", 0))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicUsize;

    fn frame(qpc: i64) -> Event {
        Event::Frame {
            baseline: None,
            qpc,
            width: 1,
            height: 1,
            pixels: vec![1, 2, 3, 255],
        }
    }

    #[test]
    fn blocked_output_does_not_hold_acquisition_and_all_frames_drain_in_order() {
        let (entered, waiting) = sync_channel(1);
        let (done, produced) = sync_channel(1);
        let mut output = Vec::new();
        let result = run(
            Arc::new(Signals::default()),
            move |emit| {
                emit(Event::Ready(1000))?;
                waiting
                    .recv_timeout(Duration::from_secs(2))
                    .map_err(|_| Failure("writer_not_entered", 0))?;
                for qpc in 1..=16 {
                    emit(frame(qpc))?;
                }
                emit(Event::End(17))?;
                done.send(()).map_err(|_| Failure("test_receiver", 0))?;
                Ok(())
            },
            |event| {
                if matches!(event, Event::Ready(_)) {
                    entered.send(()).map_err(|_| Failure("test_sender", 0))?;
                    // A synchronous capture-and-write loop cannot finish production
                    // while this writer is waiting, and fails this regression test.
                    produced
                        .recv_timeout(Duration::from_secs(2))
                        .map_err(|_| Failure("writer_blocked_capture", 0))?;
                }
                write_event(&mut output, event)
            },
        );
        assert!(result.is_ok());
        let text = String::from_utf8(output).unwrap();
        let frames: Vec<_> = text
            .lines()
            .filter(|line| line.contains("\"kind\":\"frame\""))
            .collect();
        assert_eq!(frames.len(), 16);
        for (index, line) in frames.iter().enumerate() {
            assert!(line.contains(&format!("\"qpc\":{},", index + 1)));
            assert!(line.contains("\"pixels\":\"010203ff\""));
        }
        assert!(text.ends_with("{\"kind\":\"end\",\"qpc\":17}\n"));
    }

    #[test]
    fn a_full_queue_fails_instead_of_dropping_or_coalescing_frames() {
        let (entered, waiting) = sync_channel(1);
        let (done, produced) = sync_channel(1);
        let count = Arc::new(AtomicUsize::new(0));
        let captured = count.clone();
        let result = run(
            Arc::new(Signals::default()),
            move |emit| {
                emit(Event::Ready(1000))?;
                waiting
                    .recv_timeout(Duration::from_secs(2))
                    .map_err(|_| Failure("writer_not_entered", 0))?;
                let result = (|| {
                    for qpc in 1..=65 {
                        emit(frame(qpc))?;
                        captured.fetch_add(1, Ordering::Relaxed);
                    }
                    Ok(())
                })();
                let _ = done.send(());
                result
            },
            |event| {
                if matches!(event, Event::Ready(_)) {
                    let _ = entered.send(());
                    produced
                        .recv_timeout(Duration::from_secs(2))
                        .map_err(|_| Failure("writer_blocked_capture", 0))?;
                }
                Ok(())
            },
        );
        assert!(matches!(result, Err(Failure("queue_overflow", 0))));
        assert_eq!(count.load(Ordering::Relaxed), 64);
    }

    #[test]
    fn output_failure_stops_and_joins_the_acquisition_owner() {
        let signals = Arc::new(Signals::default());
        let shared = signals.clone();
        let exited = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let finished = exited.clone();
        let result = run(
            signals,
            move |emit| {
                emit(Event::Ready(1000))?;
                let deadline = Instant::now() + Duration::from_secs(2);
                while !shared.stop.load(Ordering::Acquire) {
                    if Instant::now() >= deadline {
                        return Err(Failure("producer_not_stopped", 0));
                    }
                    std::thread::yield_now();
                }
                finished.store(true, Ordering::Release);
                Ok(())
            },
            |_| Err(Failure("parent_output", 0)),
        );
        assert!(matches!(result, Err(Failure("parent_output", 0))));
        assert!(exited.load(Ordering::Acquire));
    }

    #[test]
    fn capture_failures_and_panics_cannot_become_successful_streams() {
        let result = run(
            Arc::new(Signals::default()),
            |_| Err(Failure("accumulated_frames", 0)),
            |_| Ok(()),
        );
        assert!(matches!(result, Err(Failure("accumulated_frames", 0))));
        let result = run(
            Arc::new(Signals::default()),
            |_| panic!("injected acquisition panic"),
            |_| Ok(()),
        );
        assert!(matches!(result, Err(Failure("capture_worker_panic", 0))));
        assert!(
            write_event(
                &mut Vec::new(),
                Event::Frame {
                    baseline: None,
                    qpc: 1,
                    width: 1,
                    height: 1,
                    pixels: vec![0]
                }
            )
            .is_err()
        );
    }
    #[test]
    fn baseline_serialization_keeps_actual_pixels_clock_and_accumulation() {
        let mut bytes = Vec::new();
        write_event(
            &mut bytes,
            Event::Frame {
                baseline: Some(2),
                qpc: 17,
                width: 1,
                height: 1,
                pixels: vec![1, 2, 3, 255],
            },
        )
        .unwrap();
        assert_eq!(
            String::from_utf8(bytes).unwrap(),
            "{\"kind\":\"baseline\",\"qpc\":17,\"width\":1,\"height\":1,\"pixels\":\"010203ff\",\"accumulated_frames\":2}\n"
        );
        assert_eq!(
            write_event(
                &mut Vec::new(),
                Event::Frame {
                    baseline: Some(0),
                    qpc: 17,
                    width: 1,
                    height: 1,
                    pixels: vec![1, 2, 3, 255],
                }
            ),
            Err(Failure("baseline_count", 0))
        );
    }
}
