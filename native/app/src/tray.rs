//! The tray owns its HWND, HICONs, power subscription and network callback on
//! one native thread. It doesn't depend on the GPUI window remaining open.
use anyhow::{Context, Result};
use netlights_core::{locale::Language, model::Availability};
use std::{
    cell::RefCell,
    collections::VecDeque,
    ffi::c_void,
    mem::size_of,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicIsize, AtomicU64, Ordering},
        mpsc::{self, SyncSender, TrySendError},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use windows::{
    Win32::{
        Foundation::{COLORREF, HANDLE, HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, WPARAM},
        Graphics::Gdi::*,
        NetworkManagement::IpHelper::{
            CancelMibChangeNotify2, MIB_IPINTERFACE_ROW, MIB_NOTIFICATION_TYPE,
            NotifyIpInterfaceChange,
        },
        Networking::WinSock::AF_UNSPEC,
        System::{
            LibraryLoader::GetModuleHandleW,
            Power::{
                HPOWERNOTIFY, RegisterSuspendResumeNotification,
                UnregisterSuspendResumeNotification,
            },
        },
        UI::{
            HiDpi::{GetDpiForSystem, GetSystemMetricsForDpi},
            Shell::{
                NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE, NIM_MODIFY, NOTIFYICONDATAW,
                Shell_NotifyIconW,
            },
            WindowsAndMessaging::*,
        },
    },
    core::{PCWSTR, w},
};

