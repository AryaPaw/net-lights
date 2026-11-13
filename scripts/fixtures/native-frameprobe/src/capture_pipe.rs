use super::Failure;
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, Ordering},
};
use std::time::{Duration, Instant};
use windows::Win32::{
    Foundation::{CloseHandle, HANDLE},
    Storage::FileSystem::ReadFile,
    System::{
        IO::CancelSynchronousIo,
        Pipes::PeekNamedPipe,
        Threading::{GetCurrentThreadId, OpenThread, THREAD_TERMINATE},
    },
};
type Result<T> = std::result::Result<T, Failure>;
#[derive(Default)]
pub(super) struct Signals {
    pub(super) stop: AtomicBool,
    abort: AtomicBool,
    finished: AtomicBool,
    pub(super) failure: Mutex<Option<Failure>>,
}
impl Signals {
    pub(super) fn fail(&self, failure: Failure) {
        if let Ok(mut slot) = self.failure.lock() {
            if slot.is_none() {
                *slot = Some(failure);
            }
        }
        self.abort.store(true, Ordering::Release);
        self.stop.store(true, Ordering::Release);
    }
}
pub(super) struct Watch {
    signals: Arc<Signals>,
    thread: Option<std::thread::JoinHandle<()>>,
}
impl Watch {
    pub(super) fn start(signals: Arc<Signals>, stdin: HANDLE) -> Result<Self> {
        let id = unsafe { GetCurrentThreadId() };
        let input = stdin.0 as usize;
        let (sender, receiver) = std::sync::mpsc::sync_channel(1);
        let shared = signals.clone();
        let thread = std::thread::spawn(move || {
            let handle = match unsafe { OpenThread(THREAD_TERMINATE, false, id) } {
                Ok(handle) => {
                    let _ = sender.send(Ok(()));
                    handle
                }
                Err(error) => {
                    let _ = sender.send(Err(Failure("watch_thread", error.code().0)));
                    return;
                }
            };
            let deadline = Instant::now() + Duration::from_secs(15);
            let mut command = Vec::new();
            while !shared.finished.load(Ordering::Acquire) {
                if Instant::now() >= deadline {
                    shared.fail(Failure("deadline", 0));
                }
                if !shared.stop.load(Ordering::Acquire) {
                    let mut available = 0;
                    match unsafe {
                        PeekNamedPipe(
                            HANDLE(input as *mut _),
                            None,
                            0,
                            None,
                            Some(&mut available),
                            None,
                        )
                    } {
                        Ok(()) if available > 0 => {
                            let mut bytes = [0u8; 16];
                            let count = available.min(16) as usize;
                            let mut read = 0;
                            match unsafe {
                                ReadFile(
                                    HANDLE(input as *mut _),
                                    Some(&mut bytes[..count]),
                                    Some(&mut read),
                                    None,
                                )
                            } {
                                Ok(()) => {
                                    command.extend_from_slice(&bytes[..read as usize]);
                                    if command == b"stop"
                                        || command == b"stop\n"
                                        || command == b"stop\r\n"
                                    {
                                        shared.stop.store(true, Ordering::Release);
                                    } else if read == 0 {
                                        shared.fail(Failure("parent_pipe_closed", 0));
                                    } else if command.len() >= 6 || command.contains(&b'\n') {
                                        shared.fail(Failure("invalid_command", 0));
                                    }
                                }
                                Err(error) => {
                                    shared.fail(Failure("parent_pipe_closed", error.code().0))
                                }
                            }
                        }
                        Err(error)
                            if [109, 232, 233]
                                .contains(&((error.code().0 as u32 & 65535) as i32)) =>
                        {
                            shared.fail(Failure("parent_pipe_closed", error.code().0))
                        }
                        Err(error) => shared.fail(Failure("parent_pipe", error.code().0)),
                        _ => {}
                    }
                }
                if shared.abort.load(Ordering::Acquire) {
                    let _ = unsafe { CancelSynchronousIo(handle) };
                }
                std::thread::sleep(Duration::from_millis(2));
            }
            let _ = unsafe { CloseHandle(handle) };
        });
        match receiver.recv_timeout(Duration::from_secs(1)) {
            Ok(Ok(())) => Ok(Self {
                signals,
                thread: Some(thread),
            }),
            value => {
                signals.finished.store(true, Ordering::Release);
                let _ = thread.join();
                Err(match value {
                    Ok(Err(error)) => error,
                    _ => Failure("watch_ready", 0),
                })
            }
        }
    }
}
impl Drop for Watch {
    fn drop(&mut self) {
        self.signals.finished.store(true, Ordering::Release);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn healthy_stop_preserves_writes_and_first_failure_is_retained() {
        let signals = Signals::default();
        signals.stop.store(true, Ordering::Release);
        assert!(!signals.abort.load(Ordering::Acquire));
        signals.fail(Failure("queue_overflow", 0));
        signals.fail(Failure("deadline", 0));
        assert!(signals.abort.load(Ordering::Acquire));
        assert_eq!(signals.failure.lock().unwrap().unwrap().0, "queue_overflow");
    }
}
