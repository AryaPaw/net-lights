use gpui_kit::{App, Window};
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use windows::Win32::{
    Foundation::{HWND, RECT},
    Graphics::Gdi::{
        GetMonitorInfoW, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromRect, MonitorFromWindow,
    },
    UI::WindowsAndMessaging::{
        GetWindowRect, IsIconic, IsZoomed, SW_HIDE, SW_RESTORE, SW_SHOW, SWP_NOACTIVATE,
        SWP_NOZORDER, SetForegroundWindow, SetWindowPos, ShowWindow,
    },
};

fn handle(window: &Window) -> Option<HWND> {
    match HasWindowHandle::window_handle(window).ok()?.as_raw() {
        RawWindowHandle::Win32(raw) => Some(HWND(raw.hwnd.get() as *mut _)),
        _ => None,
    }
}
pub fn mica_supported() -> bool {
    static SUPPORTED: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *SUPPORTED.get_or_init(|| {
        let mut version = Default::default();
        // Match GPUI's backdrop capability check; GetVersionEx depends on the manifest.
        unsafe {
            windows::Wdk::System::SystemServices::RtlGetVersion(&mut version).is_ok()
                && version.dwBuildNumber >= 22621
        }
    })
}
/// Persistent geometry uses physical outer-window coordinates, as the legacy application did.
pub fn outer_rect(window: &Window) -> Option<[i32; 4]> {
    let hwnd = handle(window)?;
    unsafe {
        if IsIconic(hwnd).as_bool() || IsZoomed(hwnd).as_bool() {
            return None;
        }
        let mut rect = RECT::default();
        GetWindowRect(hwnd, &mut rect).ok()?;
        Some([
            rect.left,
            rect.top,
            rect.right - rect.left,
            rect.bottom - rect.top,
        ])
    }
}
pub fn restore_geometry(window: &Window, left: i32, top: i32, width: i32, height: i32) -> bool {
    let Some(hwnd) = handle(window) else {
        return false;
    };
    restore_native_geometry(hwnd, left, top, width, height)
}
pub fn restore_after_creation(
    window: &Window,
    left: i32,
    top: i32,
    width: i32,
    height: i32,
    cx: &App,
) {
    let owner = window.window_handle();
    // Like GPUI's Windows resize implementation, release the App borrow before
    // SetWindowPos dispatches synchronous move/resize callbacks into GPUI.
    cx.spawn(async move |cx| {
        let native = owner
            .update(cx, |_, window, _| handle(window))
            .ok()
            .flatten();
        if let Some(hwnd) = native {
            restore_native_geometry(hwnd, left, top, width, height);
        }
    })
    .detach();
}
fn restore_native_geometry(hwnd: HWND, left: i32, top: i32, width: i32, height: i32) -> bool {
    unsafe {
        if IsZoomed(hwnd).as_bool() || IsIconic(hwnd).as_bool() {
            let _ = ShowWindow(hwnd, SW_RESTORE);
        }
        let centered = left == i32::MIN || top == i32::MIN;
        let saved = RECT {
            left,
            top,
            right: left.saturating_add(width),
            bottom: top.saturating_add(height),
        };
        let monitor = if centered {
            MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
        } else {
            MonitorFromRect(&saved, MONITOR_DEFAULTTONEAREST)
        };
        let mut info = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        if !GetMonitorInfoW(monitor, &mut info).as_bool() {
            return false;
        }
        let work = info.rcWork;
        let w = width.min(work.right - work.left);
        let h = height.min(work.bottom - work.top);
        let x = if centered {
            work.left + (work.right - work.left - w) / 2
        } else {
            left.clamp(work.left, work.right - w)
        };
        let y = if centered {
            work.top + (work.bottom - work.top - h) / 2
        } else {
            top.clamp(work.top, work.bottom - h)
        };
        SetWindowPos(hwnd, None, x, y, w, h, SWP_NOACTIVATE | SWP_NOZORDER).is_ok()
    }
}
pub fn reset_size(window: &Window) -> bool {
    let Some(hwnd) = handle(window) else {
        return false;
    };
    let mut rect = RECT::default();
    unsafe {
        if GetWindowRect(hwnd, &mut rect).is_err() {
            return false;
        }
    }
    restore_geometry(window, rect.left, rect.top, 1450, 1300)
}
pub fn hide(window: &Window) {
    if let Some(hwnd) = handle(window) {
        unsafe {
            let _ = ShowWindow(hwnd, SW_HIDE);
        }
    }
}
pub fn show(window: &Window) {
    if let Some(hwnd) = handle(window) {
        unsafe {
            let mode = if IsIconic(hwnd).as_bool() {
                SW_RESTORE
            } else {
                SW_SHOW
            };
            let _ = ShowWindow(hwnd, mode);
            let _ = SetForegroundWindow(hwnd);
        }
    }
}
