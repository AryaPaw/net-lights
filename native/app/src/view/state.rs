use super::*;
use crate::dashboard::{self, EventKind, HistoryEvent};
use gpui_kit::component::{Icon, IconName};

fn event_title(event: &HistoryEvent, lang: Language) -> String {
    match &event.kind {
        EventKind::Availability {
            group,
            state,
            paused,
        } => format!(
            "{}: {}",
            crate::text::translate(lang, group.label()),
            crate::text::translate(lang, if *paused { "Пауза" } else { state.label() })
        ),
        EventKind::Country {
            previous, current, ..
        } => format!(
            "{} → {}",
            lang.country_name(previous),
            lang.country_name(current)
        ),
    }
}
impl NetLights {
    fn overview_hero(&self, cx: &mut Context<Self>) -> AnyElement {
        let lang = language(cx);
        let snapshot = &self.state.monitor;
        let (status, paused) = dashboard::overall(snapshot);
        let color = rgb(charts::status_color(status, paused));
        let title = if paused {
            lang.choose("Проверки приостановлены", "Monitoring paused")
        } else if snapshot.monitor_error.is_some() {
            lang.choose("Ошибка монитора", "Monitor error")
        } else {
            match status {
                Availability::Online => lang.choose("Соединение доступно", "Connection available"),
                Availability::Limited => lang.choose("Есть ограничения", "Some checks are limited"),
                Availability::Offline => lang.choose(
                    "Есть недоступные направления",
                    "Some destinations are unavailable",
                ),
                Availability::Unknown => {
                    lang.choose("Ожидаем свежие данные", "Waiting for recent results")
                }
            }
        };
        // Publications aren't network checks: require fresh results in both groups.
        let completed = snapshot
            .groups
            .iter()
            .map(|g| {
                g.endpoints
                    .iter()
                    .filter(|e| e.fresh)
                    .filter_map(|e| e.completed_utc)
                    .max()
            })
            .collect::<Option<Vec<_>>>()
            .and_then(|v| v.into_iter().min());
        let freshness = completed
            .filter(|at| *at <= snapshot.generated_utc)
            .map(|at| {
                format!(
                    "{} {} {}",
                    lang.choose("Свежие результаты", "Recent results"),
                    lang.duration((snapshot.generated_utc - at).num_milliseconds()),
                    lang.choose("назад", "ago")
                )
            })
            .unwrap_or_else(|| {
                lang.choose(
                    "Ожидается завершение проверок",
                    "Waiting for checks to complete",
                )
                .into()
            });
        let freshness = if snapshot.monitor_error.is_some() {
            lang.choose(
                "Проверки остановлены из-за ошибки монитора.",
                "Checks stopped because of a monitor error.",
            )
            .into()
        } else if paused {
            lang.choose(
                "История сохранена. Новые проверки приостановлены.",
                "History is preserved. New checks are paused.",
            )
            .into()
        } else if snapshot.network_unavailable {
            lang.choose(
                "Проверки возобновятся при появлении сетевого подключения.",
                "Checks will resume when a network connection is available.",
            )
            .into()
        } else {
            freshness
        };
        let current = self.state.locations.observations.last().filter(|o| {
            self.settings.geo_country_detection_enabled
                && !self.state.locations.observation_boundary
                && !snapshot.network_unavailable
                && o.confirmed
                && snapshot.generated_utc >= o.until()
                && snapshot.generated_utc - o.until() < ChronoDuration::minutes(2)
        });
        let code = current.and_then(|o| o.country_code.as_deref());
        let country = code.map(|iso| lang.country_name(iso)).unwrap_or_else(|| {
            if self.settings.geo_country_detection_enabled {
                lang.choose("Нет свежего определения", "No recent country result")
            } else {
                lang.choose("Определение страны выключено", "Country detection is off")
            }
        });
        panel(cx)
            .p_5()
            .child(
                div()
                    .flex()
                    .items_center()
                    .justify_between()
                    .gap_4()
                    .flex_wrap()
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap_4()
                            .min_w_0()
                            .child(
                                div()
                                    .size(px(46.))
                                    .rounded_lg()
                                    .bg(color.opacity(0.12))
                                    .text_color(color)
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .child(
                                        Icon::new(if status == Availability::Online && !paused {
                                            IconName::Check
                                        } else {
                                            IconName::Info
                                        })
                                        .size(px(24.)),
                                    ),
                            )
                            .child(
                                div()
                                    .flex()
                                    .flex_col()
                                    .gap_1()
                                    .child(
                                        div()
                                            .text_size(px(11.))
                                            .text_color(cx.theme().muted_foreground)
                                            .child(lang.choose("СЕЙЧАС", "RIGHT NOW")),
                                    )
                                    .child(
                                        div()
                                            .text_size(px(23.))
                                            .font_weight(FontWeight::SEMIBOLD)
                                            .child(text!(id = "overview-status", title)),
                                    )
                                    .child(
                                        div()
                                            .text_size(px(12.))
                                            .text_color(cx.theme().muted_foreground)
                                            .child(freshness),
                                    ),
                            ),
                    )
                    .child(
                        crate::fluent::button("overview-country")
                            .ghost()
                            .label(format!("{}   {}", code.unwrap_or("–"), country))
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.select_page(Page::Locations, cx);
                                this.overview_selected = None;
                                this.timeline
                                    .update(cx, |timeline, cx| timeline.select(None, cx));
                                cx.notify();
                            })),
                    ),
            )
            .into_any_element()
    }
    fn overview_event(&self, cx: &mut Context<Self>) -> Option<AnyElement> {
        let event = self.overview_selected.as_ref()?;
        let lang = language(cx);
        let range = match &event.kind {
            EventKind::Availability { .. } => format!(
                "{} – {}",
                charts::local_time(event.start),
                charts::local_time(event.end)
            ),
            EventKind::Country { previous_end, .. } => format!(
                "{} – {}",
                charts::local_time(*previous_end),
                charts::local_time(event.start)
            ),
        };
        let detail=match event.kind {
            EventKind::Availability {..}=>format!("{}: {}. {}",lang.choose("Записанная длительность","Recorded duration"),charts::recorded_duration(event.end-event.start,lang),lang.choose("Время соответствует истории монитора, а не точному моменту сетевого перехода.","Times refer to monitor history, not the exact physical network transition.")),
            EventKind::Country {..}=>lang.choose("Страна изменилась между наблюдениями. Точный момент перехода неизвестен.","The country changed between observations. The exact transition time is unknown.").into(),
        };
        let country = dashboard::country_at(&self.state.locations, event.start)
            .map(|iso| lang.country_name(iso))
            .unwrap_or_else(|| lang.choose("не записана", "not recorded"));
        let mut card = panel(cx)
            .border_color(cx.theme().primary.opacity(0.45))
            .child(
                div()
                    .flex()
                    .items_center()
                    .justify_between()
                    .gap_3()
                    .child(section_title("Выбранное событие", cx))
                    .child(
                        crate::fluent::compact_button("close-event")
                            .ghost()
                            .label(lang.choose("Закрыть", "Close"))
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.overview_selected = None;
                                this.timeline
                                    .update(cx, |timeline, cx| timeline.select(None, cx));
                                cx.notify();
                            })),
                    ),
            )
            .child(
                div()
                    .mt_2()
                    .font_weight(FontWeight::SEMIBOLD)
                    .child(text!(id = "selected-event-title", event_title(event, lang))),
            )
            .child(
                div()
                    .mt_2()
                    .text_size(px(12.))
                    .child(text!(id = "selected-event-time", range)),
            )
            .child(muted(detail, cx))
            .when(event.ongoing, |d| {
                d.child(muted(
                    lang.choose(
                        "Снимок открытого участка на момент выбора.",
                        "Snapshot of an open interval at selection time.",
                    ),
                    cx,
                ))
            })
            .child(muted(
                format!(
                    "{}: {country}",
                    lang.choose(
                        "Страна в начале участка",
                        "Country at the start of this interval"
                    )
                ),
                cx,
            ));
        if let EventKind::Availability { group, .. } = event.kind {
            let endpoint = self.state.monitor.groups[group.index()]
                .endpoints
                .iter()
                .find(|e| !e.in_flight)
                .map(|e| e.endpoint.id.clone());
            card=card.child(div().mt_3().flex().items_center().gap_3().flex_wrap()
                .child(crate::fluent::compact_button("event-diagnose-now").label(lang.choose("Диагностика сейчас","Diagnose now")).disabled_with_cursor(endpoint.is_none()||self.state.monitor.paused||self.state.monitor.monitor_error.is_some())
                    .on_click(cx.listener(move |this,_,_,cx|{if let Some(id)=&endpoint {this.send(Command::Diagnose(id.clone()),cx);}})))
                .child(muted(lang.choose("Проверка текущего состояния. Исторические HTTP-ответы и задержки не сохранялись.","Checks the current connection. Historical HTTP responses and response times weren't recorded."),cx)));
        }
        Some(card.into_any_element())
    }
    pub(super) fn overview(&self, cx: &mut Context<Self>) -> AnyElement {
        let lang = language(cx);
        let mut header = div()
            .flex()
            .items_center()
            .justify_between()
            .gap_3()
            .flex_wrap()
            .child(section_title("История доступности", cx))
            .child(self.period(cx));
        if let Some(range) = self.overview_range {
            header = header.child(
                div()
                    .flex()
                    .items_center()
                    .gap_3()
                    .flex_wrap()
                    .child(
                        div()
                            .text_size(px(12.))
                            .text_color(cx.theme().muted_foreground)
                            .child(text!(
                                id = "overview-zoom-range",
                                format!(
                                    "{}: {} – {}",
                                    lang.choose("Участок", "Range"),
                                    charts::local_time(range.start),
                                    charts::local_time(range.end)
                                )
                            )),
                    )
                    .child(
                        crate::fluent::compact_button("reset-overview-zoom")
                            .ghost()
                            .label(lang.choose("Весь период", "Full period"))
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.overview_range = None;
                                this.overview_selected = None;
                                this.timeline
                                    .update(cx, |timeline, cx| timeline.select(None, cx));
                                this.chart_key = None;
                                this.refresh_charts(cx);
                                cx.notify();
                            })),
                    ),
            );
        }
        let mut history=panel(cx).child(header)
            .child(div().mt_4().child(self.timeline.clone()))
            .child(div().mt_4().pt_3().border_t_1().border_color(cx.theme().border.opacity(0.65)).flex().items_center().justify_between().gap_3().flex_wrap().child(legend(cx))
                .child(crate::fluent::switch("short-events").checked(self.settings.emphasize_short_statuses).focus_ring(false).label(tr(cx,"Увеличивать короткие отметки"))
                    .tooltip(tr(cx,"Увеличивает короткие события только на графике. В подсказке показана фактическая длительность."))
                    .on_change(cx.listener(|this,value,_,cx|{this.settings.emphasize_short_statuses = *value;this.save_settings(cx);this.refresh_charts(cx);}))))
            .child(div().mt_3().text_size(px(11.)).text_color(cx.theme().muted_foreground).child(lang.choose("Выделите участок мышью, чтобы увеличить. Нажмите на полосу или событие для подробностей.","Drag across a lane to zoom. Click a lane or an event for details.")));
        if let Some(until) = self.state.availability.capacity_truncated_through_utc {
            history = history.child(muted(
                format!(
                    "{} {}",
                    lang.choose(
                        "Часть ранних записей удалена из-за лимита. Граница:",
                        "Some older records were removed at the storage limit. Boundary:"
                    ),
                    charts::local_time(until)
                ),
                cx,
            ));
        }
        let mut events = panel(cx).child(
            div()
                .flex()
                .items_center()
                .justify_between()
                .gap_3()
                .child(section_title("Последние события", cx))
                .child(
                    div()
                        .text_size(px(11.))
                        .text_color(cx.theme().muted_foreground)
                        .child(lang.choose("В выбранном диапазоне", "In the selected range")),
                ),
        );
        if self.overview_events.is_empty() {
            events = events.child(div().py_4().text_color(cx.theme().muted_foreground).child(
                lang.choose(
                    "Событий в сохранённых наблюдениях нет.",
                    "No events in the recorded observations.",
                ),
            ));
        }
        for (i, event) in self.overview_events.iter().enumerate() {
            let selected = self
                .overview_selected
                .as_ref()
                .is_some_and(|s| s.start == event.start && s.kind == event.kind);
            let event = event.clone();
            let duration = match event.kind {
                EventKind::Country { .. } => lang.choose("Смена страны", "Country change").into(),
                _ => charts::recorded_duration(event.end - event.start, lang),
            };
            let label = format!(
                "{}   {}   {}",
                event.start.with_timezone(&Local).format("%d.%m %H:%M:%S"),
                event_title(&event, lang),
                duration
            );
            events = events.child(
                div()
                    .mt_2()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(
                        div()
                            .w(px(3.))
                            .flex_shrink_0()
                            .h(px(24.))
                            .rounded_sm()
                            .bg(rgb(event.color())),
                    )
                    .child(
                        crate::fluent::button(("recent-event", i))
                            .ghost()
                            .w_full()
                            .flex_1()
                            .min_w_0()
                            .h(px(54.))
                            .justify_start()
                            .when(selected, |b| b.bg(cx.theme().primary.opacity(0.12)))
                            .accessibility_label(label)
                            .child(
                                div()
                                    .w_full()
                                    .flex()
                                    .items_center()
                                    .justify_between()
                                    .gap_3()
                                    .child(
                                        div()
                                            .flex_1()
                                            .min_w_0()
                                            .flex()
                                            .flex_col()
                                            .items_start()
                                            .gap_1()
                                            .child(
                                                div()
                                                    .whitespace_normal()
                                                    .text_size(px(13.))
                                                    .font_weight(FontWeight::MEDIUM)
                                                    .child(event_title(&event, lang)),
                                            )
                                            .child(
                                                div()
                                                    .text_size(px(11.))
                                                    .text_color(cx.theme().muted_foreground)
                                                    .child(
                                                        event
                                                            .start
                                                            .with_timezone(&Local)
                                                            .format("%d.%m %H:%M:%S")
                                                            .to_string(),
                                                    ),
                                            ),
                                    )
                                    .child(
                                        div()
                                            .flex_shrink_0()
                                            .text_size(px(12.))
                                            .text_color(cx.theme().muted_foreground)
                                            .child(duration),
                                    ),
                            )
                            .on_click(cx.listener(move |this, _, _, cx| {
                                this.overview_selected = Some(event.clone());
                                this.timeline.update(cx, |timeline, cx| {
                                    timeline.select(Some(event.clone()), cx)
                                });
                                cx.notify();
                            })),
                    ),
            );
        }
        let mut body = div()
            .flex()
            .flex_col()
            .gap_3()
            .child(self.overview_hero(cx))
            .child(history);
        if let Some(event) = self.overview_event(cx) {
            body = body.child(event);
        }
        if !self.state.diagnostic.is_empty() {
            body = body.child(
                panel(cx)
                    .child(section_title("Текущая диагностика", cx))
                    .child(muted(self.state.diagnostic.get(lang), cx)),
            );
        }
        body.child(events).into_any_element()
    }
}
