use chrono::{DateTime, Local, Utc};
use gpui_kit::{component::ActiveTheme, prelude::*, *};
use netlights_core::locale::Language;
use netlights_core::{countries, history::*, model::*};
use std::{
    cell::{Cell, RefCell},
    rc::Rc,
    sync::Arc,
};
mod timeline;
pub use timeline::{AvailabilityTimeline, TimelineEvent};

#[derive(Clone, Debug)]
pub struct Segment {
    pub title: String,
    pub detail: String,
}
#[derive(Clone)]
pub struct ChartModel {
    pub language: Language,
    pub start: DateTime<Utc>,
    pub end: DateTime<Utc>,
    segments: Vec<PlotSegment>,
    pub buckets: Vec<FrequencyBucket>,
    pub emphasize: bool,
    source: Option<SourceHistory>,
    #[cfg(test)]
    detail_builds: Arc<std::sync::atomic::AtomicUsize>,
}
#[derive(Clone, Copy, Debug)]
struct PlotSegment {
    start: DateTime<Utc>,
    end: DateTime<Utc>,
    label: &'static str,
    detail: SegmentDetail,
    color: u32,
    priority: u8,
}
#[derive(Clone, Copy, Debug)]
enum SegmentDetail {
    Availability {
        start: DateTime<Utc>,
        end: DateTime<Utc>,
        state: Availability,
        paused: bool,
        open: bool,
    },
    Country {
        start: DateTime<Utc>,
        end: DateTime<Utc>,
    },
    Source(usize),
}
#[derive(Clone)]
struct SourceHistory {
    history: Arc<LocationHistory>,
    which: usize,
}
#[derive(Clone)]
struct Tooltip {
    title: SharedString,
    detail: SharedString,
}
impl ChartModel {
    pub fn empty(start: DateTime<Utc>, end: DateTime<Utc>) -> Self {
        Self {
            language: Language::Ru,
            start,
            end,
            segments: vec![],
            buckets: vec![],
            emphasize: false,
            source: None,
            #[cfg(test)]
            detail_builds: Arc::new(std::sync::atomic::AtomicUsize::new(0)),
        }
    }
    fn materialize(&self, index: usize) -> Option<Segment> {
        let lang = self.language;
        let segment = self.segments.get(index)?;
        #[cfg(test)]
        self.detail_builds
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        let (title, detail) = match segment.detail {
            SegmentDetail::Availability {
                start,
                end,
                state,
                paused,
                ..
            } => (
                if paused {
                    lang.choose("Монитор на паузе", "Monitor paused")
                } else {
                    crate::text::translate(lang, state.label())
                },
                format!(
                    "{} – {}\n{}",
                    local_time(start),
                    local_time(end),
                    recorded_duration(end - start, lang)
                ),
            ),
            SegmentDetail::Country { start, end } => (
                lang.country_name(segment.label),
                format!(
                    "{}: {} – {}\n{}\n{}",
                    lang.choose("Период истории", "History period"),
                    local_time(start),
                    local_time(end),
                    recorded_duration(end - start, lang),
                    lang.choose(
                        "Точное время перехода между проверками неизвестно.",
                        "The exact transition time between checks is unknown."
                    )
                ),
            ),
            SegmentDetail::Source(observation) => {
                let source = self.source.as_ref()?;
                let o = source.history.observations.get(observation)?;
                let version = match source.which {
                    0 => "country.is",
                    1 => o
                        .database_version
                        .as_deref()
                        .unwrap_or(lang.choose("версия не записана", "version not recorded")),
                    _ => o
                        .iplocate_database_version
                        .as_deref()
                        .unwrap_or(lang.choose("версия не записана", "version not recorded")),
                };
                let disagreement = [
                    o.online_country.as_deref(),
                    o.local_country.as_deref(),
                    o.iplocate_country.as_deref(),
                ]
                .iter()
                .flatten()
                .any(|code| *code != segment.label);
                (
                    lang.country_name(segment.label),
                    format!(
                        "{}: {} – {}\n{}: {}\n{}{}{}",
                        lang.choose("Проверки", "Checks"),
                        local_time(o.observed_utc),
                        local_time(o.until()),
                        if o.resolution_source.as_deref() == Some("local-estimate") {
                            lang.choose("Последний известный IP", "Last known IP")
                        } else {
                            "IP"
                        },
                        o.public_ip
                            .as_deref()
                            .unwrap_or(lang.choose("не записан", "not recorded")),
                        version,
                        if disagreement {
                            lang.choose("\nИсточники расходятся.", "\nSources disagree.")
                        } else {
                            ""
                        },
                        if o.resolution_source.as_deref() == Some("local-estimate") {
                            lang.choose(
                                "\nОценка по базе; онлайн-страна не подтверждена.",
                                "\nDatabase estimate; the online country isn't confirmed.",
                            )
                        } else {
                            ""
                        }
                    ),
                )
            }
        };
        Some(Segment {
            title: title.into(),
            detail,
        })
    }
    fn tooltip(&self, index: usize) -> Option<Tooltip> {
        if let Some(bucket) = self.buckets.get(index) {
            return Some(Tooltip {
                title: format!(
                    "{}: {}",
                    self.language.choose("Смен", "Changes"),
                    bucket.changes
                )
                .into(),
                detail: format!(
                    "{} – {}{}",
                    local_time(bucket.start),
                    local_time(bucket.end),
                    if bucket.observed {
                        ""
                    } else {
                        self.language.choose(
                            "\nВ этом интервале нет наблюдений страны.",
                            "\nNo country observations in this interval.",
                        )
                    }
                )
                .into(),
            });
        }
        let segment = self.materialize(index)?;
        Some(Tooltip {
            title: segment.title.into(),
            detail: segment.detail.into(),
        })
    }
}
fn canonical_iso(code: &str) -> Option<&'static str> {
    countries::COUNTRIES
        .binary_search_by_key(&code, |(iso, _)| *iso)
        .ok()
        .map(|index| countries::COUNTRIES[index].0)
}
pub fn country_color(iso: &str) -> u32 {
    match iso {
        "RU" => 0xdc0000,
        "DE" => 0x0f7e76,
        "PL" => 0x6c50a8,
        "NL" => 0xa83850,
        "FI" => 0x206c94,
        "US" => 0x3685cc,
        "JP" => 0xd65880,
        "FR" => 0xc87824,
        "GB" => 0x5e68b4,
        "TR" => 0xd26c4e,
        "IN" => 0xb77d30,
        "CA" => 0xb94f6c,
        _ => {
            let colors = [
                0x478d83, 0x7270b7, 0x9a64a1, 0x3c8fa7, 0x8b8242, 0x4b8d66, 0xae6e66, 0x5a79a0,
            ];
            colors[iso
                .bytes()
                .fold(0usize, |a, b| a.wrapping_mul(31) + b as usize)
                % colors.len()]
        }
    }
}
pub fn status_color(state: Availability, paused: bool) -> u32 {
    if paused {
        return 0x8b7bc1;
    }
    match state {
        Availability::Online => 0x2aa87a,
        Availability::Limited => 0xe9b33c,
        Availability::Offline => 0xe65060,
        Availability::Unknown => 0x98a5b5,
    }
}
pub fn country_text_color(color: u32) -> u32 {
    let channel = |v: u32| {
        let s = v as f64 / 255.;
        if s <= 0.04045 {
            s / 12.92
        } else {
            ((s + 0.055) / 1.055).powf(2.4)
        }
    };
    let luminance = 0.2126 * channel((color >> 16) & 255)
        + 0.7152 * channel((color >> 8) & 255)
        + 0.0722 * channel(color & 255);
    if luminance > 0.179 {
        0x101820
    } else {
        0xffffff
    }
}
pub fn local_time(t: DateTime<Utc>) -> String {
    t.with_timezone(&Local).format("%d.%m %H:%M:%S").to_string()
}
pub fn recorded_duration(elapsed: chrono::Duration, lang: Language) -> String {
    let ms = elapsed.num_milliseconds();
    if elapsed > chrono::Duration::zero() && ms == 0 {
        lang.choose("<1 мс", "<1 ms").into()
    } else if (1..1000).contains(&ms) {
        format!("{ms} {}", lang.choose("мс", "ms"))
    } else {
        lang.duration(ms.max(0))
    }
}
pub fn availability(
    history: &AvailabilityHistory,
    group: Group,
    start: DateTime<Utc>,
    end: DateTime<Utc>,
    emphasize: bool,
) -> ChartModel {
    let segments =
        crate::dashboard::intervals(history, group, crate::dashboard::TimeRange { start, end })
            .into_iter()
            .map(|interval| {
                let s = &history.spans[interval.source];
                PlotSegment {
                    start: interval.start,
                    end: interval.end,
                    label: "",
                    detail: SegmentDetail::Availability {
                        start: s.started_utc,
                        end: s.ended_utc.unwrap_or(end),
                        state: s.state,
                        paused: s.paused,
                        open: s.ended_utc.is_none(),
                    },
                    color: status_color(s.state, s.paused),
                    priority: crate::dashboard::priority(s),
                }
            })
            .collect();
    let mut model = ChartModel::empty(start, end);
    model.segments = segments;
    model.emphasize = emphasize;
    model
}
pub fn locations(
    history: &LocationHistory,
    start: DateTime<Utc>,
    end: DateTime<Utc>,
) -> ChartModel {
    let segments = history
        .stays
        .iter()
        .filter_map(|s| {
            let b = s
                .ended_utc
                .unwrap_or(history.last_observed_utc.unwrap_or(s.started_utc))
                .min(end);
            if b < start
                || s.started_utc > end
                || (s.ended_utc.is_some() && b == start && s.started_utc < b)
            {
                return None;
            }
            let label = canonical_iso(&s.iso)?;
            Some(PlotSegment {
                start: s.started_utc.max(start),
                end: b,
                label,
                detail: SegmentDetail::Country {
                    start: s.started_utc,
                    end: b,
                },
                color: country_color(&s.iso),
                priority: 1,
            })
        })
        .collect();
    let mut model = ChartModel::empty(start, end);
    model.segments = segments;
    model
}
pub fn source(
    history: &Arc<LocationHistory>,
    which: usize,
    start: DateTime<Utc>,
    end: DateTime<Utc>,
) -> ChartModel {
    let segments = history
        .observations
        .iter()
        .enumerate()
        .filter_map(|(index, o)| {
            if which == 0 && o.resolution_source.as_deref() != Some("country.is") {
                return None;
            }
            if o.until() < start || o.observed_utc > end {
                return None;
            }
            let code = match which {
                0 => o.online_country.as_deref(),
                1 => o.local_country.as_deref(),
                _ => o.iplocate_country.as_deref(),
            };
            let code = canonical_iso(code?)?;
            Some(PlotSegment {
                start: o.observed_utc.max(start),
                end: o.until().min(end),
                label: code,
                detail: SegmentDetail::Source(index),
                color: country_color(code),
                priority: 1,
            })
        })
        .collect();
    let mut model = ChartModel::empty(start, end);
    model.segments = segments;
    model.source = Some(SourceHistory {
        history: Arc::clone(history),
        which,
    });
    model
}
pub fn frequency_model(
    history: &LocationHistory,
    start: DateTime<Utc>,
    end: DateTime<Utc>,
) -> ChartModel {
    let mut bounded = LocationHistory {
        stays: history.stays.clone(),
        last_observed_utc: history.last_observed_utc,
        ..Default::default()
    };
    for stay in &mut bounded.stays {
        if stay.ended_utc.is_none() {
            stay.ended_utc = history.last_observed_utc;
        }
    }
    let mut model = ChartModel::empty(start, end);
    model.buckets = frequency(&bounded, start, end, 120);
    model
}