const WM_STATE: u32 = WM_APP + 41;
const WM_TRAY: u32 = WM_APP + 42;
const WM_NETWORK: u32 = WM_APP + 43;
const TIMER: usize = 1;
const MAIN_ICON: u32 = 1;
const COUNTRY_ICON: u32 = 2;
const MAX_PENDING: usize = 64;
const RESTORE_ATTEMPTS: u8 = 12;
static HOST_SEQUENCE: AtomicU64 = AtomicU64::new(0);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum OsEvent {
    Suspend,
    Resume,
    NetworkChanged,
    Show,
    Exit,
    TogglePause,
    CheckNow,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TrayState {
    pub language: Language,
    pub provider: Availability,
    pub world: Availability,
    pub paused: bool,
    pub country: String,
    pub country_fresh: bool,
    pub detection_enabled: bool,
    pub country_icon_enabled: bool,
    pub letter_scale: i32,
}
impl Default for TrayState {
    fn default() -> Self {
        Self {
            language: Language::Ru,
            provider: Availability::Unknown,
            world: Availability::Unknown,
            paused: false,
            country: String::new(),
            country_fresh: false,
            detection_enabled: false,
            country_icon_enabled: false,
            letter_scale: 1,
        }
    }
}
struct Shared {
    state: Mutex<TrayState>,
    hwnd: AtomicIsize,
    ready: AtomicBool,
    state_posted: AtomicBool,
}
pub struct TrayHost {
    shared: Arc<Shared>,
    thread: Option<JoinHandle<()>>,
    done: mpsc::Receiver<()>,
}
impl TrayHost {
    pub fn new(sender: SyncSender<OsEvent>) -> Result<Self> {
        let shared = Arc::new(Shared {
            state: Mutex::new(TrayState::default()),
            hwnd: AtomicIsize::new(0),
            ready: AtomicBool::new(false),
            state_posted: AtomicBool::new(false),
        });
        let (started_tx, started_rx) = mpsc::sync_channel(1);
        let (done_tx, done) = mpsc::sync_channel(1);
        let worker_shared = shared.clone();
        let thread = thread::Builder::new()
            .name("netlights-tray".into())
            .spawn(move || {
                let _ = run(worker_shared, sender, started_tx);
                let _ = done_tx.send(());
            })
            .context("Couldn't start the tray thread")?;
        match started_rx.recv_timeout(Duration::from_secs(5)) {
            Ok(Ok(())) => Ok(Self {
                shared,
                thread: Some(thread),
                done,
            }),
            result => {
                let handle = shared.hwnd.load(Ordering::Acquire);
                if handle != 0 {
                    unsafe {
                        let _ =
                            PostMessageW(Some(HWND(handle as _)), WM_CLOSE, WPARAM(0), LPARAM(0));
                    }
                }
                if done.recv_timeout(Duration::from_secs(2)).is_ok() {
                    let _ = thread.join();
                }
                match result {
                    Ok(Err(message)) => Err(anyhow::anyhow!(message)),
                    _ => anyhow::bail!("The tray thread didn't initialize"),
                }
            }
        }
    }
    pub fn update(&self, state: TrayState) {
        let mut current = self.shared.state.lock().unwrap_or_else(|e| e.into_inner());
        if *current == state {
            return;
        }
        *current = state;
        self.shared.ready.store(false, Ordering::Release);
        drop(current);
        let handle = self.shared.hwnd.load(Ordering::Acquire);
        if handle != 0
            && !self.shared.state_posted.swap(true, Ordering::AcqRel)
            && unsafe { PostMessageW(Some(HWND(handle as _)), WM_STATE, WPARAM(0), LPARAM(0)) }
                .is_err()
        {
            self.shared.state_posted.store(false, Ordering::Release);
        }
    }
    pub fn ready(&self) -> bool {
        self.shared.ready.load(Ordering::Acquire)
    }
}
impl Drop for TrayHost {
    fn drop(&mut self) {
        self.shared.ready.store(false, Ordering::Release);
        let hwnd = self.shared.hwnd.load(Ordering::Acquire);
        if hwnd != 0 {
            unsafe {
                let _ = PostMessageW(Some(HWND(hwnd as _)), WM_CLOSE, WPARAM(0), LPARAM(0));
            }
        }
        // A broken shell mustn't prevent persistence and application shutdown.
        // The detached fallback retains its Arc and owns all native resources.
        if self.done.recv_timeout(Duration::from_secs(2)).is_ok()
            && let Some(thread) = self.thread.take()
        {
            let _ = thread.join();
        }
    }
}

struct OwnedIcon(HICON);
impl Drop for OwnedIcon {
    fn drop(&mut self) {
        unsafe {
            let _ = DestroyIcon(self.0);
        }
    }
}
struct NetworkContext {
    destination: Mutex<Option<isize>>,
    posted: AtomicBool,
}
fn retire_network(
    context: Option<Box<NetworkContext>>,
    cancel: impl FnOnce() -> bool,
) -> Option<*mut NetworkContext> {
    if let Some(context) = context {
        // Serialize the last PostMessage with HWND destruction, including cancellation failure.
        *context
            .destination
            .lock()
            .unwrap_or_else(|e| e.into_inner()) = None;
        if !cancel() {
            // Windows may still call this registration. A small inert tombstone must outlive it.
            // Successful cancellation, the normal path, releases the allocation immediately.
            return Some(Box::into_raw(context));
        }
    } else {
        let _ = cancel();
    }
    None
}
struct WindowSlot {
    state: RefCell<WindowState>,
    shared: Arc<Shared>,
}
struct WindowState {
    shared: Arc<Shared>,
    sender: SyncSender<OsEvent>,
    pending: VecDeque<OsEvent>,
    hwnd: HWND,
    taskbar_created: u32,
    main: Option<OwnedIcon>,
    country: Option<OwnedIcon>,
    main_present: bool,
    country_present: bool,
    rendered: Option<TrayState>,
    attempts: u8,
    next_retry: Instant,
    network: HANDLE,
    network_context: Option<Box<NetworkContext>>,
    power: Option<HPOWERNOTIFY>,
    closed: bool,
}
impl WindowState {
    fn emit(&mut self, event: OsEvent) {
        // Try delivery before allocating queue entries. Full queues are retried
        // by our own timer, never by blocking the shell's message loop.
        if self.pending.is_empty() {
            match self.sender.try_send(event) {
                Ok(()) => return,
                Err(TrySendError::Disconnected(_)) => return,
                Err(TrySendError::Full(_)) => {}
            }
        }
        enqueue(&mut self.pending, event);
        self.arm_timer();
    }
    fn flush(&mut self) {
        while let Some(event) = self.pending.front().copied() {
            match self.sender.try_send(event) {
                Ok(()) => {
                    self.pending.pop_front();
                }
                Err(TrySendError::Full(_)) => break,
                Err(TrySendError::Disconnected(_)) => {
                    self.pending.clear();
                    break;
                }
            }
        }
    }
    fn arm_timer(&self) {
        unsafe {
            SetTimer(Some(self.hwnd), TIMER, 200, None);
        }
    }
    fn begin_restore(&mut self) {
        self.main_present = false;
        self.country_present = false;
        self.shared.ready.store(false, Ordering::Release);
        self.attempts = RESTORE_ATTEMPTS;
        self.next_retry = Instant::now();
        self.refresh();
        self.arm_timer();
    }
    fn refresh(&mut self) {
        let state = self
            .shared
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clone();
        let result = self.refresh_inner(&state);
        let country_expected = state.detection_enabled && state.country_icon_enabled;
        let ready =
            result.is_ok() && self.main_present && (!country_expected || self.country_present);
        self.shared.ready.store(ready, Ordering::Release);
        if ready {
            self.attempts = 0;
            self.rendered = Some(state);
        } else if self.attempts == 0 {
            self.attempts = RESTORE_ATTEMPTS;
            self.next_retry = Instant::now() + Duration::from_secs(1);
            self.arm_timer();
        }
    }
    fn refresh_inner(&mut self, state: &TrayState) -> Result<()> {
        let size = icon_size();
        if self.main.is_none()
            || self.rendered.as_ref().is_none_or(|old| {
                old.provider != state.provider
                    || old.world != state.world
                    || old.paused != state.paused
                    || old.language != state.language
            })
        {
            let next = split_icon(size, state.provider, state.world, state.paused)?;
            let tip = primary_tip(state);
            self.main_present = publish(self.hwnd, MAIN_ICON, next.0, &tip, self.main_present);
            if self.main_present {
                self.main = Some(next);
            }
        } else if !self.main_present
            && let Some(icon) = &self.main
        {
            let tip = primary_tip(state);
            self.main_present = publish(self.hwnd, MAIN_ICON, icon.0, &tip, false);
        }
        let expected = state.detection_enabled && state.country_icon_enabled;
        if !expected {
            if self.country_present {
                remove_icon(self.hwnd, COUNTRY_ICON);
            }
            self.country_present = false;
            self.country = None;
        } else if self.country.is_none()
            || self.rendered.as_ref().is_none_or(|old| {
                old.country != state.country
                    || old.country_fresh != state.country_fresh
                    || old.letter_scale != state.letter_scale
                    || old.language != state.language
            })
        {
            let code = country_code(&state.country);
            let next = country_icon(size, &code, state.country_fresh, state.letter_scale)?;
            self.country_present = publish(
                self.hwnd,
                COUNTRY_ICON,
                next.0,
                &country_tip(state, &code),
                self.country_present,
            );
            if self.country_present {
                self.country = Some(next);
            }
        } else if !self.country_present
            && let Some(icon) = &self.country
        {
            self.country_present = publish(
                self.hwnd,
                COUNTRY_ICON,
                icon.0,
                &country_tip(state, &country_code(&state.country)),
                false,
            );
        }
        Ok(())
    }
    fn cleanup(&mut self) {
        if self.closed {
            return;
        }
        self.closed = true;
        self.shared.ready.store(false, Ordering::Release);
        self.shared.hwnd.store(0, Ordering::Release);
        unsafe {
            let _ = KillTimer(Some(self.hwnd), TIMER);
            let network = std::mem::take(&mut self.network);
            let _ = retire_network(self.network_context.take(), || {
                network.is_invalid() || CancelMibChangeNotify2(network).is_ok()
            });
            if let Some(power) = self.power.take() {
                let _ = UnregisterSuspendResumeNotification(power);
            }
        }
        remove_icon(self.hwnd, MAIN_ICON);
        remove_icon(self.hwnd, COUNTRY_ICON);
        self.main = None;
        self.country = None;
        self.main_present = false;
        self.country_present = false;
    }
}
impl Drop for WindowState {
    fn drop(&mut self) {
        self.cleanup();
    }
}

fn enqueue(queue: &mut VecDeque<OsEvent>, event: OsEvent) {
    if event == OsEvent::Exit {
        queue.retain(|e| matches!(e, OsEvent::Suspend | OsEvent::Resume));
        while queue.len() >= MAX_PENDING {
            queue.remove(1);
        }
        if !queue.contains(&event) {
            queue.push_back(event);
        }
        return;
    }
    if queue.contains(&OsEvent::Exit) {
        return;
    }
    if matches!(
        event,
        OsEvent::Show | OsEvent::CheckNow | OsEvent::NetworkChanged
    ) && queue.contains(&event)
    {
        return;
    }
    if event == OsEvent::TogglePause && queue.back() == Some(&event) {
        queue.pop_back();
        return;
    }
    if matches!(event, OsEvent::Suspend | OsEvent::Resume) && queue.back() == Some(&event) {
        return;
    }
    if queue.len() == MAX_PENDING {
        if let Some(index) = queue.iter().position(|e| {
            matches!(
                e,
                OsEvent::Show | OsEvent::CheckNow | OsEvent::NetworkChanged
            )
        }) {
            queue.remove(index);
        } else {
            // Preserve evidence of a power gap and the latest power state even
            // if a stalled consumer receives many sleep/wake notifications.
            let had_suspend = queue.contains(&OsEvent::Suspend);
            let previous = queue
                .iter()
                .rev()
                .find(|e| matches!(e, OsEvent::Suspend | OsEvent::Resume))
                .copied();
            let pause_parity = queue.iter().filter(|e| **e == OsEvent::TogglePause).count() % 2;
            queue.clear();
            if had_suspend {
                queue.push_back(OsEvent::Suspend);
            }
            if previous == Some(OsEvent::Resume) {
                queue.push_back(OsEvent::Resume);
            }
            if pause_parity == 1 {
                queue.push_back(OsEvent::TogglePause);
            }
            if event == OsEvent::TogglePause && queue.back() == Some(&event) {
                queue.pop_back();
                return;
            }
        }
    }
    queue.push_back(event);
}
fn country_code(text: &str) -> String {
    if text.len() == 2 && text.bytes().all(|b| b.is_ascii_alphabetic()) {
        text.to_ascii_uppercase()
    } else {
        "??".into()
    }
}
fn country_tip(state: &TrayState, code: &str) -> String {
    let language = state.language;
    let name = language.country_name(code);
    if state.country_fresh {
        format!(
            "Net Lights\n{}: {name} ({code})",
            language.choose("Страна", "Country")
        )
    } else {
        format!(
            "Net Lights\n{}: {name} ({code})\n{}",
            language.choose("Последняя страна", "Last country"),
            language.choose("Нет свежего подтверждения", "No recent confirmation")
        )
    }
}
fn primary_tip(state: &TrayState) -> String {
    let language = state.language;
    if state.paused {
        return format!(
            "Net Lights\n{}",
            language.choose("Проверки приостановлены", "Checks paused")
        );
    }
    let status = |availability| match availability {
        Availability::Online => language.choose("Доступно", "Online"),
        Availability::Limited => language.choose("Ограничено", "Limited"),
        Availability::Offline => language.choose("Нет ответа", "No response"),
        Availability::Unknown => language.choose("Нет свежих данных", "No recent data"),
    };
    format!(
        "Net Lights\n{}: {}\n{}: {}",
        language.choose("Провайдер", "Provider"),
        status(state.provider),
        language.choose("Мир", "World"),
        status(state.world)
    )
}
fn icon_size() -> i32 {
    unsafe { GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem()).clamp(16, 64) }
}
fn notify_data(hwnd: HWND, id: u32) -> NOTIFYICONDATAW {
    NOTIFYICONDATAW {
        cbSize: size_of::<NOTIFYICONDATAW>() as u32,
        hWnd: hwnd,
        uID: id,
        ..Default::default()
    }
}
fn publish(hwnd: HWND, id: u32, icon: HICON, tip: &str, present: bool) -> bool {
    let mut data = notify_data(hwnd, id);
    data.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    data.uCallbackMessage = WM_TRAY;
    data.hIcon = icon;
    let mut encoded: Vec<u16> = tip.encode_utf16().take(127).collect();
    if encoded
        .last()
        .is_some_and(|c| (0xD800..=0xDBFF).contains(c))
    {
        encoded.pop();
    }
    data.szTip[..encoded.len()].copy_from_slice(&encoded);
    unsafe {
        let (first, second) = if present {
            (NIM_MODIFY, NIM_ADD)
        } else {
            (NIM_ADD, NIM_MODIFY)
        };
        Shell_NotifyIconW(first, &data).as_bool() || Shell_NotifyIconW(second, &data).as_bool()
    }
}
fn remove_icon(hwnd: HWND, id: u32) {
    unsafe {
        let _ = Shell_NotifyIconW(NIM_DELETE, &notify_data(hwnd, id));
    }
}

