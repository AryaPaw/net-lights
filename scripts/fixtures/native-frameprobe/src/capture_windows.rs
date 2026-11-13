use super::pipe::{Signals, Watch};
use super::*;
use std::io::Write;
use std::sync::{Arc, Mutex, atomic::Ordering};
use std::time::{Duration, Instant};
use windows::Foundation::TypedEventHandler;
use windows::Graphics::Capture::{
    Direct3D11CaptureFrame, Direct3D11CaptureFramePool, GraphicsCaptureItem, GraphicsCaptureSession,
};
use windows::Graphics::DirectX::{Direct3D11::IDirect3DDevice, DirectXPixelFormat};
use windows::Win32::Foundation::{HMODULE, HWND, POINT, RECT};
use windows::Win32::Graphics::{
    Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP},
    Direct3D11::*,
    Dwm::{DWMWA_EXTENDED_FRAME_BOUNDS, DwmGetWindowAttribute},
    Dxgi::{
        Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC},
        IDXGIDevice,
    },
    Gdi::ClientToScreen,
};
use windows::Win32::Storage::FileSystem::{FILE_TYPE_PIPE, GetFileType};
use windows::Win32::System::{
    Console::{GetStdHandle, STD_INPUT_HANDLE, STD_OUTPUT_HANDLE},
    Performance::{QueryPerformanceCounter, QueryPerformanceFrequency},
    WinRT::{
        Direct3D11::{CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess},
        Graphics::Capture::IGraphicsCaptureItemInterop,
        RO_INIT_MULTITHREADED, RoInitialize, RoUninitialize,
    },
};
use windows::Win32::UI::{
    HiDpi::{
        DPI_AWARENESS_CONTEXT, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
        SetThreadDpiAwarenessContext,
    },
    WindowsAndMessaging::{
        GetClientRect, GetForegroundWindow, GetWindowRect, GetWindowThreadProcessId,
        IsWindowVisible,
    },
};
use windows::core::{IInspectable, Interface, factory};
type Result<T> = std::result::Result<T, Failure>;
fn checked<T>(value: windows::core::Result<T>, code: &'static str) -> Result<T> {
    value.map_err(|error| Failure(code, error.code().0))
}
fn geometry(spec: Spec) -> Result<Geometry> {
    // FrameArrived runs on a WGC worker, not on the capture setup thread.
    let _dpi = Dpi::enter()?;
    let hwnd = HWND(spec.hwnd as *mut _);
    let mut pid = 0;
    unsafe {
        GetWindowThreadProcessId(hwnd, Some(&mut pid));
    }
    if pid != spec.pid
        || unsafe { GetForegroundWindow() } != hwnd
        || !unsafe { IsWindowVisible(hwnd) }.as_bool()
    {
        return Err(Failure("window_owner_or_foreground", 0));
    }
    let mut client = RECT::default();
    let mut origin = POINT::default();
    let mut bounds = RECT::default();
    let mut raw = RECT::default();
    checked(unsafe { GetWindowRect(hwnd, &mut raw) }, "window_rect")?;
    checked(unsafe { GetClientRect(hwnd, &mut client) }, "client_rect")?;
    checked(
        unsafe { ClientToScreen(hwnd, &mut origin) }.ok(),
        "client_origin",
    )?;
    checked(
        unsafe {
            DwmGetWindowAttribute(
                hwnd,
                DWMWA_EXTENDED_FRAME_BOUNDS,
                &mut bounds as *mut _ as _,
                std::mem::size_of::<RECT>() as u32,
            )
        },
        "window_bounds",
    )?;
    let x = origin
        .x
        .checked_add(client.right)
        .ok_or(Failure("client_overflow", 0))?;
    let y = origin
        .y
        .checked_add(client.bottom)
        .ok_or(Failure("client_overflow", 0))?;
    Ok(Geometry {
        client: [origin.x, origin.y, x, y],
        raw: [raw.left, raw.top, raw.right, raw.bottom],
        extended: [bounds.left, bounds.top, bounds.right, bounds.bottom],
    })
}
struct Apartment;
impl Drop for Apartment {
    fn drop(&mut self) {
        unsafe { RoUninitialize() };
    }
}
struct Dpi(DPI_AWARENESS_CONTEXT);
impl Dpi {
    fn enter() -> Result<Self> {
        let previous =
            unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
        if previous.0.is_null() {
            return Err(Failure("dpi_context", 0));
        }
        Ok(Self(previous))
    }
}
impl Drop for Dpi {
    fn drop(&mut self) {
        unsafe { SetThreadDpiAwarenessContext(self.0) };
    }
}
struct FrameGuard(Direct3D11CaptureFrame);
impl Drop for FrameGuard {
    fn drop(&mut self) {
        let _ = self.0.Close();
    }
}
struct Capture {
    pool: Direct3D11CaptureFramePool,
    session: GraphicsCaptureSession,
    token: Option<i64>,
    signals: Arc<Signals>,
    closed: bool,
}
impl Capture {
    fn close(&mut self) -> Result<()> {
        if self.closed {
            return Ok(());
        }
        self.closed = true;
        self.signals.stop.store(true, Ordering::Release);
        let handler = self
            .token
            .take()
            .map(|token| self.pool.RemoveFrameArrived(token));
        let session = self.session.Close();
        let pool = self.pool.Close();
        if let Some(result) = handler {
            checked(result, "handler_close")?;
        }
        checked(session, "session_close")?;
        checked(pool, "pool_close")
    }
}
impl Drop for Capture {
    fn drop(&mut self) {
        let _ = self.close();
    }
}
struct State {
    queue: Queue,
    context: ID3D11DeviceContext,
    staging: ID3D11Texture2D,
    spec: Spec,
    initial: Geometry,
    layout: Option<FrameLayout>,
    frequency: i64,
}
impl State {
    fn receive(&mut self, pool: &Direct3D11CaptureFramePool) -> Result<()> {
        let geometry = geometry(self.spec)?;
        if geometry != self.initial {
            return Err(Failure("geometry_changed", 0));
        }
        let frame = FrameGuard(checked(pool.TryGetNextFrame(), "next_frame")?);
        let duration = checked(frame.0.SystemRelativeTime(), "frame_timestamp")?.Duration;
        let mut received_qpc = 0;
        checked(
            unsafe { QueryPerformanceCounter(&mut received_qpc) },
            "frame_qpc",
        )?;
        let qpc = captured_qpc(duration, self.frequency, received_qpc, self.queue.last_qpc)?;
        let size = checked(frame.0.ContentSize(), "frame_size")?;
        let layout = bind_frame_layout(
            self.spec.roi,
            self.initial,
            geometry,
            (size.Width, size.Height),
            self.layout,
        )
        .ok_or(Failure("frame_layout_unmatched", 0))?;
        self.layout = Some(layout);
        let access: IDirect3DDxgiInterfaceAccess = checked(
            checked(frame.0.Surface(), "surface")?.cast(),
            "surface_interface",
        )?;
        let source: ID3D11Texture2D = checked(unsafe { access.GetInterface() }, "source_texture")?;
        let mut desc = D3D11_TEXTURE2D_DESC::default();
        unsafe { source.GetDesc(&mut desc) };
        if desc.Width < layout.local[2]
            || desc.Height < layout.local[3]
            || desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM
        {
            return Err(Failure("source_layout", 0));
        }
        let region = D3D11_BOX {
            left: layout.local[0],
            top: layout.local[1],
            front: 0,
            right: layout.local[2],
            bottom: layout.local[3],
            back: 1,
        };
        unsafe {
            self.context.CopySubresourceRegion(
                &self.staging,
                0,
                0,
                0,
                0,
                &source,
                0,
                Some(&region),
            );
            self.context.Flush();
        }
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        let deadline = Instant::now() + Duration::from_millis(100);
        loop {
            match unsafe {
                self.context.Map(
                    &self.staging,
                    0,
                    D3D11_MAP_READ,
                    D3D11_MAP_FLAG_DO_NOT_WAIT.0 as u32,
                    Some(&mut mapped),
                )
            } {
                Ok(()) => break,
                Err(error) if error.code().0 as u32 == 0x887A000A && Instant::now() < deadline => {
                    std::thread::sleep(Duration::from_millis(1))
                }
                Err(error) => return Err(Failure("staging_map", error.code().0)),
            }
        }
        let (width, height) = dimensions(self.spec.roi).ok_or(Failure("roi_size", 0))?;
        let row = width as usize * 4;
        let mut pixels = Vec::with_capacity(row * height as usize);
        if mapped.pData.is_null() || (mapped.RowPitch as usize) < row {
            unsafe { self.context.Unmap(&self.staging, 0) };
            return Err(Failure("staging_layout", 0));
        }
        for y in 0..height as usize {
            pixels.extend_from_slice(unsafe {
                std::slice::from_raw_parts(
                    (mapped.pData as *const u8).add(y * mapped.RowPitch as usize),
                    row,
                )
            });
        }
        unsafe { self.context.Unmap(&self.staging, 0) };
        self.queue.push(Frame { qpc, pixels })
    }
}
fn capture(spec: Spec, signals: Arc<Signals>, output: &mut impl Write) -> Result<()> {
    let _dpi = Dpi::enter()?;
    let geometry = geometry(spec)?;
    checked(unsafe { RoInitialize(RO_INIT_MULTITHREADED) }, "winrt")?;
    let _apartment = Apartment;
    if !checked(GraphicsCaptureSession::IsSupported(), "capture_support")? {
        return Err(Failure("capture_unsupported", 0));
    }
    let interop: IGraphicsCaptureItemInterop = checked(
        factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>(),
        "capture_factory",
    )?;
    let item: GraphicsCaptureItem = checked(
        unsafe { interop.CreateForWindow(HWND(spec.hwnd as *mut _)) },
        "capture_item",
    )?;
    let size = checked(item.Size(), "item_size")?;
    let shape = (size.Width, size.Height);
    let bounds = capture_bounds(geometry.raw, geometry.extended, shape)
        .ok_or(Failure("capture_bounds_unmatched", 0))?;
    local_roi(spec.roi, geometry.client, bounds, shape)
        .ok_or(Failure("roi_or_capture_bounds", 0))?;
    let mut device = None;
    let mut context = None;
    let create = |driver,
                  device: &mut Option<ID3D11Device>,
                  context: &mut Option<ID3D11DeviceContext>| unsafe {
        D3D11CreateDevice(
            None,
            driver,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            None,
            D3D11_SDK_VERSION,
            Some(device),
            None,
            Some(context),
        )
    };
    if create(D3D_DRIVER_TYPE_HARDWARE, &mut device, &mut context).is_err() {
        checked(
            create(D3D_DRIVER_TYPE_WARP, &mut device, &mut context),
            "d3d_device",
        )?;
    }
    let device = device.ok_or(Failure("missing_device", 0))?;
    let context = context.ok_or(Failure("missing_context", 0))?;
    let dxgi: IDXGIDevice = checked(device.cast(), "dxgi_device")?;
    let direct: IDirect3DDevice = checked(
        checked(
            unsafe { CreateDirect3D11DeviceFromDXGIDevice(&dxgi) },
            "winrt_device",
        )?
        .cast(),
        "winrt_device_interface",
    )?;
    let (width, height) = dimensions(spec.roi).ok_or(Failure("roi_size", 0))?;
    let mut staging = None;
    let desc = D3D11_TEXTURE2D_DESC {
        Width: width,
        Height: height,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Usage: D3D11_USAGE_STAGING,
        CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
        ..Default::default()
    };
    checked(
        unsafe { device.CreateTexture2D(&desc, None, Some(&mut staging)) },
        "staging_texture",
    )?;
    let mut frequency = 0;
    checked(
        unsafe { QueryPerformanceFrequency(&mut frequency) },
        "qpc_frequency",
    )?;
    if frequency <= 0 {
        return Err(Failure("qpc_frequency", 0));
    }
    let state = Arc::new(Mutex::new(State {
        queue: Queue::default(),
        context,
        staging: staging.ok_or(Failure("missing_staging", 0))?,
        spec,
        initial: geometry,
        layout: None,
        frequency,
    }));
    let pool = checked(
        Direct3D11CaptureFramePool::CreateFreeThreaded(
            &direct,
            DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2,
            size,
        ),
        "frame_pool",
    )?;
    let session = match pool.CreateCaptureSession(&item) {
        Ok(session) => session,
        Err(error) => {
            let _ = pool.Close();
            return Err(Failure("capture_session", error.code().0));
        }
    };
    let mut capture = Capture {
        pool,
        session,
        token: None,
        signals: signals.clone(),
        closed: false,
    };
    checked(
        capture.session.SetIsCursorCaptureEnabled(false),
        "cursor_disabled",
    )?;
    let callback_state = state.clone();
    let callback_signals = signals.clone();
    capture.token = Some(checked(
        capture.pool.FrameArrived(
            &TypedEventHandler::<Direct3D11CaptureFramePool, IInspectable>::new(
                move |sender, _| {
                    if callback_signals.stop.load(Ordering::Acquire) {
                        return Ok(());
                    }
                    let result = callback_state
                        .lock()
                        .map_err(|_| Failure("capture_lock", 0))
                        .and_then(|mut state| {
                            if callback_signals.stop.load(Ordering::Acquire) {
                                Ok(())
                            } else {
                                state.receive(sender.as_ref().ok_or(Failure("frame_sender", 0))?)
                            }
                        });
                    if let Err(error) = result {
                        callback_signals.fail(error);
                    }
                    Ok(())
                },
            ),
        ),
        "frame_handler",
    )?);
    checked(capture.session.StartCapture(), "capture_start")?;
    writeln!(output, "{{\"kind\":\"ready\",\"frequency\":{frequency}}}")
        .map_err(|_| Failure("parent_output", 0))?;
    loop {
        if signals.stop.load(Ordering::Acquire) && !capture.closed {
            capture.close()?;
            // An in-flight readback finishes before we inspect/drain the queue.
            drop(state.lock().map_err(|_| Failure("capture_lock", 0))?);
        }
        let frame = state
            .lock()
            .map_err(|_| Failure("capture_lock", 0))?
            .queue
            .pop();
        if let Some(frame) = frame {
            let mut hex = String::with_capacity(frame.pixels.len() * 2);
            const DIGITS: &[u8; 16] = b"0123456789abcdef";
            for value in frame.pixels {
                hex.push(DIGITS[(value >> 4) as usize] as char);
                hex.push(DIGITS[(value & 15) as usize] as char);
            }
            writeln!(output,"{{\"kind\":\"frame\",\"qpc\":{},\"width\":{width},\"height\":{height},\"pixels\":\"{hex}\"}}",frame.qpc).map_err(|_|Failure("parent_output",0))?;
        } else if capture.closed {
            break;
        } else {
            std::thread::sleep(Duration::from_millis(1));
        }
    }
    if let Some(failure) = *signals
        .failure
        .lock()
        .map_err(|_| Failure("signals_lock", 0))?
    {
        return Err(failure);
    }
    let mut qpc = 0;
    checked(unsafe { QueryPerformanceCounter(&mut qpc) }, "end_qpc")?;
    writeln!(output, "{{\"kind\":\"end\",\"qpc\":{qpc}}}")
        .map_err(|_| Failure("parent_output", 0))?;
    Ok(())
}
pub fn run(spec: Spec) -> u8 {
    let handles = unsafe {
        GetStdHandle(STD_INPUT_HANDLE)
            .and_then(|input| GetStdHandle(STD_OUTPUT_HANDLE).map(|output| (input, output)))
    };
    let (input, output) = match handles {
        Ok(handles) => handles,
        Err(error) => {
            super::error("parent_handles", error.code().0);
            return 1;
        }
    };
    if unsafe { GetFileType(input) } != FILE_TYPE_PIPE
        || unsafe { GetFileType(output) } != FILE_TYPE_PIPE
    {
        super::error("parent_pipes_required", 0);
        return 1;
    }
    let signals = Arc::new(Signals::default());
    let _watch = match Watch::start(signals.clone(), input) {
        Ok(watch) => watch,
        Err(error) => {
            super::error(error.0, error.1);
            return 1;
        }
    };
    let stdout = std::io::stdout();
    let mut output = stdout.lock();
    match capture(spec, signals, &mut output) {
        Ok(()) => 0,
        Err(Failure(code, hresult)) => {
            let _ = writeln!(
                output,
                "{{\"kind\":\"error\",\"code\":\"{code}\",\"hresult\":{hresult}}}"
            );
            1
        }
    }
}
