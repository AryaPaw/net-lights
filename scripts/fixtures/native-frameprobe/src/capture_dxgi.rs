use super::super::readback::{CAPACITY, DEADLINE, Queue};
#[path = "capture_desktop.rs"]
mod desktop_memory;
use super::stream::{self, Event};
use super::*;
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_UNKNOWN;
use windows::Win32::Graphics::Dxgi::Common::DXGI_MODE_ROTATION_IDENTITY;
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, DXGI_OUTDUPL_FRAME_INFO, IDXGIAdapter, IDXGIFactory1, IDXGIOutput1,
    IDXGIOutputDuplication, IDXGIResource,
};
use windows::Win32::Graphics::Gdi::{MONITOR_DEFAULTTONULL, MonitorFromWindow};
use windows::Win32::UI::WindowsAndMessaging::{
    CURSOR_SHOWING, CURSORINFO, GW_HWNDNEXT, GetCursorInfo, GetSystemMetrics, GetTopWindow,
    GetWindow, IDC_ARROW, IDC_HAND, LoadCursorW, SM_CXCURSOR, SM_CYCURSOR,
};

fn intersects(a: [i64; 4], b: [i64; 4]) -> bool {
    a[0] < b[2] && b[0] < a[2] && a[1] < b[3] && b[1] < a[3]
}

fn unobscured(spec: Spec, initial: Geometry) -> Result<()> {
    if geometry(spec)? != initial {
        return Err(Failure("geometry_changed", 0));
    }
    let roi = spec.roi.map(i64::from);
    let mut current = checked(unsafe { GetTopWindow(None) }, "window_stack")?;
    let mut found = false;
    for _ in 0..512 {
        if current.0 as usize == spec.hwnd {
            found = true;
            break;
        }
        if unsafe { IsWindowVisible(current) }.as_bool() {
            let mut rect = RECT::default();
            checked(unsafe { GetWindowRect(current, &mut rect) }, "overlay_rect")?;
            if intersects(
                roi,
                [rect.left, rect.top, rect.right, rect.bottom].map(i64::from),
            ) {
                return Err(Failure("roi_occluded", 0));
            }
        }
        current = checked(unsafe { GetWindow(current, GW_HWNDNEXT) }, "window_stack")?;
    }
    if !found {
        return Err(Failure("window_stack_limit", 0));
    }
    let mut cursor = CURSORINFO {
        cbSize: size_of::<CURSORINFO>() as u32,
        ..Default::default()
    };
    checked(unsafe { GetCursorInfo(&mut cursor) }, "cursor_info")?;
    if cursor.flags.0 & CURSOR_SHOWING.0 != 0 {
        let size = unsafe { GetSystemMetrics(SM_CXCURSOR).max(GetSystemMetrics(SM_CYCURSOR)) };
        if !(1..=256).contains(&size) {
            return Err(Failure("cursor_size", 0));
        }
        let arrow = checked(unsafe { LoadCursorW(None, IDC_ARROW) }, "cursor_shape")?;
        let hand = checked(unsafe { LoadCursorW(None, IDC_HAND) }, "cursor_shape")?;
        if cursor.hCursor != arrow && cursor.hCursor != hand {
            return Err(Failure("cursor_shape", 0));
        }
        let (x, y, size) = (
            i64::from(cursor.ptScreenPos.x),
            i64::from(cursor.ptScreenPos.y),
            256i64,
        );
        if intersects(roi, [x - size, y - size, x + size, y + size]) {
            return Err(Failure("cursor_over_roi", 0));
        }
    }
    Ok(())
}

struct Acquired<'a> {
    duplication: &'a IDXGIOutputDuplication,
    released: bool,
}
impl Acquired<'_> {
    fn close(&mut self) -> Result<()> {
        self.released = true;
        checked(unsafe { self.duplication.ReleaseFrame() }, "release_frame")
    }
}
impl Drop for Acquired<'_> {
    fn drop(&mut self) {
        if !self.released {
            let _ = self.close();
        }
    }
}