pub struct Chart {
    model: Arc<ChartModel>,
    hover: Option<usize>,
    tooltip: Option<Tooltip>,
    pointer: Option<f32>,
    hover_position: Option<(f32, f32)>,
    hover_bounds: Option<Bounds<Pixels>>,
    bounds: Rc<Cell<Bounds<Pixels>>>,
    selected: Option<Segment>,
    geometry: Rc<RefCell<Geometry>>,
}
#[derive(Default)]
struct Geometry {
    width: usize,
    hits: Vec<Option<usize>>,
    marker_hits: Vec<Option<usize>>,
    runs: Vec<(usize, usize, usize)>,
    marker_runs: Vec<(usize, usize, usize)>,
    markers: Vec<bool>,
    rects: Vec<(f32, f32)>,
}
impl Geometry {
    fn build(model: &ChartModel, width: usize) -> Self {
        let width = width.clamp(1, 16384);
        let mut hits = vec![None; width];
        let mut painted = vec![None; width];
        let mut marked = vec![None; width];
        let mut marker_hits: Vec<Option<usize>> = vec![None; width];
        let availability_model = model
            .segments
            .first()
            .is_some_and(|s| matches!(s.detail, SegmentDetail::Availability { .. }));
        let mut rects: Vec<(f32, f32)> = Vec::with_capacity(model.segments.len());
        let mut markers = Vec::with_capacity(model.segments.len());
        let priorities: Vec<_> = model
            .segments
            .iter()
            .map(|s| {
                if matches!(s.detail, SegmentDetail::Availability { .. }) {
                    s.priority
                } else if (fraction(model, s.end) - fraction(model, s.start)) * (width as f32) < 1.
                {
                    2
                } else {
                    1
                }
            })
            .collect();
        for (index, s) in model.segments.iter().enumerate() {
            let natural_a = fraction(model, s.start) * width as f32;
            let natural_b = fraction(model, s.end) * width as f32;
            let (a, b) = (natural_a, natural_b);
            let availability = matches!(s.detail, SegmentDetail::Availability { .. });
            let marker = !availability && b - a < 1.;
            let min = if availability {
                if model.emphasize { 6. } else { 0. }
            } else {
                0.
            };
            let center = (a + b) / 2.;
            let (a, b) = if marker {
                let a = center.round().clamp(0., width as f32 - 1.);
                (a, a + 1.)
            } else {
                (
                    a.min(center - min / 2.).max(0.),
                    b.max(center + min / 2.).min(width as f32),
                )
            };
            rects.push((a, b));
            markers.push(marker);
            let update = |raster: &mut [Option<usize>], left: f32, right: f32| {
                for pixel in &mut raster
                    [(left.floor() as usize).min(width)..(right.ceil() as usize).min(width)]
                {
                    if pixel.is_none_or(|old| {
                        (priorities[index], s.start) >= (priorities[old], model.segments[old].start)
                    }) {
                        *pixel = Some(index);
                    }
                }
            };
            if !availability {
                // Sample real time at each pixel centre. Tiny events remain visible
                // above the band without expanding their duration or hiding coverage.
                let left = (natural_a - 0.5).ceil().max(0.) as usize;
                let right = (natural_b - 0.5).ceil().max(0.) as usize;
                for pixel in &mut painted[left.min(width)..right.min(width)] {
                    *pixel = Some(index);
                }
                if marker {
                    update(&mut marked, a, b);
                    let left = (center - 3.).floor().max(0.) as usize;
                    let right = (center + 3.).ceil().min(width as f32) as usize;
                    for (pixel, hit) in marker_hits.iter_mut().enumerate().take(right).skip(left) {
                        let distance = (pixel as f32 + 0.5 - (a + b) / 2.).abs();
                        if hit.is_none_or(|old| {
                            let (old_a, old_b) = rects[old];
                            distance <= (pixel as f32 + 0.5 - (old_a + old_b) / 2.).abs()
                        }) {
                            *hit = Some(index);
                        }
                    }
                }
            }
            if availability {
                update(&mut hits, a, b);
            }
        }
        // The exact visible tick wins over a neighbouring tick's enlarged target.
        for (hit, visible) in marker_hits.iter_mut().zip(&marked) {
            if visible.is_some() {
                *hit = *visible;
            }
        }
        let runs = raster_runs(if availability_model { &hits } else { &painted });
        if !availability_model {
            hits = painted;
        }
        Self {
            width,
            hits,
            marker_hits,
            runs,
            marker_runs: raster_runs(&marked),
            markers,
            rects,
        }
    }
}
fn raster_runs(pixels: &[Option<usize>]) -> Vec<(usize, usize, usize)> {
    let mut runs = Vec::new();
    let mut i = 0;
    while i < pixels.len() {
        let first = i;
        let value = pixels[i];
        while i < pixels.len() && pixels[i] == value {
            i += 1;
        }
        if let Some(index) = value {
            runs.push((first, i, index));
        }
    }
    runs
}
impl Chart {
    pub fn new(model: ChartModel) -> Self {
        Self {
            model: Arc::new(model),
            hover: None,
            tooltip: None,
            pointer: None,
            hover_position: None,
            hover_bounds: None,
            bounds: Rc::new(Cell::new(Bounds::default())),
            selected: None,
            geometry: Rc::new(RefCell::new(Geometry::default())),
        }
    }
    pub fn set(&mut self, model: ChartModel, cx: &mut Context<Self>) {
        self.replace_model(model);
        cx.notify();
    }
    fn replace_model(&mut self, model: ChartModel) {
        let same_context = self.model.language == model.language
            && self.model.end - self.model.start == model.end - model.start
            && self.model.buckets.is_empty() == model.buckets.is_empty()
            && self.model.source.as_ref().map(|s| s.which)
                == model.source.as_ref().map(|s| s.which)
            && self
                .model
                .segments
                .first()
                .map(|s| std::mem::discriminant(&s.detail))
                == model
                    .segments
                    .first()
                    .map(|s| std::mem::discriminant(&s.detail))
            && !(model.segments.is_empty() && model.buckets.is_empty());
        let width = self.geometry.borrow().width;
        let position = self
            .hover_position
            .filter(|_| same_context && width > 0 && self.hover_bounds == Some(self.bounds.get()));
        self.model = Arc::new(model);
        self.hover = None;
        self.tooltip = None;
        self.hover_position = position;
        if let Some((x, y)) = position {
            *self.geometry.borrow_mut() = Geometry::build(&self.model, width);
            // Re-evaluate against the new data, even when the segment index is
            // unchanged. A stationary cursor mustn't keep an obsolete tooltip.
            self.update_hover(self.hit(x, y));
        } else {
            self.geometry.borrow_mut().width = 0;
            self.pointer = None;
            self.hover_bounds = None;
        }
        // A click queued for the previous period/language can't revive an obsolete card.
        self.selected = None;
    }
    pub fn take_selected(&mut self) -> Option<Segment> {
        self.selected.take()
    }
    pub fn clear_pointer(&mut self, cx: &mut Context<Self>) {
        let changed = self.update_hover(None) || self.pointer.is_some();
        self.pointer = None;
        self.hover_position = None;
        self.hover_bounds = None;
        if changed {
            cx.notify();
        }
    }
    fn update_hover(&mut self, hit: Option<usize>) -> bool {
        if self.hover == hit {
            return false;
        }
        self.hover = hit;
        self.tooltip = hit.and_then(|index| self.model.tooltip(index));
        if let Some(index) = hit
            && self.geometry.borrow().markers.get(index) == Some(&true)
            && let Some(tooltip) = self.tooltip.as_mut()
        {
            tooltip.detail = format!(
                "{}\n{}",
                tooltip.detail,
                self.model.language.choose(
                    "Короткое событие: размер метки не передаёт длительность.",
                    "Short event marker; its width doesn't represent duration."
                )
            )
            .into();
        }
        true
    }
    fn hit(&self, x: f32, y: f32) -> Option<usize> {
        if !self.model.buckets.is_empty() {
            return Some(
                ((x * self.model.buckets.len() as f32) as usize).min(self.model.buckets.len() - 1),
            );
        }
        let geometry = self.geometry.borrow();
        let i = ((x * geometry.width as f32) as usize).min(geometry.width.saturating_sub(1));
        let hits = if (0. ..12.).contains(&y) {
            &geometry.marker_hits
        } else if (12. ..=28.).contains(&y) {
            &geometry.hits
        } else {
            return None;
        };
        hits.get(i).copied().flatten()
    }
}
fn fraction(model: &ChartModel, time: DateTime<Utc>) -> f32 {
    ((time - model.start).num_milliseconds() as f64
        / (model.end - model.start).num_milliseconds().max(1) as f64)
        .clamp(0., 1.) as f32
}
fn frequency_max(buckets: &[FrequencyBucket]) -> usize {
    buckets
        .iter()
        .map(|b| b.changes)
        .max()
        .unwrap_or(0)
        .max(4)
        .div_ceil(4)
        * 4
}
impl Render for Chart {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let primary = theme.primary;
        let axis_text = theme.muted_foreground;
        let grid = theme
            .border
            .opacity(if theme.is_dark() { 0.55 } else { 0.7 });
        let no_data = primary.opacity(if theme.is_dark() { 0.16 } else { 0.08 });
        let short_border = rgb(0xffffff).opacity(if theme.is_dark() { 0.7 } else { 0.9 });
        let tooltip_surface = theme.popover;
        let tooltip_text = theme.popover_foreground;
        let tooltip_border = theme.border;
        let model = self.model.clone();
        let for_paint = model.clone();
        let measured = self.bounds.clone();
        let geometry = self.geometry.clone();
        let painting_geometry = geometry.clone();
        let preparing_model = model.clone();
        let hover = self.hover;
        let pointer = self.pointer;
        let is_frequency = !model.buckets.is_empty();
        let height = if is_frequency { 138. } else { 42. };
        let mut root = div()
            .id("plot")
            .relative()
            .w_full()
            .font_family("Segoe UI")
            .text_size(px(12.))
            .h(px(height + 30.))
            .when(is_frequency, |d| d.pl(px(26.)))
            .cursor_crosshair()
            .on_mouse_move(cx.listener(|this, event: &MouseMoveEvent, _, cx| {
                let b = this.bounds.get();
                let x: f32 = (event.position.x - b.origin.x) / b.size.width;
                if !(0. ..=1.).contains(&x) {
                    return;
                }
                let y: f32 = (event.position.y - b.origin.y).into();
                this.hover_position = Some((x, y));
                this.hover_bounds = Some(b);
                let hit = this.hit(x, y);
                let pixel: f32 = event.position.x.into();
                if this.update_hover(hit) || this.pointer.is_none_or(|p| (p - pixel).abs() >= 1.) {
                    this.pointer = Some(pixel);
                    cx.notify();
                }
            }))
            .on_hover(cx.listener(|this, inside, _, cx| {
                if !inside {
                    this.update_hover(None);
                    this.pointer = None;
                    this.hover_position = None;
                    this.hover_bounds = None;
                    cx.notify();
                }
            }))
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(|this, _, _, cx| {
                    if let Some(i) = this.hover {
                        this.selected = this.model.materialize(i);
                        cx.notify();
                    }
                }),
            )
            .child(
                canvas(
                    move |bounds, _, _| {
                        measured.set(bounds);
                        let width = f32::from(bounds.size.width).round().max(1.) as usize;
                        if geometry.borrow().width != width {
                            *geometry.borrow_mut() = Geometry::build(&preparing_model, width);
                        }
                    },
                    move |bounds, _, window, cx| {
                        let w: f32 = bounds.size.width.into();
                        let origin = bounds.origin;
                        let y = origin.y + px(if is_frequency { 8. } else { 12. });
                        let h = if is_frequency { height - 16. } else { 16. };
                        let rect = |a: f32, b: f32| {
                            Bounds::new(
                                point(origin.x + px(a), y),
                                size(px((b - a).max(0.)), px(h)),
                            )
                        };
                        if !is_frequency {
                            window.paint_quad(fill(rect(0., w), no_data));
                            let geometry = painting_geometry.borrow();
                            for &(a, b, i) in &geometry.runs {
                                let s = &for_paint.segments[i];
                                let a = a as f32;
                                let b = b as f32;
                                window.paint_quad(fill(rect(a, b), rgb(s.color)));
                                // A border must leave a coloured interior. Dense
                                // histories can split a visible mark into 1px runs.
                                if for_paint.emphasize && (4. ..=7.).contains(&(b - a)) {
                                    window.paint_quad(outline(
                                        rect(a, b),
                                        short_border,
                                        BorderStyle::Solid,
                                    ));
                                }
                                if !s.label.is_empty() && b - a >= 28. {
                                    let line = window.text_system().shape_line(
                                        s.label.into(),
                                        px(12.),
                                        &[TextRun {
                                            len: s.label.len(),
                                            font: font("Segoe UI").bold(),
                                            color: rgb(country_text_color(s.color)).into(),
                                            ..Default::default()
                                        }],
                                        None,
                                    );
                                    let _ = line.paint(
                                        point(origin.x + px((a + b) / 2.) - line.width / 2., y),
                                        px(16.),
                                        TextAlign::Left,
                                        None,
                                        window,
                                        cx,
                                    );
                                }
                            }
                            // Subpixel country intervals are events, not inflated
                            // durations. Keep their ticks above the actual colour band.
                            for &(a, b, i) in &geometry.marker_runs {
                                window.paint_quad(fill(
                                    Bounds::new(
                                        point(origin.x + px(a as f32), origin.y + px(4.)),
                                        size(px((b - a) as f32), px(6.)),
                                    ),
                                    rgb(for_paint.segments[i].color),
                                ));
                            }
                            if let Some(i) = hover
                                && let Some(&(a, b)) = geometry.rects.get(i)
                            {
                                let marker = geometry.markers[i];
                                let r = Bounds::new(
                                    point(
                                        origin.x + px(a - 3.),
                                        if marker {
                                            origin.y + px(1.)
                                        } else {
                                            y - px(4.)
                                        },
                                    ),
                                    size(px(b - a + 6.), px(if marker { 12. } else { h + 8. })),
                                );
                                let mut q = outline(r, primary, BorderStyle::Solid);
                                q.border_widths = Edges::all(px(2.));
                                q.corner_radii = Corners::all(px(4.));
                                window.paint_quad(q);
                            }
                        } else {
                            let max = frequency_max(&for_paint.buckets) as f32;
                            for (i, b) in for_paint.buckets.iter().enumerate() {
                                if !b.observed {
                                    window.paint_quad(fill(
                                        rect(i as f32 * w / 120., (i + 1) as f32 * w / 120.),
                                        no_data,
                                    ));
                                }
                            }
                            for tick in 0..=4 {
                                let y = y + px(h * tick as f32 / 4.);
                                window.paint_quad(fill(
                                    Bounds::new(point(origin.x, y), size(px(w), px(0.75))),
                                    grid,
                                ));
                            }
                            let mut path = PathBuilder::stroke(px(1.5));
                            for (i, b) in for_paint.buckets.iter().enumerate() {
                                let p = point(
                                    origin.x
                                        + px((i as f32 + 0.5) * w / for_paint.buckets.len() as f32),
                                    y + px(h * (1. - b.changes as f32 / max)),
                                );
                                if i == 0 {
                                    path.move_to(p);
                                } else {
                                    path.line_to(p);
                                }
                            }
                            if let Ok(path) = path.build() {
                                window.paint_path(path, primary);
                            }
                        }
                        if let Some(x) = pointer {
                            let left: f32 = origin.x.into();
                            let x = x.clamp(left, left + w);
                            window.paint_quad(fill(
                                Bounds::new(point(px(x), origin.y), size(px(1.), px(height))),
                                primary.opacity(0.7),
                            ));
                        }
                    },
                )
                .w_full()
                .h(px(height)),
            );
        if is_frequency {
            let max = frequency_max(&model.buckets);
            for tick in 0..=4 {
                root = root.child(
                    div()
                        .absolute()
                        .left_0()
                        .top(px((height - 16.) * tick as f32 / 4.))
                        .w(px(20.))
                        .text_right()
                        .text_size(px(12.))
                        .line_height(px(16.))
                        .text_color(axis_text)
                        .child(format!("{:.0}", max as f32 * (1. - tick as f32 / 4.))),
                );
            }
        }
        root = root.child(
            div()
                .absolute()
                .bottom_0()
                .left(px(if is_frequency { 26. } else { 0. }))
                .right_0()
                .flex()
                .justify_between()
                .text_size(px(12.))
                .line_height(px(16.))
                .text_color(axis_text)
                .child(
                    model
                        .start
                        .with_timezone(&Local)
                        .format("%d.%m %H:%M")
                        .to_string(),
                )
                .child(model.end.with_timezone(&Local).format("%H:%M").to_string()),
        );
        if let Some(tooltip) = &self.tooltip {
            let below = self.bounds.get().origin.y < px(240.);
            root = root.child(
                deferred(
                    div()
                        .absolute()
                        .when(below, |d| d.top(px(height + 38.)))
                        .when(!below, |d| d.bottom(px(height + 26.)))
                        .right_0()
                        .max_w(px(430.).min(self.bounds.get().size.width))
                        .p_3()
                        .rounded_md()
                        .border_1()
                        .border_color(tooltip_border)
                        .bg(tooltip_surface)
                        .text_color(tooltip_text)
                        .text_size(px(13.))
                        .line_height(px(18.))
                        .font_weight(FontWeight::MEDIUM)
                        .shadow_md()
                        .child(
                            div()
                                .font_weight(FontWeight::SEMIBOLD)
                                .child(tooltip.title.clone()),
                        )
                        .child(
                            div()
                                .mt_1()
                                .text_size(px(12.))
                                .child(tooltip.detail.clone()),
                        ),
                )
                .with_priority(2),
            );
        }
        root
    }
}

