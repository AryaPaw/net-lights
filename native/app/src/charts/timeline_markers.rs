use super::*;
use std::collections::HashMap;

const LABEL_TOP: f32 = 12.;
const ROW_HEIGHT: f32 = 18.;
const LINE_HEIGHT: f32 = 14.;
const GAP: f32 = 6.;

#[derive(Clone, Copy, Debug)]
pub(super) struct Tick {
    pub x: f32,
    pub first: usize,
    pub last: usize,
    pub count: usize,
}
#[derive(Clone, Copy, Debug)]
struct Placement {
    left: f32,
    width: f32,
    row: usize,
}

fn pack(width: f32, items: &[(f32, f32)]) -> Vec<Placement> {
    let desired: Vec<_> = items
        .iter()
        .map(|&(x, w)| {
            let w = w.min(width);
            ((x - w / 2.).clamp(0., (width - w).max(0.)), w)
        })
        .collect();
    let arrange = |max_shift: f32, sort: bool| {
        let mut order: Vec<_> = (0..items.len()).collect();
        if sort {
            order.sort_by(|a, b| desired[*a].0.total_cmp(&desired[*b].0));
        }
        let mut ends = Vec::<f32>::new();
        let mut placements = vec![
            Placement {
                left: 0.,
                width: 0.,
                row: 0
            };
            items.len()
        ];
        for i in order {
            let (start, w) = desired[i];
            let existing = ends.iter().enumerate().find_map(|(row, &end)| {
                let left = start.max(end + GAP);
                (left + w <= width && left - start <= max_shift).then_some((row, left))
            });
            let (row, left) = existing.unwrap_or_else(|| {
                ends.push(-GAP);
                (ends.len() - 1, start)
            });
            ends[row] = left + w;
            placements[i] = Placement {
                left,
                width: w,
                row,
            };
        }
        placements
    };
    let shifted = arrange(24., false);
    let centred = arrange(0., true);
    let rows = |labels: &[Placement]| labels.iter().map(|label| label.row + 1).max().unwrap_or(0);
    // Offsets help small groups, but mustn't make dense groups taller than
    // optimal interval partitioning of centred labels, particularly at edges.
    if rows(&centred) < rows(&shifted) {
        centred
    } else {
        shifted
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
    rows: Vec<Vec<usize>>,
    height: f32,
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
        self.ticks = group(changes, range, width);
        let previous = if self.ink == Some(ink) {
            std::mem::take(&mut self.shaped)
        } else {
            HashMap::new()
        };
        let mut shaped = HashMap::new();
        let mut lines = Vec::with_capacity(self.ticks.len());
        for tick in &self.ticks {
            let label = if tick.count > 1 {
                language
                    .choose(
                        &format!("Смен: {}", tick.count),
                        &format!("{} changes", tick.count),
                    )
                    .to_string()
            } else if let EventKind::Country {
                previous, current, ..
            } = &changes[tick.last].kind
            {
                format!("{previous} → {current}")
            } else {
                language.choose("Изменение", "Change").to_string()
            };
            let shape = |font_size| {
                window.text_system().shape_line(
                    label.clone().into(),
                    px(font_size),
                    &[TextRun {
                        len: label.len(),
                        font: Font {
                            weight: FontWeight::MEDIUM,
                            ..font("Segoe UI")
                        },
                        color: ink,
                        ..Default::default()
                    }],
                    None,
                )
            };
            let line = shaped
                .get(&label)
                .or_else(|| previous.get(&label))
                .cloned()
                .unwrap_or_else(|| shape(11.));
            shaped.insert(label.clone(), line.clone());
            let available = (width - 8.).max(1.);
            let line = if f32::from(line.width) > available {
                shape(11. * available / f32::from(line.width))
            } else {
                line
            };
            lines.push(line);
        }
        let sizes: Vec<_> = self
            .ticks
            .iter()
            .zip(&lines)
            .map(|(tick, line)| (tick.x, f32::from(line.width) + 8.))
            .collect();
        self.labels = pack(width, &sizes)
            .into_iter()
            .zip(lines)
            .enumerate()
            .map(|(tick, (placement, line))| Label {
                tick,
                placement,
                line,
            })
            .collect();
        self.rows.clear();
        for (i, label) in self.labels.iter().enumerate() {
            self.rows
                .resize_with(self.rows.len().max(label.placement.row + 1), Vec::new);
            self.rows[label.placement.row].push(i);
        }
        for row in &mut self.rows {
            row.sort_by(|a, b| {
                self.labels[*a]
                    .placement
                    .left
                    .total_cmp(&self.labels[*b].placement.left)
            });
        }
        self.height = if self.rows.is_empty() {
            0.
        } else {
            LABEL_TOP + (self.rows.len() - 1) as f32 * ROW_HEIGHT + LINE_HEIGHT + 4.
        };
        self.width = samples;
        self.plot_width = width;
        self.language = Some(language);
        self.ink = Some(ink);
        self.shaped = shaped;
    }
    pub fn hit(&self, x: f32, y: f32) -> Option<usize> {
        if self.width == 0 || x < 0. || x > self.plot_width || y < 0. || y > self.height {
            return None;
        }
        if y >= LABEL_TOP {
            let row = ((y - LABEL_TOP) / ROW_HEIGHT) as usize;
            let labels = self.rows.get(row)?;
            let i = labels
                .partition_point(|i| self.labels[*i].placement.left <= x)
                .checked_sub(1)?;
            let label = &self.labels[labels[i]];
            let p = label.placement;
            return (x <= p.left + p.width
                && y <= LABEL_TOP + row as f32 * ROW_HEIGHT + LINE_HEIGHT)
                .then_some(self.ticks[label.tick].last);
        }
        let i = self.ticks.partition_point(|tick| tick.x < x);
        [i.checked_sub(1), Some(i)]
            .into_iter()
            .flatten()
            .filter_map(|i| self.ticks.get(i))
            .filter(|tick| (tick.x - x).abs() <= 6.)
            .min_by(|a, b| (a.x - x).abs().total_cmp(&(b.x - x).abs()))
            .map(|tick| tick.last)
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
    pub background: Hsla,
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
        let cache = self.cache.clone();
        let changes = self.changes.clone();
        let (range, language, ink) = (self.range, self.language, self.ink);
        let style = Style {
            size: size(relative(1.).into(), Length::Auto),
            flex_shrink: 0.,
            ..Default::default()
        };
        let id = window.request_measured_layout(style, move |known, available, window, _| {
            let width = known
                .width
                .or(match available.width {
                    AvailableSpace::Definite(w) => Some(w),
                    _ => None,
                })
                .unwrap_or(px(300.));
            let mut cache = cache.borrow_mut();
            cache.prepare(
                f32::from(width).max(1.),
                &changes,
                range,
                language,
                ink,
                window,
            );
            size(width, px(cache.height))
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
        for tick in &cache.ticks {
            window.paint_quad(fill(
                Bounds::new(
                    point(bounds.origin.x + px(tick.x), bounds.origin.y),
                    size(px(2_f32.min(cache.plot_width - tick.x)), px(8.)),
                ),
                self.primary.opacity(0.85),
            ));
        }
        for label in &cache.labels {
            let tick = cache.ticks[label.tick];
            let p = label.placement;
            let top = LABEL_TOP + p.row as f32 * ROW_HEIGHT;
            let mut path = PathBuilder::stroke(px(0.75));
            path.move_to(bounds.origin + point(px(tick.x + 0.5), px(8.)));
            path.line_to(bounds.origin + point(px(p.left + p.width / 2.), px(top - 2.)));
            if let Ok(path) = path.build() {
                window.paint_path(path, self.primary.opacity(0.4));
            }
        }
        for label in &cache.labels {
            let p = label.placement;
            let top = LABEL_TOP + p.row as f32 * ROW_HEIGHT;
            window.paint_quad(fill(
                Bounds::new(
                    bounds.origin + point(px(p.left), px(top - 1.)),
                    size(px(p.width), px(LINE_HEIGHT + 2.)),
                ),
                self.background,
            ));
            let _ = label.line.paint(
                bounds.origin + point(px(p.left + 4.), px(top)),
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
    use super::{GAP, Label, MarkerCache, Placement, ShapedLine, Tick, group, pack};
    use crate::dashboard::{EventKind, HistoryEvent, TimeRange};
    use chrono::{DateTime, Duration, Utc};
    fn validate(width: f32, items: &[(f32, f32)]) -> Vec<Placement> {
        let labels = pack(width, items);
        assert_eq!(labels.len(), items.len());
        for (i, a) in labels.iter().enumerate() {
            assert!(a.left >= 0. && a.left + a.width <= width);
            for b in &labels[..i] {
                assert!(
                    a.row != b.row
                        || a.left >= b.left + b.width + GAP
                        || b.left >= a.left + a.width + GAP
                );
            }
        }
        labels
    }
    #[test]
    fn close_changes_keep_every_label_with_offsets_and_extra_rows() {
        let labels = validate(300., &[(100., 46.), (115., 46.), (130., 46.)]);
        assert!(labels.iter().any(|label| label.row > 0));
        assert!(labels.iter().any(|label| label.left > 100.));
    }
    #[test]
    fn edges_and_dense_data_never_overlap_or_leave_the_plot() {
        for width in [160., 300., 900., 1400.] {
            let items: Vec<_> = (0..width as usize).map(|x| (x as f32, 54.)).collect();
            let labels = validate(width, &items);
            let overlap = (0..=width as usize)
                .map(|x| {
                    items
                        .iter()
                        .filter(|&&(anchor, w)| {
                            let left = (anchor - w / 2.).clamp(0., width - w);
                            left <= x as f32 && (x as f32) < left + w + GAP
                        })
                        .count()
                })
                .max()
                .unwrap();
            assert!(labels.iter().map(|label| label.row + 1).max().unwrap() <= overlap);
        }
        validate(160., &[(0., 46.), (1., 46.), (158., 46.), (159., 46.)]);
    }
    #[test]
    fn shifted_labels_and_ticks_hit_the_same_actual_event() {
        let mut cache = MarkerCache {
            width: 300,
            plot_width: 300.,
            height: 48.,
            ticks: vec![Tick {
                x: 100.,
                first: 2,
                last: 2,
                count: 1,
            }],
            ..Default::default()
        };
        cache.labels.push(Label {
            tick: 0,
            placement: Placement {
                left: 116.,
                width: 46.,
                row: 1,
            },
            line: ShapedLine::default(),
        });
        cache.rows = vec![vec![], vec![0]];
        assert_eq!(cache.hit(100., 4.), Some(2));
        assert_eq!(cache.hit(130., 32.), Some(2));
        assert_eq!(cache.hit(130., 48.), None);
        assert_eq!(cache.hit(99., 32.), None);
        cache.width = 0;
        assert_eq!(cache.hit(100., 4.), None);
        assert_eq!(cache.hit(130., 32.), None);
    }

    #[test]
    fn pixel_groups_preserve_every_change_and_use_the_complete_plot_width() {
        let start: DateTime<Utc> = "2026-11-16T00:00:00Z".parse().unwrap();
        let range = TimeRange {
            start,
            end: start + Duration::hours(1),
        };
        let events: Vec<_> = (0..20000)
            .map(|i| HistoryEvent {
                start: start + Duration::milliseconds(i * 180),
                end: start + Duration::milliseconds(i * 180),
                ongoing: false,
                kind: EventKind::Country {
                    previous: "DE".into(),
                    current: "PL".into(),
                    previous_end: start,
                },
            })
            .collect();
        for width in [160., 300.5, 900., 32768.] {
            let ticks = group(&events, range, width);
            assert_eq!(
                ticks.iter().map(|tick| tick.count).sum::<usize>(),
                events.len()
            );
            assert!(ticks.len() <= width.ceil().min(16384.) as usize);
            assert!(ticks.last().unwrap().x >= width * 0.99);
            assert!(
                ticks
                    .iter()
                    .all(|tick| tick.x >= 0. && tick.x < width && tick.first <= tick.last)
            );
        }
    }
}
