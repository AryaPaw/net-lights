use gpui_kit::*;
use netlights_core::model::Settings;

pub fn restore(settings: &Settings, scale: f32, work: Bounds<Pixels>) -> Bounds<Pixels> {
    let scale = scale.max(1.);
    let width = px(settings.window_width as f32 / scale).min(work.size.width);
    let height = px(settings.window_height as f32 / scale).min(work.size.height);
    let centered = point(
        work.origin.x + (work.size.width - width) / 2.,
        work.origin.y + (work.size.height - height) / 2.,
    );
    let position = if settings.window_x == i32::MIN || settings.window_y == i32::MIN {
        centered
    } else {
        point(
            px(settings.window_x as f32 / scale),
            px(settings.window_y as f32 / scale),
        )
    };
    Bounds::new(
        point(
            position.x.max(work.origin.x).min(work.right() - width),
            position.y.max(work.origin.y).min(work.bottom() - height),
        ),
        size(width, height),
    )
}
pub fn initial(settings: &Settings, cx: &App) -> (Bounds<Pixels>, Option<DisplayId>) {
    use windows::Win32::{
        Foundation::RECT,
        Graphics::Gdi::{MONITOR_DEFAULTTONEAREST, MonitorFromRect},
        UI::HiDpi::{GetDpiForMonitor, MDT_EFFECTIVE_DPI},
    };
    let saved = RECT {
        left: settings.window_x,
        top: settings.window_y,
        right: settings.window_x.saturating_add(settings.window_width),
        bottom: settings.window_y.saturating_add(settings.window_height),
    };
    let monitor = if settings.window_x != i32::MIN && settings.window_y != i32::MIN {
        Some(unsafe { MonitorFromRect(&saved, MONITOR_DEFAULTTONEAREST) })
    } else {
        None
    };
    let display = monitor
        .and_then(|m| {
            cx.displays()
                .into_iter()
                .find(|d| u64::from(d.id()) == m.0 as u64)
        })
        .or_else(|| cx.primary_display());
    if let Some(display) = display {
        let mut x = 96;
        let mut y = 96;
        let handle = windows::Win32::Graphics::Gdi::HMONITOR(u64::from(display.id()) as *mut _);
        let _ = unsafe { GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, &mut x, &mut y) };
        (
            restore(settings, x as f32 / 96., display.visible_bounds()),
            Some(display.id()),
        )
    } else {
        (
            Bounds::new(point(px(0.), px(0.)), size(px(960.), px(800.))),
            None,
        )
    }
}
#[cfg(test)]
mod tests {
    use super::restore;
    use gpui_kit::{Bounds, point, px, size};
    use netlights_core::model::Settings;
    #[test]
    fn restoring_physical_bounds_uses_the_target_monitor_dpi() {
        let settings = Settings {
            window_x: 4200,
            window_y: 400,
            window_width: 1400,
            window_height: 1200,
            ..Default::default()
        };
        let work = Bounds::new(point(px(1920.), px(0.)), size(px(1920.), px(1080.)));
        let b = restore(&settings, 2., work);
        assert_eq!(f32::from(b.origin.x) * 2., 4200.);
        assert_eq!(f32::from(b.origin.y) * 2., 400.);
        assert_eq!(f32::from(b.size.width) * 2., 1400.);
        assert_eq!(f32::from(b.size.height) * 2., 1200.);
    }
    #[test]
    fn removed_monitor_and_oversized_window_are_clamped_to_work_area() {
        let s = Settings {
            window_x: 9000,
            window_y: -9000,
            ..Default::default()
        };
        let work = Bounds::new(point(px(0.), px(0.)), size(px(1280.), px(720.)));
        let b = restore(&s, 1., work);
        assert_eq!(b, work);
    }
}
