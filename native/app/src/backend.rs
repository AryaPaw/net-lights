mod export;
mod journal;
use anyhow::Result;
use chrono::Utc;
use journal::Journal;
use netlights_core::{
    diagnostics::Diagnostics,
    geo::GeoService,
    history::*,
    locale::{Language, LocalizedText},
    model::*,
    monitor::{Monitor, ProbeWork},
    persistence::{Profile, SaveSnapshot},
    traffic::{Category, Traffic, TrafficGuard, TrafficRow},
    transport::Transport,
    update,
};
use std::{
    collections::HashMap,
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
};
use tokio::{
    sync::{mpsc, oneshot, watch},
    task::JoinSet,
};

#[derive(Clone, Debug)]
pub enum Command {
    Pause(bool),
    CheckNow,
    NetworkChanged(bool),
    Suspend,
    Resume,
    Settings(Box<Settings>),
    Diagnose(String),
    CheckUpdates,
    Export,
}
#[derive(Clone)]
pub struct State {
    pub monitor: MonitorSnapshot,
    pub settings: Settings,
    pub availability: Arc<AvailabilityHistory>,
    pub locations: Arc<LocationHistory>,
    pub traffic: Vec<TrafficRow>,
    pub warnings: Vec<LocalizedText>,
    pub update_status: LocalizedText,
    pub update_busy: bool,
    pub diagnostic: LocalizedText,
    pub traffic_warning: bool,
    pub traffic_limit: Option<u64>,
    pub traffic_baseline: Option<u64>,
    pub database_status: [String; 2],
    pub export_path: Option<PathBuf>,
    pub export_busy: bool,
    pub shutting_down: bool,
    pub events: Vec<(chrono::DateTime<Utc>, String)>,
    pub settings_generation: u64,
    pub installed: bool,
    pub isolated: bool,
}
pub struct Backend {
    sender: mpsc::Sender<Command>,
    state: watch::Receiver<Arc<State>>,
    stop: Arc<AtomicBool>,
    done: Arc<AtomicBool>,
    _thread: Option<thread::JoinHandle<()>>,
    closed_reported: bool,
}
impl Backend {
    pub fn start(profile: Profile, isolated: bool, suppress_updates: bool) -> Result<Self> {
        let monitor = Monitor::new(
            profile.endpoints.clone(),
            profile.settings.availability_polling_seconds as u64,
            0,
        )?;
        let mut monitor = monitor;
        let initial = Arc::new(State {
            monitor: monitor.snapshot(0, Utc::now()),
            settings: profile.settings.clone(),
            availability: Arc::new(profile.availability.clone()),
            locations: Arc::new(profile.locations.clone()),
            traffic: Vec::new(),
            warnings: profile.warnings.clone(),
            update_status: LocalizedText::default(),
            update_busy: false,
            diagnostic: LocalizedText::default(),
            traffic_warning: false,
            traffic_limit: None,
            traffic_baseline: None,
            database_status: ["Не загружена".into(), "Не загружена".into()],
            export_path: None,
            export_busy: false,
            shutting_down: false,
            events: vec![],
            settings_generation: 0,
            installed: std::env::current_exe()
                .ok()
                .and_then(|p| p.parent().map(|p| p.join("unins000.exe").is_file()))
                .unwrap_or(false),
            isolated,
        });
        let (sender, receiver) = mpsc::channel(64);
        let (state_sender, state) = watch::channel(initial);
        let stop = Arc::new(AtomicBool::new(false));
        let done = Arc::new(AtomicBool::new(false));
        let stopping = stop.clone();
        let finished = done.clone();
        let worker = thread::Builder::new()
            .name("netlights-monitor".into())
            .spawn(move || {
                let journal = Journal::new(&profile.root);
                journal.event("runtime-start");
                let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                    let runtime = tokio::runtime::Builder::new_multi_thread()
                        .worker_threads(2)
                        .max_blocking_threads(3)
                        .enable_all()
                        .build()?;
                    let result = runtime.block_on(run(
                        profile,
                        monitor,
                        receiver,
                        state_sender,
                        stopping,
                        RuntimePolicy {
                            isolated,
                            suppress_updates,
                        },
                        journal.clone(),
                    ));
                    runtime.shutdown_timeout(Duration::from_secs(2));
                    result
                }));
                match outcome {
                    Ok(Ok(())) => journal.event("runtime-end"),
                    Ok(Err(_)) => journal.event("runtime-failed"),
                    Err(_) => journal.event("runtime-panic"),
                }
                finished.store(true, Ordering::Release);
            })?;
        Ok(Self {
            sender,
            state,
            stop,
            done,
            _thread: Some(worker),
            closed_reported: false,
        })
    }
    pub fn command(&self, command: Command) -> bool {
        self.sender.try_send(command).is_ok()
    }
    pub fn current(&self) -> Arc<State> {
        self.state.borrow().clone()
    }
    pub fn changed(&mut self) -> Option<Arc<State>> {
        let changed = match self.state.has_changed() {
            Ok(changed) => changed,
            Err(_) => {
                let first = !self.closed_reported;
                self.closed_reported = true;
                first
            }
        };
        if changed {
            Some(self.state.borrow_and_update().clone())
        } else {
            None
        }
    }
    pub fn shutdown(&self) {
        self.stop.store(true, Ordering::Release);
    }
    pub fn finished(&self) -> bool {
        self.done.load(Ordering::Acquire)
    }
}
impl Drop for Backend {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
    }
}