#[cfg(test)]
mod tests {
    use super::{
        Chart, ChartModel, Geometry, PlotSegment, SegmentDetail, availability, duration_label,
        frequency_max, local_time, locations, source,
    };
    use chrono::Utc;
    use netlights_core::{
        history::{
            AvailabilityHistory, AvailabilitySpan, LocationHistory, LocationObservation,
            LocationStay,
        },
        model::{Availability, Group},
    };
    use std::sync::{Arc, atomic::Ordering};

    #[test]
    fn short_recorded_durations_never_round_positive_events_to_zero() {
        use chrono::Duration;
        use netlights_core::locale::Language::{En, Ru};
        for (duration, en, ru) in [
            (Duration::zero(), "0 s", "0 с"),
            (Duration::microseconds(500), "<1 ms", "<1 мс"),
            (Duration::milliseconds(9), "9 ms", "9 мс"),
            (Duration::milliseconds(29), "29 ms", "29 мс"),
            (Duration::seconds(2), "2 s", "2 с"),
            (Duration::milliseconds(-1), "0 s", "0 с"),
        ] {
            assert_eq!(super::recorded_duration(duration, En), en);
            assert_eq!(super::recorded_duration(duration, Ru), ru);
        }
    }

    fn observation(at: chrono::DateTime<Utc>) -> LocationObservation {
        LocationObservation {
            observed_utc: at,
            public_ip_hash: None,
            country_code: Some("DE".into()),
            local_country: Some("DE".into()),
            online_country: Some("DE".into()),
            third_country: None,
            database_version: Some("v2fly-test".into()),
            public_ip_changed: false,
            country_changed: false,
            classification_changed: false,
            confirmed: true,
            public_ip: Some("192.0.2.1".into()),
            observed_until_utc: Some(at + chrono::Duration::seconds(6)),
            iplocate_country: Some("DE".into()),
            iplocate_database_version: Some("iplocate-test".into()),
            resolution_source: Some("country.is".into()),
        }
    }
    #[test]
    fn changing_tooltip_language_preserves_observations_times_and_source_conflicts() {
        use netlights_core::locale::Language;
        let begin = Utc::now() - chrono::Duration::minutes(2);
        let end = begin + chrono::Duration::minutes(1);
        let mut record = observation(begin);
        record.local_country = Some("FR".into());
        record.observed_until_utc = Some(end);
        let history = Arc::new(LocationHistory {
            stays: vec![LocationStay {
                iso: "DE".into(),
                started_utc: begin,
                ended_utc: Some(end),
            }],
            observations: vec![record],
            last_observed_utc: Some(end),
            ..Default::default()
        });
        let original = serde_json::to_vec(&*history).unwrap();
        let mut main = locations(&history, begin, end);
        let ru = main.materialize(0).unwrap();
        assert_eq!(ru.title, "Германия");
        main.language = Language::En;
        let en = main.materialize(0).unwrap();
        assert_eq!(en.title, "Germany");
        assert!(en.detail.contains("1 min"));
        assert!(en.detail.contains(&local_time(begin)));
        assert!(en.detail.contains("exact transition time"));
        let mut debug = source(&history, 1, begin, end);
        debug.language = Language::En;
        let en = debug.materialize(0).unwrap();
        assert_eq!(en.title, "France");
        assert!(en.detail.contains("Sources disagree"));
        assert_eq!(serde_json::to_vec(&*history).unwrap(), original);
    }
    #[test]
    fn replacing_the_period_or_language_discards_a_click_queued_for_the_old_model() {
        let now = Utc::now();
        let mut chart = Chart::new(ChartModel::empty(now, now));
        chart.selected = Some(super::Segment {
            title: "Старое событие".into(),
            detail: "previous period".into(),
        });
        let mut next = ChartModel::empty(now, now);
        next.language = netlights_core::locale::Language::En;
        chart.replace_model(next);
        assert!(chart.take_selected().is_none());
    }
    #[test]
    fn stationary_country_hover_uses_new_data_and_doesnt_invent_a_result_in_a_gap() {
        let start = Utc::now();
        let end = start + chrono::Duration::hours(1);
        let model = |iso: &str, until| {
            locations(
                &LocationHistory {
                    stays: vec![LocationStay {
                        iso: iso.into(),
                        started_utc: start,
                        ended_utc: Some(until),
                    }],
                    ..Default::default()
                },
                start,
                end,
            )
        };
        let mut chart = Chart::new(model("DE", end));
        *chart.geometry.borrow_mut() = Geometry::build(&chart.model, 100);
        chart.hover_position = Some((0.5, 20.));
        chart.hover_bounds = Some(chart.bounds.get());
        chart.pointer = Some(50.);
        chart.update_hover(chart.hit(0.5, 20.));
        assert_eq!(chart.tooltip.as_ref().unwrap().title, "Германия");
        chart.replace_model(model("PL", end));
        assert_eq!(chart.pointer, Some(50.));
        assert_eq!(chart.tooltip.as_ref().unwrap().title, "Польша");
        // The cursor hasn't moved; the new snapshot no longer covers its time.
        chart.replace_model(model("PL", start + chrono::Duration::minutes(10)));
        assert_eq!(chart.pointer, Some(50.));
        assert!(chart.hover.is_none() && chart.tooltip.is_none());
        for next in [ChartModel::empty(start, end), model("DE", end)] {
            let mut changed = next;
            changed.language = netlights_core::locale::Language::En;
            chart.replace_model(changed);
            assert!(chart.pointer.is_none() && chart.hover_position.is_none());
            assert!(chart.tooltip.is_none());
        }
    }
    #[test]
    fn stationary_availability_hover_survives_rolling_retention_and_advancing_time() {
        let now = Utc::now();
        let mut history = AvailabilityHistory {
            spans: vec![AvailabilitySpan {
                group: Group::Ru,
                state: Availability::Online,
                paused: false,
                started_utc: now - chrono::Duration::days(8),
                ended_utc: None,
            }],
            ..Default::default()
        };
        history.prune(now);
        let mut chart = Chart::new(availability(
            &history,
            Group::Ru,
            now - chrono::Duration::hours(1),
            now,
            true,
        ));
        *chart.geometry.borrow_mut() = Geometry::build(&chart.model, 100);
        chart.hover_position = Some((0.5, 20.));
        chart.hover_bounds = Some(chart.bounds.get());
        chart.pointer = Some(50.);
        chart.update_hover(chart.hit(0.5, 20.));
        for seconds in 1..=12 {
            let end = now + chrono::Duration::seconds(seconds);
            history.prune(end);
            chart.replace_model(availability(
                &history,
                Group::Ru,
                end - chrono::Duration::hours(1),
                end,
                true,
            ));
            assert_eq!(chart.pointer, Some(50.));
            assert_eq!(chart.hover_position, Some((0.5, 20.)));
            assert!(
                chart
                    .tooltip
                    .as_ref()
                    .unwrap()
                    .detail
                    .contains(&local_time(end))
            );
        }
        chart.replace_model(availability(
            &history,
            Group::Ru,
            now - chrono::Duration::hours(3),
            now,
            true,
        ));
        assert!(chart.pointer.is_none() && chart.tooltip.is_none());
    }
    #[test]
    fn stationary_frequency_hover_recomputes_the_bucket_even_when_its_index_is_unchanged() {
        let start = Utc::now();
        let end = start + chrono::Duration::hours(1);
        let model = |changes| {
            let mut result = ChartModel::empty(start, end);
            result
                .buckets
                .push(netlights_core::history::FrequencyBucket {
                    start,
                    end,
                    changes,
                    observed: true,
                });
            result
        };
        let mut chart = Chart::new(model(0));
        *chart.geometry.borrow_mut() = Geometry::build(&chart.model, 100);
        chart.hover_position = Some((0.5, 30.));
        chart.hover_bounds = Some(chart.bounds.get());
        chart.pointer = Some(50.);
        chart.update_hover(chart.hit(0.5, 30.));
        chart.replace_model(model(4));
        assert_eq!(chart.hover, Some(0));
        assert_eq!(chart.pointer, Some(50.));
        assert_eq!(chart.tooltip.as_ref().unwrap().title, "Смен: 4");
    }
    #[test]
    fn resizing_or_moving_the_plot_cannot_reuse_a_hover_from_its_old_geometry() {
        let start = Utc::now();
        let end = start + chrono::Duration::hours(1);
        let model = || {
            locations(
                &LocationHistory {
                    stays: vec![LocationStay {
                        iso: "DE".into(),
                        started_utc: start,
                        ended_utc: Some(end),
                    }],
                    ..Default::default()
                },
                start,
                end,
            )
        };
        for (left, width) in [(0., 200.), (10., 100.)] {
            let mut chart = Chart::new(model());
            let old = gpui_kit::Bounds::new(
                gpui_kit::point(gpui_kit::px(0.), gpui_kit::px(0.)),
                gpui_kit::size(gpui_kit::px(100.), gpui_kit::px(42.)),
            );
            chart.bounds.set(old);
            chart.hover_bounds = Some(old);
            chart.hover_position = Some((0.5, 20.));
            chart.pointer = Some(50.);
            *chart.geometry.borrow_mut() = Geometry::build(&chart.model, 100);
            chart.update_hover(chart.hit(0.5, 20.));
            assert!(chart.tooltip.is_some());
            chart.bounds.set(gpui_kit::Bounds::new(
                gpui_kit::point(gpui_kit::px(left), gpui_kit::px(0.)),
                gpui_kit::size(gpui_kit::px(width), gpui_kit::px(42.)),
            ));
            *chart.geometry.borrow_mut() = Geometry::build(&chart.model, width as usize);
            chart.replace_model(model());
            assert!(chart.pointer.is_none() && chart.hover_position.is_none());
            assert!(chart.hover.is_none() && chart.tooltip.is_none());
        }
    }
    #[test]
    fn integer_axis_labels_share_the_line_scale_when_peak_is_five() {
        let now = Utc::now();
        let buckets = vec![netlights_core::history::FrequencyBucket {
            start: now,
            end: now,
            changes: 5,
            observed: true,
        }];
        let max = frequency_max(&buckets);
        assert_eq!(max, 8);
        assert_eq!(
            (0..=4).map(|i| max - i * max / 4).collect::<Vec<_>>(),
            vec![8, 6, 4, 2, 0]
        );
    }
    #[test]
    fn stable_status_uses_current_frontier_without_copying_history() {
        let now = Utc::now();
        let history = AvailabilityHistory {
            last_observed_utc: Some(now - chrono::Duration::hours(1)),
            spans: vec![AvailabilitySpan {
                group: Group::Ru,
                state: Availability::Online,
                paused: false,
                started_utc: now - chrono::Duration::hours(2),
                ended_utc: None,
            }],
            ..Default::default()
        };
        let chart = availability(
            &history,
            Group::Ru,
            now - chrono::Duration::hours(3),
            now,
            true,
        );
        assert_eq!(chart.segments[0].end, now);
    }
    #[test]
    fn realistic_status_width_is_proportional_and_enlargement_is_opt_in() {
        let now = Utc::now();
        let history = AvailabilityHistory {
            last_observed_utc: Some(now + chrono::Duration::hours(1)),
            spans: vec![AvailabilitySpan {
                group: Group::Ru,
                state: Availability::Offline,
                paused: false,
                started_utc: now + chrono::Duration::minutes(30),
                ended_utc: Some(now + chrono::Duration::minutes(30) + chrono::Duration::seconds(1)),
            }],
            ..Default::default()
        };
        let model = availability(
            &history,
            Group::Ru,
            now,
            now + chrono::Duration::hours(1),
            false,
        );
        let natural = Geometry::build(&model, 1000);
        let width = natural.rects[0].1 - natural.rects[0].0;
        assert!((width - 1000. / 3600.).abs() < 0.001);
        let mut enlarged = model;
        enlarged.emphasize = true;
        let enlarged = Geometry::build(&enlarged, 1000);
        assert_eq!(enlarged.rects[0].1 - enlarged.rects[0].0, 6.);
    }
    #[test]
    fn red_point_dominates_expanded_yellow_and_outline_uses_the_drawn_rect() {
        let now = Utc::now();
        let mut model = ChartModel::empty(now, now + chrono::Duration::hours(1));
        model.emphasize = true;
        for priority in [3, 2] {
            model.segments.push(PlotSegment {
                start: now + chrono::Duration::minutes(30),
                end: now + chrono::Duration::minutes(30),
                label: "",
                detail: SegmentDetail::Availability {
                    start: now + chrono::Duration::minutes(30),
                    end: now + chrono::Duration::minutes(30),
                    state: Availability::Offline,
                    paused: false,
                    open: false,
                },
                color: 0,
                priority,
            });
        }
        let geometry = Geometry::build(&model, 1000);
        assert_eq!(geometry.hits[500], Some(0));
        assert_eq!(geometry.rects[0], (497., 503.));
    }
    #[test]
    fn maximum_history_has_constant_time_hover_and_bounded_draw_operations() {
        let now = Utc::now();
        let history = Arc::new(LocationHistory {
            observations: (0..70_000)
                .map(|i| observation(now + chrono::Duration::seconds(i * 8)))
                .collect(),
            ..Default::default()
        });
        let mut models = (0..3)
            .map(|which| source(&history, which, now, now + chrono::Duration::days(7)))
            .collect::<Vec<_>>();
        assert_eq!(Arc::strong_count(&history), 4);
        assert!(!std::mem::needs_drop::<PlotSegment>());
        for model in &models {
            assert!(Arc::ptr_eq(
                &model.source.as_ref().unwrap().history,
                &history
            ));
            assert_eq!(model.segments.len(), 70_000);
            assert_eq!(model.detail_builds.load(Ordering::Relaxed), 0);
        }
        let mut chart = Chart::new(models.remove(0));
        let geometry = Geometry::build(&chart.model, 1200);
        assert_eq!(geometry.hits.len(), 1200);
        assert!(geometry.runs.len() <= 1200);
        assert_eq!(geometry.rects.len(), 70_000);
        assert_eq!(chart.model.detail_builds.load(Ordering::Relaxed), 0);
        *chart.geometry.borrow_mut() = geometry;
        let mut expected_details = 0;
        for pixel in 0..1200 {
            let hit = chart.hit((pixel as f32 + 0.5) / 1200., 20.);
            if hit.is_some() && hit != chart.hover {
                expected_details += 1;
            }
            for _ in 0..20 {
                chart.update_hover(hit);
            }
        }
        assert!(expected_details > 0 && expected_details <= 1200);
        assert_eq!(
            chart.model.detail_builds.load(Ordering::Relaxed),
            expected_details
        );
        assert!(chart.update_hover(Some(0)));
        assert!(chart.update_hover(None));
        assert!(chart.tooltip.is_none());
    }
    #[test]
    fn dense_subpixel_country_events_preserve_continuous_recorded_coverage() {
        let start = Utc::now();
        let end = start + chrono::Duration::days(7);
        let history = LocationHistory {
            stays: (0..70_000)
                .map(|i| LocationStay {
                    iso: if i % 2 == 0 { "DE" } else { "PL" }.into(),
                    started_utc: start + (end - start) * i / 70_000,
                    ended_utc: Some(start + (end - start) * (i + 1) / 70_000),
                })
                .collect(),
            ..Default::default()
        };
        let geometry = Geometry::build(&locations(&history, start, end), 1200);
        assert!(geometry.markers.iter().all(|marker| *marker));
        assert_eq!(
            geometry.runs.iter().map(|(a, b, _)| b - a).sum::<usize>(),
            1200
        );
        assert_eq!(
            geometry
                .marker_runs
                .iter()
                .map(|(a, b, _)| b - a)
                .sum::<usize>(),
            1200
        );
        assert!(geometry.runs.len() + geometry.marker_runs.len() <= 2400);
        assert!(geometry.hits.iter().all(Option::is_some));
    }
    #[test]
    fn country_hover_matches_its_plane_and_the_visible_neighbouring_tick() {
        let start = Utc::now();
        let end = start + chrono::Duration::seconds(100);
        let history = LocationHistory {
            stays: [
                (0, 10000, "DE"),
                (10000, 10100, "RU"),
                (10100, 12000, "PL"),
                (12000, 12100, "FR"),
                (12100, 100000, "DE"),
            ]
            .into_iter()
            .map(|(a, b, iso)| LocationStay {
                iso: iso.into(),
                started_utc: start + chrono::Duration::milliseconds(a),
                ended_utc: Some(start + chrono::Duration::milliseconds(b)),
            })
            .collect(),
            ..Default::default()
        };
        let mut chart = Chart::new(locations(&history, start, end));
        *chart.geometry.borrow_mut() = Geometry::build(&chart.model, 100);
        assert_eq!(
            chart.model.segments[chart.hit(0.085, 20.).unwrap()].label,
            "DE"
        );
        assert_eq!(
            chart.model.segments[chart.hit(0.105, 20.).unwrap()].label,
            "PL"
        );
        assert_eq!(
            chart.model.segments[chart.hit(0.105, 6.).unwrap()].label,
            "RU"
        );
        assert_eq!(
            chart.model.segments[chart.hit(0.125, 6.).unwrap()].label,
            "FR"
        );
        assert_eq!(chart.hit(0.105, 40.), None);
        // Tiny point at the period boundary is reachable only in the marker lane.
        let point = locations(
            &LocationHistory {
                stays: vec![LocationStay {
                    iso: "RU".into(),
                    started_utc: start,
                    ended_utc: Some(start),
                }],
                ..Default::default()
            },
            start,
            end,
        );
        chart.replace_model(point);
        *chart.geometry.borrow_mut() = Geometry::build(&chart.model, 100);
        assert_eq!(chart.hit(0.005, 6.), Some(0));
        assert_eq!(chart.hit(0.005, 20.), None);
    }
    #[test]
    fn source_graph_never_fabricates_observations_from_country_stays() {
        let now = Utc::now();
        let history = Arc::new(LocationHistory {
            stays: vec![LocationStay {
                iso: "DE".into(),
                started_utc: now - chrono::Duration::hours(1),
                ended_utc: Some(now),
            }],
            ..Default::default()
        });
        for i in 0..3 {
            assert!(
                source(&history, i, now - chrono::Duration::hours(3), now)
                    .segments
                    .is_empty()
            );
        }
    }
    #[test]
    fn legacy_online_source_is_not_relabelled_as_country_is() {
        let now = Utc::now();
        let mut legacy = observation(now);
        legacy.resolution_source = None;
        let history = Arc::new(LocationHistory {
            observations: vec![legacy],
            ..Default::default()
        });
        assert!(
            source(&history, 0, now, now + chrono::Duration::hours(1))
                .segments
                .is_empty()
        );
        assert_eq!(
            source(&history, 1, now, now + chrono::Duration::hours(1))
                .segments
                .len(),
            1
        );
    }
    #[test]
    fn local_estimate_tooltip_does_not_claim_a_fresh_confirmed_ip() {
        let now = Utc::now();
        let mut row = observation(now);
        row.resolution_source = Some("local-estimate".into());
        row.confirmed = false;
        row.country_code = None;
        row.online_country = None;
        let history = Arc::new(LocationHistory {
            observations: vec![row],
            ..Default::default()
        });
        let tooltip = source(&history, 1, now, now + chrono::Duration::hours(1))
            .materialize(0)
            .unwrap();
        assert!(tooltip.detail.contains("Последний известный IP"));
        assert!(tooltip.detail.contains("онлайн-страна не подтверждена"));
    }
    #[test]
    fn source_details_use_the_original_index_and_observation_without_display_width() {
        let now = Utc::now();
        let mut missing = observation(now - chrono::Duration::minutes(1));
        missing.local_country = None;
        let mut observed = observation(now);
        observed.observed_until_utc = Some(now + chrono::Duration::seconds(1));
        observed.local_country = Some("FR".into());
        let history = Arc::new(LocationHistory {
            observations: vec![missing, observed],
            ..Default::default()
        });
        let model = source(
            &history,
            1,
            now - chrono::Duration::hours(1),
            now + chrono::Duration::hours(1),
        );
        let geometry = Geometry::build(&model, 1000);
        assert_eq!(model.segments.len(), 1);
        assert_eq!(geometry.rects[0].1 - geometry.rects[0].0, 1.);
        assert!(geometry.markers[0]);
        let selected = model.materialize(0).unwrap();
        assert_eq!(model.segments[0].label, "FR");
        assert_eq!(selected.title, "Франция");
        assert_eq!(model.segments[0].start, now);
        assert_eq!(model.segments[0].end, now + chrono::Duration::seconds(1));
        assert!(selected.detail.contains(&local_time(now)));
        assert!(selected.detail.contains(&local_time(model.segments[0].end)));
        assert!(selected.detail.contains("192.0.2.1"));
        assert!(selected.detail.contains("v2fly-test"));
        assert!(selected.detail.contains("Источники расходятся"));
    }
    #[test]
    fn expanded_country_marker_keeps_the_real_duration_and_selection() {
        let now = Utc::now();
        let history = LocationHistory {
            stays: vec![LocationStay {
                iso: "RU".into(),
                started_utc: now,
                ended_utc: Some(now + chrono::Duration::seconds(1)),
            }],
            ..Default::default()
        };
        let model = locations(
            &history,
            now - chrono::Duration::hours(1),
            now + chrono::Duration::hours(1),
        );
        let geometry = Geometry::build(&model, 1000);
        assert_eq!(geometry.rects[0].1 - geometry.rects[0].0, 1.);
        assert!(geometry.markers[0]);
        assert!(
            geometry
                .marker_hits
                .iter()
                .filter(|hit| **hit == Some(0))
                .count()
                >= 6
        );
        let mut chart = Chart::new(model);
        *chart.geometry.borrow_mut() = geometry;
        assert!(chart.update_hover(Some(0)));
        let detail = chart.tooltip.as_ref().unwrap().detail.clone();
        assert!(detail.contains(&duration_label(1000)));
        assert!(detail.contains("размер метки не передаёт длительность"));
        assert!(detail.contains(&local_time(now + chrono::Duration::seconds(1))));
        chart.selected = chart.model.materialize(0);
        let selected = chart.take_selected().unwrap();
        assert_eq!(chart.model.segments[0].color, 0xdc0000);
        assert_eq!(
            chart.model.segments[0].end - chart.model.segments[0].start,
            chrono::Duration::seconds(1)
        );
        assert!(detail.starts_with(selected.detail.as_str()));
        assert!(
            !selected
                .detail
                .contains("размер метки не передаёт длительность")
        );
        assert!(chart.take_selected().is_none());
    }
    #[test]
    fn short_country_events_cannot_repaint_a_day_as_russia_in_five_or_seven_day_views() {
        for hours in [120, 168] {
            let end = Utc::now();
            let start = end - chrono::Duration::hours(hours);
            let day = end - chrono::Duration::days(1);
            let mut history = LocationHistory::default();
            for hour in 0..24 {
                let begin = day + chrono::Duration::hours(hour);
                for (a, b, iso) in [
                    (0, 1800, "DE"),
                    (1800, 1815, "RU"),
                    (1815, 3240, "PL"),
                    (3360, 3600, "DE"),
                ] {
                    history.stays.push(LocationStay {
                        iso: iso.into(),
                        started_utc: begin + chrono::Duration::seconds(a),
                        ended_utc: Some(begin + chrono::Duration::seconds(b)),
                    });
                }
            }
            let model = locations(&history, start, end);
            let geometry = Geometry::build(&model, 1400);
            let russia_band: usize = geometry
                .runs
                .iter()
                .filter(|(_, _, i)| model.segments[*i].label == "RU")
                .map(|(a, b, _)| b - a)
                .sum();
            assert!(
                russia_band <= 24,
                "Each short visit can cover at most one actual pixel centre"
            );
            let russia_ticks: usize = geometry
                .marker_runs
                .iter()
                .filter(|(_, _, i)| model.segments[*i].label == "RU")
                .map(|(a, b, _)| b - a)
                .sum();
            assert_eq!(
                russia_ticks, 24,
                "All short visits remain visible as separate ticks"
            );
            let known_band: usize = geometry.runs.iter().map(|(a, b, _)| b - a).sum();
            assert!(known_band as f64 > 1400.0 * 24.0 / hours as f64 * 0.85);
            for (_, _, index) in &geometry.marker_runs {
                assert!(geometry.marker_hits.contains(&Some(*index)));
            }
        }
    }
    #[test]
    fn closed_spans_from_before_the_window_dont_create_red_left_markers() {
        let start = Utc::now();
        let end = start + chrono::Duration::hours(1);
        let history = AvailabilityHistory {
            spans: vec![
                AvailabilitySpan {
                    group: Group::Ru,
                    state: Availability::Offline,
                    paused: false,
                    started_utc: start - chrono::Duration::hours(1),
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
        let model = availability(&history, Group::Ru, start, end, true);
        assert_eq!(model.segments.len(), 1);
        assert_eq!(
            model.segments[0].color,
            super::status_color(Availability::Online, false)
        );
        assert_eq!(Geometry::build(&model, 1000).hits[0], Some(0));
        let locations = LocationHistory {
            stays: vec![
                LocationStay {
                    iso: "RU".into(),
                    started_utc: start - chrono::Duration::hours(1),
                    ended_utc: Some(start),
                },
                LocationStay {
                    iso: "DE".into(),
                    started_utc: start,
                    ended_utc: Some(end),
                },
            ],
            ..Default::default()
        };
        let model = super::locations(&locations, start, end);
        assert_eq!(model.segments.len(), 1);
        assert_eq!(model.segments[0].label, "DE");
        assert_eq!(Geometry::build(&model, 1000).hits[0], Some(0));
    }
    #[test]
    fn genuine_points_and_source_observation_endpoints_remain_visible_at_window_start() {
        let start = Utc::now();
        let end = start + chrono::Duration::hours(1);
        let history = AvailabilityHistory {
            spans: vec![AvailabilitySpan {
                group: Group::Ru,
                state: Availability::Offline,
                paused: false,
                started_utc: start,
                ended_utc: Some(start),
            }],
            ..Default::default()
        };
        assert_eq!(
            availability(&history, Group::Ru, start, end, true)
                .segments
                .len(),
            1
        );
        let mut observation = observation(start - chrono::Duration::seconds(10));
        observation.observed_until_utc = Some(start);
        let history = Arc::new(LocationHistory {
            stays: vec![LocationStay {
                iso: "RU".into(),
                started_utc: start,
                ended_utc: Some(start),
            }],
            observations: vec![observation],
            ..Default::default()
        });
        assert_eq!(locations(&history, start, end).segments.len(), 1);
        for which in 0..3 {
            let model = source(&history, which, start, end);
            assert_eq!(model.segments.len(), 1);
            assert_eq!(model.segments[0].start, start);
            assert_eq!(model.segments[0].end, start);
            assert!(
                model
                    .materialize(0)
                    .unwrap()
                    .detail
                    .contains(&local_time(start - chrono::Duration::seconds(10)))
            );
        }
    }
}