fn run(
    shared: Arc<Shared>,
    sender: SyncSender<OsEvent>,
    started: SyncSender<std::result::Result<(), String>>,
) -> Result<()> {
    let result = unsafe { initialize(shared.clone(), sender) };
    let (slot, class, instance) = match result {
        Ok(result) => result,
        Err(error) => {
            let _ = started.send(Err(format!("{error:#}")));
            return Err(error);
        }
    };
    let _ = started.send(Ok(()));
    unsafe {
        let mut message = MSG::default();
        loop {
            let result = GetMessageW(&mut message, None, 0, 0).0;
            if result <= 0 {
                break;
            }
            let _ = TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        let (closed, hwnd) = {
            let state = slot.state.borrow();
            (state.closed, state.hwnd)
        };
        if !closed {
            let _ = DestroyWindow(hwnd);
        }
        slot.state.borrow_mut().cleanup();
        let _ = UnregisterClassW(PCWSTR(class.as_ptr()), Some(instance));
    }
    Ok(())
}
unsafe fn initialize(
    shared: Arc<Shared>,
    sender: SyncSender<OsEvent>,
) -> Result<(Box<WindowSlot>, Vec<u16>, HINSTANCE)> {
    let instance = HINSTANCE(unsafe { GetModuleHandleW(None)? }.0);
    let class: Vec<u16> = format!(
        "NetLights.NativeTray.{}.{}",
        std::process::id(),
        HOST_SEQUENCE.fetch_add(1, Ordering::Relaxed)
    )
    .encode_utf16()
    .chain(Some(0))
    .collect();
    let descriptor = WNDCLASSW {
        lpfnWndProc: Some(window_proc),
        hInstance: instance,
        lpszClassName: PCWSTR(class.as_ptr()),
        ..Default::default()
    };
    anyhow::ensure!(
        unsafe { RegisterClassW(&descriptor) } != 0,
        "Couldn't register the tray window"
    );
    let slot = Box::new(WindowSlot {
        shared: shared.clone(),
        state: RefCell::new(WindowState {
            shared: shared.clone(),
            sender,
            pending: VecDeque::new(),
            hwnd: HWND::default(),
            taskbar_created: unsafe { RegisterWindowMessageW(w!("TaskbarCreated")) },
            main: None,
            country: None,
            main_present: false,
            country_present: false,
            rendered: None,
            attempts: 0,
            next_retry: Instant::now(),
            network: HANDLE::default(),
            network_context: None,
            power: None,
            closed: false,
        }),
    });
    let created = unsafe {
        CreateWindowExW(
            WINDOW_EX_STYLE::default(),
            PCWSTR(class.as_ptr()),
            w!("Net Lights tray host"),
            WINDOW_STYLE::default(),
            0,
            0,
            0,
            0,
            None,
            None,
            Some(instance),
            Some((&*slot as *const WindowSlot).cast()),
        )
    };
    let hwnd = match created {
        Ok(hwnd) => hwnd,
        Err(error) => {
            unsafe {
                let _ = UnregisterClassW(PCWSTR(class.as_ptr()), Some(instance));
            }
            return Err(error.into());
        }
    };
    shared.hwnd.store(hwnd.0 as isize, Ordering::Release);
    let registrations = (|| -> Result<()> {
        let mut state = slot.state.borrow_mut();
        state.power = Some(unsafe {
            RegisterSuspendResumeNotification(HANDLE(hwnd.0), DEVICE_NOTIFY_WINDOW_HANDLE)?
        });
        state.network_context = Some(Box::new(NetworkContext {
            destination: Mutex::new(Some(hwnd.0 as isize)),
            posted: AtomicBool::new(false),
        }));
        let context = state.network_context.as_ref().unwrap().as_ref() as *const NetworkContext;
        unsafe {
            NotifyIpInterfaceChange(
                AF_UNSPEC,
                Some(network_changed),
                Some(context.cast()),
                false,
                &mut state.network,
            )
            .ok()?;
        }
        anyhow::ensure!(
            state.taskbar_created != 0,
            "Couldn't register Explorer recovery"
        );
        Ok(())
    })();
    if let Err(error) = registrations {
        unsafe {
            let _ = DestroyWindow(hwnd);
            let _ = UnregisterClassW(PCWSTR(class.as_ptr()), Some(instance));
        }
        return Err(error);
    }
    slot.state.borrow_mut().begin_restore();
    Ok((slot, class, instance))
}
unsafe extern "system" fn network_changed(
    context: *const c_void,
    _: *const MIB_IPINTERFACE_ROW,
    _: MIB_NOTIFICATION_TYPE,
) {
    if context.is_null() {
        return;
    }
    let context = unsafe { &*context.cast::<NetworkContext>() };
    let destination = context
        .destination
        .lock()
        .unwrap_or_else(|e| e.into_inner());
    let Some(hwnd) = *destination else {
        return;
    };
    if !context.posted.swap(true, Ordering::AcqRel)
        && unsafe { PostMessageW(Some(HWND(hwnd as _)), WM_NETWORK, WPARAM(0), LPARAM(0)) }.is_err()
    {
        context.posted.store(false, Ordering::Release);
    }
}
unsafe extern "system" fn window_proc(hwnd: HWND, message: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| unsafe {
        dispatch(hwnd, message, wp, lp)
    }))
    .unwrap_or_else(|_| {
        unsafe {
            let _ = PostMessageW(Some(hwnd), WM_CLOSE, WPARAM(0), LPARAM(0));
        }
        LRESULT(0)
    })
}
unsafe fn dispatch(hwnd: HWND, message: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    if message == WM_NCCREATE {
        let create = unsafe { &*(lp.0 as *const CREATESTRUCTW) };
        unsafe {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, create.lpCreateParams as isize);
        }
        let slot = unsafe { &*create.lpCreateParams.cast::<WindowSlot>() };
        slot.state.borrow_mut().hwnd = hwnd;
        return LRESULT(1);
    }
    let pointer = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *const WindowSlot;
    if pointer.is_null() {
        return unsafe { DefWindowProcW(hwnd, message, wp, lp) };
    }
    if message == WM_CLOSE {
        unsafe {
            let _ = DestroyWindow(hwnd);
        }
        return LRESULT(0);
    }
    let slot = unsafe { &*pointer };
    if message == WM_NCDESTROY {
        unsafe {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
        }
        return unsafe { DefWindowProcW(hwnd, message, wp, lp) };
    }
    if message == WM_DESTROY {
        slot.shared.ready.store(false, Ordering::Release);
        slot.shared.hwnd.store(0, Ordering::Release);
        // DestroyWindow can reenter while Shell_NotifyIcon is still using the
        // state. Cleanup then happens after the outer callback returns.
        if let Ok(mut state) = slot.state.try_borrow_mut() {
            state.cleanup();
        }
        unsafe {
            PostQuitMessage(0);
        }
        return LRESULT(0);
    }
    // TrackPopupMenu pumps nested messages. Don't retain a mutable state
    // reference across it; nested timer/network delivery uses the same HWND.
    if message == WM_TRAY && matches!(lp.0 as u32, WM_RBUTTONUP | WM_CONTEXTMENU) {
        let available = slot.state.try_borrow_mut().is_ok();
        if !available {
            unsafe {
                let _ = PostMessageW(Some(hwnd), message, wp, lp);
            }
            return LRESULT(0);
        }
        let (paused, language) = {
            let state = slot.shared.state.lock().unwrap_or_else(|e| e.into_inner());
            (state.paused, state.language)
        };
        if let Some(event) = popup(hwnd, paused, language)
            && let Ok(mut state) = slot.state.try_borrow_mut()
        {
            state.emit(event);
        }
        return LRESULT(0);
    }
    let Ok(mut state) = slot.state.try_borrow_mut() else {
        // Only owned scalar messages can be replayed. Windows pointer-bearing
        // messages are left to DefWindowProc while a native call is in flight.
        if matches!(
            message,
            WM_STATE
                | WM_NETWORK
                | WM_TRAY
                | WM_POWERBROADCAST
                | WM_TIMER
                | WM_SETTINGCHANGE
                | WM_QUERYENDSESSION
                | WM_ENDSESSION
        ) && (message != WM_SETTINGCHANGE || lp.0 == 0)
        {
            unsafe {
                let _ = PostMessageW(Some(hwnd), message, wp, lp);
            }
            return LRESULT(
                if matches!(message, WM_POWERBROADCAST | WM_QUERYENDSESSION) {
                    1
                } else {
                    0
                },
            );
        }
        return unsafe { DefWindowProcW(hwnd, message, wp, lp) };
    };
    if message == state.taskbar_created {
        state.begin_restore();
        return LRESULT(0);
    }
    match message {
        WM_STATE => {
            state.shared.state_posted.store(false, Ordering::Release);
            state.refresh();
        }
        WM_NETWORK => {
            if let Some(context) = &state.network_context {
                context.posted.store(false, Ordering::Release);
            }
            state.emit(OsEvent::NetworkChanged);
        }
        WM_TRAY if matches!(lp.0 as u32, WM_LBUTTONUP | WM_LBUTTONDBLCLK) => {
            state.emit(OsEvent::Show)
        }
        WM_POWERBROADCAST => {
            match wp.0 as u32 {
                PBT_APMSUSPEND => state.emit(OsEvent::Suspend),
                PBT_APMRESUMEAUTOMATIC | PBT_APMRESUMESUSPEND => state.emit(OsEvent::Resume),
                _ => {}
            }
            return LRESULT(1);
        }
        WM_QUERYENDSESSION => {
            state.emit(OsEvent::Exit);
            return LRESULT(1);
        }
        WM_ENDSESSION if wp.0 != 0 => {
            state.emit(OsEvent::Exit);
        }
        WM_TIMER if wp.0 == TIMER => {
            state.flush();
            if state.attempts > 0 && Instant::now() >= state.next_retry {
                state.next_retry = Instant::now() + Duration::from_secs(1);
                state.refresh();
                state.attempts = state.attempts.saturating_sub(1);
            }
            if state.pending.is_empty() && state.attempts == 0 {
                unsafe {
                    let _ = KillTimer(Some(hwnd), TIMER);
                }
            }
        }
        WM_DPICHANGED | WM_SETTINGCHANGE => {
            state.rendered = None;
            state.refresh();
        }
        _ => return unsafe { DefWindowProcW(hwnd, message, wp, lp) },
    }
    LRESULT(0)
}
fn popup_labels(paused: bool, language: Language) -> [&'static str; 4] {
    [
        language.choose("Открыть Net Lights", "Open Net Lights"),
        language.choose("Проверить сейчас", "Check now"),
        if paused {
            language.choose("Продолжить проверки", "Resume checks")
        } else {
            language.choose("Приостановить проверки", "Pause checks")
        },
        language.choose("Выход", "Exit"),
    ]
}
fn popup(hwnd: HWND, paused: bool, language: Language) -> Option<OsEvent> {
    unsafe {
        let menu = CreatePopupMenu().ok()?;
        let result = (|| -> Result<u32> {
            for (index, label) in popup_labels(paused, language).into_iter().enumerate() {
                if index == 3 {
                    AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null())?;
                }
                let label: Vec<u16> = label.encode_utf16().chain(Some(0)).collect();
                AppendMenuW(menu, MF_STRING, index + 1, PCWSTR(label.as_ptr()))?;
            }
            let mut point = POINT::default();
            GetCursorPos(&mut point)?;
            let _ = SetForegroundWindow(hwnd);
            Ok(TrackPopupMenu(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                point.x,
                point.y,
                None,
                hwnd,
                None,
            )
            .0 as u32)
        })();
        let _ = DestroyMenu(menu);
        let _ = PostMessageW(Some(hwnd), WM_NULL, WPARAM(0), LPARAM(0));
        match result.ok()? {
            1 => Some(OsEvent::Show),
            2 => Some(OsEvent::CheckNow),
            3 => Some(OsEvent::TogglePause),
            4 => Some(OsEvent::Exit),
            _ => None,
        }
    }
}