fn try_read_pixels(
    context: &ID3D11DeviceContext,
    staging: &ID3D11Texture2D,
    width: u32,
    height: u32,
) -> Result<Option<Vec<u8>>> {
    let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
    match unsafe {
        context.Map(
            staging,
            0,
            D3D11_MAP_READ,
            D3D11_MAP_FLAG_DO_NOT_WAIT.0 as u32,
            Some(&mut mapped),
        )
    } {
        Ok(()) => (),
        Err(error) if error.code().0 as u32 == 0x887A000A => return Ok(None),
        Err(error) => return Err(Failure("staging_map", error.code().0)),
    }
    let row = width as usize * 4;
    if mapped.pData.is_null() || (mapped.RowPitch as usize) < row {
        unsafe { context.Unmap(staging, 0) };
        return Err(Failure("staging_layout", 0));
    }
    let mut pixels = Vec::with_capacity(row * height as usize);
    for y in 0..height as usize {
        pixels.extend_from_slice(unsafe {
            std::slice::from_raw_parts(
                (mapped.pData as *const u8).add(y * mapped.RowPitch as usize),
                row,
            )
        });
    }
    unsafe { context.Unmap(staging, 0) };
    Ok(Some(pixels))
}

struct Pending {
    baseline: Option<u32>,
    qpc: i64,
    texture: ID3D11Texture2D,
}

pub(super) fn capture(spec: Spec, signals: Arc<Signals>, output: &mut impl Write) -> Result<()> {
    let capture_signals = signals.clone();
    stream::run(
        signals,
        move |emit| capture_frames(spec, capture_signals, emit),
        |event| stream::write_event(output, event),
    )
}

