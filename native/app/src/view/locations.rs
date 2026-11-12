use super::*;

impl NetLights {
    pub(super) fn locations_header(&self, cx: &mut Context<Self>) -> AnyElement {
        let lang = language(cx);
        let mut body = div().flex().flex_col().gap_3().child(self.period(cx));
        if let Some(through) = self.state.locations.capacity_truncated_through_utc {
            let through = charts::local_time(through);
            body = body.child(muted(
                if lang == Language::En {
                    format!("Older records were removed at the storage limit, through {through}.")
                } else {
                    format!("Часть старых записей удалена из-за ограничения объёма: до {through}")
                },
                cx,
            ));
        }
        if !self.settings.geo_country_detection_enabled {
            body = body.child(
                panel(cx)
                    .child(tr(cx, "Определение страны выключено"))
                    .child(muted(
                        "Сохранённая история доступна. Включить новые проверки можно в параметрах.",
                        cx,
                    )),
            );
        }
        body = body
            .child(
                panel(cx)
                    .child(section_title("История страны", cx))
                    .child(div().mt_5().child(self.charts[2].clone())),
            )
            .child(
                panel(cx)
                    .child(section_title("Частота смен страны", cx))
                    .child(div().mt_3().child(self.charts[3].clone()))
                    .child(muted(
                        "Голубой фон – нет наблюдений. Ноль – наблюдения есть, смен не было.",
                        cx,
                    )),
            )
            .child(
                panel(cx).child(
                    Switch::new("debug-sources")
                        .checked(self.settings.show_location_debug_sources)
                        .label(tr(cx, "Debug: источники определения"))
                        .on_change(cx.listener(|this, value, _, cx| {
                            this.settings.show_location_debug_sources = *value;
                            this.save_settings(cx);
                            this.refresh_charts(cx);
                        })),
                ),
            );
        if self.settings.show_location_debug_sources {
            body=body.child(muted("Сохранённые результаты проверок. Старые онлайн-записи без имени источника не приписываются country.is.",cx));
            for (i, title) in ["Debug: country.is", "Debug: V2Fly", "Debug: IPLocate"]
                .iter()
                .enumerate()
            {
                body = body.child(
                    panel(cx)
                        .child(section_title(title, cx))
                        .child(div().mt_3().child(self.charts[4 + i].clone())),
                );
            }
        }
        body = body.child(
            div()
                .pt_1()
                .pb_2()
                .child(section_title("Периоды наблюдения", cx))
                .when(self.location_rows.is_empty(), |d| {
                    d.child(
                        panel(cx)
                            .mt_3()
                            .child(muted("В выбранном периоде нет записей.", cx)),
                    )
                }),
        );
        div().p_4().pb_0().child(body).into_any_element()
    }

    pub(super) fn location_row(&self, index: usize, cx: &App) -> AnyElement {
        let lang = language(cx);
        let (stay_index, ip) = &self.location_rows[index];
        let history = &self.state.locations;
        let stay = &history.stays[*stay_index];
        let until = stay
            .ended_utc
            .unwrap_or(history.last_observed_utc.unwrap_or(stay.started_utc));
        let current = stay.ended_utc.is_none();
        let color = charts::country_color(&stay.iso);
        let started = stay.started_utc.with_timezone(&Local);
        let ended = until.with_timezone(&Local);
        let period = format!(
            "{} – {}{}",
            started.format("%d.%m %H:%M"),
            ended.format(if started.date_naive() == ended.date_naive() {
                "%H:%M"
            } else {
                "%d.%m %H:%M"
            }),
            if current {
                lang.choose("  Текущая", "  Current")
            } else {
                ""
            }
        );
        let duration = if until == stay.started_utc {
            tr(cx, "1 проверка")
        } else {
            lang.duration((until - stay.started_utc).num_milliseconds())
        };
        let full: SharedString = format!(
            "{}\n{}: {} – {}\n{}{}\n{}",
            lang.country_name(&stay.iso),
            lang.choose("Период истории", "History period"),
            charts::local_time(stay.started_utc),
            charts::local_time(until),
            duration,
            ip.as_ref()
                .map(|ip| format!("\n{}: {ip}", lang.choose("Последний IP", "Last IP")))
                .unwrap_or_default(),
            lang.choose(
                "Точное время перехода между проверками неизвестно.",
                "The exact transition time between checks is unknown."
            )
        )
        .into();
        let row = div()
            .id(("location-row", *stay_index))
            .tooltip(move |window, cx| {
                component::tooltip::Tooltip::new(full.clone()).build(window, cx)
            })
            .w_full()
            .h(px(64.))
            .px_4()
            .flex()
            .gap_3()
            .items_center()
            .border_x_1()
            .border_b_1()
            .border_color(cx.theme().border.opacity(0.7))
            .bg(surface(cx))
            .when(index == 0, |d| d.border_t_1().rounded_t_lg())
            .when(index + 1 == self.location_rows.len(), |d| d.rounded_b_lg())
            .child(
                div()
                    .w(px(35.))
                    .h(px(30.))
                    .rounded_md()
                    .bg(rgb(color))
                    .text_color(rgb(charts::country_text_color(color)))
                    .flex()
                    .items_center()
                    .justify_center()
                    .font_weight(FontWeight::BOLD)
                    .child(stay.iso.clone()),
            )
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .child(
                        div()
                            .truncate()
                            .font_weight(FontWeight::MEDIUM)
                            .when(stay.iso == "RU", |d| {
                                d.text_color(rgb(if cx.theme().is_dark() {
                                    0xff6b6b
                                } else {
                                    color
                                }))
                            })
                            .child(lang.country_name(&stay.iso).to_string()),
                    )
                    .child(
                        div()
                            .truncate()
                            .text_size(px(12.))
                            .text_color(cx.theme().muted_foreground)
                            .child(period),
                    )
                    .when_some(ip.as_ref(), |d, ip| {
                        d.child(
                            div()
                                .truncate()
                                .text_size(px(12.))
                                .text_color(cx.theme().muted_foreground)
                                .child(format!("{}: {ip}", lang.choose("Последний IP", "Last IP"))),
                        )
                    }),
            )
            .child(
                div()
                    .flex_shrink_0()
                    .text_size(px(12.))
                    .font_weight(FontWeight::MEDIUM)
                    .child(duration),
            );
        div().w_full().px_4().child(row).into_any_element()
    }

    pub(super) fn locations(&self, cx: &mut Context<Self>) -> AnyElement {
        let items = list(
            self.history_scroll.clone(),
            cx.processor(|this, index, _, cx| {
                if index == 0 {
                    this.locations_header(cx)
                } else {
                    this.location_row(index - 1, cx)
                }
            }),
        )
        .size_full();
        div()
            .size_full()
            .relative()
            .child(items)
            .vertical_scrollbar(&self.history_scroll)
            .into_any_element()
    }
}