fn color(state: Availability, paused: bool) -> [u8; 3] {
    if paused {
        return [74, 144, 196];
    }
    match state {
        Availability::Online => [0, 220, 92],
        Availability::Limited => [228, 178, 12],
        Availability::Offline => [232, 64, 64],
        Availability::Unknown => [188, 190, 192],
    }
}
fn split_pixels(size: i32, left: Availability, right: Availability, paused: bool) -> Vec<u8> {
    let mut pixels = vec![0; (size * size * 4) as usize];
    let center = size as f32 / 2.;
    let radius = center - (size as f32 * 0.03).max(1.);
    let gap = if size < 24 { 2. } else { 3. };
    let a = color(left, paused);
    let b = color(right, paused);
    for y in 0..size {
        for x in 0..size {
            let mut sums = [0u32; 4];
            for sy in 0..4 {
                for sx in 0..4 {
                    let px = x as f32 + (sx as f32 + 0.5) / 4.;
                    let py = y as f32 + (sy as f32 + 0.5) / 4.;
                    if (px - center).powi(2) + (py - center).powi(2) <= radius * radius
                        && (px - center).abs() >= gap / 2.
                    {
                        let rgb = if px < center { a } else { b };
                        sums[0] += rgb[2] as u32;
                        sums[1] += rgb[1] as u32;
                        sums[2] += rgb[0] as u32;
                        sums[3] += 255;
                    }
                }
            }
            let offset = ((y * size + x) * 4) as usize;
            for c in 0..4 {
                pixels[offset + c] = (sums[c] / 16) as u8;
            }
        }
    }
    pixels
}
fn split_icon(size: i32, a: Availability, b: Availability, paused: bool) -> Result<OwnedIcon> {
    icon_from_pixels(size, &split_pixels(size, a, b, paused))
}
struct OwnedBitmap(HBITMAP);
impl Drop for OwnedBitmap {
    fn drop(&mut self) {
        unsafe {
            let _ = DeleteObject(HGDIOBJ(self.0.0));
        }
    }
}
fn dib(size: i32) -> Result<(OwnedBitmap, *mut u8)> {
    let mut info = BITMAPINFO::default();
    info.bmiHeader.biSize = size_of::<BITMAPINFOHEADER>() as u32;
    info.bmiHeader.biWidth = size;
    info.bmiHeader.biHeight = -size;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB.0;
    let mut bits = std::ptr::null_mut();
    let bitmap =
        OwnedBitmap(unsafe { CreateDIBSection(None, &info, DIB_RGB_COLORS, &mut bits, None, 0)? });
    anyhow::ensure!(!bits.is_null(), "Windows didn't allocate icon pixels");
    Ok((bitmap, bits.cast()))
}
fn icon_from_pixels(size: i32, pixels: &[u8]) -> Result<OwnedIcon> {
    anyhow::ensure!(
        pixels.len() == (size * size * 4) as usize,
        "Invalid icon raster"
    );
    let (bitmap, bits) = dib(size)?;
    unsafe {
        std::ptr::copy_nonoverlapping(pixels.as_ptr(), bits, pixels.len());
    }
    let mask_data = vec![0u8; (((size + 15) / 16) * 2 * size) as usize];
    let mask =
        OwnedBitmap(unsafe { CreateBitmap(size, size, 1, 1, Some(mask_data.as_ptr().cast())) });
    anyhow::ensure!(!mask.0.is_invalid(), "Couldn't allocate the icon mask");
    Ok(OwnedIcon(unsafe {
        CreateIconIndirect(&ICONINFO {
            fIcon: true.into(),
            hbmMask: mask.0,
            hbmColor: bitmap.0,
            ..Default::default()
        })?
    }))
}
struct OwnedDc(HDC);
impl Drop for OwnedDc {
    fn drop(&mut self) {
        unsafe {
            let _ = DeleteDC(self.0);
        }
    }
}
struct OwnedFont(HFONT);
impl Drop for OwnedFont {
    fn drop(&mut self) {
        unsafe {
            let _ = DeleteObject(HGDIOBJ(self.0.0));
        }
    }
}
fn country_font(height: i32) -> Result<OwnedFont> {
    let font = OwnedFont(unsafe {
        CreateFontW(
            -height,
            0,
            0,
            0,
            700,
            0,
            0,
            0,
            DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS,
            ANTIALIASED_QUALITY,
            0,
            w!("Consolas"),
        )
    });
    anyhow::ensure!(
        !font.0.is_invalid(),
        "Couldn't allocate the country icon font"
    );
    Ok(font)
}
fn country_glyph_mask(high: i32, inset: i32, code: &str, scale: i32) -> Result<Vec<u8>> {
    let available = high - inset * 2;
    anyhow::ensure!(available > 0, "Country icon has no drawable text area");
    // Font-cell ascent/descent and advance width aren't visible glyph bounds.
    // Keep the font's natural proportions, then uniformly fit its visible ink.
    let canvas = high * 2;
    let (bitmap, bits) = dib(canvas)?;
    let dc = OwnedDc(unsafe { CreateCompatibleDC(None) });
    anyhow::ensure!(
        !dc.0.is_invalid(),
        "Couldn't allocate the icon drawing context"
    );
    let previous = unsafe { SelectObject(dc.0, HGDIOBJ(bitmap.0.0)) };
    anyhow::ensure!(!previous.is_invalid(), "Couldn't select the icon bitmap");
    let draw = (|| -> Result<()> {
        let font = country_font(available * 3 / 2)?;
        let previous_font = unsafe { SelectObject(dc.0, HGDIOBJ(font.0.0)) };
        anyhow::ensure!(
            !previous_font.is_invalid(),
            "Couldn't select the country icon font"
        );
        unsafe {
            std::ptr::write_bytes(bits, 0, (canvas * canvas * 4) as usize);
            SetTextColor(dc.0, COLORREF(0x00ffffff));
            SetBkMode(dc.0, TRANSPARENT);
            let mut text: Vec<u16> = code.encode_utf16().collect();
            let mut rect = RECT {
                left: 0,
                top: 0,
                right: canvas,
                bottom: canvas,
            };
            let drawn = DrawTextW(
                dc.0,
                &mut text,
                &mut rect,
                DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_NOCLIP,
            );
            let _ = GdiFlush();
            SelectObject(dc.0, previous_font);
            anyhow::ensure!(drawn > 0, "Couldn't draw the country icon");
        }
        Ok(())
    })();
    unsafe {
        SelectObject(dc.0, previous);
    }
    draw?;
    let source = unsafe { std::slice::from_raw_parts(bits, (canvas * canvas * 4) as usize) };
    let (mut left, mut top, mut right, mut bottom) = (canvas, canvas, -1, -1);
    for y in 0..canvas {
        for x in 0..canvas {
            let offset = ((y * canvas + x) * 4) as usize;
            if source[offset..offset + 3]
                .iter()
                .any(|&channel| channel != 0)
            {
                left = left.min(x);
                top = top.min(y);
                right = right.max(x);
                bottom = bottom.max(y);
            }
        }
    }
    anyhow::ensure!(
        right >= left && bottom >= top,
        "Country code has no visible glyphs"
    );
    let width = right - left + 1;
    let height = bottom - top + 1;
    let percent = match scale {
        0 => 77,
        2 => 100,
        _ => 90,
    };
    let target = (available * percent / 100).max(1);
    let fitted_width = (width * target / width.max(height)).max(1);
    let fitted_height = (height * target / width.max(height)).max(1);
    let origin_x = (high - fitted_width) / 2;
    let origin_y = (high - fitted_height) / 2;
    let mut alpha = vec![0u8; (high * high) as usize];
    let sample = |x: i32, y: i32| {
        let offset =
            (((top + y.clamp(0, height - 1)) * canvas + left + x.clamp(0, width - 1)) * 4) as usize;
        source[offset]
            .max(source[offset + 1])
            .max(source[offset + 2]) as f32
    };
    for y in 0..fitted_height {
        for x in 0..fitted_width {
            let sx = (x as f32 + 0.5) * width as f32 / fitted_width as f32 - 0.5;
            let sy = (y as f32 + 0.5) * height as f32 / fitted_height as f32 - 0.5;
            let (x0, y0) = (sx.floor() as i32, sy.floor() as i32);
            let (fx, fy) = (sx - sx.floor(), sy - sy.floor());
            let upper = sample(x0, y0) * (1.0 - fx) + sample(x0 + 1, y0) * fx;
            let lower = sample(x0, y0 + 1) * (1.0 - fx) + sample(x0 + 1, y0 + 1) * fx;
            alpha[((origin_y + y) * high + origin_x + x) as usize] =
                (upper * (1.0 - fy) + lower * fy).round() as u8;
        }
    }
    Ok(alpha)
}
fn country_icon(size: i32, code: &str, fresh: bool, scale: i32) -> Result<OwnedIcon> {
    icon_from_pixels(size, &country_pixels(size, code, fresh, scale)?)
}
fn country_pixels(size: i32, code: &str, fresh: bool, scale: i32) -> Result<Vec<u8>> {
    let high = size * 4;
    let radius = (size / 20).max(1) * 4;
    let inset = radius + 4;
    let alpha = country_glyph_mask(high, inset, code, scale)?;
    let mut raster = vec![0u8; (high * high * 4) as usize];
    for y in 0..high {
        for x in 0..high {
            let index = (y * high + x) as usize;
            let fill = alpha[index] as u32;
            let mut stroke = fill;
            for sy in (y - radius).max(0)..=(y + radius).min(high - 1) {
                for sx in (x - radius).max(0)..=(x + radius).min(high - 1) {
                    if (sx - x).pow(2) + (sy - y).pow(2) <= radius * radius {
                        stroke = stroke.max(alpha[(sy * high + sx) as usize] as u32);
                    }
                }
            }
            let under = stroke * (255 - fill) / 255;
            let a = fill + under;
            for (channel, dark) in [40u32, 36, 32].into_iter().enumerate() {
                raster[index * 4 + channel] = ((252 * fill + dark * under) / 255) as u8;
            }
            raster[index * 4 + 3] = a as u8;
        }
    }
    let mut pixels = vec![0u8; (size * size * 4) as usize];
    for y in 0..size {
        for x in 0..size {
            for channel in 0..4 {
                let mut sum = 0u32;
                for sy in 0..4 {
                    for sx in 0..4 {
                        sum += raster[(((y * 4 + sy) * high + x * 4 + sx) * 4) as usize + channel]
                            as u32;
                    }
                }
                let value = sum / 16;
                pixels[((y * size + x) * 4) as usize + channel] = if fresh {
                    value as u8
                } else {
                    (value * 55 / 100) as u8
                };
            }
        }
    }
    Ok(pixels)
}