#[derive(Clone)]
struct GeoControl {
    enabled: bool,
    seconds: u64,
    generation: u64,
}
impl GeoControl {
    fn restart(&mut self, enabled: bool) {
        self.enabled = enabled;
        self.generation = self.generation.saturating_add(1);
    }
}
struct RuntimePolicy {
    isolated: bool,
    suppress_updates: bool,
}
enum Event {
    Geo {
        epoch: u64,
        generation: u64,
        observation: Box<LocationObservation>,
        versions: [String; 2],
        quota_failed: bool,
    },
    Update {
        text: LocalizedText,
        exit: bool,
    },
    Export(Result<PathBuf>),
}
async fn geo_loop(
    root: PathBuf,
    traffic: Arc<Traffic>,
    mut control: watch::Receiver<GeoControl>,
    events: mpsc::Sender<Event>,
    epoch: watch::Receiver<u64>,
) {
    while !control.borrow().enabled {
        if control.changed().await.is_err() {
            return;
        }
    }
    let mut service: Option<GeoService> = None;
    loop {
        let state = control.borrow_and_update().clone();
        if !state.enabled {
            if let Some(mut worker) = service.take() {
                worker.shutdown().await;
            }
            if control.changed().await.is_err() {
                break;
            }
            continue;
        }
        if service.is_none() {
            service = GeoService::new(root.clone(), traffic.clone()).ok();
        }
        let Some(service) = service.as_mut() else {
            tokio::select! { _ = tokio::time::sleep(Duration::from_secs(30)) => {}, _ = control.changed() => {} }
            continue;
        };
        let monitor_epoch = *epoch.borrow();
        let observation = tokio::select! {
            result = service.observe() => result.ok(),
            change = control.changed() => { if change.is_err() { break; } service.network_changed(); continue; }
        };
        if let Some(observation) = observation {
            let databases = service.database_status();
            let versions = [
                databases
                    .v2fly
                    .as_ref()
                    .map(|s| s.version.clone())
                    .unwrap_or_else(|| "Не загружена".into()),
                databases
                    .iplocate
                    .as_ref()
                    .map(|s| s.version.clone())
                    .unwrap_or_else(|| "Не загружена".into()),
            ];
            let event = Event::Geo {
                epoch: monitor_epoch,
                generation: state.generation,
                observation: Box::new(observation),
                versions,
                quota_failed: service.quota_failure().is_some(),
            };
            // Closing the consumer wakes a blocked publisher during shutdown.
            // An interval change mustn't discard a successfully observed IP.
            if events.send(event).await.is_err() {
                break;
            }
        }
        let delay = service.next_delay(state.seconds);
        tokio::select! {
            _ = tokio::time::sleep(delay) => {},
            change = control.changed() => { if change.is_err() { break; } service.network_changed(); },
        }
    }
    if let Some(mut service) = service {
        service.shutdown().await;
    }
}

