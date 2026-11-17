use super::{DEADLINE, Failure, Result, checked};
use std::time::Instant;
use windows::Win32::Graphics::Dxgi::{DXGI_MAPPED_RECT, IDXGIOutputDuplication};

struct Mapped<'a> {
    duplication: &'a IDXGIOutputDuplication,
    open: bool,
}
impl Mapped<'_> {
    fn close(&mut self) -> Result<()> {
        self.open = false;
        checked(
            unsafe { self.duplication.UnMapDesktopSurface() },
            "desktop_unmap",
        )
    }
}
impl Drop for Mapped<'_> {
    fn drop(&mut self) {
        if self.open {
            let _ = self.close();
        }
    }
}

// Called only while the caller owns an acquired system-memory desktop frame.
// DXGI's mapping remains valid until UnMapDesktopSurface; copy only the ROI.
pub(super) fn read(
    duplication: &IDXGIOutputDuplication,
    size: (u32, u32),
    roi: [u32; 4],
) -> Result<Vec<u8>> {
    let started = Instant::now();
    let mapped = checked(unsafe { duplication.MapDesktopSurface() }, "desktop_map")?;
    let mut guard = Mapped {
        duplication,
        open: true,
    };
    let pixels = unsafe { copy_roi(mapped, size, roi) };
    guard.close()?;
    if started.elapsed() >= DEADLINE {
        return Err(Failure("desktop_map_timeout", 0));
    }
    pixels
}

// Safety: pBits must address the full mapped desktop with Pitch bytes per row,
// and that mapping must remain alive and immutable for this entire copy.
unsafe fn copy_roi(mapped: DXGI_MAPPED_RECT, size: (u32, u32), roi: [u32; 4]) -> Result<Vec<u8>> {
    if mapped.pBits.is_null()
        || mapped.Pitch <= 0
        || size.0 == 0
        || size.1 == 0
        || size.0 > 8192
        || size.1 > 8192
        || roi[0] >= roi[2]
        || roi[1] >= roi[3]
        || roi[2] > size.0
        || roi[3] > size.1
    {
        return Err(Failure("desktop_layout", 0));
    }
    let pitch = mapped.Pitch as usize;
    let row = (roi[2] - roi[0]) as usize * 4;
    let height = (roi[3] - roi[1]) as usize;
    if pitch < size.0 as usize * 4
        || row * height > 65536 * 4
        || pitch
            .checked_mul(size.1 as usize)
            .is_none_or(|n| n > isize::MAX as usize)
    {
        return Err(Failure("desktop_layout", 0));
    }
    let mut pixels = Vec::with_capacity(row * height);
    for y in roi[1]..roi[3] {
        let offset = y as usize * pitch + roi[0] as usize * 4;
        pixels.extend_from_slice(unsafe {
            std::slice::from_raw_parts(mapped.pBits.add(offset), row)
        });
    }
    Ok(pixels)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn copies_only_offset_roi_and_ignores_padded_desktop_rows() {
        let mut bytes: Vec<u8> = (0..96).collect();
        let mapped = DXGI_MAPPED_RECT {
            Pitch: 32,
            pBits: bytes.as_mut_ptr(),
        };
        assert_eq!(
            unsafe { copy_roi(mapped, (5, 3), [1, 1, 3, 3]) }.unwrap(),
            [&bytes[36..44], &bytes[68..76]].concat()
        );
    }
    #[test]
    fn rejects_invalid_pitch_null_pointer_and_out_of_bounds_before_reading() {
        let mut bytes = [0u8; 96];
        for pitch in [-32, 0, 19] {
            let mapped = DXGI_MAPPED_RECT {
                Pitch: pitch,
                pBits: bytes.as_mut_ptr(),
            };
            assert_eq!(
                unsafe { copy_roi(mapped, (5, 3), [1, 1, 3, 3]) },
                Err(Failure("desktop_layout", 0))
            );
        }
        let mapped = DXGI_MAPPED_RECT {
            Pitch: 32,
            pBits: bytes.as_mut_ptr(),
        };
        for roi in [[0, 0, 6, 1], [0, 0, 1, 4], [2, 0, 1, 1], [0, 1, 1, 1]] {
            assert_eq!(
                unsafe { copy_roi(mapped, (5, 3), roi) },
                Err(Failure("desktop_layout", 0))
            );
        }
        let null = DXGI_MAPPED_RECT {
            Pitch: 32,
            pBits: std::ptr::null_mut(),
        };
        assert_eq!(
            unsafe { copy_roi(null, (5, 3), [0, 0, 1, 1]) },
            Err(Failure("desktop_layout", 0))
        );
    }
    #[test]
    fn rejects_oversized_roi_without_dereferencing_a_short_allocation() {
        let mut byte = 0u8;
        let mapped = DXGI_MAPPED_RECT {
            Pitch: 8192 * 4,
            pBits: &mut byte,
        };
        assert_eq!(
            unsafe { copy_roi(mapped, (8192, 8192), [0, 0, 8192, 9]) },
            Err(Failure("desktop_layout", 0))
        );
    }
}
