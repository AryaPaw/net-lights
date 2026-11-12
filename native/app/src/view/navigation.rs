use super::*;
use gpui_kit::base::{
    ElementExt,
    motion::{Transition, TransitionState},
};
use std::cell::RefCell;

pub(super) struct Selected(pub Page);
pub(super) struct Navigation {
    selected: Page,
    focus: [FocusHandle; 5],
    regions: Rc<RefCell<[Bounds<Pixels>; 6]>>,
    marker: TransitionState<Point<Pixels>>,
    layout: Option<(Size<Pixels>, Language)>,
}
impl EventEmitter<Selected> for Navigation {}
impl Navigation {
    pub(super) fn new(cx: &mut Context<Self>) -> Self {
        Self {
            selected: Page::State,
            focus: std::array::from_fn(|_| cx.focus_handle()),
            regions: Rc::new(RefCell::new([Bounds::default(); 6])),
            marker: TransitionState::default(),
            layout: None,
        }
    }
    fn set(&mut self, page: Page, cx: &mut Context<Self>) {
        if self.selected != page {
            self.selected = page;
            cx.notify();
        }
    }
}
impl NetLights {
    pub(super) fn select_page(&mut self, page: Page, cx: &mut Context<Self>) {
        if self.page == page {
            return;
        }
        self.page_direction = if page as usize > self.page as usize {
            1.
        } else {
            -1.
        };
        self.page_transition = self.page_transition.wrapping_add(1);
        self.page = page;
        self.navigation.update(cx, |nav, cx| nav.set(page, cx));
        super::about::clear_copy_version_notice(&mut self.notice);
        self.timeline
            .update(cx, |timeline, cx| timeline.clear_pointer(cx));
        self.incident = None;
        self.refresh_charts(cx);
        cx.notify();
    }
}
impl Render for Navigation {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let layout = (window.viewport_size(), language(cx));
        if self.layout != Some(layout) {
            self.layout = Some(layout);
            self.marker = TransitionState::default();
            *self.regions.borrow_mut() = [Bounds::default(); 6];
        }
        let foreground = cx.theme().foreground;
        let muted = cx.theme().muted_foreground;
        let primary = cx.theme().primary;
        let hover = crate::fluent::hover(cx);
        let strong = rgb(0xffffff).opacity(if cx.theme().is_dark() { 0.065 } else { 0.7 });
        let press = rgb(if cx.theme().is_dark() { 0xffffff } else { 0 }).opacity(0.03);
        let selected = self.selected as usize;
        let bounds = *self.regions.borrow();
        let measured = bounds[selected + 1];
        let marker = (measured.size.width > px(0.)).then(|| {
            let target = point(
                measured.center().x - bounds[0].left() - px(crate::fluent::INDICATOR_WIDTH / 2.),
                measured.bottom() - bounds[0].top() - px(2. + crate::fluent::INDICATOR_HEIGHT),
            );
            let value = self
                .marker
                .sample(
                    target,
                    Transition::new(Duration::from_millis(250))
                        .ease(gpui_kit::base::animation::cubic_bezier(0.33, 0., 0.1, 1.)),
                    window,
                    cx,
                )
                .value;
            window.pixel_snap_point(value)
        });
        let owner = cx.entity().downgrade();
        let mut tabs = gpui_kit::base::Tabs::new("navigation")
            .relative()
            .flex()
            .flex_wrap()
            .gap(px(2.))
            .pb(px(6.))
            .on_prepaint(track(self.regions.clone(), owner.clone(), 0))
            .children(Page::ALL.into_iter().enumerate().map(|(index, page)| {
                let chosen = index == selected;
                let label = tr(cx, page.label());
                gpui_kit::base::Tab::new(("page", index))
                    .selected(chosen)
                    .set_position(index + 1, Page::ALL.len())
                    .accessibility_label(label.clone())
                    .track_focus(&self.focus[index])
                    .tab_stop(chosen)
                    .relative()
                    .flex_shrink_0()
                    .h(px(crate::fluent::TAB))
                    .px(px(12.))
                    .rounded(px(4.))
                    .border_1()
                    .border_color(transparent_black())
                    .line_height(relative(1.4))
                    .text_size(px(crate::fluent::BODY))
                    .text_color(if chosen { foreground } else { muted })
                    .hover(move |style| {
                        style
                            .bg(if chosen { strong } else { hover.into() })
                            .text_color(foreground)
                    })
                    .active(move |style| style.bg(if chosen { strong } else { press }))
                    .focus_visible(move |style| style.border_color(primary))
                    .on_prepaint(track(self.regions.clone(), owner.clone(), index + 1))
                    .child(
                        div()
                            .relative()
                            .child(
                                div()
                                    .font_weight(FontWeight::MEDIUM)
                                    .invisible()
                                    .child(label.clone()),
                            )
                            .child(
                                div()
                                    .absolute()
                                    .inset_0()
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .when(chosen, |d| d.font_weight(FontWeight::MEDIUM))
                                    .child(label),
                            ),
                    )
                    .on_click(cx.listener(move |this, _, window, cx| {
                        window.focus(&this.focus[index], cx);
                        cx.emit(Selected(page));
                    }))
                    .on_key_down(cx.listener(move |this, event: &KeyDownEvent, window, cx| {
                        let next = match event.keystroke.key.as_str() {
                            "left" => Some((index + Page::ALL.len() - 1) % Page::ALL.len()),
                            "right" => Some((index + 1) % Page::ALL.len()),
                            "home" => Some(0),
                            "end" => Some(Page::ALL.len() - 1),
                            "enter" | "space" => Some(index),
                            _ => None,
                        };
                        if let Some(next) = next {
                            window.focus(&this.focus[next], cx);
                            cx.emit(Selected(Page::ALL[next]));
                            cx.stop_propagation();
                        }
                    }))
            }));
        if let Some(position) = marker {
            tabs = tabs.child(
                div()
                    .absolute()
                    .left(position.x)
                    .top(position.y)
                    .w(px(crate::fluent::INDICATOR_WIDTH))
                    .h(px(crate::fluent::INDICATOR_HEIGHT))
                    .rounded(px(2.))
                    .bg(primary),
            );
        }
        tabs
    }
}
fn track(
    regions: Rc<RefCell<[Bounds<Pixels>; 6]>>,
    owner: WeakEntity<Navigation>,
    index: usize,
) -> impl Fn(Bounds<Pixels>, &mut Window, &mut App) + 'static {
    move |bounds, window, cx| {
        if regions.borrow()[index] == bounds {
            return;
        }
        regions.borrow_mut()[index] = bounds;
        let owner = owner.clone();
        window.defer(cx, move |_, cx| {
            let _ = owner.update(cx, |_, cx| cx.notify());
        });
    }
}
