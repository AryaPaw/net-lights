use super::*;
use std::collections::{HashMap, HashSet};

const LABEL_TOP: f32 = 12.;
const LINE_HEIGHT: f32 = 14.;
const GAP: f32 = 6.;

#[derive(Clone, Copy, Debug)]
pub(super) struct Tick {
    pub x: f32,
    pub first: usize,
    pub last: usize,
    pub count: usize,
    first_x: f32,
    last_x: f32,
}
#[derive(Clone, Copy, Debug)]
struct Placement {
    left: f32,
    width: f32,
}

fn cluster(
    width: f32,
    ticks: &[Tick],
    mut measure: impl FnMut(&Tick) -> f32,
) -> Vec<(Tick, Placement)> {
    let mut result: Vec<(Tick, Placement)> = Vec::new();
    for &tick in ticks {
        let mut tick = tick;
        loop {
            let w = measure(&tick).min(width);
            let p = Placement {
                left: (tick.x - w / 2.).clamp(0., (width - w).max(0.)),
                width: w,
            };
            let Some((previous, bounds)) = result.last() else {
                result.push((tick, p));
                break;
            };
            if bounds.left + bounds.width + GAP <= p.left {
                result.push((tick, p));
                break;
            }
            tick = Tick {
                first: previous.first,
                last: tick.last,
                count: previous.count + tick.count,
                first_x: previous.first_x,
                last_x: tick.last_x,
                x: (previous.first_x + tick.last_x) / 2.,
            };
            result.pop();
        }
    }
    result
}
pub(super) fn group_title(language: Language, count: usize) -> String {
    if language == Language::En {
        return format!("{count} {}", if count == 1 { "change" } else { "changes" });
    }
    let noun = if (11..=14).contains(&(count % 100)) {
        "смен"
    } else {
        match count % 10 {
            1 => "смена",
            2..=4 => "смены",
            _ => "смен",
        }
    };
    format!("{count} {noun}")
}
fn label_text(tick: &Tick, changes: &[HistoryEvent], language: Language) -> String {
    if tick.count > 1 {
        return format!("{} ▾", group_title(language, tick.count));
    }
    match &changes[tick.last].kind {
        EventKind::Country {
            previous, current, ..
        } => format!("{previous} → {current}"),
        _ => language.choose("Изменение", "Change").to_string(),
    }
}
fn group(changes: &[HistoryEvent], range: TimeRange, width: f32) -> Vec<Tick> {
    let samples = width.ceil().clamp(1., 16384.) as usize;
    let mut pixels: Vec<Option<Tick>> = vec![None; samples];
    for (i, event) in changes.iter().enumerate() {
        let x = (((event.start - range.start).num_milliseconds() as f64
            / range.millis().max(1) as f64
            * samples as f64)
            .round() as usize)
            .min(samples - 1);
        pixels[x] = Some(match pixels[x] {
            Some(old) => Tick {
                last: i,
                count: old.count + 1,
                ..old
            },
            None => Tick {
                x: x as f32 * width / samples as f32,
                first: i,
                last: i,
                count: 1,
                first_x: x as f32 * width / samples as f32,
                last_x: x as f32 * width / samples as f32,
            },
        });
    }
    pixels.into_iter().flatten().collect()
}

