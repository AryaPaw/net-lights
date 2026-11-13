use crate::{
    backend::{Backend, Command, State},
    charts::{self, Chart},
    fluent::ControlCursor,
    text::{UiLanguage, language, tr},
    tray::{OsEvent, TrayHost, TrayState},
};
use chrono::{Duration as ChronoDuration, Local, Utc};
use gpui_kit::{
    component::{
        ActiveTheme, FocusableExt, IndexPath, Sizable, Theme, ThemeMode, TitleBar,
        button::ButtonVariants,
        scroll::ScrollableElement,
        searchable_list::SearchableVec,
        select::{Select, SelectEvent, SelectState},
    },
    prelude::*,
    *,
};
use netlights_core::{
    locale::Language,
    model::*,
    platform::{self, Instance},
};
use std::{
    path::PathBuf,
    rc::Rc,
    sync::{Arc, mpsc::Receiver},
    time::{Duration, Instant},
};

mod about;
mod diagnostics;
mod locations;
mod navigation;
mod settings;
mod state;

#[derive(Clone, Copy, PartialEq)]
enum Page {
    State,
    Locations,
    Diagnostics,
    Settings,
    About,
}
impl Page {
    const ALL: [Self; 5] = [
        Self::State,
        Self::Locations,
        Self::Diagnostics,
        Self::Settings,
        Self::About,
    ];
    fn label(self) -> &'static str {
        match self {
            Self::State => "Состояние",
            Self::Locations => "Локации",
            Self::Diagnostics => "Диагностика",
            Self::Settings => "Параметры",
            Self::About => "О программе",
        }
    }
}
type Selector = Entity<SelectState<SearchableVec<String>>>;

