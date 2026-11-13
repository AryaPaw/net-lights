//! Two availability lanes share one UTC cursor, selection and time axis.
use super::*;
use crate::dashboard::{self, EventKind, HistoryEvent, Stats, TimeRange};
use crate::fluent::ControlCursor;
use gpui_kit::component::button::ButtonVariants;

pub enum TimelineEvent {
    Select(HistoryEvent),
    Zoom(TimeRange),
    Diagnose(String),
}
pub struct AvailabilityTimeline {
    models: [Arc<ChartModel>; 2],
    stats: [Stats; 2],
    positive: [Vec<usize>; 2],
    selected: Option<HistoryEvent>,
    live: MonitorSnapshot,
    availability: Arc<AvailabilityHistory>,
    locations: Arc<LocationHistory>,
    changes: Arc<Vec<HistoryEvent>>,
    geometry: [Rc<RefCell<Geometry>>; 2],
    bounds: [Rc<Cell<Bounds<Pixels>>>; 2],
    pointer: Option<DateTime<Utc>>,
    pointer_fraction: Option<f32>,
    hover_row: usize,
    drag: Option<(usize, f32, f32)>,
    tooltip: Option<(String, String)>,
    tooltip_key: Option<(i64, Option<usize>, Option<usize>)>,
    expanded: [bool; 2],
    focus: FocusHandle,
    marker_bounds: Rc<Cell<Bounds<Pixels>>>,
    marker_cache: Rc<RefCell<MarkerCache>>,
    marker_hover: Option<usize>,
}
#[derive(Default)]
struct MarkerCache {
    width: usize,
    ticks: Vec<(usize, usize, usize)>,
}
fn inspected_at(model: &ChartModel, g: &Geometry, x: f32) -> DateTime<Utc> {
    let at = model.start
        + chrono::Duration::milliseconds(
            ((model.end - model.start).num_milliseconds() as f64 * x.clamp(0., 1.) as f64) as i64,
        );
    if g.width == 0 {
        return at;
    }
    let pixel = ((x.clamp(0., 1.) * g.width as f32) as usize).min(g.width.saturating_sub(1));
    if let Some(i) = g.hits.get(pixel).copied().flatten()
        && let Some(s) = model.segments.get(i)
        && !contains(s, at)
    {
        return s.start + (s.end - s.start) / 2;
    }
    at
}
fn contains(s: &PlotSegment, at: DateTime<Utc>) -> bool {
    s.start <= at
        && (at < s.end
            || (at == s.end
                && (s.start == s.end
                    || matches!(s.detail, SegmentDetail::Availability { open: true, .. })
                    || matches!(s.detail,SegmentDetail::Availability{end,..} if end>s.end))))
}
fn recorded_index(model: &ChartModel, positive: &[usize], at: DateTime<Utc>) -> Option<usize> {
    let i = model
        .segments
        .partition_point(|s| s.start <= at)
        .checked_sub(1)?;
    if contains(&model.segments[i], at) {
        return Some(i);
    }
    let j = positive
        .partition_point(|i| model.segments[*i].start <= at)
        .checked_sub(1)?;
    let i = positive[j];
    contains(&model.segments[i], at).then_some(i)
}
fn selection_extent(model: &ChartModel, event: &HistoryEvent, width: f32) -> (f32, f32) {
    let left = (fraction(model, event.start) * width - 2.).clamp(0., (width - 6.).max(0.));
    let right = (fraction(model, event.end) * width + 2.)
        .max(left + 6.)
        .min(width);
    (left, (right - left).max(0.))
}
impl EventEmitter<TimelineEvent> for AvailabilityTimeline {}
impl AvailabilityTimeline {
    pub fn new(live: MonitorSnapshot, cx: &mut Context<Self>) -> Self {
        let now = live.generated_utc;
        Self {
            models: std::array::from_fn(|_| {
                Arc::new(ChartModel::empty(now - chrono::Duration::hours(24), now))
            }),
            stats: Default::default(),
            positive: Default::default(),
            selected: None,
            live,
            availability: Arc::default(),
            locations: Arc::default(),
            changes: Arc::default(),
            geometry: std::array::from_fn(|_| Rc::new(RefCell::new(Geometry::default()))),
            bounds: std::array::from_fn(|_| Rc::new(Cell::new(Bounds::default()))),
            pointer: None,
            pointer_fraction: None,
            hover_row: 0,
            drag: None,
            tooltip: None,
            tooltip_key: None,
            expanded: [false; 2],
            focus: cx.focus_handle(),
            marker_bounds: Rc::new(Cell::new(Bounds::default())),
            marker_cache: Rc::new(RefCell::new(MarkerCache::default())),
            marker_hover: None,
        }
    }
    pub fn set(
        &mut self,
        models: [ChartModel; 2],
        stats: [Stats; 2],
        availability: Arc<AvailabilityHistory>,
        locations: Arc<LocationHistory>,
        changes: Arc<Vec<HistoryEvent>>,
        cx: &mut Context<Self>,
    ) {
        let context_changed = self.models[0].language != models[0].language
            || self.models[0].end - self.models[0].start != models[0].end - models[0].start;
        let widths = self.geometry.each_ref().map(|g| g.borrow().width);
        let marker_at = self
            .marker_hover
            .and_then(|i| self.changes.get(i))
            .map(|e| e.start);
        self.models = models.map(Arc::new);
        self.positive = self.models.each_ref().map(|m| {
            m.segments
                .iter()
                .enumerate()
                .filter_map(|(i, s)| (s.end > s.start).then_some(i))
                .collect()
        });
        self.stats = stats;
        self.availability = availability;
        self.locations = locations;
        self.changes = changes;
        for geometry in &self.geometry {
            geometry.borrow_mut().width = 0;
        }
        self.marker_cache.borrow_mut().width = 0;
        self.tooltip_key = None;
        self.marker_hover = None;
        if context_changed {
            self.clear_pointer(cx);
        } else if let Some(at) = marker_at {
            let i = self.changes.partition_point(|e| e.start < at);
            if self.changes.get(i).is_some_and(|e| e.start == at) {
                self.country_readout(i);
            } else {
                self.clear_pointer(cx);
            }
        } else if let Some(x) = self.pointer_fraction {
            for (row, width) in widths.into_iter().enumerate() {
                if width > 0 {
                    *self.geometry[row].borrow_mut() = Geometry::build(&self.models[row], width);
                }
            }
            self.readout(self.inspected_time(self.hover_row, x));
        }
        cx.notify();
    }
    pub fn live(&mut self, snapshot: MonitorSnapshot, cx: &mut Context<Self>) {
        self.live = snapshot;
        cx.notify();
    }
    pub fn select(&mut self, event: Option<HistoryEvent>, cx: &mut Context<Self>) {
        self.selected = event;
        self.clear_pointer(cx);
    }
    pub fn clear_pointer(&mut self, cx: &mut Context<Self>) {
        self.pointer = None;
        self.pointer_fraction = None;
        self.drag = None;
        self.tooltip = None;
        self.tooltip_key = None;
        self.marker_hover = None;
        cx.notify();
    }
    fn range(&self) -> TimeRange {
        TimeRange {
            start: self.models[0].start,
            end: self.models[0].end,
        }
    }
    pub fn release(&mut self, cx: &mut Context<Self>) {
        let r = self.range();
        self.models = std::array::from_fn(|_| Arc::new(ChartModel::empty(r.start, r.end)));
        self.availability = Arc::default();
        self.positive = Default::default();
        self.selected = None;
        self.locations = Arc::default();
        self.changes = Arc::default();
        for g in &self.geometry {
            *g.borrow_mut() = Geometry::default();
        }
        *self.marker_cache.borrow_mut() = MarkerCache::default();
        self.clear_pointer(cx);
    }
    fn time(&self, x: f32) -> DateTime<Utc> {
        self.range().start
            + chrono::Duration::milliseconds(
                (self.range().millis() as f64 * x.clamp(0., 1.) as f64) as i64,
            )
    }
    fn index(&self, row: usize, at: DateTime<Utc>) -> Option<usize> {
        recorded_index(&self.models[row], &self.positive[row], at)
    }
    fn inspected_time(&self, row: usize, x: f32) -> DateTime<Utc> {
        inspected_at(&self.models[row], &self.geometry[row].borrow(), x)
    }
    fn readout(&mut self, at: DateTime<Utc>) {
        let indices = [self.index(0, at), self.index(1, at)];
        let key = (at.timestamp(), indices[0], indices[1]);
        self.pointer = Some(at);
        if self.tooltip_key == Some(key) {
            return;
        }
        self.tooltip_key = Some(key);
        let lang = self.models[0].language;
        let mut lines = Vec::new();
        for (row, index) in indices.into_iter().enumerate() {
            let name = crate::text::translate(lang, Group::ALL[row].label());
            let detail = index.and_then(|i| self.models[row].materialize(i));
            lines.push(match detail {
                Some(s) => format!("{name}: {}\n{}", s.title, s.detail),
                None => format!("{name}: {}", lang.choose("нет записей", "no records")),
            });
        }
        let country = dashboard::country_at(&self.locations, at)
            .map(|iso| lang.country_name(iso))
            .unwrap_or_else(|| lang.choose("не записана", "not recorded"));
        lines.push(format!(
            "{}: {country}",
            lang.choose("Страна по истории", "Recorded country")
        ));
        if self.pointer_fraction.is_some_and(|x| self.time(x) != at) {
            lines.push(
                lang.choose(
                    "Увеличенная короткая метка: указатель привязан к фактическому участку.",
                    "Enlarged short marker: the cursor snaps to the recorded interval.",
                )
                .into(),
            );
        }
        self.tooltip = Some((local_time(at), lines.join("\n\n")));
    }
    fn country_readout(&mut self, i: usize) {
        let Some(e) = self.changes.get(i) else {
            return;
        };
        let lang = self.models[0].language;
        if let EventKind::Country {
            previous,
            current,
            previous_end,
        } = &e.kind
        {
            self.pointer = Some(e.start);
            self.pointer_fraction = Some(fraction(&self.models[0], e.start));
            self.marker_hover = Some(i);
            self.tooltip = Some((
                format!(
                    "{} → {}",
                    lang.country_name(previous),
                    lang.country_name(current)
                ),
                format!(
                    "{} – {}\n{}",
                    local_time(*previous_end),
                    local_time(e.start),
                    lang.choose(
                        "Изменение между наблюдениями. Точный момент неизвестен.",
                        "Changed between observations. Exact transition time unknown."
                    )
                ),
            ));
            self.tooltip_key = None;
        }
    }
    fn finish_drag(&mut self, row: usize, position: Point<Pixels>, cx: &mut Context<Self>) {
        let Some((owner, start, _)) = self.drag else {
            return;
        };
        if owner != row {
            return;
        }
        self.drag = None;
        let b = self.bounds[row].get();
        let x: f32 = (position.x - b.origin.x) / b.size.width;
        if (x - start).abs() * f32::from(b.size.width) >= 6. {
            if let Some(range) = self.range().zoom(start, x) {
                cx.emit(TimelineEvent::Zoom(range));
            }
        } else {
            let at = self.inspected_time(row, x);
            if let Some(event) = dashboard::select_availability(
                &self.availability,
                Group::ALL[row],
                at,
                self.live.generated_utc,
            ) {
                self.selected = Some(event.clone());
                cx.emit(TimelineEvent::Select(event));
            }
        }
        cx.notify();
    }
    fn plot(&self, row: usize, cx: &mut Context<Self>) -> AnyElement {
        let model = self.models[row].clone();
        let geometry = self.geometry[row].clone();
        let prepared = geometry.clone();
        let bounds_cell = self.bounds[row].clone();
        let pointer = self.pointer;
        let drag = self.drag;
        let selected = self.selected.clone();
        let primary = cx.theme().primary;
        let no_data = primary.opacity(if cx.theme().is_dark() { 0.12 } else { 0.07 });
        let grid = cx.theme().border.opacity(0.6);
        let white = rgb(0xffffff).opacity(0.7);
        div()
            .id(("availability-plot", row))
            .w_full()
            .h(px(48.))
            .relative()
            .cursor_crosshair()
            .on_mouse_move(cx.listener(move |this, event: &MouseMoveEvent, _, cx| {
                let bounds = this.bounds[row].get();
                if f32::from(bounds.size.width) <= 0. {
                    return;
                }
                let x: f32 =
                    ((event.position.x - bounds.origin.x) / bounds.size.width).clamp(0., 1.);
                this.hover_row = row;
                this.pointer_fraction = Some(x);
                if let Some((owner, a, _)) = this.drag {
                    this.drag = Some((owner, a, x));
                }
                this.readout(this.inspected_time(row, x));
                cx.notify();
            }))
            .on_hover(cx.listener(|this, inside, _, cx| {
                if !inside && this.drag.is_none() {
                    this.clear_pointer(cx);
                }
            }))
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(move |this, event: &MouseDownEvent, window, cx| {
                    let b = this.bounds[row].get();
                    if f32::from(b.size.width) > 0. {
                        let x: f32 = ((event.position.x - b.origin.x) / b.size.width).clamp(0., 1.);
                        this.drag = Some((row, x, x));
                        this.hover_row = row;
                        window.focus(&this.focus, cx);
                        cx.notify();
                    }
                }),
            )
            .on_mouse_up(
                MouseButton::Left,
                cx.listener(move |this, event: &MouseUpEvent, _, cx| {
                    this.finish_drag(row, event.position, cx)
                }),
            )
            .on_mouse_up_out(
                MouseButton::Left,
                cx.listener(move |this, event: &MouseUpEvent, _, cx| {
                    if this.drag.is_some_and(|(owner, _, _)| owner == row) {
                        this.finish_drag(row, event.position, cx);
                    }
                }),
            )
            .child(
                canvas(
                    move |b, _, _| {
                        bounds_cell.set(b);
                        let width = f32::from(b.size.width).round().max(1.) as usize;
                        if prepared.borrow().width != width {
                            *prepared.borrow_mut() = Geometry::build(&model, width);
                        }
                    },
                    {
                        let model = self.models[row].clone();
                        move |b, _, window, _| {
                            let width = f32::from(b.size.width);
                            let origin = b.origin;
                            let rect = |x: f32, w: f32, y: f32, h: f32| {
                                Bounds::new(
                                    point(origin.x + px(x), origin.y + px(y)),
                                    size(px(w), px(h)),
                                )
                            };
                            window.paint_quad(fill(rect(0., width, 12., 20.), no_data));
                            for tick in 1..4 {
                                window.paint_quad(fill(
                                    rect(width * tick as f32 / 4., 1., 4., 36.),
                                    grid,
                                ));
                            }
                            let g = geometry.borrow();
                            for &(a, z, i) in &g.runs {
                                window.paint_quad(fill(
                                    rect(a as f32, (z - a) as f32, 12., 20.),
                                    rgb(model.segments[i].color),
                                ));
                                if model.emphasize && (4..=7).contains(&(z - a)) {
                                    window.paint_quad(outline(
                                        rect(a as f32, (z - a) as f32, 12., 20.),
                                        white,
                                        BorderStyle::Solid,
                                    ));
                                }
                            }
                            if let Some(event) = &selected {
                                let applies = match event.kind {
                                    EventKind::Availability { group, .. } => group.index() == row,
                                    EventKind::Country { .. } => true,
                                };
                                if applies && event.end >= model.start && event.start <= model.end {
                                    let (left, width) = selection_extent(&model, event, width);
                                    let mut q = outline(
                                        rect(left, width, 8., 28.),
                                        primary,
                                        BorderStyle::Solid,
                                    );
                                    q.border_widths = Edges::all(px(2.));
                                    q.corner_radii = Corners::all(px(4.));
                                    window.paint_quad(q);
                                }
                            }
                            if let Some(at) = pointer {
                                let x = fraction(&model, at) * width;
                                window.paint_quad(fill(rect(x, 1., 0., 44.), primary));
                            }
                            if let Some((_, a, z)) = drag {
                                window.paint_quad(fill(
                                    rect(a.min(z) * width, (a - z).abs() * width, 1., 42.),
                                    primary.opacity(0.18),
                                ));
                            }
                        }
                    },
                )
                .w_full()
                .h_full(),
            )
            .into_any_element()
    }
}
impl Render for AvailabilityTimeline {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let lang = self.models[0].language;
        let mut body = div()
            .id("availability-timeline")
            .track_focus(&self.focus)
            .relative()
            .flex()
            .flex_col()
            .gap_4()
            .on_key_down(cx.listener(|this, event: &KeyDownEvent, _, cx| {
                if event.keystroke.key == "escape" {
                    this.clear_pointer(cx);
                }
            }));
        for row in 0..2 {
            let group = &self.live.groups[row];
            let stats = &self.stats[row];
            let successes = group
                .endpoints
                .iter()
                .filter(|e| e.fresh && e.outcome == Some(Outcome::Reachable))
                .count();
            let label = crate::text::translate(
                lang,
                if self.live.paused && self.live.monitor_error.is_none() {
                    "Пауза"
                } else {
                    group.availability.label()
                },
            );
            let color = rgb(status_color(
                group.availability,
                self.live.paused && self.live.monitor_error.is_none(),
            ));
            let mut lane = div()
                .flex()
                .flex_col()
                .when(row > 0, |d| {
                    d.pt_4()
                        .border_t_1()
                        .border_color(cx.theme().border.opacity(0.65))
                })
                .child(
                    div()
                        .flex()
                        .items_center()
                        .justify_between()
                        .gap_2()
                        .flex_wrap()
                        .child(
                            div()
                                .flex()
                                .items_center()
                                .gap_3()
                                .child(
                                    div()
                                        .text_size(px(15.))
                                        .font_weight(FontWeight::SEMIBOLD)
                                        .child(text!(
                                            id = ("lane-name", row),
                                            crate::text::translate(lang, group.group.label())
                                        )),
                                )
                                .child(
                                    div()
                                        .flex()
                                        .items_center()
                                        .gap_2()
                                        .text_size(px(12.))
                                        .text_color(cx.theme().muted_foreground)
                                        .child(div().size(px(7.)).rounded_full().bg(color))
                                        .child(label),
                                ),
                        )
                        .child(
                            crate::fluent::compact_button(("expand-endpoints", row))
                                .ghost()
                                .label(format!(
                                    "{successes}/{}  {}",
                                    group.endpoints.len(),
                                    lang.choose(
                                        if self.expanded[row] {
                                            "Скрыть адреса"
                                        } else {
                                            "Адреса"
                                        },
                                        if self.expanded[row] {
                                            "Hide addresses"
                                        } else {
                                            "Addresses"
                                        }
                                    )
                                ))
                                .on_click(cx.listener(move |this, _, _, cx| {
                                    this.expanded[row] = !this.expanded[row];
                                    this.clear_pointer(cx);
                                    cx.notify();
                                })),
                        ),
                )
                .child(self.plot(row, cx));
            let percent = if stats.observed_ms > 0 {
                format!(
                    "{:.2}%",
                    stats.online_ms as f64 * 100. / stats.observed_ms as f64
                )
            } else {
                "–".into()
            };
            let summary = if stats.recorded {
                format!(
                    "{}: {percent}    {}: {}    {}: {}",
                    lang.choose("Доступность", "Availability"),
                    lang.choose("Сбоев", "Outages"),
                    stats.outages,
                    lang.choose("Нет ответа", "No response"),
                    recorded_duration(stats.offline_duration, lang)
                )
            } else {
                lang.choose(
                    "Нет наблюдений в этом периоде",
                    "No observations in this period",
                )
                .into()
            };
            lane = lane.child(
                div()
                    .flex()
                    .justify_between()
                    .gap_2()
                    .flex_wrap()
                    .text_size(px(12.))
                    .text_color(cx.theme().muted_foreground)
                    .child(text!(id = ("lane-summary", row), summary))
                    .child(text!(
                        id = ("lane-coverage", row),
                        format!(
                            "{} {} / {}",
                            lang.choose("Наблюдалось", "Observed"),
                            lang.duration(stats.observed_ms),
                            lang.duration(self.range().millis())
                        )
                    )),
            );
            if self.expanded[row] {
                let mut endpoints = div()
                    .mt_3()
                    .p_3()
                    .rounded_md()
                    .bg(cx.theme().background.opacity(0.4))
                    .flex()
                    .flex_col()
                    .gap_2()
                    .child(
                        div()
                            .text_size(px(11.))
                            .text_color(cx.theme().muted_foreground)
                            .child(lang.choose(
                                "Результаты текущих проверок адресов",
                                "Current address check results",
                            )),
                    );
                for e in &group.endpoints {
                    let age = e
                        .completed_utc
                        .map(|t| (self.live.generated_utc - t).num_milliseconds());
                    let state = if !e.fresh {
                        lang.choose("нет свежих данных", "no recent data")
                    } else {
                        match e.outcome {
                            Some(Outcome::Reachable) => lang.choose("ответ получен", "responding"),
                            Some(Outcome::Unreachable) => lang.choose("нет ответа", "no response"),
                            _ => lang.choose("не подтверждено", "unconfirmed"),
                        }
                    };
                    let detail = format!(
                        "{state}   {}   {}",
                        e.elapsed_ms
                            .map(|ms| format!("{ms} {}", lang.choose("мс", "ms")))
                            .unwrap_or_else(|| "–".into()),
                        age.filter(|a| *a >= 0)
                            .map(|a| format!(
                                "{} {}",
                                lang.duration(a),
                                lang.choose("назад", "ago")
                            ))
                            .unwrap_or_else(|| "–".into())
                    );
                    let id = e.endpoint.id.clone();
                    endpoints = endpoints.child(
                        div()
                            .flex()
                            .items_center()
                            .justify_between()
                            .gap_2()
                            .flex_wrap()
                            .child(
                                div()
                                    .flex()
                                    .flex_col()
                                    .min_w_0()
                                    .child(e.endpoint.uri.host_str().unwrap_or("").to_string())
                                    .child(
                                        div()
                                            .text_size(px(11.))
                                            .text_color(cx.theme().muted_foreground)
                                            .child(detail),
                                    ),
                            )
                            .child(
                                crate::fluent::compact_button(SharedString::from(format!(
                                    "dashboard-probe-{id}"
                                )))
                                .ghost()
                                .disabled_with_cursor(
                                    e.in_flight
                                        || self.live.paused
                                        || self.live.monitor_error.is_some(),
                                )
                                .label(lang.choose("Проверить сейчас", "Check now"))
                                .on_click(cx.listener(
                                    move |_, _, _, cx| cx.emit(TimelineEvent::Diagnose(id.clone())),
                                )),
                            ),
                    );
                }
                lane = lane.child(endpoints);
            }
            body = body.child(lane);
        }
        if !self.changes.is_empty() {
            let range = self.range();
            let changes = self.changes.clone();
            let paint_changes = changes.clone();
            let measured = self.marker_bounds.clone();
            let prepared = self.marker_cache.clone();
            let painting = prepared.clone();
            let primary = cx.theme().primary;
            let ink = cx.theme().muted_foreground;
            body = body.child(
                div()
                    .flex()
                    .flex_col()
                    .gap_1()
                    .child(
                        div()
                            .text_size(px(11.))
                            .text_color(ink)
                            .child(lang.choose("Смена страны", "Country changes")),
                    )
                    .child(
                        div()
                            .id("country-change-markers")
                            .w_full()
                            .h(px(30.))
                            .cursor_pointer()
                            .on_mouse_move(cx.listener(|this, event: &MouseMoveEvent, _, cx| {
                                let b = this.marker_bounds.get();
                                let width = f32::from(b.size.width);
                                if width <= 0. {
                                    return;
                                }
                                let x: f32 =
                                    ((event.position.x - b.origin.x) / b.size.width).clamp(0., 1.);
                                let at = this.time(x);
                                let index = this.changes.partition_point(|e| e.start < at);
                                let chosen = [index.checked_sub(1), Some(index)]
                                    .into_iter()
                                    .flatten()
                                    .filter(|i| *i < this.changes.len())
                                    .min_by_key(|i| {
                                        (this.changes[*i].start - at).num_milliseconds().abs()
                                    });
                                this.marker_hover = chosen.filter(|i| {
                                    ((this.changes[*i].start - at).num_milliseconds() as f64).abs()
                                        / this.range().millis().max(1) as f64
                                        * width as f64
                                        <= 6.
                                });
                                if let Some(i) = this.marker_hover {
                                    this.country_readout(i);
                                } else {
                                    this.pointer = None;
                                    this.pointer_fraction = None;
                                    this.tooltip = None;
                                }
                                this.tooltip_key = None;
                                cx.notify();
                            }))
                            .on_hover(cx.listener(|this, inside, _, cx| {
                                if !inside {
                                    this.clear_pointer(cx);
                                }
                            }))
                            .on_click(cx.listener(|this, _, _, cx| {
                                if let Some(event) =
                                    this.marker_hover.and_then(|i| this.changes.get(i)).cloned()
                                {
                                    this.selected = Some(event.clone());
                                    cx.emit(TimelineEvent::Select(event));
                                    this.clear_pointer(cx);
                                }
                            }))
                            .child(
                                canvas(
                                    move |b, _, _| {
                                        measured.set(b);
                                        let width =
                                            f32::from(b.size.width).round().clamp(1., 16384.)
                                                as usize;
                                        if prepared.borrow().width != width {
                                            let mut pixels: Vec<Option<(usize, usize)>> =
                                                vec![None; width];
                                            for (i, e) in changes.iter().enumerate() {
                                                let x =
                                                    (((e.start - range.start).num_milliseconds()
                                                        as f64
                                                        / range.millis().max(1) as f64
                                                        * width as f64)
                                                        .round()
                                                        as usize)
                                                        .min(width - 1);
                                                pixels[x] =
                                                    Some((i, pixels[x].map_or(1, |(_, n)| n + 1)));
                                            }
                                            let ticks = pixels
                                                .into_iter()
                                                .enumerate()
                                                .filter_map(|(x, v)| v.map(|(i, n)| (x, i, n)))
                                                .collect();
                                            *prepared.borrow_mut() = MarkerCache { width, ticks };
                                        }
                                    },
                                    move |b, _, window, cx| {
                                        let cache = painting.borrow();
                                        let width = f32::from(b.size.width);
                                        for (j, &(x, i, count)) in cache.ticks.iter().enumerate() {
                                            let x = x as f32 * width / cache.width.max(1) as f32;
                                            window.paint_quad(fill(
                                                Bounds::new(
                                                    point(b.origin.x + px(x), b.origin.y),
                                                    size(px(2.), px(8.)),
                                                ),
                                                primary.opacity(0.85),
                                            ));
                                            let next = cache
                                                .ticks
                                                .get(j + 1)
                                                .map_or(width, |t| t.0 as f32);
                                            let previous = j
                                                .checked_sub(1)
                                                .map_or(-100., |k| cache.ticks[k].0 as f32);
                                            if count == 1
                                                && x - previous >= 54.
                                                && next - x >= 54.
                                                && let EventKind::Country {
                                                    previous, current, ..
                                                } = &paint_changes[i].kind
                                            {
                                                let label = format!("{previous} → {current}");
                                                let line = window.text_system().shape_line(
                                                    label.clone().into(),
                                                    px(10.),
                                                    &[TextRun {
                                                        len: label.len(),
                                                        font: font("Segoe UI"),
                                                        color: ink,
                                                        ..Default::default()
                                                    }],
                                                    None,
                                                );
                                                let left = (x - f32::from(line.width) / 2.).clamp(
                                                    0.,
                                                    (width - f32::from(line.width)).max(0.),
                                                );
                                                let _ = line.paint(
                                                    point(
                                                        b.origin.x + px(left),
                                                        b.origin.y + px(10.),
                                                    ),
                                                    px(14.),
                                                    TextAlign::Left,
                                                    None,
                                                    window,
                                                    cx,
                                                );
                                            }
                                        }
                                    },
                                )
                                .w_full()
                                .h_full(),
                            ),
                    ),
            );
        }
        let range = self.range();
        let mut axis = div()
            .flex()
            .justify_between()
            .text_size(px(11.))
            .text_color(cx.theme().muted_foreground);
        for i in 0..=4 {
            let at = range.start + chrono::Duration::milliseconds(range.millis() * i / 4);
            axis = axis.child(text!(
                id = ("shared-axis", i as usize),
                at.with_timezone(&Local)
                    .format(if i == 0 || i == 4 {
                        "%d.%m %H:%M:%S"
                    } else if range.millis() > 86_400_000 {
                        "%d.%m %H:%M"
                    } else {
                        "%H:%M"
                    })
                    .to_string()
            ));
        }
        body = body.child(axis);
        if let Some((title, detail)) = &self.tooltip {
            body = body.child(
                deferred(
                    div()
                        .absolute()
                        .right_0()
                        .top(px(if self.hover_row == 0 { 72. } else { 8. }))
                        .max_w(px(360.))
                        .p_3()
                        .rounded_lg()
                        .border_1()
                        .border_color(cx.theme().border)
                        .bg(cx.theme().popover)
                        .text_color(cx.theme().popover_foreground)
                        .shadow_md()
                        .text_size(px(13.))
                        .line_height(px(18.))
                        .font_weight(FontWeight::MEDIUM)
                        .child(
                            div()
                                .font_weight(FontWeight::SEMIBOLD)
                                .child(text!(id = "timeline-readout-title", title.clone())),
                        )
                        .child(
                            div()
                                .mt_2()
                                .child(text!(id = "timeline-readout-detail", detail.clone())),
                        ),
                )
                .with_priority(2),
            );
        }
        body
    }
}

