use gpui_kit::{App, Window};
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use windows::Win32::{
    Foundation::{
        ERROR_SUCCESS, GetLastError, GlobalFree, HANDLE, HGLOBAL, HWND, RECT, SetLastError,
    },
    Graphics::Gdi::{
        GetMonitorInfoW, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromRect, MonitorFromWindow,
    },
    System::{
        DataExchange::{CloseClipboard, EmptyClipboard, OpenClipboard, SetClipboardData},
        Memory::{GMEM_MOVEABLE, GMEM_ZEROINIT, GlobalAlloc, GlobalLock, GlobalUnlock},
        Ole::CF_UNICODETEXT,
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

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ClipboardError {
    OwnerUnavailable,
    InvalidText,
    Allocate,
    Lock,
    Unlock,
    Open,
    Empty,
    Publish,
    Close,
}
type ClipboardResult<T> = Result<T, ClipboardError>;

trait ClipboardApi {
    type Memory;
    fn allocate(&mut self, text: &[u16]) -> ClipboardResult<Self::Memory>;
    fn open(&mut self, owner: HWND) -> ClipboardResult<()>;
    fn empty(&mut self) -> ClipboardResult<()>;
    fn publish(&mut self, memory: &mut Self::Memory) -> ClipboardResult<()>;
    fn close(&mut self) -> ClipboardResult<()>;
}

struct OpenClipboardGuard<'a, A: ClipboardApi> {
    api: &'a mut A,
    open: bool,
}
impl<A: ClipboardApi> OpenClipboardGuard<'_, A> {
    fn close(mut self) -> ClipboardResult<()> {
        self.open = false;
        self.api.close()
    }
}
impl<A: ClipboardApi> Drop for OpenClipboardGuard<'_, A> {
    fn drop(&mut self) {
        if self.open {
            let _ = self.api.close();
        }
    }
}

fn copy_text_with(api: &mut impl ClipboardApi, owner: HWND, text: &str) -> ClipboardResult<()> {
    if owner.0.is_null() {
        return Err(ClipboardError::OwnerUnavailable);
    }
    if text.contains('\0') {
        return Err(ClipboardError::InvalidText);
    }
    let text: Vec<u16> = text.encode_utf16().chain(std::iter::once(0)).collect();
    // Prepare the complete allocation before EmptyClipboard can discard old data.
    let mut memory = api.allocate(&text)?;
    api.open(owner)?;
    let clipboard = OpenClipboardGuard { api, open: true };
    clipboard.api.empty()?;
    clipboard.api.publish(&mut memory)?;
    clipboard.close()
}