fn capture_frames(
    spec: Spec,
    signals: Arc<Signals>,
    emit: &mut dyn FnMut(Event) -> Result<()>,
) -> Result<()> {
    let _dpi = Dpi::enter()?;
    let initial = geometry(spec)?;
    unobscured(spec, initial)?;
    let monitor = unsafe { MonitorFromWindow(HWND(spec.hwnd as *mut _), MONITOR_DEFAULTTONULL) };
    if monitor.0.is_null() {
        return Err(Failure("window_monitor", 0));
    }
    let factory: IDXGIFactory1 = checked(unsafe { CreateDXGIFactory1() }, "dxgi_factory")?;
    let mut selected = None;
    for index in 0..32 {
        let adapter = match unsafe { factory.EnumAdapters1(index) } {
            Ok(adapter) => adapter,
            Err(error) if error.code().0 as u32 == 0x887A0002 => break,
            Err(error) => return Err(Failure("dxgi_adapter", error.code().0)),
        };
        for number in 0..32 {
            let candidate = match unsafe { adapter.EnumOutputs(number) } {
                Ok(candidate) => candidate,
                Err(error) if error.code().0 as u32 == 0x887A0002 => break,
                Err(error) => return Err(Failure("dxgi_output", error.code().0)),
            };
            let desc = checked(unsafe { candidate.GetDesc() }, "output_desc")?;
            if desc.Monitor == monitor && desc.AttachedToDesktop.as_bool() {
                selected = Some((
                    adapter,
                    checked(candidate.cast::<IDXGIOutput1>(), "output_interface")?,
                    desc,
                ));
                break;
            }
        }
        if selected.is_some() {
            break;
        }
    }
    let (adapter, desktop, desc) = selected.ok_or(Failure("output_not_found", 0))?;
    if desc.Rotation != DXGI_MODE_ROTATION_IDENTITY {
        return Err(Failure("output_rotation", 0));
    }
    let rect = desc.DesktopCoordinates;
    let bounds = [rect.left, rect.top, rect.right, rect.bottom];
    if initial.raw[0] < bounds[0]
        || initial.raw[1] < bounds[1]
        || initial.raw[2] > bounds[2]
        || initial.raw[3] > bounds[3]
    {
        return Err(Failure("window_spans_outputs", 0));
    }
    let size = (
        (i64::from(rect.right) - i64::from(rect.left)),
        (i64::from(rect.bottom) - i64::from(rect.top)),
    );
    if size.0 <= 0 || size.0 > 8192 || size.1 <= 0 || size.1 > 8192 {
        return Err(Failure("output_size", 0));
    }
    let local = local_roi(
        spec.roi,
        initial.client,
        bounds,
        (size.0 as i32, size.1 as i32),
    )
    .ok_or(Failure("roi_bounds", 0))?;
    let adapter: IDXGIAdapter = checked(adapter.cast(), "adapter_interface")?;
    let (mut device, mut context) = (None, None);
    checked(
        unsafe {
            D3D11CreateDevice(
                &adapter,
                D3D_DRIVER_TYPE_UNKNOWN,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                None,
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                Some(&mut context),
            )
        },
        "dxgi_device",
    )?;
    let device = device.ok_or(Failure("missing_device", 0))?;
    let context = context.ok_or(Failure("missing_context", 0))?;
    let duplication = checked(
        unsafe { desktop.DuplicateOutput(&device) },
        "duplicate_output",
    )?;
    let duplication_desc = unsafe { duplication.GetDesc() };
    if duplication_desc.ModeDesc.Width != size.0 as u32
        || duplication_desc.ModeDesc.Height != size.1 as u32
        || duplication_desc.ModeDesc.Format != DXGI_FORMAT_B8G8R8A8_UNORM
        || duplication_desc.Rotation != DXGI_MODE_ROTATION_IDENTITY
    {
        return Err(Failure("duplication_geometry", 0));
    }
    let (width, height) = dimensions(spec.roi).ok_or(Failure("roi_size", 0))?;
    let system_memory = duplication_desc.DesktopImageInSystemMemory.as_bool();
    let staging_desc = D3D11_TEXTURE2D_DESC {
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
    let mut free = Vec::with_capacity(CAPACITY);
    for _ in 0..if system_memory { 0 } else { CAPACITY } {
        let mut texture = None;
        checked(
            unsafe { device.CreateTexture2D(&staging_desc, None, Some(&mut texture)) },
            "staging_texture",
        )?;
        free.push(texture.ok_or(Failure("missing_staging", 0))?);
    }
    let mut pending: Queue<Pending> = Queue::new();
    let mut frequency = 0;
    checked(
        unsafe { QueryPerformanceFrequency(&mut frequency) },
        "qpc_frequency",
    )?;
    if frequency <= 0 {
        return Err(Failure("qpc_frequency", 0));
    }
    emit(Event::Ready(frequency))?;
    let region = D3D11_BOX {
        left: local[0],
        top: local[1],
        front: 0,
        right: local[2],
        bottom: local[3],
        back: 1,
    };
    let (mut previous, mut events, mut bytes) = (0, 0u32, 0usize);
    let mut previous_stages = [0i64; 4];
    let mut captured_frames = 0u32;
    while !signals.stop.load(Ordering::Acquire) || !pending.is_empty() {
        if let Some(failure) = *signals
            .failure
            .lock()
            .map_err(|_| Failure("signals_lock", 0))?
        {
            return Err(failure);
        }
        while let Some((frame, pixels)) = pending.poll(Instant::now(), |frame| {
            try_read_pixels(&context, &frame.texture, width, height)
        }).inspect_err(|_| {
            if let Some((front, age, count)) = pending.diagnostic_front(Instant::now()) {
                let mut poll_qpc = 0;
                let _ = unsafe { QueryPerformanceCounter(&mut poll_qpc) };
                // Observation only: the original failure is still returned.
                let ready = match try_read_pixels(&context, &front.texture, width, height) {
                    Ok(Some(_)) => 1, Ok(None) => 0, Err(_) => -1,
                };
                eprintln!(concat!(
                    "{{\"age_micros\":{},\"pending_readbacks\":{},\"map_ready\":{},\"system_memory\":{},",
                    "\"front_present_qpc\":{},\"poll_qpc\":{},\"frequency\":{},\"captured_frames\":{},",
                    "\"last_acquired_qpc\":{},\"last_readback_qpc\":{},\"last_released_qpc\":{},\"last_enqueued_qpc\":{}}}"
                ), age.as_micros().min(i64::MAX as u128), count, ready,
                i32::from(duplication_desc.DesktopImageInSystemMemory.as_bool()),
                front.qpc, poll_qpc, frequency, captured_frames,
                previous_stages[0], previous_stages[1], previous_stages[2], previous_stages[3]);
            }
        })? {
            let mut readback_qpc = 0;
            checked(
                unsafe { QueryPerformanceCounter(&mut readback_qpc) },
                "frame_qpc",
            )?;
            previous_stages[1] = readback_qpc;
            unobscured(spec, initial)?;
            bytes += pixels.len();
            if bytes > 32 * 1024 * 1024 {
                return Err(Failure("pixel_bound", 0));
            }
            emit(Event::Frame {
                baseline: frame.baseline,
                qpc: frame.qpc,
                width,
                height,
                pixels,
            })?;
            checked(
                unsafe { QueryPerformanceCounter(&mut previous_stages[3]) },
                "enqueue_qpc",
            )?;
            captured_frames += 1;
            // Map/CPU copy/Unmap all completed before this slot becomes reusable.
            free.push(frame.texture);
        }
        if signals.stop.load(Ordering::Acquire) {
            if !pending.is_empty() {
                std::thread::sleep(Duration::from_millis(1));
            }
            continue;
        }
        unobscured(spec, initial)?;
        let current = checked(unsafe { desktop.GetDesc() }, "output_desc")?;
        if current.DesktopCoordinates != rect
            || current.Rotation != desc.Rotation
            || current.Monitor != monitor
        {
            return Err(Failure("output_changed", 0));
        }
        let mut info = DXGI_OUTDUPL_FRAME_INFO::default();
        let mut resource: Option<IDXGIResource> = None;
        match unsafe {
            duplication.AcquireNextFrame(
                if pending.is_empty() { 50 } else { 5 },
                &mut info,
                &mut resource,
            )
        } {
            Ok(()) => (),
            Err(error) if error.code().0 as u32 == 0x887A0027 => continue,
            Err(error) => return Err(Failure("acquire_frame", error.code().0)),
        }
        let mut acquired = Acquired {
            duplication: &duplication,
            released: false,
        };
        events += 1;
        if events > 512 || info.ProtectedContentMaskedOut.as_bool() {
            return Err(Failure("desktop_frame_bound", 0));
        }
        let mut received = 0;
        checked(
            unsafe { QueryPerformanceCounter(&mut received) },
            "frame_qpc",
        )?;
        let acquired_qpc = received;
        let baseline = (previous == 0).then_some(info.AccumulatedFrames);
        let present = if baseline.is_some() {
            desktop_baseline_time(
                info.LastPresentTime,
                info.AccumulatedFrames,
                info.TotalMetadataBufferSize,
                info.LastMouseUpdateTime,
                received,
            )
        } else {
            desktop_frame_time(
                info.LastPresentTime,
                info.AccumulatedFrames,
                info.TotalMetadataBufferSize,
                info.LastMouseUpdateTime,
                received,
                previous,
            )
        }
        .inspect_err(|_| {
            eprintln!(
                concat!(
                    "{{\"present_qpc\":{},\"accumulated_frames\":{},\"metadata_bytes\":{},",
                    "\"mouse_qpc\":{},\"received_qpc\":{},\"previous_qpc\":{},\"frequency\":{},",
                    "\"last_acquired_qpc\":{},\"last_readback_qpc\":{},",
                    "\"last_released_qpc\":{},\"last_enqueued_qpc\":{},\"captured_frames\":{}}}"
                ),
                info.LastPresentTime,
                info.AccumulatedFrames,
                info.TotalMetadataBufferSize,
                info.LastMouseUpdateTime,
                received,
                previous,
                frequency,
                previous_stages[0],
                previous_stages[1],
                previous_stages[2],
                previous_stages[3],
                captured_frames
            );
        })?;
        if present.is_none() {
            unobscured(spec, initial)?;
            acquired.close()?;
            emit(Event::Pointer(info.LastMouseUpdateTime))?;
            continue;
        }
        let source: ID3D11Texture2D = checked(
            resource.ok_or(Failure("missing_frame", 0))?.cast(),
            "source_texture",
        )?;
        let mut source_desc = D3D11_TEXTURE2D_DESC::default();
        unsafe { source.GetDesc(&mut source_desc) };
        if source_desc.Width != size.0 as u32
            || source_desc.Height != size.1 as u32
            || source_desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM
        {
            return Err(Failure("source_layout", 0));
        }
        if system_memory {
            // DXGI already owns CPU-readable pixels here. A staging roundtrip
            // adds unnecessary work and delays readback behind acquisition.
            let pixels = desktop_memory::read(&duplication, (size.0 as u32, size.1 as u32), local)?;
            checked(
                unsafe { QueryPerformanceCounter(&mut previous_stages[1]) },
                "frame_qpc",
            )?;
            drop(source);
            acquired.close()?;
            previous_stages[0] = acquired_qpc;
            checked(
                unsafe { QueryPerformanceCounter(&mut previous_stages[2]) },
                "release_qpc",
            )?;
            previous = present.ok_or(Failure("missing_present_time", 0))?;
            unobscured(spec, initial)?;
            bytes += pixels.len();
            if bytes > 32 * 1024 * 1024 {
                return Err(Failure("pixel_bound", 0));
            }
            emit(Event::Frame {
                baseline,
                qpc: previous,
                width,
                height,
                pixels,
            })?;
            checked(
                unsafe { QueryPerformanceCounter(&mut previous_stages[3]) },
                "enqueue_qpc",
            )?;
            captured_frames += 1;
            continue;
        }
        let texture = free.pop().ok_or(Failure("readback_queue_overflow", 0))?;
        let submitted = Instant::now();
        // Submit a copy to our own surface before releasing DXGI ownership.
        // No Map/wait here: keep accepting presentations while the GPU works.
        unsafe {
            context.CopySubresourceRegion(&texture, 0, 0, 0, 0, &source, 0, Some(&region));
            context.Flush();
        }
        let qpc = present.ok_or(Failure("missing_present_time", 0))?;
        drop(source);
        acquired.close()?;
        previous_stages[0] = acquired_qpc;
        checked(
            unsafe { QueryPerformanceCounter(&mut previous_stages[2]) },
            "release_qpc",
        )?;
        previous = qpc;
        pending.push(
            submitted,
            Pending {
                baseline,
                qpc,
                texture,
            },
        )?;
    }
    if let Some(failure) = *signals
        .failure
        .lock()
        .map_err(|_| Failure("signals_lock", 0))?
    {
        return Err(failure);
    }
    let mut end = 0;
    checked(unsafe { QueryPerformanceCounter(&mut end) }, "end_qpc")?;
    eprintln!(
        "{{\"system_memory\":{},\"readback_frames\":{}}}",
        i32::from(system_memory),
        captured_frames
    );
    emit(Event::End(end))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn overlay_intersection_is_strict_and_accepts_negative_monitor_coordinates() {
        let roi = [-200, 50, -100, 60];
        assert!(intersects(roi, [-150, 55, 0, 80]));
        assert!(!intersects(roi, [-100, 50, 0, 60]));
        assert!(!intersects(roi, [-200, 60, -100, 80]));
        assert!(!intersects(roi, [-300, 0, -201, 80]));
    }
}