struct Label {
    tick: usize,
    placement: Placement,
    line: ShapedLine,
}
#[derive(Default)]
pub(super) struct MarkerCache {
    pub width: usize,
    plot_width: f32,
    pub ticks: Vec<Tick>,
    labels: Vec<Label>,
    raw_ticks: Vec<Tick>,
    language: Option<Language>,
    ink: Option<Hsla>,
    shaped: HashMap<String, ShapedLine>,
}
impl MarkerCache {
    fn prepare(
        &mut self,
        width: f32,
        changes: &[HistoryEvent],
        range: TimeRange,
        language: Language,
        ink: Hsla,
        window: &mut Window,
    ) {
        let samples = width.ceil().clamp(1., 16384.) as usize;
        if self.width == samples
            && self.plot_width == width
            && self.language == Some(language)
            && self.ink == Some(ink)
        {
            return;
        }
        self.raw_ticks = group(changes, range, width);
        let previous = if self.ink == Some(ink) {
            std::mem::take(&mut self.shaped)
        } else {
            HashMap::new()
        };
        let mut shaped: HashMap<String, ShapedLine> = HashMap::new();
        let mut line_for = |tick: &Tick| {
            let text = label_text(tick, changes, language);
            let line = shaped
                .get(&text)
                .or_else(|| previous.get(&text))
                .cloned()
                .unwrap_or_else(|| {
                    window.text_system().shape_line(
                        text.clone().into(),
                        px(11.),
                        &[TextRun {
                            len: text.len(),
                            font: Font {
                                weight: FontWeight::MEDIUM,
                                ..font("Segoe UI")
                            },
                            color: ink,
                            ..Default::default()
                        }],
                        None,
                    )
                });
            shaped.insert(text, line.clone());
            line
        };
        let groups = cluster(width, &self.raw_ticks, |tick| {
            f32::from(line_for(tick).width) + 8.
        });
        self.ticks = groups.iter().map(|(tick, _)| *tick).collect();
        self.labels = groups
            .iter()
            .enumerate()
            .map(|(i, (tick, p))| Label {
                tick: i,
                placement: *p,
                line: line_for(tick),
            })
            .collect();
        self.width = samples;
        self.plot_width = width;
        self.language = Some(language);
        self.ink = Some(ink);
        let retained: HashSet<_> = self
            .ticks
            .iter()
            .map(|tick| label_text(tick, changes, language))
            .collect();
        shaped.retain(|text, _| retained.contains(text));
        self.shaped = shaped;
    }
    pub fn hit(&self, x: f32, y: f32) -> Option<usize> {
        if self.width == 0 || x < 0. || x > self.plot_width || !(0. ..=30.).contains(&y) {
            return None;
        }
        if y >= LABEL_TOP {
            let i = self
                .labels
                .partition_point(|label| label.placement.left <= x)
                .checked_sub(1)?;
            let label = &self.labels[i];
            return (x <= label.placement.left + label.placement.width)
                .then_some(self.ticks[label.tick].last);
        }
        // A real tick can be outside its group's centred caption.
        let i = self.raw_ticks.partition_point(|tick| tick.x < x);
        let nearest = [i.checked_sub(1), Some(i)]
            .into_iter()
            .flatten()
            .filter_map(|i| self.raw_ticks.get(i))
            .filter(|tick| (tick.x - x).abs() <= 6.)
            .min_by(|a, b| (a.x - x).abs().total_cmp(&(b.x - x).abs()))?;
        self.ticks
            .iter()
            .find(|tick| tick.first <= nearest.first && tick.last >= nearest.last)
            .map(|tick| tick.last)
    }
    pub fn hovered_group(&self, last: usize) -> Option<(usize, usize)> {
        (self.width != 0)
            .then(|| self.ticks.iter().find(|tick| tick.last == last))
            .flatten()
            .map(|tick| (tick.first, tick.last))
    }
}