struct ClipboardMemory {
    owned: Option<HGLOBAL>,
    locked: bool,
}
impl Drop for ClipboardMemory {
    fn drop(&mut self) {
        if let Some(handle) = self.owned {
            unsafe {
                if self.locked {
                    let _ = GlobalUnlock(handle);
                }
                let _ = GlobalFree(Some(handle));
            }
        }
    }
}
struct NativeClipboard;
impl ClipboardApi for NativeClipboard {
    type Memory = ClipboardMemory;
    fn allocate(&mut self, text: &[u16]) -> ClipboardResult<ClipboardMemory> {
        let bytes = std::mem::size_of_val(text);
        let handle = unsafe { GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, bytes) }
            .map_err(|_| ClipboardError::Allocate)?;
        let mut memory = ClipboardMemory {
            owned: Some(handle),
            locked: false,
        };
        let destination = unsafe { GlobalLock(handle) }.cast::<u16>();
        if destination.is_null() {
            return Err(ClipboardError::Lock);
        }
        memory.locked = true;
        unsafe {
            std::ptr::copy_nonoverlapping(text.as_ptr(), destination, text.len());
            // A fully unlocked movable allocation returns FALSE with NO_ERROR.
            SetLastError(ERROR_SUCCESS);
            let result = GlobalUnlock(handle);
            if result.is_err() && GetLastError() != ERROR_SUCCESS {
                return Err(ClipboardError::Unlock);
            }
        }
        memory.locked = false;
        Ok(memory)
    }
    fn open(&mut self, owner: HWND) -> ClipboardResult<()> {
        // A NULL owner followed by EmptyClipboard makes SetClipboardData fail.
        unsafe { OpenClipboard(Some(owner)) }.map_err(|_| ClipboardError::Open)
    }
    fn empty(&mut self) -> ClipboardResult<()> {
        unsafe { EmptyClipboard() }.map_err(|_| ClipboardError::Empty)
    }
    fn publish(&mut self, memory: &mut ClipboardMemory) -> ClipboardResult<()> {
        let handle = memory.owned.ok_or(ClipboardError::Publish)?;
        unsafe { SetClipboardData(CF_UNICODETEXT.0.into(), Some(HANDLE(handle.0))) }
            .map_err(|_| ClipboardError::Publish)?;
        // Windows owns the movable allocation only after successful publication.
        memory.owned = None;
        Ok(())
    }
    fn close(&mut self) -> ClipboardResult<()> {
        unsafe { CloseClipboard() }.map_err(|_| ClipboardError::Close)
    }
}
pub fn copy_text(window: &Window, text: &str) -> ClipboardResult<()> {
    let owner = handle(window).ok_or(ClipboardError::OwnerUnavailable)?;
    copy_text_with(&mut NativeClipboard, owner, text)
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::{cell::RefCell, rc::Rc};

    struct FakeMemory {
        log: Rc<RefCell<Vec<&'static str>>>,
        transferred: bool,
    }
    impl Drop for FakeMemory {
        fn drop(&mut self) {
            if !self.transferred {
                self.log.borrow_mut().push("free");
            }
        }
    }
    #[derive(Default)]
    struct FakeClipboard {
        failure: Option<ClipboardError>,
        log: Rc<RefCell<Vec<&'static str>>>,
        text: Vec<u16>,
    }
    impl FakeClipboard {
        fn step(&mut self, event: &'static str, error: ClipboardError) -> ClipboardResult<()> {
            self.log.borrow_mut().push(event);
            if self.failure == Some(error) {
                Err(error)
            } else {
                Ok(())
            }
        }
    }
    impl ClipboardApi for FakeClipboard {
        type Memory = FakeMemory;
        fn allocate(&mut self, text: &[u16]) -> ClipboardResult<FakeMemory> {
            self.step("allocate", ClipboardError::Allocate)?;
            self.text = text.to_vec();
            Ok(FakeMemory {
                log: self.log.clone(),
                transferred: false,
            })
        }
        fn open(&mut self, owner: HWND) -> ClipboardResult<()> {
            assert_eq!(owner.0 as usize, 23);
            self.step("open-owned", ClipboardError::Open)
        }
        fn empty(&mut self) -> ClipboardResult<()> {
            self.step("empty", ClipboardError::Empty)
        }
        fn publish(&mut self, memory: &mut FakeMemory) -> ClipboardResult<()> {
            self.step("publish", ClipboardError::Publish)?;
            memory.transferred = true;
            self.log.borrow_mut().push("transfer");
            Ok(())
        }
        fn close(&mut self) -> ClipboardResult<()> {
            self.step("close", ClipboardError::Close)
        }
    }
    fn owner() -> HWND {
        HWND(23usize as *mut _)
    }

    #[test]
    fn clipboard_publication_owns_the_window_and_transfers_terminated_utf16_once() {
        let mut api = FakeClipboard::default();
        copy_text_with(&mut api, owner(), "Net Lights 日本 🌍").unwrap();
        assert_eq!(
            api.text,
            "Net Lights 日本 🌍"
                .encode_utf16()
                .chain(Some(0))
                .collect::<Vec<_>>()
        );
        assert_eq!(
            *api.log.borrow(),
            [
                "allocate",
                "open-owned",
                "empty",
                "publish",
                "transfer",
                "close"
            ]
        );
    }
    #[test]
    fn clipboard_failures_close_only_an_open_clipboard_and_free_only_unpublished_data() {
        for (error, expected) in [
            (ClipboardError::Allocate, vec!["allocate"]),
            (ClipboardError::Open, vec!["allocate", "open-owned", "free"]),
            (
                ClipboardError::Empty,
                vec!["allocate", "open-owned", "empty", "close", "free"],
            ),
            (
                ClipboardError::Publish,
                vec![
                    "allocate",
                    "open-owned",
                    "empty",
                    "publish",
                    "close",
                    "free",
                ],
            ),
            (
                ClipboardError::Close,
                vec![
                    "allocate",
                    "open-owned",
                    "empty",
                    "publish",
                    "transfer",
                    "close",
                ],
            ),
        ] {
            let mut api = FakeClipboard {
                failure: Some(error),
                ..Default::default()
            };
            assert_eq!(
                copy_text_with(&mut api, owner(), "Net Lights 1.7.0"),
                Err(error)
            );
            assert_eq!(*api.log.borrow(), expected);
        }
    }
    #[test]
    fn invalid_clipboard_inputs_fail_before_allocating_or_opening() {
        let mut api = FakeClipboard::default();
        assert_eq!(
            copy_text_with(&mut api, HWND::default(), "version"),
            Err(ClipboardError::OwnerUnavailable)
        );
        assert_eq!(
            copy_text_with(&mut api, owner(), "version\0truncated"),
            Err(ClipboardError::InvalidText)
        );
        assert!(api.log.borrow().is_empty());
    }
}
