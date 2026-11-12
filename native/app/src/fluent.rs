//! Shared Fluent typography, surfaces and GPUI Kit control metrics.
use gpui_kit::{
    component::{ActiveTheme, Sizable, button::Button},
    *,
};

pub const FONT: &str = "Segoe UI Variable Text";
pub const DISPLAY_FONT: &str = "Segoe UI Variable Display";
pub const BODY: f32 = 13.;
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
        match SettingsAssets.load(path)? {
            Some(bytes) => Ok(Some(bytes)),
            None => gpui_kit::assets::Assets.load(path),
        }
    }
    fn list(&self, path: &str) -> Result<Vec<SharedString>> {
        let mut entries = gpui_kit::assets::Assets.list(path)?;
        entries.extend(SettingsAssets.list(path)?);
        entries.sort();
        entries.dedup();
        Ok(entries)
    }
}

pub fn button(id: impl Into<ElementId>) -> Button {
    Button::new(id)
        .large()
        .h(px(BUTTON))
        .px(px(12.))
        .rounded(px(4.))
        .text_size(px(BODY))
}
pub fn compact_button(id: impl Into<ElementId>) -> Button {
    button(id).h(px(COMPACT_BUTTON)).px(px(8.))
}
pub fn input<T: Styled>(control: T) -> T {
    control
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

#[cfg(test)]
mod tests {
    use super::Assets;
    use gpui_kit::AssetSource;
    #[test]
    fn selected_fluent_icons_and_default_kit_icons_are_both_available() {
        for path in [
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