#[cfg(test)]
mod tests {
    use super::*;
    // GetGuiResources observes the whole test process. Keep native raster tests
    // apart even when a developer runs cargo test with its default parallelism.
    static GDI_TEST_SERIAL: Mutex<()> = Mutex::new(());
    #[test]
    fn tray_language_changes_all_tooltips_and_native_menu_labels() {
        let ru = TrayState {
            provider: Availability::Online,
            world: Availability::Offline,
            country: "DE".into(),
            country_fresh: true,
            ..Default::default()
        };
        let mut en = TrayState {
            language: Language::En,
            ..ru.clone()
        };
        assert_ne!(
            ru, en,
            "A language change must invalidate the rendered tray state"
        );
        assert!(primary_tip(&ru).contains("Провайдер: Доступно"));
        assert!(primary_tip(&en).contains("Provider: Online"));
        assert!(primary_tip(&en).contains("World: No response"));
        assert!(country_tip(&ru, "DE").contains("Германия"));
        assert!(country_tip(&en, "DE").contains("Country: Germany"));
        en.country_fresh = false;
        assert!(country_tip(&en, "DE").contains("Last country"));
        assert!(country_tip(&en, "DE").contains("No recent confirmation"));
        en.paused = true;
        assert!(primary_tip(&en).contains("Checks paused"));
        assert_eq!(
            popup_labels(false, Language::En),
            ["Open Net Lights", "Check now", "Pause checks", "Exit"]
        );
        assert_eq!(popup_labels(true, Language::En)[2], "Resume checks");
        assert_eq!(popup_labels(false, Language::Ru)[3], "Выход");
    }
    #[test]
    fn country_codes_keep_natural_wide_proportions_and_are_centered_at_every_dpi() {
        let _serial = GDI_TEST_SERIAL.lock().unwrap_or_else(|e| e.into_inner());
        for size in [16, 20, 24, 32, 48, 64] {
            let high = size * 4;
            let inset = (size / 20).max(1) * 4 + 4;
            for code in ["DE", "RU", "MM"] {
                let mask = country_glyph_mask(high, inset, code, 1).unwrap();
                let ink: Vec<_> = mask
                    .iter()
                    .enumerate()
                    .filter(|(_, v)| **v > 0)
                    .map(|(i, _)| (i as i32 % high, i as i32 / high))
                    .collect();
                let left = ink.iter().map(|p| p.0).min().unwrap();
                let right = ink.iter().map(|p| p.0).max().unwrap();
                let top = ink.iter().map(|p| p.1).min().unwrap();
                let bottom = ink.iter().map(|p| p.1).max().unwrap();
                assert!(
                    (right - left + 1) * 4 >= (bottom - top + 1) * 5,
                    "{code} is horizontally condensed at {size}px"
                );
                assert!(
                    (left + right + 1 - high).abs() <= 2,
                    "{code} is off-centre horizontally"
                );
                assert!(
                    (top + bottom + 1 - high).abs() <= 2,
                    "{code} is off-centre vertically"
                );
            }
        }
    }
    #[test]
    fn country_glyphs_and_outlines_fit_every_icon_size_with_three_distinct_scales() {
        let _serial = GDI_TEST_SERIAL.lock().unwrap_or_else(|e| e.into_inner());
        for size in [16, 20, 24, 32, 48, 64] {
            let high = size * 4;
            let inset = (size / 20).max(1) * 4 + 4;
            for code in netlights_core::countries::COUNTRIES
                .iter()
                .map(|row| row.0)
                .chain(["??"])
            {
                let mask = country_glyph_mask(high, inset, code, 2).unwrap();
                assert!(
                    mask.iter().any(|&alpha| alpha > 0),
                    "{code} has no visible glyphs"
                );
                for y in 0..high {
                    for x in 0..high {
                        if x < inset || y < inset || x >= high - inset || y >= high - inset {
                            assert_eq!(
                                mask[(y * high + x) as usize],
                                0,
                                "{code} exceeded the outline reserve at size {size}"
                            );
                        }
                    }
                }
            }
            let mut previous_area = 0;
            for scale in 0..=2 {
                let de = country_pixels(size, "DE", true, scale).unwrap();
                let area = de
                    .as_chunks::<4>()
                    .0
                    .iter()
                    .filter(|pixel| pixel[3] >= 32)
                    .count();
                assert!(
                    area > previous_area,
                    "The three visible choices collapsed at size {size}, scale {scale}"
                );
                previous_area = area;
                if scale == 1 {
                    let occupied: Vec<_> = de
                        .as_chunks::<4>()
                        .0
                        .iter()
                        .enumerate()
                        .filter(|(_, p)| p[3] >= 32)
                        .map(|(i, _)| (i as i32 % size, i as i32 / size))
                        .collect();
                    let width = occupied.iter().map(|p| p.0).max().unwrap()
                        - occupied.iter().map(|p| p.0).min().unwrap()
                        + 1;
                    assert!(
                        width * 10 >= (size - 2) * 8,
                        "Country code doesn't use the available width at size {size}"
                    );
                    let stale = country_pixels(size, "DE", false, scale).unwrap();
                    assert!(
                        stale
                            .iter()
                            .zip(&de)
                            .all(|(a, b)| *a == (*b as u32 * 55 / 100) as u8)
                    );
                }
                for code in ["MM", "RU", "??"] {
                    let pixels = country_pixels(size, code, true, scale).unwrap();
                    assert!(pixels.as_chunks::<4>().0.iter().any(|pixel| pixel[3] > 0));
                    for y in 0..size {
                        for x in 0..size {
                            if x == 0 || y == 0 || x == size - 1 || y == size - 1 {
                                assert_eq!(
                                    pixels[((y * size + x) * 4 + 3) as usize],
                                    0,
                                    "{code} glyph/outline touches the edge at size {size}, scale {scale}, ({x},{y})"
                                );
                            }
                        }
                    }
                }
            }
        }
    }
    #[test]
    fn failed_network_cancellation_retains_only_an_inert_callback_context() {
        let context = Box::new(NetworkContext {
            destination: Mutex::new(Some(123)),
            posted: AtomicBool::new(false),
        });
        let pointer = retire_network(Some(context), || false)
            .expect("failure must preserve callback lifetime");
        // Simulate a late OS callback. It must neither touch an HWND nor dereference freed memory.
        unsafe {
            network_changed(pointer.cast(), std::ptr::null(), MIB_NOTIFICATION_TYPE(0));
        }
        assert!(unsafe { &*pointer }.destination.lock().unwrap().is_none());
        assert!(!unsafe { &*pointer }.posted.load(Ordering::Acquire));
        // No real registration exists in this fixture, so its retained tombstone can be reclaimed.
        unsafe {
            drop(Box::from_raw(pointer));
        }
        assert!(
            retire_network(
                Some(Box::new(NetworkContext {
                    destination: Mutex::new(None),
                    posted: AtomicBool::new(false)
                })),
                || true
            )
            .is_none()
        );
    }
    #[test]
    #[ignore = "Requires an interactive Windows shell and briefly creates two owned tray icons"]
    fn synthetic_taskbar_created_restores_both_owned_icons() {
        let _serial = GDI_TEST_SERIAL.lock().unwrap_or_else(|e| e.into_inner());
        use windows::Win32::UI::Shell::{NOTIFYICONIDENTIFIER, Shell_NotifyIconGetRect};
        let (sender, _receiver) = mpsc::sync_channel(16);
        let host = TrayHost::new(sender).unwrap();
        host.update(TrayState {
            provider: Availability::Online,
            world: Availability::Limited,
            country: "DE".into(),
            country_fresh: true,
            detection_enabled: true,
            country_icon_enabled: true,
            ..Default::default()
        });
        let deadline = Instant::now() + Duration::from_secs(4);
        while !host.ready() && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(20));
        }
        assert!(host.ready(), "Both enabled tray icons must be ready");
        let hwnd = HWND(host.shared.hwnd.load(Ordering::Acquire) as _);
        let visible = |id| unsafe {
            Shell_NotifyIconGetRect(&NOTIFYICONIDENTIFIER {
                cbSize: size_of::<NOTIFYICONIDENTIFIER>() as u32,
                hWnd: hwnd,
                uID: id,
                ..Default::default()
            })
            .is_ok()
        };
        assert!(visible(MAIN_ICON));
        assert!(visible(COUNTRY_ICON));
        unsafe {
            PostMessageW(
                Some(hwnd),
                RegisterWindowMessageW(w!("TaskbarCreated")),
                WPARAM(0),
                LPARAM(0),
            )
            .unwrap();
        }
        thread::sleep(Duration::from_millis(200));
        assert!(host.ready());
        assert!(visible(MAIN_ICON));
        assert!(visible(COUNTRY_ICON));
        drop(host);
        assert!(
            !unsafe { IsWindow(Some(hwnd)) }.as_bool(),
            "The owned tray HWND must be destroyed on drop"
        );
    }
    #[test]
    fn primary_icon_has_transparent_gap_and_distinct_halves() {
        let pixels = split_pixels(32, Availability::Online, Availability::Offline, false);
        let pixel = |x: usize, y: usize| &pixels[(y * 32 + x) * 4..(y * 32 + x) * 4 + 4];
        assert_eq!(pixel(6, 16), &[92, 220, 0, 255]);
        assert_eq!(pixel(25, 16), &[64, 64, 232, 255]);
        assert_eq!(pixel(15, 16)[3], 0);
        assert_eq!(pixel(16, 16)[3], 0);
        assert_eq!(pixel(0, 0)[3], 0);
        assert!(
            pixels
                .as_chunks::<4>()
                .0
                .iter()
                .all(|p| p[..3].iter().all(|c| *c <= p[3]))
        );
    }
    #[test]
    fn event_backpressure_is_bounded_and_preserves_power_gap() {
        let mut events = VecDeque::new();
        for _ in 0..1000 {
            enqueue(&mut events, OsEvent::Suspend);
            enqueue(&mut events, OsEvent::Resume);
        }
        assert!(events.len() <= MAX_PENDING);
        assert_eq!(events.front(), Some(&OsEvent::Suspend));
        assert_eq!(events.back(), Some(&OsEvent::Resume));
        for _ in 0..1000 {
            enqueue(&mut events, OsEvent::NetworkChanged);
        }
        assert!(events.len() <= MAX_PENDING);
        enqueue(&mut events, OsEvent::Exit);
        enqueue(&mut events, OsEvent::CheckNow);
        assert_eq!(events.back(), Some(&OsEvent::Exit));
    }
    #[test]
    fn country_codes_cannot_inject_tooltips_or_large_glyphs() {
        for value in ["", "USA", "DE\nExit", "日本"] {
            assert_eq!(country_code(value), "??");
        }
        assert_eq!(country_code("de"), "DE");
    }
    #[test]
    fn icon_rasters_create_and_release_native_resources() {
        let _serial = GDI_TEST_SERIAL.lock().unwrap_or_else(|e| e.into_inner());
        use windows::Win32::System::Threading::{
            GR_GDIOBJECTS, GR_USEROBJECTS, GetCurrentProcess, GetGuiResources,
        };
        drop(country_icon(16, "DE", true, 1).unwrap());
        let process = unsafe { GetCurrentProcess() };
        let gdi_before = unsafe { GetGuiResources(process, GR_GDIOBJECTS) };
        let user_before = unsafe { GetGuiResources(process, GR_USEROBJECTS) };
        for _ in 0..24 {
            let _ = split_icon(16, Availability::Online, Availability::Offline, false).unwrap();
            let _ = country_icon(16, "DE", true, 1).unwrap();
        }
        unsafe {
            let _ = GdiFlush();
        }
        assert!(
            unsafe { GetGuiResources(process, GR_GDIOBJECTS) } <= gdi_before + 1,
            "Icon rasterization leaked GDI objects"
        );
        assert!(
            unsafe { GetGuiResources(process, GR_USEROBJECTS) } <= user_before + 1,
            "Icon rasterization leaked USER objects"
        );
    }
}