async fn run(
    mut profile: Profile,
    mut monitor: Monitor,
    mut commands: mpsc::Receiver<Command>,
    state_sender: watch::Sender<Arc<State>>,
    stop: Arc<AtomicBool>,
    policy: RuntimePolicy,
    journal: Journal,
) -> Result<()> {
    let RuntimePolicy {
        isolated,
        suppress_updates,
    } = policy;
    let started = Instant::now();
    monitor.network_unavailable = !netlights_core::platform::network_available();
    let traffic = Arc::new(Traffic::default());
    journal.event("transport-initializing");
    let provider = Transport::new(traffic.clone(), Category::Provider)?;
    let transports = [provider.clone(), provider.for_category(Category::World)];
    let update_transport = Transport::new(traffic.clone(), Category::Updates)?;
    let diagnostics = Diagnostics::new().ok().map(Arc::new);
    journal.event("transport-ready");
    let (events_sender, mut events) = mpsc::channel(16);
    let (cancel_sender, cancel) = watch::channel(0u64);
    let (epoch_sender, epoch) = watch::channel(monitor.epoch());
    let (geo_sender, geo_control) = watch::channel(GeoControl {
        enabled: profile.settings.geo_country_detection_enabled && !monitor.network_unavailable,
        seconds: profile.settings.country_polling_seconds as u64,
        generation: 0,
    });
    // A disabled feature doesn't open/download databases. Its worker starts on enable.
    let mut geo_task = tokio::spawn(geo_loop(
        profile.root.clone(),
        traffic.clone(),
        geo_control,
        events_sender.clone(),
        epoch,
    ));
    let mut probes: JoinSet<(ProbeWork, ProbeResult, Option<LocalizedText>)> = JoinSet::new();
    let mut probe_owners = HashMap::new();
    let mut auxiliary: JoinSet<()> = JoinSet::new();
    let mut save_task: Option<SaveJob> = None;
    let mut persisted = Persisted::default();
    let mut ticker = tokio::time::interval(Duration::from_millis(100));
    ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    let mut state = (*state_sender.borrow().as_ref()).clone();
    let mut last_publish = Instant::now() - Duration::from_secs(1);
    let mut last_save = Instant::now();
    let mut last_prune = Instant::now();
    let mut previous_tick = 0u64;
    let mut suspended = false;
    let mut changed = true;
    let mut save_requested = false;
    let mut guard = TrafficGuard::default();
    let mut next_update = Instant::now() + Duration::from_secs(30);
    let installed = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|p| p.join("unins000.exe").is_file()))
        .unwrap_or(false);
    if installed
        && !isolated
        && profile.settings_writable()
        && let Ok(exe) = std::env::current_exe()
        && netlights_core::platform::set_auto_start(profile.settings.auto_start, &exe).is_err()
    {
        profile.settings.auto_start =
            netlights_core::platform::auto_start_enabled(&exe).unwrap_or(false);
        warn(
            &mut state.warnings,
            LocalizedText::new(
                "Не удалось применить автозапуск. Запись другой установки сохранена.",
                "Couldn't apply auto-start. Another installation's entry was preserved.",
            ),
        );
        journal.event("autostart-sync-failed");
    }
    loop {
        // A settings change accepted just before Exit must reach the final persisted snapshot.
        if stop.load(Ordering::Acquire) && commands.is_empty() {
            events.close();
            while let Ok(event) = events.try_recv() {
                apply_event(
                    event,
                    &mut profile,
                    &mut state,
                    monitor.epoch(),
                    geo_sender.borrow().generation,
                    !suspended && !monitor.network_unavailable,
                );
            }
            break;
        }
        let now = started.elapsed().as_millis().min(u64::MAX as u128) as u64;
        tokio::select! {
            _ = ticker.tick() => {
                if now.saturating_sub(previous_tick) > 120_000 {
                    profile.availability.close(state.monitor.generated_utc); profile.locations.close(profile.locations.last_observed_utc.unwrap_or(state.monitor.generated_utc));
                    monitor.new_epoch(now); cancel_sender.send_modify(|g| *g += 1); epoch_sender.send_replace(monitor.epoch());
                    geo_sender.send_modify(|g| g.restart(g.enabled)); for t in &transports { t.recycle(); } changed = true;
                }
                previous_tick = now;
                if !suspended {
                    for work in monitor.tick(now) { let id=spawn_probe(&mut probes, work.clone(), &transports, cancel.clone(),diagnostics.clone());probe_owners.insert(id,work); }
                }
            },
            Some(command) = commands.recv() => {
                match command {
                    Command::Pause(paused) => { monitor.set_paused(paused, now); cancel_sender.send_modify(|g| *g += 1); journal.event(if paused { "paused" } else { "resumed" }); },
                    Command::CheckNow => monitor.request_check(now),
                    Command::Suspend => {
                        suspended = true; profile.availability.close(Utc::now()); profile.locations.close(profile.locations.last_observed_utc.unwrap_or(Utc::now()));
                        cancel_sender.send_modify(|g| *g += 1); geo_sender.send_modify(|g| g.restart(false)); journal.event("power-suspend");
                        start_save(&profile, state.settings_generation, &persisted, &mut save_task);
                    },
                    Command::Resume | Command::NetworkChanged(true) => {
                        suspended = false; monitor.new_epoch(now); cancel_sender.send_modify(|g| *g += 1); epoch_sender.send_replace(monitor.epoch());
                        monitor.network_unavailable = !netlights_core::platform::network_available();
                        geo_sender.send_modify(|g| g.restart(profile.settings.geo_country_detection_enabled&&!monitor.network_unavailable));
                        for t in &transports { t.recycle(); } journal.event("network-epoch");
                    },
                    Command::NetworkChanged(false) => {
                        monitor.network_unavailable = true; cancel_sender.send_modify(|g| *g += 1);geo_sender.send_modify(|g| g.restart(false));
                        profile.locations.close(profile.locations.last_observed_utc.unwrap_or(Utc::now()));
                    },
                    Command::Settings(settings) => {
                        state.settings_generation=state.settings_generation.saturating_add(1);
                        let previous_geo = profile.settings.geo_country_detection_enabled;
                        let previous_country_seconds=profile.settings.country_polling_seconds;
                        let mut next = *settings; next.normalize();
                        if next.auto_start != profile.settings.auto_start && !isolated && installed
                            && std::env::current_exe().ok().is_none_or(|exe| netlights_core::platform::set_auto_start(next.auto_start, &exe).is_err()) {
                                next.auto_start = profile.settings.auto_start;
                                warn(&mut state.warnings, LocalizedText::new("Не удалось изменить автозапуск. Проверьте разрешения Windows.", "Couldn't change auto-start. Check Windows permissions."));
                            }
                        let interval_changed=profile.settings.availability_polling_seconds!=next.availability_polling_seconds;
                        profile.settings = next;
                        if isolated { profile.settings.auto_start = false; profile.settings.auto_update_enabled = false; }
                        if interval_changed { monitor.set_interval(profile.settings.availability_polling_seconds as u64, now); }
                        if previous_geo!=profile.settings.geo_country_detection_enabled||previous_country_seconds!=profile.settings.country_polling_seconds {
                            geo_sender.send_modify(|g| {
                                if previous_geo != profile.settings.geo_country_detection_enabled {
                                    g.restart(profile.settings.geo_country_detection_enabled && !suspended && !monitor.network_unavailable);
                                }
                                g.seconds = profile.settings.country_polling_seconds as u64;
                            });
                        }
                        if previous_geo && !profile.settings.geo_country_detection_enabled { profile.locations.close(profile.locations.last_observed_utc.unwrap_or(Utc::now())); }
                        save_requested = true;
                    },
                    Command::Diagnose(id) => {
                        match monitor.reserve_manual(&id, now) { Ok(work) => { state.diagnostic = LocalizedText::new("Проверка HTTPS, TCP и ICMP...", "Checking HTTPS, TCP and ICMP..."); let id=spawn_probe(&mut probes, work.clone(), &transports, cancel.clone(),diagnostics.clone());probe_owners.insert(id,work); },
                            Err(_) => state.diagnostic = LocalizedText::new("Диагностика отложена: адрес уже проверяется, действует пауза или достигнут лимит.", "Diagnostics postponed: the endpoint is already being checked, paused or rate-limited.") }
                    },
                    Command::CheckUpdates => {
                        if !state.update_busy && !isolated && installed {
                            state.update_busy = true; state.update_status = LocalizedText::new("Проверка обновления...", "Checking for updates...");
                            spawn_update(&mut auxiliary, update_transport.clone(), profile.root.clone(), events_sender.clone(), false);
                        } else if !installed || isolated { state.update_status = LocalizedText::new("Обновление доступно в установленной версии.", "Updates are available in the installed version."); }
                    },
                    Command::Export => {
                        if state.export_busy { continue; } state.export_busy=true;
                        let data = state.clone(); let root = profile.root.clone(); let sender = events_sender.clone();
                        auxiliary.spawn(async move {
                            let result = tokio::task::spawn_blocking(move || export::write(&data, &root)).await.unwrap_or_else(|_| Err(anyhow::anyhow!("Export worker failed")));
                            let _ = sender.send(Event::Export(result)).await;
                        });
                    },
                }
                changed = true;
            },
            result = probes.join_next_with_id(), if !probes.is_empty() => {
                if let Some(Ok((id,(work, result,detail)))) = result {
                    probe_owners.remove(&id);
                    if work.manual { state.diagnostic = diagnostic_text(&result, detail.as_ref()); }
                    monitor.complete(&work, result, started.elapsed().as_millis() as u64, Utc::now());
                } else {
                    if let Some(Err(error))=result && let Some(work)=probe_owners.remove(&error.id()) {
                        monitor.complete(&work,ProbeResult{outcome:Outcome::Indeterminate,status:None,failure:Some("internal"),elapsed_ms:0,retry_after_ms:0},now,Utc::now());
                    }
                    monitor.monitor_error = Some("Неожиданная ошибка сетевой задачи.".into()); journal.event("probe-worker-failed");
                }
                changed = true;
            },
            Some(event) = events.recv() => {
                if apply_event(event,&mut profile,&mut state,monitor.epoch(),geo_sender.borrow().generation,!suspended&&!monitor.network_unavailable) {
                    journal.event("update-handoff");stop.store(true,Ordering::Release);
                }
                changed = true;
            },
            _ = auxiliary.join_next(), if !auxiliary.is_empty() => {},
        }
        changed |=
            prune_locations_if_due(&mut profile, &mut last_prune, Instant::now(), Utc::now());
        let save_result = save_task
            .as_mut()
            .and_then(|task| match task.receiver.try_recv() {
                Ok(result) => Some(result),
                Err(oneshot::error::TryRecvError::Empty) => None,
                Err(_) => Some(vec!["history-save-worker-failed"]),
            });
        if let Some(result) = save_result {
            let completed = save_task.take().expect("Completed write has an owner");
            changed |=
                persisted.accept_with_feedback(completed.stamp, &result, &mut state.warnings);
            for code in result {
                journal.event(code);
            }
        }
        if (save_requested || last_save.elapsed() >= Duration::from_secs(10)) && save_task.is_none()
        {
            start_save(
                &profile,
                state.settings_generation,
                &persisted,
                &mut save_task,
            );
            last_save = Instant::now();
            save_requested = false;
        }
        if !suspended && (changed || last_publish.elapsed() >= Duration::from_secs(1)) {
            state.monitor = monitor.snapshot(started.elapsed().as_millis() as u64, Utc::now());
            let revision = profile.availability.revision;
            profile.availability.observe(&state.monitor);
            profile.locations.seal_stale(state.monitor.generated_utc);
            if revision != profile.availability.revision || state.availability.revision != revision
            {
                state.availability = Arc::new(profile.availability.clone());
            }
            if profile.locations.revision != state.locations.revision {
                state.locations = Arc::new(profile.locations.clone());
            }
            state.settings = profile.settings.clone();
            state.traffic = traffic.snapshot();
            state.events = journal
                .events
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .iter()
                .cloned()
                .collect();
            let custom_limit = profile
                .settings
                .extra
                .get("trafficWarningBytesPerHour")
                .and_then(|v| v.as_u64())
                .filter(|n| *n >= 1024 * 1024);
            guard.observe(
                started.elapsed().as_secs(),
                traffic.regular_total(),
                custom_limit,
            );
            state.traffic_warning = guard.warning;
            state.traffic_limit = guard.threshold();
            state.traffic_baseline = guard.baseline_bytes_per_hour();
            state_sender.send_replace(Arc::new(state.clone()));
            last_publish = Instant::now();
            changed = false;
        }
        if !isolated
            && installed
            && !suppress_updates
            && profile.settings.auto_update_enabled
            && !state.update_busy
            && Instant::now() >= next_update
            && !monitor.network_unavailable
        {
            state.update_busy = true;
            spawn_update(
                &mut auxiliary,
                update_transport.clone(),
                profile.root.clone(),
                events_sender.clone(),
                true,
            );
            next_update = Instant::now() + Duration::from_secs(6 * 3600);
        }
    }
    state.shutting_down = true;
    state_sender.send_replace(Arc::new(state));
    cancel_sender.send_modify(|g| *g += 1);
    geo_sender.send_modify(|control| control.enabled = false);
    drop(geo_sender);
    if tokio::time::timeout(Duration::from_secs(3), &mut geo_task)
        .await
        .is_err()
    {
        journal.event("geo-shutdown-timeout");
        geo_task.abort();
        let _ = geo_task.await;
    }
    auxiliary.abort_all();
    while auxiliary.join_next().await.is_some() {}
    let _ = tokio::time::timeout(Duration::from_secs(2), async {
        while probes.join_next().await.is_some() {}
    })
    .await;
    if let Some(mut task) = save_task
        && tokio::time::timeout(Duration::from_secs(5), &mut task.receiver)
            .await
            .is_err()
    {
        journal.event("final-save-busy");
        return Ok(());
    }
    profile.availability.close(Utc::now());
    profile
        .locations
        .close(profile.locations.last_observed_utc.unwrap_or(Utc::now()));
    let saved = save_owned(profile.snapshot(true, true, true));
    match tokio::time::timeout(Duration::from_secs(5), saved).await {
        Ok(Ok(failures)) => {
            for code in failures {
                journal.event(code);
            }
        }
        _ => journal.event("final-save-timeout"),
    }
    Ok(())
}

