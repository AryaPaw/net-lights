//! Animated presentation of the settings scroll position. The page owns the
//! selected section; animation frames invalidate only this small rail.
use super::*;
use gpui_kit::base::motion::TransitionState;

pub(super) const SECTIONS: [&str; 3] = ["Проверки и трафик", "Локация", "Приложение"];
pub(super) struct Selected(pub usize);
pub(super) struct SettingsNavigation {
    selected: usize,
    marker: TransitionState<Pixels>,
    hovered: [bool; 3],
    hover_motion: [TransitionState<f32>; 3],
}
impl EventEmitter<Selected> for SettingsNavigation {}
impl SettingsNavigation {
    pub(super) fn new(_: &mut Context<Self>) -> Self {
        Self {
            selected: 0,
            marker: TransitionState::default(),
            hovered: [false; 3],
            hover_motion: std::array::from_fn(|_| TransitionState::default()),
        }
    }
    pub(super) fn set(&mut self, index: usize, cx: &mut Context<Self>) {
        let index = index.min(SECTIONS.len() - 1);
        if self.selected != index {
            self.selected = index;
            cx.notify();
        }
    }
}
impl Render for SettingsNavigation {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let primary = cx.theme().primary;
        let border = cx.theme().border;
        let foreground = cx.theme().foreground;
        let muted = cx.theme().muted_foreground;
        let selected = self.selected;
        let y = self
            .marker
            .sample(
                px(selected as f32 * 38.),
                crate::fluent::navigation_transition(),
                window,
                cx,
            )
            .value;
        let hover_color = crate::fluent::hover(cx);
        div()
            .id("settings-navigation")
            .w_full()
            .role(Role::Navigation)
            .relative()
            .flex()
            .flex_col()
            .gap(px(2.))
            .child(
                div()
                    .absolute()
                    .left_0()
                    .right_0()
                    .top(window.pixel_snap(y))
                    .h(px(36.))
                    .rounded(px(6.))
                    .border_1()
                    .border_color(border)
                    .bg(surface(cx))
                    .child(
                        div()
                            .absolute()
                            .left_0()
                            .top(px(8.))
                            .w(px(3.))
                            .h(px(18.))
                            .rounded(px(2.))
                            .bg(primary),
                    ),
            )
            .children(SECTIONS.into_iter().enumerate().map(|(index, label)| {
                let chosen = index == selected;
                let hover = self.hover_motion[index]
                    .sample(
                        if self.hovered[index] { 1. } else { 0. },
                        crate::fluent::navigation_transition(),
                        window,
                        cx,
                    )
                    .value;
                let owner = cx.entity().downgrade();
                gpui_kit::base::Button::new(("settings-nav", index))
                    .cursor_pointer()
                    .relative()
                    .w_full()
                    .h(px(36.))
                    .justify_start()
                    .px(px(14.))
                    .gap(px(10.))
                    .line_height(relative(1.4))
                    .rounded(px(6.))
                    .border_1()
                    .border_color(transparent_black())
                    .focus_visible(move |style| style.border_color(primary))
                    .bg(hover_color.opacity(if chosen { 0. } else { hover }))
                    .text_color(if chosen {
                        foreground
                    } else {
                        muted.blend(foreground.opacity(hover))
                    })
                    .accessibility_label(if chosen {
                        format!(
                            "{}, {}",
                            tr(cx, label),
                            language(cx).choose("текущий раздел", "current section")
                        )
                    } else {
                        tr(cx, label)
                    })
                    .on_hover(cx.listener(move |this, inside, _, cx| {
                        if this.hovered[index] != *inside {
                            this.hovered[index] = *inside;
                            cx.notify();
                        }
                    }))
                    .child(
                        gpui_kit::component::Icon::new(match index {
                            0 => gpui_kit::assets::IconName::Gauge,
                            1 => gpui_kit::assets::IconName::MapPin,
                            _ => gpui_kit::assets::IconName::Paintbrush,
                        })
                        .size(px(16.)),
                    )
                    .child(div().whitespace_nowrap().child(tr(cx, label)))
                    .on_click(cx.listener(move |_, _, _, cx| cx.emit(Selected(index))))
                    .on_a11y_action(AccessibleAction::Click, move |_, _, cx| {
                        let _ = owner.update(cx, |_, cx| cx.emit(Selected(index)));
                    })
                    .on_mouse_down(MouseButton::Left, |_, _, cx| cx.stop_propagation())
            }))
    }
}

/// Sweep the reading line across the visible page as it is scrolled. Unlike
/// forcing the final section at the bottom, this gives every section a real
/// selection interval even when most of the page fits in a tall window.
pub(super) fn section_at_scroll(
    headers: &[f32],
    viewport: f32,
    offset: f32,
    maximum: f32,
) -> usize {
    if headers.is_empty()
        || !viewport.is_finite()
        || viewport <= 0.
        || !maximum.is_finite()
        || maximum <= 0.
        || !offset.is_finite()
    {
        return 0;
    }
    let y = offset.clamp(0., maximum);
    let line = y + viewport * (y / maximum);
    headers
        .partition_point(|top| *top <= line + 0.5)
        .saturating_sub(1)
}

#[cfg(test)]
mod tests {
    use super::section_at_scroll;
    #[test]
    fn short_and_long_scroll_ranges_include_the_country_section_at_every_scale() {
        for scale in [1., 1.25, 1.5, 2.] {
            for maximum in [70., 500., 1100.] {
                let viewport = 800. * scale;
                let total = viewport + maximum * scale;
                let headers = [0., total * 0.3, total * 0.6];
                let sequence: Vec<_> = (0..=100)
                    .map(|step| {
                        section_at_scroll(
                            &headers,
                            viewport,
                            maximum * scale * step as f32 / 100.,
                            maximum * scale,
                        )
                    })
                    .collect();
                assert_eq!(sequence[0], 0);
                assert_eq!(*sequence.last().unwrap(), 2);
                assert!(sequence.contains(&1));
                assert!(
                    sequence
                        .windows(2)
                        .all(|pair| pair[0] <= pair[1] && pair[1] - pair[0] <= 1)
                );
            }
        }
    }
    #[test]
    fn missing_geometry_and_invalid_scroll_values_do_not_fabricate_a_last_section() {
        for offset in [f32::NAN, f32::INFINITY, -10.] {
            assert_eq!(section_at_scroll(&[0., 300., 600.], 800., offset, 70.), 0);
        }
        assert_eq!(section_at_scroll(&[], 800., 0., 70.), 0);
        assert_eq!(section_at_scroll(&[0., 300., 600.], 800., 0., 0.), 0);
    }
}