#[cfg(test)]
mod tests {
    use super::{inspected_at, recorded_index, selection_extent};
    use crate::{
        charts::{Geometry, availability, fraction, status_color},
        dashboard::{self, EventKind},
    };
    use chrono::{DateTime, Utc};
    use netlights_core::{
        history::{AvailabilityHistory, AvailabilitySpan},
        model::{Availability, Group},
    };
    #[test]
    fn enlarged_marker_remains_attached_to_real_time_after_live_geometry_refresh() {
        let end: DateTime<Utc> = "2026-11-12T12:00:00Z".parse().unwrap();
        let start = end - chrono::Duration::days(7);
        let at = start + chrono::Duration::hours(30);
        let h = AvailabilityHistory {
            spans: vec![
                AvailabilitySpan {
                    group: Group::Ru,
                    state: Availability::Online,
                    paused: false,
                    started_utc: start,
                    ended_utc: Some(end),
                },
                AvailabilitySpan {
                    group: Group::Ru,
                    state: Availability::Offline,
                    paused: false,
                    started_utc: at,
                    ended_utc: Some(at + chrono::Duration::seconds(1)),
                },
            ],
            ..Default::default()
        };
        for delta in [0, 10] {
            let model = availability(
                &h,
                Group::Ru,
                start + chrono::Duration::seconds(delta),
                end + chrono::Duration::seconds(delta),
                true,
            );
            let geometry = Geometry::build(&model, 1000);
            let x = fraction(&model, at) - 0.002;
            let inspected = inspected_at(&model, &geometry, x);
            assert!(inspected >= at && inspected <= at + chrono::Duration::seconds(1));
            assert!(matches!(
                dashboard::select_availability(&h, Group::Ru, inspected, end)
                    .unwrap()
                    .kind,
                EventKind::Availability {
                    state: Availability::Offline,
                    ..
                }
            ));
        }
    }
    #[test]
    fn closed_failure_boundary_and_readout_select_the_following_online_interval() {
        let end: DateTime<Utc> = "2026-11-12T12:00:00Z".parse().unwrap();
        let at = end - chrono::Duration::seconds(10);
        let start = at - chrono::Duration::seconds(10);
        let h = AvailabilityHistory {
            spans: vec![
                AvailabilitySpan {
                    group: Group::Ru,
                    state: Availability::Offline,
                    paused: false,
                    started_utc: start,
                    ended_utc: Some(at),
                },
                AvailabilitySpan {
                    group: Group::Ru,
                    state: Availability::Online,
                    paused: false,
                    started_utc: at,
                    ended_utc: Some(end),
                },
            ],
            ..Default::default()
        };
        let m = availability(&h, Group::Ru, start, end, true);
        let positive: Vec<_> = m
            .segments
            .iter()
            .enumerate()
            .filter_map(|(i, s)| (s.start < s.end).then_some(i))
            .collect();
        let selected = dashboard::select_availability(&h, Group::Ru, at, end).unwrap();
        assert!(matches!(
            selected.kind,
            EventKind::Availability {
                state: Availability::Online,
                ..
            }
        ));
        let read = recorded_index(&m, &positive, at).unwrap();
        assert_eq!(
            m.segments[read].color,
            status_color(Availability::Online, false)
        );
        let e = dashboard::HistoryEvent {
            start: start - chrono::Duration::hours(2),
            end: start + chrono::Duration::seconds(2),
            ongoing: false,
            kind: selected.kind,
        };
        let (left, width) = selection_extent(&m, &e, 1000.);
        assert_eq!(left, 0.);
        assert!((100. ..110.).contains(&width));
        let e = dashboard::HistoryEvent {
            start: end,
            end,
            ..e
        };
        let (left, width) = selection_extent(&m, &e, 1000.);
        assert_eq!(left + width, 1000.);
        assert_eq!(width, 6.);
    }
    #[test]
    fn natural_zero_width_record_does_not_hide_the_following_positive_interval() {
        let end: DateTime<Utc> = "2026-11-12T12:00:00Z".parse().unwrap();
        let start = end - chrono::Duration::hours(1);
        let h = AvailabilityHistory {
            spans: vec![
                AvailabilitySpan {
                    group: Group::Ru,
                    state: Availability::Offline,
                    paused: false,
                    started_utc: start,
                    ended_utc: Some(start),
                },
                AvailabilitySpan {
                    group: Group::Ru,
                    state: Availability::Online,
                    paused: false,
                    started_utc: start,
                    ended_utc: Some(end),
                },
            ],
            ..Default::default()
        };
        let m = availability(&h, Group::Ru, start, end, true);
        let g = Geometry::build(&m, 1000);
        assert_eq!(
            m.segments[g.hits[500].unwrap()].color,
            status_color(Availability::Online, false)
        );
        assert!(
            m.segments
                .iter()
                .any(|s| s.start == s.end && s.priority == 3)
        );
    }
}
