//! Manual secondary checks use the actual HTTPS peer. They never perform a
//! second DNS lookup and never change the availability monitor's evidence.
use crate::locale::Language;
use crate::transport::ObservedAddress;
use anyhow::Result;
use std::{
    net::SocketAddr,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, SyncSender},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use tokio::{net::TcpStream, sync::oneshot};
use url::Url;
use windows::Win32::{
    Foundation::{GetLastError, HANDLE},
    NetworkManagement::IpHelper::{
        ICMP_ECHO_REPLY, ICMPV6_ECHO_REPLY_LH, IP_REQ_TIMED_OUT, IP_SUCCESS, Icmp6CreateFile,
        Icmp6SendEcho2, IcmpCloseHandle, IcmpCreateFile, IcmpSendEcho,
    },
    Networking::WinSock::{AF_INET6, IN6_ADDR, IN6_ADDR_0, SOCKADDR_IN6, SOCKADDR_IN6_0},
};

const BUDGET: Duration = Duration::from_secs(6);
const WORKERS: usize = 2;
const QUEUE: usize = 4;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CheckState {
    Reachable,
    Unreachable,
    TimedOut,
    NotObserved,
    Busy,
    Cancelled,
    Error,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Check {
    pub state: CheckState,
    pub elapsed_ms: Option<u64>,
    pub os_status: Option<u32>,
}
impl Check {
    fn state(state: CheckState) -> Self {
        Self {
            state,
            elapsed_ms: None,
            os_status: None,
        }
    }
    pub fn label(&self) -> String {
        self.label_in(Language::Ru)
    }
    pub fn label_in(&self, language: Language) -> String {
        let (ru, en) = match self.state {
            CheckState::Reachable => ("Ответ получен", "Response received"),
            CheckState::Unreachable => ("Ответ не получен", "No response"),
            CheckState::TimedOut => ("Время ожидания истекло", "Timed out"),
            CheckState::NotObserved => (
                "Не выполнялось: IP ещё не наблюдался",
                "Not run: no IP has been observed yet",
            ),
            CheckState::Busy => (
                "Не выполнялось: диагностика занята",
                "Not run: diagnostics are busy",
            ),
            CheckState::Cancelled => ("Отменено", "Cancelled"),
            CheckState::Error => ("Не удалось выполнить проверку", "Couldn't run the check"),
        };
        let mut value = language.choose(ru, en).to_string();
        if let Some(elapsed) = self.elapsed_ms {
            value.push_str(&format!(", {elapsed} {}", language.choose("мс", "ms")));
        }
        if let Some(code) = self.os_status {
            value.push_str(&format!(
                " ({} {code})",
                language.choose("код Windows", "Windows code")
            ));
        }
        value
    }
}
#[derive(Clone, Debug)]
pub struct DiagnosticResult {
    pub address: Option<ObservedAddress>,
    pub port: u16,
    pub tcp: Check,
    pub icmp: Check,
}
impl DiagnosticResult {
    pub fn text(&self) -> String {
        self.text_in(Language::Ru)
    }
    pub fn text_in(&self, language: Language) -> String {
        let address = self
            .address
            .map(|a| {
                format!(
                    "{} ({}, {} UTC)",
                    a.address.ip(),
                    language.choose(
                        "последний наблюдавшийся HTTPS-адрес",
                        "last observed HTTPS address"
                    ),
                    a.observed_utc.format("%d.%m %H:%M:%S")
                )
            })
            .unwrap_or_else(|| {
                language
                    .choose(
                        "Не наблюдался. Дополнительный DNS-запрос не выполнялся.",
                        "Not observed. No additional DNS lookup was made.",
                    )
                    .into()
            });
        format!(
            "TCP {}: {}\nICMP: {}\nIP: {address}\n{}",
            self.port,
            self.tcp.label_in(language),
            self.icmp.label_in(language),
            language.choose(
                "TCP и ICMP не определяют цвет состояния. Маршруты могут различаться.",
                "TCP and ICMP don't determine the availability colour. Routes may differ."
            )
        )
    }
}
struct Job {
    address: SocketAddr,
    deadline: Instant,
    cancel: Arc<AtomicBool>,
    result: oneshot::Sender<Check>,
}
struct Worker {
    thread: JoinHandle<()>,
    done: Mutex<mpsc::Receiver<()>>,
}
pub struct Diagnostics {
    sender: Option<SyncSender<Job>>,
    stopping: Arc<AtomicBool>,
    workers: Vec<Worker>,
}
struct CancelOnDrop(Arc<AtomicBool>);
impl Drop for CancelOnDrop {
    fn drop(&mut self) {
        self.0.store(true, Ordering::Release);
    }
}
type Icmp = dyn Fn(SocketAddr, u32) -> Check + Send + Sync + 'static;

impl Diagnostics {
    pub fn new() -> Result<Self> {
        Self::with_icmp(Arc::new(icmp))
    }
    fn with_icmp(probe: Arc<Icmp>) -> Result<Self> {
        let (sender, receiver) = mpsc::sync_channel::<Job>(QUEUE);
        let receiver = Arc::new(Mutex::new(receiver));
        let stopping = Arc::new(AtomicBool::new(false));
        let mut workers = Vec::new();
        for number in 0..WORKERS {
            let receiver = receiver.clone();
            let probe = probe.clone();
            let stopping = stopping.clone();
            let (done_sender, done) = mpsc::sync_channel(1);
            let worker = thread::Builder::new()
                .name(format!("netlights-icmp-{number}"))
                .spawn(move || {
                    loop {
                        let received = receiver.lock().unwrap_or_else(|e| e.into_inner()).recv();
                        let Ok(job) = received else {
                            break;
                        };
                        if stopping.load(Ordering::Acquire) {
                            break;
                        }
                        if job.cancel.load(Ordering::Acquire) || job.result.is_closed() {
                            continue;
                        }
                        let remaining = job.deadline.saturating_duration_since(Instant::now());
                        let check = if remaining.is_zero() {
                            Check::state(CheckState::TimedOut)
                        } else {
                            probe(job.address, remaining.as_millis().clamp(1, 6000) as u32)
                        };
                        if !job.cancel.load(Ordering::Acquire) {
                            let _ = job.result.send(check);
                        }
                    }
                    let _ = done_sender.send(());
                })?;
            workers.push(Worker {
                thread: worker,
                done: Mutex::new(done),
            });
        }
        Ok(Self {
            sender: Some(sender),
            stopping,
            workers,
        })
    }
    pub async fn run(&self, uri: &Url, observed: Option<ObservedAddress>) -> DiagnosticResult {
        let port = uri.port_or_known_default().unwrap_or(443);
        let Some(observed) = observed else {
            return DiagnosticResult {
                address: None,
                port,
                tcp: Check::state(CheckState::NotObserved),
                icmp: Check::state(CheckState::NotObserved),
            };
        };
        let mut target = observed.address;
        target.set_port(port);
        let deadline = Instant::now() + BUDGET;
        let cancelled = Arc::new(AtomicBool::new(false));
        let _cancel = CancelOnDrop(cancelled.clone());
        let (result, reply) = oneshot::channel();
        let admitted = self.sender.as_ref().is_some_and(|sender| {
            sender
                .try_send(Job {
                    address: target,
                    deadline,
                    cancel: cancelled,
                    result,
                })
                .is_ok()
        });
        let tcp = async {
            let start = Instant::now();
            match tokio::time::timeout_at(deadline.into(), TcpStream::connect(target)).await {
                Ok(Ok(stream)) => {
                    drop(stream);
                    Check {
                        state: CheckState::Reachable,
                        elapsed_ms: Some(start.elapsed().as_millis() as u64),
                        os_status: None,
                    }
                }
                Ok(Err(error)) => Check {
                    state: CheckState::Unreachable,
                    elapsed_ms: Some(start.elapsed().as_millis() as u64),
                    os_status: error.raw_os_error().map(|s| s as u32),
                },
                Err(_) => Check::state(CheckState::TimedOut),
            }
        };
        let icmp = async {
            if !admitted {
                return Check::state(CheckState::Busy);
            }
            match tokio::time::timeout_at(deadline.into(), reply).await {
                Ok(Ok(check)) => check,
                Ok(Err(_)) => Check::state(CheckState::Cancelled),
                Err(_) => Check::state(CheckState::TimedOut),
            }
        };
        let (tcp, icmp) = tokio::join!(tcp, icmp);
        DiagnosticResult {
            address: Some(observed),
            port,
            tcp,
            icmp,
        }
    }
}
impl Drop for Diagnostics {
    fn drop(&mut self) {
        self.stopping.store(true, Ordering::Release);
        self.sender = None;
        // An active synchronous Windows ICMP call owns its reply buffer and
        // handle until its <=6s OS deadline. Never block the UI waiting for it.
        for worker in self.workers.drain(..) {
            if worker
                .done
                .into_inner()
                .unwrap_or_else(|e| e.into_inner())
                .recv_timeout(Duration::from_millis(50))
                .is_ok()
            {
                let _ = worker.thread.join();
            }
        }
    }
}
struct IcmpHandle(HANDLE);
impl Drop for IcmpHandle {
    fn drop(&mut self) {
        unsafe {
            let _ = IcmpCloseHandle(self.0);
        }
    }
}
fn icmp(target: SocketAddr, timeout: u32) -> Check {
    let started = Instant::now();
    let request = *b"NetLights";
    let bytes = std::mem::size_of::<ICMP_ECHO_REPLY>()
        .max(std::mem::size_of::<ICMPV6_ECHO_REPLY_LH>())
        + request.len()
        + 16;
    // Windows writes aligned native reply structures into this buffer.
    let mut reply = vec![0usize; bytes.div_ceil(std::mem::size_of::<usize>())];
    let reply_bytes = (reply.len() * std::mem::size_of::<usize>()) as u32;
    let result = match target {
        SocketAddr::V4(address) => {
            let handle = match unsafe { IcmpCreateFile() } {
                Ok(handle) => IcmpHandle(handle),
                Err(error) => {
                    return Check {
                        state: CheckState::Error,
                        elapsed_ms: None,
                        os_status: Some(error.code().0 as u32),
                    };
                }
            };
            let count = unsafe {
                IcmpSendEcho(
                    handle.0,
                    u32::from_ne_bytes(address.ip().octets()),
                    request.as_ptr().cast(),
                    request.len() as u16,
                    None,
                    reply.as_mut_ptr().cast(),
                    reply_bytes,
                    timeout,
                )
            };
            if count == 0 {
                Err(unsafe { GetLastError() }.0)
            } else {
                let reply = unsafe { &*reply.as_ptr().cast::<ICMP_ECHO_REPLY>() };
                Ok((reply.Status, reply.RoundTripTime))
            }
        }
        SocketAddr::V6(address) => {
            let handle = match unsafe { Icmp6CreateFile() } {
                Ok(handle) => IcmpHandle(handle),
                Err(error) => {
                    return Check {
                        state: CheckState::Error,
                        elapsed_ms: None,
                        os_status: Some(error.code().0 as u32),
                    };
                }
            };
            let source = SOCKADDR_IN6 {
                sin6_family: AF_INET6,
                ..Default::default()
            };
            let target = SOCKADDR_IN6 {
                sin6_family: AF_INET6,
                sin6_addr: IN6_ADDR {
                    u: IN6_ADDR_0 {
                        Byte: address.ip().octets(),
                    },
                },
                Anonymous: SOCKADDR_IN6_0 {
                    sin6_scope_id: address.scope_id(),
                },
                ..Default::default()
            };
            let count = unsafe {
                Icmp6SendEcho2(
                    handle.0,
                    None,
                    None,
                    None,
                    &source,
                    &target,
                    request.as_ptr().cast(),
                    request.len() as u16,
                    None,
                    reply.as_mut_ptr().cast(),
                    reply_bytes,
                    timeout,
                )
            };
            if count == 0 {
                Err(unsafe { GetLastError() }.0)
            } else {
                let reply = unsafe { &*reply.as_ptr().cast::<ICMPV6_ECHO_REPLY_LH>() };
                Ok((reply.Status, reply.RoundTripTime))
            }
        }
    };
    match result {
        Ok((IP_SUCCESS, round_trip)) => Check {
            state: CheckState::Reachable,
            elapsed_ms: Some(round_trip as u64),
            os_status: None,
        },
        Ok((IP_REQ_TIMED_OUT, _)) | Err(IP_REQ_TIMED_OUT) => Check::state(CheckState::TimedOut),
        Ok((status, _)) => Check {
            state: CheckState::Unreachable,
            elapsed_ms: Some(started.elapsed().as_millis() as u64),
            os_status: Some(status),
        },
        Err(status) => Check {
            state: if status == 5 {
                CheckState::Error
            } else {
                CheckState::Unreachable
            },
            elapsed_ms: Some(started.elapsed().as_millis() as u64),
            os_status: Some(status),
        },
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn diagnostic_text_translates_labels_and_units_without_changing_measurements() {
        use super::*;
        let value = DiagnosticResult {
            address: None,
            port: 8443,
            tcp: Check {
                state: CheckState::Reachable,
                elapsed_ms: Some(17),
                os_status: Some(123),
            },
            icmp: Check::state(CheckState::NotObserved),
        };
        let ru = value.text_in(Language::Ru);
        let en = value.text_in(Language::En);
        assert_eq!(value.text(), ru);
        assert!(ru.contains("17 мс") && en.contains("17 ms"));
        assert!(ru.contains("код Windows 123") && en.contains("Windows code 123"));
        assert!(en.contains("TCP 8443") && en.contains("No additional DNS lookup"));
        for state in [
            CheckState::Reachable,
            CheckState::Unreachable,
            CheckState::TimedOut,
            CheckState::NotObserved,
            CheckState::Busy,
            CheckState::Cancelled,
            CheckState::Error,
        ] {
            assert_ne!(
                Check::state(state).label_in(Language::Ru),
                Check::state(state).label_in(Language::En)
            );
        }
    }
    use super::*;
    #[tokio::test]
    async fn no_observed_address_never_resolves_or_probes() {
        let calls = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let counter = calls.clone();
        let service = Diagnostics::with_icmp(Arc::new(move |_, _| {
            counter.fetch_add(1, Ordering::AcqRel);
            Check::state(CheckState::Reachable)
        }))
        .unwrap();
        let result = service
            .run(
                &Url::parse("https://does-not-exist.invalid:9443/").unwrap(),
                None,
            )
            .await;
        assert_eq!(result.port, 9443);
        assert_eq!(result.tcp.state, CheckState::NotObserved);
        assert_eq!(result.icmp.state, CheckState::NotObserved);
        assert_eq!(calls.load(Ordering::Acquire), 0);
    }
    #[tokio::test]
    async fn tcp_uses_the_uri_custom_port_and_the_observed_peer() {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let service =
            Diagnostics::with_icmp(Arc::new(|_, _| Check::state(CheckState::Reachable))).unwrap();
        let uri = Url::parse(&format!(
            "https://unresolved-fixture.invalid:{}/",
            address.port()
        ))
        .unwrap();
        let observed = ObservedAddress {
            address: "127.0.0.1:443".parse().unwrap(),
            observed_utc: chrono::Utc::now(),
        };
        let result = service.run(&uri, Some(observed)).await;
        assert_eq!(result.tcp.state, CheckState::Reachable);
        assert_eq!(result.icmp.state, CheckState::Reachable);
        assert_eq!(result.address, Some(observed));
        let (_, peer) = tokio::time::timeout(Duration::from_secs(1), listener.accept())
            .await
            .unwrap()
            .unwrap();
        assert!(peer.ip().is_loopback());
    }
    #[test]
    fn native_ipv4_and_ipv6_loopback_echo_own_and_release_handles() {
        use windows::Win32::System::Threading::{GetCurrentProcess, GetProcessHandleCount};
        for address in ["127.0.0.1:0", "[::1]:0"] {
            let result = icmp(address.parse().unwrap(), 1000);
            assert_eq!(result.state, CheckState::Reachable, "{address}: {result:?}");
        }
        let mut before = 0;
        unsafe {
            GetProcessHandleCount(GetCurrentProcess(), &mut before).unwrap();
        }
        for _ in 0..6 {
            for address in ["127.0.0.1:0", "[::1]:0"] {
                assert_eq!(
                    icmp(address.parse().unwrap(), 1000).state,
                    CheckState::Reachable
                );
            }
        }
        let mut after = 0;
        unsafe {
            GetProcessHandleCount(GetCurrentProcess(), &mut after).unwrap();
        }
        assert!(
            after <= before + 2,
            "ICMP handle growth after 12 owned calls: {before} -> {after}"
        );
    }
    #[tokio::test]
    async fn dropped_diagnostics_never_start_cancelled_queue_entries_or_more_than_two_workers() {
        use std::sync::{Condvar, atomic::AtomicUsize};
        let started = Arc::new(AtomicUsize::new(0));
        let gate = Arc::new((Mutex::new(false), Condvar::new()));
        let count = started.clone();
        let worker_gate = gate.clone();
        let service = Arc::new(
            Diagnostics::with_icmp(Arc::new(move |_, _| {
                count.fetch_add(1, Ordering::AcqRel);
                let (lock, ready) = &*worker_gate;
                let open = lock.lock().unwrap();
                let _ = ready
                    .wait_timeout_while(open, Duration::from_secs(2), |open| !*open)
                    .unwrap();
                Check::state(CheckState::Reachable)
            }))
            .unwrap(),
        );
        let uri = Url::parse("https://fixture.invalid:9/").unwrap();
        let observed = ObservedAddress {
            address: "127.0.0.1:443".parse().unwrap(),
            observed_utc: chrono::Utc::now(),
        };
        let mut jobs = tokio::task::JoinSet::new();
        for _ in 0..100 {
            let service = service.clone();
            let uri = uri.clone();
            jobs.spawn(async move { service.run(&uri, Some(observed)).await });
        }
        let ready = tokio::time::timeout(Duration::from_secs(1), async {
            while started.load(Ordering::Acquire) < WORKERS {
                tokio::time::sleep(Duration::from_millis(1)).await;
            }
        })
        .await;
        // Cancel every consumer before releasing the active native-call stand-ins.
        jobs.abort_all();
        while jobs.join_next().await.is_some() {}
        {
            let (lock, ready) = &*gate;
            *lock.lock().unwrap() = true;
            ready.notify_all();
        }
        ready.unwrap();
        drop(service);
        assert_eq!(
            started.load(Ordering::Acquire),
            WORKERS,
            "Cancelled queued calls must never execute, even under a request burst"
        );
    }
}