fn prune_locations_if_due(
    profile: &mut Profile,
    last_prune: &mut Instant,
    now: Instant,
    utc: chrono::DateTime<Utc>,
) -> bool {
    if now.saturating_duration_since(*last_prune) < Duration::from_secs(60) {
        return false;
    }
    *last_prune = now;
    let revision = profile.locations.revision;
    profile.locations.prune(utc);
    revision != profile.locations.revision
}
/// The same event owner handles normal delivery and the final accepted queue.
fn apply_event(
    event: Event,
    profile: &mut Profile,
    state: &mut State,
    epoch: u64,
    generation: u64,
    network_active: bool,
) -> bool {
    match event {
        Event::Geo {
            epoch: observed_epoch,
            generation: observed_generation,
            observation,
            versions,
            quota_failed,
        } => {
            if observed_epoch == epoch
                && observed_generation == generation
                && profile.settings.geo_country_detection_enabled
                && network_active
            {
                profile.locations.observe(*observation);
                state.database_status = versions;
                if quota_failed {
                    warn(
                        &mut state.warnings,
                        LocalizedText::new(
                            "Резервный сервис страны временно недоступен: не удалось проверить счётчик лимита.",
                            "The fallback country service is temporarily unavailable: its quota counter couldn't be verified.",
                        ),
                    );
                }
            }
        }
        Event::Update { text, exit } => {
            state.update_busy = false;
            state.update_status = text;
            return exit;
        }
        Event::Export(result) => {
            state.export_busy = false;
            match result {
                Ok(path) => {
                    state.export_path = Some(path);
                    state.diagnostic = LocalizedText::new(
                        "Диагностический архив сохранён.",
                        "The diagnostic archive was saved.",
                    );
                }
                Err(_) => warn(
                    &mut state.warnings,
                    LocalizedText::new(
                        "Не удалось сохранить диагностический архив.",
                        "Couldn't save the diagnostic archive.",
                    ),
                ),
            }
        }
    }
    false
}
fn warn(warnings: &mut Vec<LocalizedText>, text: LocalizedText) {
    if warnings.len() < 16 && !warnings.contains(&text) {
        warnings.push(text);
    }
}
fn diagnostic_text(result: &ProbeResult, detail: Option<&LocalizedText>) -> LocalizedText {
    let outcome = match result.outcome {
        Outcome::Reachable => ("ответ получен", "response received"),
        Outcome::Unreachable => ("нет ответа", "no response"),
        Outcome::Indeterminate => ("результат не подтверждён", "unconfirmed result"),
        Outcome::Cancelled => ("отменено", "cancelled"),
    };
    let status = result
        .status
        .map(|s| format!(", HTTP {s}"))
        .unwrap_or_default();
    LocalizedText::new(
        format!(
            "HTTPS: {}{}; {} мс.\n{}",
            outcome.0,
            status,
            result.elapsed_ms,
            detail.map_or("TCP/ICMP не выполнены.", |value| value
                .get(Language::Ru))
        ),
        format!(
            "HTTPS: {}{}; {} ms.\n{}",
            outcome.1,
            status,
            result.elapsed_ms,
            detail.map_or("TCP/ICMP wasn't run.", |value| value.get(Language::En))
        ),
    )
}
#[derive(Clone, Copy)]
struct SaveStamp {
    settings: u64,
    availability: (u64, Option<chrono::DateTime<Utc>>),
    locations: u64,
}
#[derive(Default)]
struct Persisted {
    settings: Option<u64>,
    availability: Option<(u64, Option<chrono::DateTime<Utc>>)>,
    locations: Option<u64>,
}
impl Persisted {
    fn accept_with_feedback(
        &mut self,
        stamp: SaveStamp,
        failures: &[&str],
        warnings: &mut Vec<LocalizedText>,
    ) -> bool {
        let before = warnings.clone();
        let worker_warning = save_warning("history-save-worker-failed").unwrap();
        if failures.contains(&"history-save-worker-failed") {
            warn(warnings, worker_warning);
            return *warnings != before;
        }
        warnings.retain(|warning| warning != &worker_warning);
        for (code, attempted) in [
            (
                "settings-save-failed",
                self.settings != Some(stamp.settings),
            ),
            (
                "availability-save-failed",
                self.availability != Some(stamp.availability),
            ),
            (
                "location-save-failed",
                self.locations != Some(stamp.locations),
            ),
        ] {
            let warning = save_warning(code).unwrap();
            if failures.contains(&code) {
                warn(warnings, warning);
            } else if attempted {
                warnings.retain(|value| value != &warning);
            }
        }
        self.accept(stamp, failures);
        *warnings != before
    }
    fn accept(&mut self, stamp: SaveStamp, failures: &[&str]) {
        if failures.contains(&"history-save-worker-failed") {
            return;
        }
        if !failures.contains(&"settings-save-failed") {
            self.settings = Some(stamp.settings);
        }
        if !failures.contains(&"availability-save-failed") {
            self.availability = Some(stamp.availability);
        }
        if !failures.contains(&"location-save-failed") {
            self.locations = Some(stamp.locations);
        }
    }
}
fn save_warning(code: &str) -> Option<LocalizedText> {
    let (ru, en) = match code {
        "settings-save-failed" => (
            "Не удалось сохранить настройки. Программа повторит запись.",
            "Couldn't save settings. The application will try again.",
        ),
        "availability-save-failed" => (
            "Не удалось сохранить историю доступности. Программа повторит запись.",
            "Couldn't save availability history. The application will try again.",
        ),
        "location-save-failed" => (
            "Не удалось сохранить историю стран. Программа повторит запись.",
            "Couldn't save country history. The application will try again.",
        ),
        "history-save-worker-failed" => (
            "Не удалось завершить сохранение данных. Программа повторит запись.",
            "Couldn't complete the data save. The application will try again.",
        ),
        _ => return None,
    };
    Some(LocalizedText::new(ru, en))
}
struct SaveJob {
    receiver: oneshot::Receiver<Vec<&'static str>>,
    stamp: SaveStamp,
}
fn start_save(
    profile: &Profile,
    generation: u64,
    persisted: &Persisted,
    job: &mut Option<SaveJob>,
) {
    if job.is_some() {
        return;
    }
    let stamp = SaveStamp {
        settings: generation,
        availability: (
            profile.availability.revision,
            profile.availability.last_observed_utc,
        ),
        locations: profile.locations.revision,
    };
    let settings = persisted.settings != Some(stamp.settings);
    let availability = persisted.availability != Some(stamp.availability);
    let locations = persisted.locations != Some(stamp.locations);
    if settings || availability || locations {
        *job = Some(SaveJob {
            receiver: save_owned(profile.snapshot(settings, availability, locations)),
            stamp,
        });
    }
}
fn save_owned(snapshot: SaveSnapshot) -> oneshot::Receiver<Vec<&'static str>> {
    let (sender, receiver) = oneshot::channel();
    // Files never compete with Windows DNS resolution or database parsing in Tokio's blocking pool.
    // The runtime admits one writer and awaits its completion before submitting another snapshot.
    let _ = thread::Builder::new()
        .name("netlights-save".into())
        .spawn(move || {
            let failures = snapshot.write();
            let _ = sender.send(failures);
        });
    receiver
}
fn spawn_probe(
    jobs: &mut JoinSet<(ProbeWork, ProbeResult, Option<LocalizedText>)>,
    work: ProbeWork,
    transports: &[Transport; 2],
    mut cancel: watch::Receiver<u64>,
    diagnostics: Option<Arc<Diagnostics>>,
) -> tokio::task::Id {
    cancel.borrow_and_update();
    let transport = transports[work.endpoint.group.index()].clone();
    jobs.spawn(async move {
        let (result,detail) = tokio::select! { result = async{
            let result=transport.probe(&work).await;
            let detail=if work.manual{if let Some(diagnostics)=diagnostics{let value=diagnostics.run(&work.endpoint.uri,transport.observed_address(&work.endpoint.uri)).await; Some(LocalizedText::new(value.text_in(Language::Ru),value.text_in(Language::En)))}else{Some(LocalizedText::new("Не удалось запустить TCP/ICMP-диагностику.", "Couldn't start TCP/ICMP diagnostics."))}}else{None};
            (result,detail)
        } => result,
            _ = cancel.changed() => (ProbeResult { outcome: Outcome::Cancelled, status: None, failure: None, elapsed_ms: 0, retry_after_ms: 0 },None) };
        (work, result,detail)
    }).id()
}
fn spawn_update(
    jobs: &mut JoinSet<()>,
    transport: Transport,
    root: PathBuf,
    sender: mpsc::Sender<Event>,
    automatic: bool,
) {
    jobs.spawn(async move {
        let outcome = async {
            let restart = std::env::current_exe()?;
            update::preflight(&root.join("updates"), &restart)?;
            let _ = update::cleanup_old_staging(&root.join("updates"), Duration::from_secs(3600));
            let Some(release) = update::find_update(&transport, netlights_core::VERSION).await?
            else {
                return Ok((
                    LocalizedText::new(
                        "Установлена актуальная версия.",
                        "The application is up to date.",
                    ),
                    false,
                ));
            };
            if automatic && !update::retry_allowed(&root.join("updates"), &release) {
                return Ok((
                    LocalizedText::new(
                        "Повтор обновления отложен после неудачной попытки.",
                        "The next update attempt was postponed after a failed attempt.",
                    ),
                    false,
                ));
            }
            let setup = update::stage_update(&transport, &release, &root.join("updates")).await?;
            let sha = release.sha256;
            let cleanup = setup.clone();
            let updates = root.join("updates");
            let result =
                tokio::task::spawn_blocking(move || update::start_agent(&setup, &sha, &restart))
                    .await;
            if !matches!(result, Ok(Ok(()))) {
                let _ = update::cleanup_staged_update(&cleanup, &updates);
            }
            result??;
            Ok::<_, anyhow::Error>((
                LocalizedText::new("Установка обновления...", "Installing the update..."),
                true,
            ))
        }
        .await;
        let (text, exit) = outcome.unwrap_or_else(|error| (update_error_message(&error), false));
        let _ = sender.send(Event::Update { text, exit }).await;
    });
}