pub(super) struct MarkerPlot {
    pub cache: Rc<RefCell<MarkerCache>>,
    pub bounds: Rc<Cell<Bounds<Pixels>>>,
    pub changes: Arc<Vec<HistoryEvent>>,
    pub range: TimeRange,
    pub language: Language,
    pub ink: Hsla,
    pub primary: Hsla,
}
impl IntoElement for MarkerPlot {
    type Element = Self;
    fn into_element(self) -> Self {
        self
    }
}
impl Element for MarkerPlot {
    type RequestLayoutState = ();
    type PrepaintState = ();
    fn id(&self) -> Option<ElementId> {
        None
    }
    fn source_location(&self) -> Option<&'static std::panic::Location<'static>> {
        None
    }
    fn request_layout(
        &mut self,
        _: Option<&GlobalElementId>,
        _: Option<&InspectorElementId>,
        window: &mut Window,
        _: &mut App,
    ) -> (LayoutId, ()) {
        let style = Style {
            size: size(relative(1.).into(), Length::Auto),
            flex_shrink: 0.,
            ..Default::default()
        };
        let id = window.request_measured_layout(style, move |known, available, _, _| {
            let width = known
                .width
                .or(match available.width {
                    AvailableSpace::Definite(w) => Some(w),
                    _ => None,
                })
                .unwrap_or(px(300.));
            size(width, px(30.))
        });
        (id, ())
    }
    fn prepaint(
        &mut self,
        _: Option<&GlobalElementId>,
        _: Option<&InspectorElementId>,
        bounds: Bounds<Pixels>,
        _: &mut (),
        window: &mut Window,
        _: &mut App,
    ) {
        self.cache.borrow_mut().prepare(
            f32::from(bounds.size.width).max(1.),
            &self.changes,
            self.range,
            self.language,
            self.ink,
            window,
        );
        self.bounds.set(bounds);
    }
    fn paint(
        &mut self,
        _: Option<&GlobalElementId>,
        _: Option<&InspectorElementId>,
        bounds: Bounds<Pixels>,
        _: &mut (),
        _: &mut (),
        window: &mut Window,
        cx: &mut App,
    ) {
        let cache = self.cache.borrow();
        for tick in &cache.raw_ticks {
            window.paint_quad(fill(
                Bounds::new(
                    point(bounds.origin.x + px(tick.x), bounds.origin.y),
                    size(px(2_f32.min(cache.plot_width - tick.x)), px(8.)),
                ),
                self.primary.opacity(0.85),
            ));
        }
        for label in &cache.labels {
            let p = label.placement;
            let _ = label.line.paint(
                bounds.origin + point(px(p.left + 4.), px(LABEL_TOP)),
                px(LINE_HEIGHT),
                TextAlign::Left,
                None,
                window,
                cx,
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{GAP, Label, MarkerCache, ShapedLine, Tick, cluster, group_title};
    use netlights_core::locale::Language;
    fn tick(x: f32, i: usize) -> Tick {
        Tick {
            x,
            first: i,
            last: i,
            count: 1,
            first_x: x,
            last_x: x,
        }
    }
    #[test]
    fn close_labels_become_one_group_without_losing_events() {
        let groups = cluster(
            300.,
            &[tick(100., 0), tick(115., 1), tick(130., 2), tick(250., 3)],
            |t| if t.count > 1 { 64. } else { 46. },
        );
        assert_eq!(groups.len(), 2);
        assert_eq!(
            (groups[0].0.first, groups[0].0.last, groups[0].0.count),
            (0, 2, 3)
        );
        assert_eq!(groups[1].0.count, 1);
    }
    #[test]
    fn all_periods_and_dense_edges_fit_one_row_and_preserve_every_event() {
        for hours in [1, 3, 6, 12, 24, 72, 120, 168] {
            for width in [160., 300.5, 900., 1400.] {
                let n = hours * 120;
                let ticks: Vec<_> = (0..n)
                    .map(|i| tick(i as f32 * width / n as f32, i))
                    .collect();
                let groups = cluster(width, &ticks, |t| if t.count > 1 { 70. } else { 46. });
                assert_eq!(groups.iter().map(|g| g.0.count).sum::<usize>(), n);
                let mut expected = 0;
                for (i, (t, p)) in groups.iter().enumerate() {
                    assert_eq!(t.first, expected);
                    assert_eq!(t.count, t.last - t.first + 1);
                    expected = t.last + 1;
                    assert!(p.left >= 0. && p.left + p.width <= width);
                    if i > 0 {
                        assert!(groups[i - 1].1.left + groups[i - 1].1.width + GAP <= p.left);
                    }
                }
                assert_eq!(expected, n);
                assert!(groups.len() <= (width / (46. + GAP)).ceil() as usize);
            }
        }
    }
    #[test]
    fn caption_and_real_ticks_hit_the_same_complete_group_and_dirty_cache_is_inert() {
        let ticks = vec![tick(100., 0), tick(115., 1), tick(130., 2)];
        let groups = cluster(300., &ticks, |_| 64.);
        let mut cache = MarkerCache {
            width: 300,
            plot_width: 300.,
            raw_ticks: ticks,
            ticks: groups.iter().map(|g| g.0).collect(),
            ..Default::default()
        };
        cache.labels = groups
            .iter()
            .enumerate()
            .map(|(i, g)| Label {
                tick: i,
                placement: g.1,
                line: ShapedLine::default(),
            })
            .collect();
        assert_eq!(cache.hit(115., 20.), Some(2));
        assert_eq!(cache.hit(100., 4.), Some(2));
        assert_eq!(cache.hovered_group(2), Some((0, 2)));
        assert_eq!(cache.hit(115., 40.), None);
        cache.width = 0;
        assert_eq!(cache.hit(115., 20.), None);
        assert_eq!(cache.hovered_group(2), None);
    }
    #[test]
    fn russian_group_counts_use_correct_plural() {
        for (count, text) in [
            (2, "2 смены"),
            (5, "5 смен"),
            (11, "11 смен"),
            (21, "21 смена"),
            (24, "24 смены"),
            (111, "111 смен"),
        ] {
            assert_eq!(group_title(Language::Ru, count), text);
        }
        assert_eq!(group_title(Language::En, 5), "5 changes");
    }
}
