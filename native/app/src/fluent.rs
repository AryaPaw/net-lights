//! Shared Fluent typography, surfaces and GPUI Kit control metrics.
use gpui_kit::{
    component::{ActiveTheme, Disableable, Sizable, button::Button, switch::Switch},
    *,
};

pub const FONT: &str = "Segoe UI Variable Text";
pub const DISPLAY_FONT: &str = "Segoe UI Variable Display";
pub const BODY: f32 = 13.;
pub const PAGE_TOP_INSET: f32 = 16.;
pub const BUTTON: f32 = 32.;
pub const COMPACT_BUTTON: f32 = 24.;
pub const TAB: f32 = 32.;
pub const INDICATOR_WIDTH: f32 = 16.;
pub const INDICATOR_HEIGHT: f32 = 3.;
pub const SETTINGS_RAIL: f32 = 180.;
pub const SETTINGS_GAP: f32 = 32.;
pub const SETTINGS_CONTENT: f32 = 780.;

gpui_kit::assets::icon_assets!(
    SettingsAssets,
    [Gauge, MapPin, Languages, Paintbrush, Power]
);
pub struct Assets;
impl AssetSource for Assets {
    fn load(&self, path: &str) -> Result<Option<std::borrow::Cow<'static, [u8]>>> {
        if path == "brand/net-lights.svg" {
            return Ok(Some(std::borrow::Cow::Borrowed(include_bytes!(
                "../../assets/brand.svg"
            ))));
        }
        match SettingsAssets.load(path)? {
            Some(bytes) => Ok(Some(bytes)),
            None => gpui_kit::assets::Assets.load(path),
        }
    }
    fn list(&self, path: &str) -> Result<Vec<SharedString>> {
        if path == "brand/" {
            return Ok(vec!["brand/net-lights.svg".into()]);
        }
        let mut entries = gpui_kit::assets::Assets.list(path)?;
        entries.extend(SettingsAssets.list(path)?);
        entries.sort();
        entries.dedup();
        Ok(entries)
    }
}
pub fn brand_mark(size: f32) -> impl IntoElement {
    img("brand/net-lights.svg")
        .size(px(size))
        .object_fit(ObjectFit::Contain)
}
pub fn navigation_transition() -> gpui_kit::base::motion::Transition {
    gpui_kit::base::motion::Transition::new(std::time::Duration::from_millis(250))
        .ease(gpui_kit::base::animation::cubic_bezier(0.33, 0., 0.1, 1.))
}

pub fn button(id: impl Into<ElementId>) -> Button {
    Button::new(id)
        .cursor_pointer()
        .large()
        .h(px(BUTTON))
        .px(px(12.))
        .rounded(px(4.))
        .text_size(px(BODY))
}
pub fn compact_button(id: impl Into<ElementId>) -> Button {
    button(id).h(px(COMPACT_BUTTON)).px(px(8.))
}
pub fn switch(id: impl Into<ElementId>) -> Switch {
    Switch::new(id).cursor_pointer()
}

/// Keep the affordance consistent with the same state that blocks interaction.
pub trait ControlCursor: Disableable + Styled {
    fn disabled_with_cursor(self, disabled: bool) -> Self {
        let control = self.disabled(disabled);
        if disabled {
            control.cursor_default()
        } else {
            control.cursor_pointer()
        }
    }
}
impl ControlCursor for Button {}
impl ControlCursor for Switch {}

pub fn input<T: Styled>(control: T) -> T {
    control
        .cursor_pointer()
        .h(px(BUTTON))
        .px(px(12.))
        .text_size(px(BODY))
        .rounded(px(4.))
}
pub fn surface(cx: &App) -> Hsla {
    let amount = if cx.theme().is_dark() { 0.035 } else { 0.5 };
    cx.theme()
        .background
        .blend(rgb(0xffffff).opacity(amount).into())
}
pub fn hover(cx: &App) -> Hsla {
    rgb(if cx.theme().is_dark() { 0xffffff } else { 0 })
        .opacity(if cx.theme().is_dark() { 0.045 } else { 0.04 })
        .into()
}

// Compile and test the exact runtime source, without starting a GPU window.
#[cfg(test)]
mod argus_scroll_patch {
    use gpui_kit::{Pixels, Point, point, px};
    mod engine {
        include!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../vendor/gpui-pre/src/elements/smooth_scroll.rs"
        ));
    }
}

#[cfg(test)]
mod tests {
    use super::Assets;
    use gpui_kit::AssetSource;
    #[test]
    fn selected_fluent_icons_and_default_kit_icons_are_both_available() {
        for path in [
            "brand/net-lights.svg",
            "icons/gauge.svg",
            "icons/map-pin.svg",
            "icons/languages.svg",
            "icons/paintbrush.svg",
            "icons/power.svg",
            "icons/globe.svg",
            "icons/info.svg",
            "icons/folder.svg",
            "icons/chevron-down.svg",
        ] {
            let bytes = Assets
                .load(path)
                .unwrap()
                .expect("Required visible icon is missing");
            assert!(
                std::str::from_utf8(&bytes).unwrap().contains("<svg"),
                "Invalid icon: {path}"
            );
        }
        let listed = Assets.list("icons/").unwrap();
        assert!(listed.windows(2).all(|pair| pair[0] < pair[1]));
        assert!(Assets.load("").unwrap().is_none());
        assert!(Assets.load("icons/not-a-kit-icon.svg").is_err());
    }
}