fn update_error_message(error: &anyhow::Error) -> LocalizedText {
    if error.is::<update::UnresolvedTransaction>() {
        LocalizedText::new(
            update::UNRESOLVED_TRANSACTION_NOTICE,
            "An unfinished update marker was preserved. Removing it automatically without verifying ownership is unsafe. Data was preserved. Running the installer again doesn't guarantee recovery; this marker needs inspection.",
        )
    } else {
        LocalizedText::new(
            "Обновление не выполнено. Приложение продолжает работу; повторите проверку позже.",
            "The update wasn't completed. The application is still running; try checking again later.",
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    #[test]
    fn disabled_country_history_expires_without_creating_observations_or_changing_os_clock() {
        let mut profile = fixture_profile();
        profile.settings.geo_country_detection_enabled = false;
        let at = Utc::now();
        profile.locations.observe(observation(at, "DE"));
        profile.locations.close(at);
        let monotonic = Instant::now();
        let mut last_prune = monotonic;
        let later = at + chrono::Duration::days(8);
        assert!(!prune_locations_if_due(
            &mut profile,
            &mut last_prune,
            monotonic + Duration::from_secs(59),
            later
        ));
        assert_eq!(profile.locations.observations.len(), 1);
        assert!(prune_locations_if_due(
            &mut profile,
            &mut last_prune,
            monotonic + Duration::from_secs(60),
            later
        ));
        assert!(profile.locations.observations.is_empty());
        assert_eq!(profile.locations.last_observed_utc, Some(at));
        assert!(
            profile
                .locations
                .stays
                .iter()
                .all(|stay| stay.ended_utc == Some(at))
        );
        assert!(!profile.settings.geo_country_detection_enabled);
        assert!(profile.snapshot(false, false, true).write().is_empty());
        let saved: LocationHistory =
            serde_json::from_slice(&fs::read(profile.root.join("location-history.json")).unwrap())
                .unwrap();
        assert!(saved.observations.is_empty());
        assert_eq!(saved.last_observed_utc, Some(at));
        assert!(!prune_locations_if_due(
            &mut profile,
            &mut last_prune,
            monotonic + Duration::from_secs(61),
            later
        ));
        fs::remove_dir_all(profile.root).unwrap();
    }
    #[test]
    fn manual_result_remains_bilingual_without_repeating_the_network_check() {
        let result = ProbeResult {
            outcome: Outcome::Indeterminate,
            status: Some(429),
            failure: None,
            elapsed_ms: 123,
            retry_after_ms: 0,
        };
        let detail = LocalizedText::new("TCP: ответ получен", "TCP: response received");
        let text = diagnostic_text(&result, Some(&detail));
        assert!(
            text.get(Language::Ru)
                .contains("результат не подтверждён, HTTP 429; 123 мс")
        );
        assert!(
            text.get(Language::En)
                .contains("unconfirmed result, HTTP 429; 123 ms")
        );
        assert!(text.get(Language::En).contains("TCP: response received"));
        assert!(
            diagnostic_text(&result, None)
                .get(Language::En)
                .contains("wasn't run")
        );
    }
    #[test]
    fn update_marker_notice_is_actionable_without_exposing_other_error_details() {
        let unresolved =
            anyhow::Error::new(update::UnresolvedTransaction).context("private-path-fixture");
        assert_eq!(
            update_error_message(&unresolved),
            update::UNRESOLVED_TRANSACTION_NOTICE
        );
        assert!(
            update_error_message(&unresolved)
                .get(Language::En)
                .contains("unfinished update marker")
        );
        for error in [
            anyhow::anyhow!("token=fixture-secret"),
            anyhow::anyhow!("private-path-fixture"),
        ] {
            let message = update_error_message(&error);
            assert!(message.contains("Приложение продолжает работу"));
            assert!(!message.contains("fixture-secret"));
            assert!(!message.contains("private-path-fixture"));
            assert!(!message.get(Language::En).contains("fixture-secret"));
            assert!(!message.get(Language::En).contains("private-path-fixture"));
        }
    }
    fn fixture_profile() -> Profile {
        static SEQUENCE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
        let root = std::env::temp_dir().join(format!(
            "NetLightsBackendContract-{}-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap(),
            SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        Profile::open(root, true).unwrap()
    }
    fn fixture_state(profile: &Profile) -> State {
        let mut monitor = Monitor::new(profile.endpoints.clone(), 5, 0).unwrap();
        State {
            monitor: monitor.snapshot(0, Utc::now()),
            settings: profile.settings.clone(),
            availability: Arc::new(profile.availability.clone()),
            locations: Arc::new(profile.locations.clone()),
            traffic: Vec::new(),
            warnings: Vec::new(),
            update_status: LocalizedText::default(),
            update_busy: false,
            diagnostic: LocalizedText::default(),
            traffic_warning: false,
            traffic_limit: None,
            traffic_baseline: None,
            database_status: ["old-v2".into(), "old-ip".into()],
            export_path: None,
            export_busy: false,
            shutting_down: false,
            events: Vec::new(),
            settings_generation: 0,
            installed: false,
            isolated: true,
        }
    }
    fn observation(at: chrono::DateTime<Utc>, country: &str) -> LocationObservation {
        serde_json::from_value(serde_json::json!({
            "observedUtc":at,"observedUntilUtc":at,"countryCode":country,
            "onlineCountry":country,"localCountry":"FR","iplocateCountry":"DE",
            "confirmed":true,"publicIp":"203.0.113.5","databaseVersion":"v2-fixture",
            "iplocateDatabaseVersion":"ip-fixture","resolutionSource":"country.is"
        }))
        .unwrap()
    }
    #[test]
    fn stale_disabled_or_disconnected_country_results_cannot_mutate_history_or_debug_metadata() {
        let mut profile = fixture_profile();
        let at = Utc::now();
        profile.locations.observe(observation(at, "DE"));
        let before = profile.locations.observations.clone();
        let stays = profile.locations.stays.clone();
        let mut state = fixture_state(&profile);
        for (event_epoch, event_generation, enabled, network_active) in [
            (4, 11, true, true),
            (5, 10, true, true),
            (5, 11, false, true),
            (5, 11, true, false),
        ] {
            profile.settings.geo_country_detection_enabled = enabled;
            assert!(!apply_event(
                Event::Geo {
                    epoch: event_epoch,
                    generation: event_generation,
                    observation: Box::new(observation(at + chrono::Duration::seconds(10), "JP")),
                    versions: ["new-v2".into(), "new-ip".into()],
                    quota_failed: true,
                },
                &mut profile,
                &mut state,
                5,
                11,
                network_active
            ));
            assert_eq!(profile.locations.observations, before);
            assert_eq!(profile.locations.stays, stays);
            assert_eq!(profile.locations.last_observed_utc, Some(at));
            assert_eq!(state.database_status, ["old-v2", "old-ip"]);
            assert!(
                state.warnings.is_empty(),
                "Rejected evidence generated a user warning"
            );
        }
        fs::remove_dir_all(profile.root).unwrap();
    }
    #[test]
    fn queued_geo_results_cannot_bridge_detection_disable_and_reenable() {
        let mut profile = fixture_profile();
        profile.settings.geo_country_detection_enabled = true;
        let at = Utc::now() - chrono::Duration::seconds(60);
        profile.locations.observe(observation(at, "DE"));
        let mut state = fixture_state(&profile);
        let mut control = GeoControl {
            enabled: true,
            seconds: 10,
            generation: 11,
        };
        let queued = Event::Geo {
            epoch: 5,
            generation: control.generation,
            observation: Box::new(observation(at + chrono::Duration::seconds(10), "DE")),
            versions: ["stale-v2".into(), "stale-ip".into()],
            quota_failed: true,
        };
        profile.settings.geo_country_detection_enabled = false;
        control.restart(false);
        profile
            .locations
            .close(profile.locations.last_observed_utc.unwrap());
        profile.settings.geo_country_detection_enabled = true;
        control.restart(true);
        assert_eq!(control.generation, 13);
        apply_event(
            queued,
            &mut profile,
            &mut state,
            5,
            control.generation,
            true,
        );
        assert_eq!(profile.locations.observations.len(), 1);
        assert_eq!(state.database_status, ["old-v2", "old-ip"]);
        assert!(state.warnings.is_empty());
        apply_event(
            Event::Geo {
                epoch: 5,
                generation: control.generation,
                observation: Box::new(observation(at + chrono::Duration::seconds(30), "DE")),
                versions: ["fresh-v2".into(), "fresh-ip".into()],
                quota_failed: false,
            },
            &mut profile,
            &mut state,
            5,
            control.generation,
            true,
        );
        assert_eq!(profile.locations.observations.len(), 2);
        assert_eq!(profile.locations.observations[0].until(), at);
        assert_eq!(
            profile.locations.observations[1].observed_utc,
            at + chrono::Duration::seconds(30)
        );
        assert_eq!(profile.locations.stays[0].ended_utc, Some(at));
        assert_eq!(
            profile.locations.stays[1].started_utc,
            at + chrono::Duration::seconds(30)
        );
        assert_eq!(state.database_status, ["fresh-v2", "fresh-ip"]);
        fs::remove_dir_all(profile.root).unwrap();
    }
    #[test]
    fn accepted_country_result_preserves_source_evidence_and_deduplicates_quota_warning() {
        let mut profile = fixture_profile();
        profile.settings.geo_country_detection_enabled = true;
        let mut state = fixture_state(&profile);
        let at = Utc::now();
        for seconds in [0, 10] {
            assert!(!apply_event(
                Event::Geo {
                    epoch: 5,
                    generation: 11,
                    observation: Box::new(observation(
                        at + chrono::Duration::seconds(seconds),
                        "JP"
                    )),
                    versions: ["new-v2".into(), "new-ip".into()],
                    quota_failed: true,
                },
                &mut profile,
                &mut state,
                5,
                11,
                true
            ));
        }
        assert_eq!(profile.locations.stays.len(), 1);
        assert_eq!(profile.locations.stays[0].iso, "JP");
        assert_eq!(profile.locations.observations.len(), 1);
        assert_eq!(
            profile.locations.observations[0].local_country.as_deref(),
            Some("FR")
        );
        assert_eq!(
            profile.locations.observations[0].until(),
            at + chrono::Duration::seconds(10)
        );
        assert_eq!(state.database_status, ["new-v2", "new-ip"]);
        assert_eq!(state.warnings.len(), 1);
        assert!(state.warnings[0].contains("счётчик лимита"));
        fs::remove_dir_all(profile.root).unwrap();
    }
    #[test]
    fn updater_and_export_receipts_release_busy_state_and_only_ready_handoff_requests_exit() {
        let mut profile = fixture_profile();
        let mut state = fixture_state(&profile);
        for exit in [false, true] {
            state.update_busy = true;
            assert_eq!(
                apply_event(
                    Event::Update {
                        text: "fixture update result".into(),
                        exit
                    },
                    &mut profile,
                    &mut state,
                    1,
                    0,
                    true
                ),
                exit
            );
            assert!(!state.update_busy);
            assert_eq!(state.update_status, "fixture update result");
        }
        let archive = profile.root.join("last-good.zip");
        state.export_busy = true;
        assert!(!apply_event(
            Event::Export(Ok(archive.clone())),
            &mut profile,
            &mut state,
            1,
            0,
            true
        ));
        assert!(!state.export_busy);
        assert_eq!(state.export_path.as_ref(), Some(&archive));
        assert!(state.diagnostic.contains("архив сохранён"));
        state.export_busy = true;
        apply_event(
            Event::Export(Err(anyhow::anyhow!("token=fixture-secret"))),
            &mut profile,
            &mut state,
            1,
            0,
            true,
        );
        assert!(!state.export_busy);
        assert_eq!(state.export_path.as_ref(), Some(&archive));
        assert_eq!(state.warnings.len(), 1);
        assert!(!state.warnings[0].contains("fixture-secret"));
        fs::remove_dir_all(profile.root).unwrap();
    }
    #[test]
    fn diagnostic_archive_whitelists_data_without_urls_ips_or_private_ui_text() {
        use std::io::Read;
        let mut profile = fixture_profile();
        profile.locations.observe(observation(Utc::now(), "DE"));
        let mut state = fixture_state(&profile);
        // Production endpoint validation correctly rejects query credentials.
        // Inject the adversarial value only into this private export fixture.
        state.monitor.groups[0].endpoints[0]
            .endpoint
            .uri
            .set_query(Some("token=fixture-secret"));
        assert!(
            state.monitor.groups[0].endpoints[0]
                .endpoint
                .uri
                .as_str()
                .contains("fixture-secret")
        );
        state.warnings.push("private-ui-text fixture-secret".into());
        state.diagnostic = "private-ui-text fixture-secret".into();
        state.events.push((Utc::now(), "runtime-start".into()));
        let path = export::write(&state, &profile.root).unwrap();
        let mut archive = zip::ZipArchive::new(fs::File::open(&path).unwrap()).unwrap();
        let mut names = Vec::new();
        for index in 0..archive.len() {
            let mut entry = archive.by_index(index).unwrap();
            names.push(entry.name().to_owned());
            let mut bytes = Vec::new();
            entry.read_to_end(&mut bytes).unwrap();
            let text = String::from_utf8(bytes).unwrap();
            for private in [
                "fixture-secret",
                "private-ui-text",
                "203.0.113.5",
                "https://",
            ] {
                assert!(
                    !text.contains(private),
                    "Private field {private} leaked into {}",
                    entry.name()
                );
            }
            let document: serde_json::Value = serde_json::from_str(&text).unwrap();
            if entry.name() == "snapshot.json" {
                assert_eq!(document["groups"][0]["endpoints"][0]["index"], 0);
            }
            if entry.name() == "events.json" {
                assert!(text.contains("runtime-start"));
            }
        }
        names.sort();
        assert_eq!(names, ["events.json", "snapshot.json", "versions.json"]);
        drop(archive);
        fs::remove_dir_all(profile.root).unwrap();
    }
    #[test]
    fn export_directory_failure_preserves_the_blocking_file() {
        let profile = fixture_profile();
        let state = fixture_state(&profile);
        let blocker = profile.root.join("exports");
        fs::write(&blocker, b"preserved user bytes").unwrap();
        assert!(export::write(&state, &profile.root).is_err());
        assert_eq!(fs::read(blocker).unwrap(), b"preserved user bytes");
        fs::remove_dir_all(profile.root).unwrap();
    }
    #[test]
    fn save_acknowledgement_does_not_hide_newer_or_failed_changes() {
        let old = SaveStamp {
            settings: 1,
            availability: (7, None),
            locations: 9,
        };
        let mut saved = Persisted::default();
        saved.accept(old, &["location-save-failed"]);
        assert_eq!(saved.settings, Some(1));
        assert_eq!(saved.locations, None);
        assert_ne!(saved.settings, Some(2));
        saved.accept(
            SaveStamp { settings: 2, ..old },
            &["history-save-worker-failed"],
        );
        assert_eq!(saved.settings, Some(1));
        saved.accept(old, &[]);
        assert_eq!(saved.locations, Some(9));
    }
    #[test]
    fn save_feedback_names_the_failed_store_and_clears_only_recovered_failures() {
        let stamp = SaveStamp {
            settings: 1,
            availability: (7, None),
            locations: 9,
        };
        let mut saved = Persisted::default();
        let mut warnings = vec![];
        assert!(saved.accept_with_feedback(
            stamp,
            &[
                "settings-save-failed",
                "save-detail-io-os-32",
                "location-save-failed"
            ],
            &mut warnings
        ));
        assert_eq!(warnings.len(), 2);
        assert!(
            warnings
                .iter()
                .any(|w| w.get(Language::En).contains("save settings"))
        );
        assert_eq!(saved.settings, None);
        assert_eq!(saved.locations, None);
        assert!(!saved.accept_with_feedback(
            stamp,
            &["settings-save-failed", "location-save-failed"],
            &mut warnings
        ));
        assert!(saved.accept_with_feedback(stamp, &["location-save-failed"], &mut warnings));
        assert_eq!(
            warnings,
            vec![save_warning("location-save-failed").unwrap()]
        );
        assert_eq!(saved.settings, Some(1));
        assert_eq!(saved.locations, None);
        assert!(saved.accept_with_feedback(stamp, &["history-save-worker-failed"], &mut warnings));
        assert_eq!(saved.locations, None);
        assert_eq!(warnings.len(), 2);
        assert!(saved.accept_with_feedback(stamp, &[], &mut warnings));
        assert!(warnings.is_empty());
        assert_eq!(saved.locations, Some(9));
    }
    #[test]
    fn persistence_is_independent_of_saturated_dns_and_database_pool() {
        let root = std::env::temp_dir().join(format!(
            "NetLightsSave-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        let profile = Profile::open(root.clone(), true).unwrap();
        let rt = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(1)
            .max_blocking_threads(3)
            .enable_all()
            .build()
            .unwrap();
        let released = Arc::new(AtomicBool::new(false));
        let started = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let result = rt.block_on(async {
            for _ in 0..3 {
                let released = released.clone();
                let started = started.clone();
                tokio::task::spawn_blocking(move || {
                    started.fetch_add(1, Ordering::Release);
                    while !released.load(Ordering::Acquire) {
                        thread::sleep(Duration::from_millis(5));
                    }
                });
            }
            tokio::time::timeout(Duration::from_secs(2), async {
                while started.load(Ordering::Acquire) < 3 {
                    tokio::time::sleep(Duration::from_millis(1)).await;
                }
            })
            .await
            .unwrap();
            tokio::time::timeout(
                Duration::from_secs(2),
                save_owned(profile.snapshot(true, true, true)),
            )
            .await
        });
        released.store(true, Ordering::Release);
        drop(rt);
        assert!(result.unwrap().unwrap().is_empty());
        assert!(root.join("settings.json").is_file());
        assert!(root.join("availability-history.json").is_file());
        assert!(root.join("location-history.json").is_file());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn journal_is_bounded_and_rejects_personal_data_in_codes() {
        let root = std::env::temp_dir().join(format!(
            "NetLightsJournal-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        let journal = Journal::new(&root);
        journal.event("https://secret.invalid/?token=secret");
        journal.event("198.51.100.10");
        for _ in 0..520 {
            journal.event("probe-failed");
        }
        assert_eq!(journal.events.lock().unwrap().len(), 512);
        let contents = fs::read_to_string(&journal.path).unwrap();
        assert!(!contents.contains("secret"));
        assert!(!contents.contains("198.51.100"));
        fs::remove_dir_all(root).unwrap();
    }
}