pub struct NetLights {
    backend: Backend,
    state: Arc<State>,
    instance: Rc<Instance>,
    tray: TrayHost,
    os: Receiver<OsEvent>,
    root: PathBuf,
    page: Page,
    navigation: Entity<navigation::Navigation>,
    page_transition: usize,
    page_direction: f32,
    settings_scroll: ScrollHandle,
    settings_target: Option<usize>,
    settings_section: usize,
    settings_picked: bool,
    settings: Settings,
    pending_settings: u64,
    charts: Vec<Entity<Chart>>,
    timeline: Entity<charts::AvailabilityTimeline>,
    overview_range: Option<crate::dashboard::TimeRange>,
    overview_events: Arc<Vec<crate::dashboard::HistoryEvent>>,
    overview_selected: Option<crate::dashboard::HistoryEvent>,
    chart_key: Option<(u64, u64, i64, bool, bool, i64)>,
    health: Option<String>,
    ready_at: Option<Instant>,
    exiting: bool,
    _subscriptions: Vec<Subscription>,
    polling: Selector,
    country_polling: Selector,
    theme_selector: Selector,
    traffic_limit: Selector,
    letter_size: Selector,
    language_selector: Selector,
    geometry_changed: Option<Instant>,
    last_bounds: Option<[i32; 4]>,
    visible: bool,
    notice: String,
    about: bool,
    incident: Option<charts::Segment>,
    history_scroll: ListState,
    location_rows: Arc<Vec<(usize, Option<String>)>>,
    network_changed: Option<Instant>,
    history_width: Option<Pixels>,
}
impl NetLights {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        backend: Backend,
        instance: Rc<Instance>,
        tray: TrayHost,
        os: Receiver<OsEvent>,
        root: PathBuf,
        health: Option<String>,
        visible: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let state = backend.current();
        let settings = state.settings.clone();
        cx.set_global(UiLanguage(Language::resolve(&settings.language)));
        let polling = selector(
            AVAILABILITY_INTERVALS
                .iter()
                .map(|s| interval(*s, language(cx)))
                .collect(),
            AVAILABILITY_INTERVALS
                .iter()
                .position(|s| *s == settings.availability_polling_seconds as u64)
                .unwrap_or(3),
            window,
            cx,
        );
        let country_polling = selector(
            COUNTRY_INTERVALS
                .iter()
                .map(|s| interval(*s, language(cx)))
                .collect(),
            COUNTRY_INTERVALS
                .iter()
                .position(|s| *s == settings.country_polling_seconds as u64)
                .unwrap_or(0),
            window,
            cx,
        );
        let theme_selector = selector(
            ["Как в Windows", "Светлая", "Тёмная"]
                .map(|s| tr(cx, s))
                .to_vec(),
            match settings.theme.as_str() {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            },
            window,
            cx,
        );
        let limit_index = match settings
            .extra
            .get("trafficWarningBytesPerHour")
            .and_then(|v| v.as_u64())
        {
            Some(67108864) => 1,
            Some(268435456) => 2,
            Some(1073741824) => 3,
            _ => 0,
        };
        let traffic_limit = selector(
            [
                "После замера",
                "64 МиБ за час",
                "256 МиБ за час",
                "1 ГиБ за час",
            ]
            .map(|s| tr(cx, s))
            .to_vec(),
            limit_index,
            window,
            cx,
        );
        let letter_size = selector(
            ["Маленькие", "Средние", "Крупные"]
                .map(|s| tr(cx, s))
                .to_vec(),
            settings.geo_country_letter_size as usize,
            window,
            cx,
        );
        let language_selector = selector(
            vec![tr(cx, "Как в системе"), "Русский".into(), "English".into()],
            match settings.language.as_str() {
                "ru" => 1,
                "en" => 2,
                _ => 0,
            },
            window,
            cx,
        );
        let mut subscriptions = vec![];
        let navigation = cx.new(navigation::Navigation::new);
        subscriptions.push(cx.subscribe(
            &navigation,
            |this, _, event: &navigation::Selected, cx| {
                this.select_page(event.0, cx);
            },
        ));
        subscriptions.push(
            cx.subscribe_in(&polling, window, |this, select, event, window, cx| {
                if let SelectEvent::Confirm(Some(_)) = event
                    && let Some(i) = select.read(cx).selected_index(cx).map(|index| index.row)
                    && i < AVAILABILITY_INTERVALS.len()
                {
                    let previous = this.settings.clone();
                    this.settings.availability_polling_seconds = AVAILABILITY_INTERVALS[i] as i64;
                    this.commit_controls(previous, window, cx);
                }
            }),
        );
        subscriptions.push(cx.subscribe_in(
            &country_polling,
            window,
            |this, select, event, window, cx| {
                if let SelectEvent::Confirm(Some(_)) = event
                    && let Some(i) = select.read(cx).selected_index(cx).map(|index| index.row)
                    && i < COUNTRY_INTERVALS.len()
                {
                    let previous = this.settings.clone();
                    this.settings.country_polling_seconds = COUNTRY_INTERVALS[i] as i64;
                    this.commit_controls(previous, window, cx);
                }
            },
        ));
        subscriptions.push(cx.subscribe_in(
            &theme_selector,
            window,
            |this, select, event, window, cx| {
                if let SelectEvent::Confirm(Some(_)) = event {
                    let previous = this.settings.clone();
                    this.settings.theme =
                        match select.read(cx).selected_index(cx).map(|index| index.row) {
                            Some(1) => "light",
                            Some(2) => "dark",
                            _ => "system",
                        }
                        .into();
                    this.commit_controls(previous, window, cx);
                }
            },
        ));
        subscriptions.push(cx.subscribe_in(
            &traffic_limit,
            window,
            |this, select, event, window, cx| {
                if let SelectEvent::Confirm(Some(_)) = event {
                    let previous = this.settings.clone();
                    let bytes = match select.read(cx).selected_index(cx).map(|index| index.row) {
                        Some(1) => 64 * 1024 * 1024u64,
                        Some(2) => 256 * 1024 * 1024,
                        Some(3) => 1024 * 1024 * 1024,
                        _ => 0,
                    };
                    this.settings
                        .extra
                        .insert("trafficWarningBytesPerHour".into(), bytes.into());
                    this.commit_controls(previous, window, cx);
                }
            },
        ));
        subscriptions.push(cx.subscribe_in(
            &letter_size,
            window,
            |this, select, event, window, cx| {
                if let SelectEvent::Confirm(Some(_)) = event {
                    let previous = this.settings.clone();
                    this.settings.geo_country_letter_size =
                        match select.read(cx).selected_index(cx).map(|index| index.row) {
                            Some(0) => 0,
                            Some(2) => 2,
                            _ => 1,
                        };
                    this.commit_controls(previous, window, cx);
                }
            },
        ));
        subscriptions.push(cx.subscribe_in(
            &language_selector,
            window,
            |this, select, event, window, cx| {
                if let SelectEvent::Confirm(Some(_)) = event {
                    let previous = this.settings.clone();
                    this.settings.language =
                        match select.read(cx).selected_index(cx).map(|index| index.row) {
                            Some(1) => "ru",
                            Some(2) => "en",
                            _ => "system",
                        }
                        .into();
                    this.commit_controls(previous, window, cx);
                }
            },
        ));
        subscriptions.push(cx.observe_window_bounds(window, |this, window, cx| {
            // MouseMove can be deduplicated after a resize/move. Don't leave a
            // tooltip describing the previous physical plot position.
            for chart in &this.charts {
                chart.update(cx, |chart, cx| chart.clear_pointer(cx));
            }
            this.timeline
                .update(cx, |timeline, cx| timeline.clear_pointer(cx));
            if !window.is_maximized()
                && window.is_visible()
                && let Some(rect) = crate::window_os::outer_rect(window)
            {
                this.last_bounds = Some(rect);
                this.geometry_changed = Some(Instant::now());
            }
        }));
        subscriptions.push(cx.observe_window_appearance(window, |this, window, cx| {
            if this.settings.theme == "system" {
                this.apply_theme(window, cx);
            }
            cx.notify();
        }));
        let weak = cx.entity().downgrade();
        window.on_window_should_close(cx, move |window, cx| {
            crate::window_os::hide(window);
            let _ = weak.update(cx, |this, cx| {
                this.visible = false;
                this.discard_chart_models(cx);
            });
            false
        });
        cx.spawn(async move |weak, cx| {
            loop {
                cx.background_executor()
                    .timer(Duration::from_millis(100))
                    .await;
                if weak.update(cx, |this, cx| this.poll(cx)).is_err() {
                    break;
                }
            }
        })
        .detach();
        let timeline = cx.new(|cx| charts::AvailabilityTimeline::new(state.monitor.clone(), cx));
        subscriptions.push(cx.subscribe(&timeline, |this, _, event, cx| {
            match event {
                charts::TimelineEvent::Select(event) => {
                    this.overview_selected = Some(event.clone())
                }
                charts::TimelineEvent::Zoom(range) => {
                    this.overview_range = Some(*range);
                    this.overview_selected = None;
                    this.chart_key = None;
                    this.refresh_charts(cx);
                }
                charts::TimelineEvent::Diagnose(id) => this.send(Command::Diagnose(id.clone()), cx),
            }
            cx.notify();
        }));
        let mut this = Self {
            backend,
            state,
            instance,
            tray,
            os,
            root,
            page: Page::State,
            navigation,
            page_transition: 0,
            page_direction: 0.,
            settings_scroll: ScrollHandle::new(),
            settings_target: None,
            settings_section: 0,
            settings_picked: false,
            settings,
            pending_settings: 0,
            charts: vec![],
            timeline,
            overview_range: None,
            overview_events: Arc::default(),
            overview_selected: None,
            chart_key: None,
            health,
            ready_at: None,
            exiting: false,
            _subscriptions: subscriptions,
            polling,
            country_polling,
            theme_selector,
            traffic_limit,
            letter_size,
            language_selector,
            geometry_changed: None,
            last_bounds: None,
            visible,
            notice: String::new(),
            about: false,
            incident: None,
            history_scroll: ListState::new(1, ListAlignment::Top, px(500.))
                .with_uniform_item_height(px(64.)),
            location_rows: Arc::new(vec![]),
            network_changed: None,
            history_width: None,
        };
        this.apply_theme(window, cx);
        if visible {
            this.refresh_charts(cx);
        } else {
            let end = this.state.monitor.generated_utc;
            let start = end - ChronoDuration::hours(this.settings.location_chart_window_hours);
            for _ in 0..4 {
                this.charts
                    .push(cx.new(|_| Chart::new(charts::ChartModel::empty(start, end))));
            }
        }
        this
    }
    fn commit_controls(&mut self, previous: Settings, window: &mut Window, cx: &mut Context<Self>) {
        let language_changed = self.settings.language != previous.language;
        let theme_changed = self.settings.theme != previous.theme;
        let accepted = self.save_settings(cx);
        if !accepted {
            self.settings = previous;
        }
        if language_changed || !accepted {
            self.apply_language(window, cx);
        }
        if theme_changed && accepted {
            self.apply_theme(window, cx);
        }
    }
    fn apply_language(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let lang = Language::resolve(&self.settings.language);
        cx.set_global(UiLanguage(lang));
        self.navigation.update(cx, |_, cx| cx.notify());
        let traffic_index = match self
            .settings
            .extra
            .get("trafficWarningBytesPerHour")
            .and_then(|v| v.as_u64())
        {
            Some(67108864) => 1,
            Some(268435456) => 2,
            Some(1073741824) => 3,
            _ => 0,
        };
        let entries = [
            (
                &self.polling,
                AVAILABILITY_INTERVALS
                    .iter()
                    .map(|s| interval(*s, lang))
                    .collect(),
                AVAILABILITY_INTERVALS
                    .iter()
                    .position(|s| *s == self.settings.availability_polling_seconds as u64)
                    .unwrap_or(3),
            ),
            (
                &self.country_polling,
                COUNTRY_INTERVALS
                    .iter()
                    .map(|s| interval(*s, lang))
                    .collect(),
                COUNTRY_INTERVALS
                    .iter()
                    .position(|s| *s == self.settings.country_polling_seconds as u64)
                    .unwrap_or(0),
            ),
            (
                &self.theme_selector,
                ["Как в Windows", "Светлая", "Тёмная"]
                    .map(|s| tr(cx, s))
                    .to_vec(),
                match self.settings.theme.as_str() {
                    "light" => 1,
                    "dark" => 2,
                    _ => 0,
                },
            ),
            (
                &self.traffic_limit,
                [
                    "После замера",
                    "64 МиБ за час",
                    "256 МиБ за час",
                    "1 ГиБ за час",
                ]
                .map(|s| tr(cx, s))
                .to_vec(),
                traffic_index,
            ),
            (
                &self.letter_size,
                ["Маленькие", "Средние", "Крупные"]
                    .map(|s| tr(cx, s))
                    .to_vec(),
                self.settings.geo_country_letter_size as usize,
            ),
            (
                &self.language_selector,
                vec![tr(cx, "Как в системе"), "Русский".into(), "English".into()],
                match self.settings.language.as_str() {
                    "ru" => 1,
                    "en" => 2,
                    _ => 0,
                },
            ),
        ];
        for (selector, items, index) in entries {
            selector.update(cx, |state, cx| {
                state.set_items(SearchableVec::new(items), window, cx);
                state.set_selected_index(Some(IndexPath::new(index)), window, cx);
            });
        }
        self.incident = None;
        self.chart_key = None;
        self.refresh_charts(cx);
        cx.notify();
    }
    fn apply_theme(&self, window: &mut Window, cx: &mut Context<Self>) {
        let mode = match self.settings.theme.as_str() {
            "light" => ThemeMode::Light,
            "dark" => ThemeMode::Dark,
            _ => match window.appearance() {
                WindowAppearance::Dark | WindowAppearance::VibrantDark => ThemeMode::Dark,
                _ => ThemeMode::Light,
            },
        };
        Theme::change(mode, Some(window), cx);
        Theme::update(cx, |theme| {
            theme.font_family = crate::fluent::FONT.into();
            theme.font_size = px(crate::fluent::BODY);
            theme.radius = px(4.);
            theme.radius_lg = px(8.);
            theme.shadow = false;
            let dark = theme.is_dark();
            let colors = &mut theme.colors;
            colors.background = rgb(if dark { 0x202020 } else { 0xf3f3f3 }).into();
            colors.foreground = rgb(if dark { 0xf1f2f6 } else { 0x1b1c22 }).into();
            colors.border = rgb(if dark { 0xffffff } else { 0 })
                .opacity(if dark { 0.07 } else { 0.075 })
                .into();
            colors.muted = rgb(if dark { 0x353535 } else { 0xeaeaea }).into();
            colors.muted_foreground = rgb(if dark { 0xa9adbb } else { 0x5b5f6d }).into();
            colors.popover = rgb(if dark { 0x303030 } else { 0xffffff }).into();
            colors.popover_foreground = colors.foreground;
            colors.title_bar = colors.background;
            colors.title_bar_border = colors.border;
            colors.primary = rgb(if dark { 0x4cc2ff } else { 0x0067c0 }).into();
            colors.primary_foreground = rgb(if dark { 0x00213b } else { 0xffffff }).into();
            colors.primary_hover = colors.primary.opacity(0.9);
            colors.primary_active = colors.primary.opacity(0.8);
            colors.button = rgb(0xffffff).opacity(if dark { 0.06 } else { 0.72 }).into();
            colors.button_foreground = colors.foreground;
            colors.button_hover = rgb(if dark { 0xffffff } else { 0xfafafa })
                .opacity(if dark { 0.085 } else { 0.55 })
                .into();
            colors.button_active = rgb(if dark { 0xffffff } else { 0 })
                .opacity(if dark { 0.03 } else { 0.07 })
                .into();
            colors.button_primary = colors.primary;
            colors.button_primary_foreground = colors.primary_foreground;
            colors.button_primary_hover = colors.primary_hover;
            colors.button_primary_active = colors.primary_active;
        });
        self.navigation.update(cx, |_, cx| cx.notify());
    }
    fn save_settings(&mut self, cx: &mut Context<Self>) -> bool {
        self.settings.normalize();
        let accepted = self
            .backend
            .command(Command::Settings(Box::new(self.settings.clone())));
        if accepted {
            self.pending_settings = self
                .pending_settings
                .max(self.state.settings_generation)
                .saturating_add(1);
        } else {
            self.notice =
                "Не удалось применить настройку: очередь занята. Повторите действие.".into();
        }
        cx.notify();
        accepted
    }
    fn send(&mut self, command: Command, cx: &mut Context<Self>) {
        if !self.backend.command(command) {
            self.notice = "Команда не принята: повторите действие через секунду.".into();
        }
        cx.notify();
    }
    fn shutdown(&mut self, cx: &mut Context<Self>) {
        if self.exiting {
            return;
        }
        self.flush_geometry(cx);
        self.exiting = true;
        self.backend.shutdown();
        cx.notify();
    }
    fn flush_geometry(&mut self, cx: &mut Context<Self>) {
        if self.geometry_changed.take().is_none() {
            return;
        }
        if let Some([x, y, width, height]) = self.last_bounds.take() {
            self.settings.window_width = width;
            self.settings.window_height = height;
            self.settings.window_x = x;
            self.settings.window_y = y;
            self.save_settings(cx);
        }
    }
    fn poll(&mut self, cx: &mut Context<Self>) {
        if self.instance.take_exit_request() {
            self.shutdown(cx);
        }
        let mut show = self.instance.take_show_request();
        while let Ok(event) = self.os.try_recv() {
            match event {
                OsEvent::Show => show = true,
                OsEvent::Exit => self.shutdown(cx),
                OsEvent::TogglePause => self.send(Command::Pause(!self.state.monitor.paused), cx),
                OsEvent::CheckNow => self.send(Command::CheckNow, cx),
                OsEvent::Suspend => self.send(Command::Suspend, cx),
                OsEvent::Resume => self.send(Command::Resume, cx),
                OsEvent::NetworkChanged => {
                    self.network_changed.get_or_insert_with(Instant::now);
                }
            }
        }
        if self
            .network_changed
            .is_some_and(|t| t.elapsed() >= Duration::from_millis(500))
        {
            self.network_changed = None;
            self.send(Command::NetworkChanged(platform::network_available()), cx);
        }
        if show {
            self.visible = true;
            for handle in cx.windows() {
                let _ = handle.update(cx, |_, window, _| crate::window_os::show(window));
            }
            self.chart_key = None;
            self.refresh_charts(cx);
            cx.notify();
        }
        if let Some(next) = self.backend.changed() {
            if next.settings_generation >= self.pending_settings {
                self.settings = next.settings.clone();
                self.pending_settings = 0;
            }
            self.state = next;
            if self.visible {
                self.timeline.update(cx, |timeline, cx| {
                    timeline.live(self.state.monitor.clone(), cx)
                });
            }
            let latest = self.state.locations.observations.last();
            let fresh = latest.is_some_and(|o| {
                let age = Utc::now() - o.until();
                o.confirmed
                    && !self.state.locations.observation_boundary
                    && !self.state.monitor.network_unavailable
                    && age >= ChronoDuration::zero()
                    && age < ChronoDuration::minutes(2)
            });
            self.tray.update(TrayState {
                provider: self.state.monitor.groups[0].availability,
                world: self.state.monitor.groups[1].availability,
                paused: self.state.monitor.paused,
                country: latest
                    .and_then(|o| o.country_code.clone())
                    .unwrap_or_default(),
                country_fresh: fresh,
                detection_enabled: self.settings.geo_country_detection_enabled,
                country_icon_enabled: self.settings.geo_country_icon_enabled,
                letter_scale: self.settings.geo_country_letter_size,
                language: language(cx),
            });
            if self.visible {
                self.refresh_charts(cx);
                cx.notify();
            }
        }
        if self.backend.finished() {
            if self.exiting || self.state.shutting_down {
                cx.quit();
                return;
            }
            self.health = None;
            self.ready_at = None;
            if self.state.monitor.monitor_error.as_deref()
                != Some("Монитор остановлен из-за внутренней ошибки.")
            {
                let mut state = (*self.state).clone();
                state.monitor.monitor_error =
                    Some("Монитор остановлен из-за внутренней ошибки.".into());
                for group in &mut state.monitor.groups {
                    group.availability = Availability::Unknown;
                    group.reason = "Монитор остановлен из-за внутренней ошибки.".into();
                    for endpoint in &mut group.endpoints {
                        endpoint.fresh = false;
                        endpoint.in_flight = false;
                    }
                }
                self.state = Arc::new(state);
                self.timeline.update(cx, |timeline, cx| {
                    timeline.live(self.state.monitor.clone(), cx)
                });
                self.notice="Монитор остановлен из-за внутренней ошибки. История сохранена до последней записи; перезапустите приложение. Подробности – в папке настроек, каталог logs.".into();
                cx.notify();
                self.tray.update(TrayState {
                    provider: Availability::Unknown,
                    world: Availability::Unknown,
                    paused: false,
                    country: String::new(),
                    country_fresh: false,
                    detection_enabled: self.settings.geo_country_detection_enabled,
                    country_icon_enabled: self.settings.geo_country_icon_enabled,
                    letter_scale: self.settings.geo_country_letter_size,
                    language: language(cx),
                });
            }
            return;
        }
        if self.visible && self.page == Page::Locations {
            let width = self.history_scroll.viewport_bounds().size.width;
            if width > px(0.) && self.history_width != Some(width) {
                // GPUI clears unmeasured height hints during its first prepaint
                // and width changes. Reapply after that layout, not before it.
                self.history_scroll = self
                    .history_scroll
                    .clone()
                    .with_uniform_item_height(px(64.));
                self.history_width = Some(width);
                cx.notify();
            }
        }
        if self.health.is_some()
            && self.tray.ready()
            && self.state.monitor.monitor_error.is_none()
            && !self.state.monitor.paused
            && self.state.monitor.groups.iter().any(|g| {
                g.endpoints.iter().any(|e| {
                    e.completed_utc.is_some()
                        && e.failure.as_deref() != Some("internal")
                        && e.outcome.is_some_and(|o| o != Outcome::Cancelled)
                })
            })
        {
            let since = self.ready_at.get_or_insert_with(Instant::now);
            if since.elapsed() >= Duration::from_secs(2)
                && let Some(name) = self.health.take()
                && platform::signal_health(&name).is_err()
            {
                self.notice = "Не удалось подтвердить запуск после обновления.".into();
            }
        } else {
            self.ready_at = None;
        }
        if self
            .geometry_changed
            .is_some_and(|t| t.elapsed() >= Duration::from_millis(600))
        {
            self.flush_geometry(cx);
        }
        for chart in &self.charts {
            if let Some(event) = chart.update(cx, |chart, _| chart.take_selected()) {
                self.incident = Some(event);
                cx.notify();
            }
        }
    }
    fn refresh_charts(&mut self, cx: &mut Context<Self>) {
        let end = self.state.monitor.generated_utc;
        let start = end - ChronoDuration::hours(self.settings.location_chart_window_hours);
        // A moving retention cutoff still dirties the file, but doesn't change
        // the topology. The time-axis bucket advances those clipped bounds.
        // Pointer movement never prepares history models.
        let key = (
            self.state.availability.structure_revision,
            self.state.locations.revision,
            self.settings.location_chart_window_hours,
            self.settings.emphasize_short_statuses,
            self.settings.show_location_debug_sources,
            end.timestamp() / 10,
        );
        if self.chart_key == Some(key) {
            return;
        }
        let period_changed = self.chart_key.is_some_and(|old| old.2 != key.2);
        let debug_changed = self.chart_key.is_some_and(|old| old.4 != key.4);
        self.chart_key = Some(key);
        let mut models = vec![
            charts::ChartModel::empty(start, end),
            charts::ChartModel::empty(start, end),
            charts::locations(&self.state.locations, start, end),
            charts::frequency_model(&self.state.locations, start, end),
        ];
        let range = self
            .overview_range
            .unwrap_or(crate::dashboard::TimeRange { start, end });
        let lang = language(cx);
        let availability = Group::ALL.map(|group| {
            let mut model = charts::availability(
                &self.state.availability,
                group,
                range.start,
                range.end,
                self.settings.emphasize_short_statuses,
            );
            model.language = lang;
            model
        });
        let stats =
            Group::ALL.map(|group| crate::dashboard::stats(&self.state.availability, group, range));
        let changes = Arc::new(crate::dashboard::country_changes(
            &self.state.locations,
            range,
        ));
        self.overview_events = Arc::new(crate::dashboard::recent_events(
            &self.state.availability,
            &changes,
            range,
        ));
        self.timeline.update(cx, |timeline, cx| {
            timeline.live(self.state.monitor.clone(), cx);
            timeline.set(
                availability,
                stats,
                self.state.availability.clone(),
                self.state.locations.clone(),
                changes,
                cx,
            )
        });
        if self.settings.show_location_debug_sources {
            for which in 0..3 {
                models.push(charts::source(&self.state.locations, which, start, end));
            }
        }
        for (i, mut model) in models.into_iter().enumerate() {
            model.language = language(cx);
            if let Some(chart) = self.charts.get(i) {
                chart.update(cx, |chart, cx| chart.set(model, cx));
            } else {
                self.charts.push(cx.new(|_| Chart::new(model)));
            }
        }
        if !self.settings.show_location_debug_sources {
            self.charts.truncate(4);
        }
        self.location_rows = Arc::new(crate::location_rows::build(
            &self.state.locations,
            start,
            end,
        ));
        let count = self.location_rows.len() + 1;
        if self.history_scroll.item_count() != count || period_changed {
            self.history_scroll
                .reset_with_uniform_height(count, px(64.));
        } else if debug_changed {
            self.history_scroll.splice(0..1, 1);
        }
    }
    fn discard_chart_models(&mut self, cx: &mut Context<Self>) {
        // Hidden Debug graphs mustn't retain an obsolete 70k-observation snapshot
        // while the monitor publishes newer history in the tray.
        let end = self.state.monitor.generated_utc;
        let start = end - ChronoDuration::hours(self.settings.location_chart_window_hours);
        for chart in &self.charts {
            chart.update(cx, |chart, cx| {
                chart.set(charts::ChartModel::empty(start, end), cx)
            });
        }
        self.location_rows = Arc::new(vec![]);
        self.history_scroll.reset_with_uniform_height(1, px(64.));
        self.chart_key = None;
        self.incident = None;
        self.overview_selected = None;
        self.overview_events = Arc::default();
        self.timeline
            .update(cx, |timeline, cx| timeline.release(cx));
    }
    fn period(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let mut row = div().flex().gap_1().flex_wrap().items_center();
        for hours in PERIODS {
            let selected = self.settings.location_chart_window_hours == hours as i64;
            row = row.child(
                crate::fluent::button(("period", hours as usize))
                    .label(if hours < 24 {
                        format!("{hours} {}", language(cx).choose("ч", "h"))
                    } else if hours == 24 {
                        language(cx).choose("24 ч", "24 h").into()
                    } else {
                        format!("{} {}", hours / 24, language(cx).choose("д", "d"))
                    })
                    .when(selected, |b| b.primary())
                    .when(!selected, |b| b.ghost())
                    .on_click(cx.listener(move |this, _, _, cx| {
                        this.settings.location_chart_window_hours = hours as i64;
                        this.overview_range = None;
                        this.overview_selected = None;
                        this.timeline
                            .update(cx, |timeline, cx| timeline.select(None, cx));
                        this.chart_key = None;
                        this.incident = None;
                        this.history_scroll.scroll_to(ListOffset::default());
                        this.save_settings(cx);
                        this.refresh_charts(cx);
                    })),
            );
        }
        row
    }
}
impl Render for NetLights {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let navigation = self.navigation.clone();
        // Commands sit outside the title bar's native drag region.
        let commands = div()
            .flex()
            .items_center()
            .gap_2()
            .child(
                crate::fluent::compact_button("check-now")
                    .label(tr(cx, "Проверить сейчас"))
                    .ghost()
                    .on_click(cx.listener(|this, _, _, cx| this.send(Command::CheckNow, cx))),
            )
            .child(
                crate::fluent::compact_button("pause")
                    .label(if self.state.monitor.paused {
                        tr(cx, "Продолжить")
                    } else {
                        language(cx).choose("Пауза", "Pause").to_string()
                    })
                    .on_click(cx.listener(|this, _, _, cx| {
                        this.send(Command::Pause(!this.state.monitor.paused), cx)
                    })),
            );
        let narrow = window.viewport_size().width < px(780.);
        // GPUI follows the OS backdrop appearance. A forced app theme can differ;
        // use its solid surface then, so title and navigation retain contrast.
        let mica = crate::window_os::mica_supported()
            && matches!(
                window.appearance(),
                WindowAppearance::Dark | WindowAppearance::VibrantDark
            ) == cx.theme().is_dark();
        let toolbar = div()
            .px_4()
            .flex_shrink_0()
            .flex()
            .when(narrow, |d| d.flex_col().items_start().pb_2().gap_1())
            .when(!narrow, |d| d.items_center().justify_between().gap_3())
            .border_b_1()
            .border_color(cx.theme().border.opacity(0.7))
            .child(navigation)
            .child(commands);
        let titlebar = TitleBar::new()
            .h(px(40.))
            .bg(transparent_black())
            .border_b_0()
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(div().size(px(12.)).rounded_sm().bg(cx.theme().primary))
                    .child(
                        div()
                            .font_weight(FontWeight::SEMIBOLD)
                            .text_size(px(13.))
                            .child("Net Lights"),
                    ),
            );
        let content = match self.page {
            Page::State => self.overview(cx),
            Page::Locations => self.locations(cx),
            Page::Diagnostics => self.diagnostics(cx),
            Page::Settings => self.settings(window, cx),
            Page::About => self.about_page(cx),
        };
        let mut main = div().flex_1().min_h_0().min_w_0().flex().flex_col();
        if self.exiting {
            main = main.child(
                div()
                    .px_4()
                    .py_2()
                    .child(tr(cx, "Сохранение истории и завершение...")),
            );
        }
        if self.state.traffic_warning {
            let limit = language(cx).bytes(self.state.traffic_limit.unwrap_or(0));
            let notice = if language(cx) == Language::En {
                format!(
                    "Unusual check traffic. The hourly limit of {limit} was exceeded. See Diagnostics."
                )
            } else {
                format!(
                    "Необычный расход трафика проверок. Превышен порог {limit} за час. Подробности – в диагностике."
                )
            };
            main = main.child(
                div()
                    .px_4()
                    .py_3()
                    .bg(rgb(0xffedc5))
                    .text_color(rgb(0x69430b))
                    .child(notice),
            );
        }
        if !self.notice.is_empty() {
            main = main.child(div().px_4().py_2().child(tr(cx, &self.notice)));
        }
        for warning in &self.state.warnings {
            main = main.child(
                div()
                    .px_4()
                    .py_2()
                    .text_size(px(12.))
                    .text_color(rgb(0xb87925))
                    .child(warning.get(language(cx)).to_string()),
            );
        }
        main = if matches!(self.page, Page::Locations | Page::Settings | Page::About) {
            main.child(div().flex_1().min_h_0().child(content))
        } else {
            main.child(
                div()
                    .id(("page-scroll", self.page as usize))
                    .flex_1()
                    .min_h_0()
                    .p_4()
                    .child(content)
                    .overflow_y_scrollbar(),
            )
        };
        let main = if self.page_transition != 0 && !cx.reduce_motion() {
            gpui_kit::base::animation::EffectTransition::new(Duration::from_millis(250))
                .ease(gpui_kit::base::animation::cubic_bezier(0.33, 0., 0.1, 1.))
                .slide_x(px(32. * self.page_direction), px(0.))
                .apply(main.relative(), ("page-enter", self.page_transition))
                .into_any_element()
        } else {
            main.into_any_element()
        };
        div()
            .size_full()
            .flex()
            .flex_col()
            .overflow_hidden()
            .font_family(crate::fluent::FONT)
            .text_size(px(crate::fluent::BODY))
            .line_height(relative(1.4))
            .when(!mica, |d| d.bg(cx.theme().background))
            .text_color(cx.theme().foreground)
            .child(titlebar)
            .child(toolbar)
            .child(main)
    }
}
fn selector(
    values: Vec<String>,
    index: usize,
    window: &mut Window,
    cx: &mut Context<NetLights>,
) -> Selector {
    cx.new(|cx| {
        SelectState::new(
            SearchableVec::new(values),
            Some(IndexPath::new(index)),
            window,
            cx,
        )
    })
}
fn interval(s: u64, lang: Language) -> String {
    if s < 60 {
        format!("{s} {}", lang.choose("с", "s"))
    } else {
        format!("{} {}", s / 60, lang.choose("мин", "min"))
    }
}
fn surface(cx: &App) -> Hsla {
    crate::fluent::surface(cx)
}
fn panel(cx: &App) -> Div {
    div()
        .w_full()
        .p_4()
        .rounded_lg()
        .border_1()
        .border_color(cx.theme().border)
        .bg(surface(cx))
}
fn section_title(text: &str, cx: &App) -> Div {
    div()
        .font_weight(FontWeight::MEDIUM)
        .text_size(px(14.))
        .child(text!(
            id = SharedString::from(format!("heading-{text}")),
            tr(cx, text)
        ))
}
fn muted(text: impl Into<String>, cx: &App) -> Div {
    let text = text.into();
    div()
        .mt_1()
        .text_size(px(12.))
        .text_color(cx.theme().muted_foreground)
        .child(tr(cx, &text))
}
fn setting_row(title: &str, description: &str, control: impl IntoElement, cx: &App) -> Div {
    use gpui_kit::assets::IconName as Glyph;
    let glyph = match title {
        "Язык интерфейса" | "Размер букв" => Glyph::Languages,
        "Оформление" => Glyph::Paintbrush,
        "Доступность сайтов" | "Предупреждать о трафике" => {
            Glyph::Gauge
        }
        "Определение страны" | "Определять страну" | "Значок страны в трее" => {
            Glyph::MapPin
        }
        "Автозапуск" => Glyph::Power,
        "Автообновление" => Glyph::RefreshCw,
        "Данные приложения" => Glyph::Folder,
        _ => Glyph::Info,
    };
    div()
        .flex()
        .items_center()
        .justify_between()
        .gap(px(16.))
        .px(px(16.))
        .py(px(12.))
        .rounded(px(6.))
        .border_1()
        .border_color(cx.theme().border)
        .bg(surface(cx))
        .child(
            div().flex_shrink_0().child(
                gpui_kit::component::Icon::new(glyph)
                    .size(px(20.))
                    .text_color(cx.theme().muted_foreground),
            ),
        )
        .child(
            div()
                .flex_1()
                .min_w_0()
                .child(tr(cx, title))
                .child(muted(description, cx)),
        )
        .child(div().flex_shrink_0().child(control))
}
fn legend(cx: &App) -> Div {
    div().flex().gap_3().flex_wrap().children(
        [
            (rgb(0x2aa87a).into(), "Доступно"),
            (rgb(0xe9b33c).into(), "Ограничено"),
            (rgb(0xe65060).into(), "Нет ответа"),
            (rgb(0x98a5b5).into(), "Неизвестно"),
            (rgb(0x8b7bc1).into(), "Пауза"),
            (
                cx.theme()
                    .primary
                    .opacity(if cx.theme().is_dark() { 0.16 } else { 0.08 }),
                "Нет записей",
            ),
        ]
        .into_iter()
        .map(|(color, label): (Hsla, &str)| {
            div()
                .flex()
                .items_center()
                .gap_1()
                .text_size(px(11.))
                .text_color(cx.theme().muted_foreground)
                .child(div().size(px(7.)).rounded_full().bg(color))
                .child(tr(cx, label))
        }),
    )
}
