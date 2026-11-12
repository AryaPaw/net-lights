use super::*;

impl NetLights {
    pub(super) fn diagnostics(&self, cx: &mut Context<Self>) -> AnyElement {
        let lang = language(cx);
        let mut body=div().flex().flex_col().gap_3().child(panel(cx).child(section_title("Проверки адресов",cx))
            .child(muted("Цвет состояния определяется HTTPS. Ручная проверка использует общие ограничения запросов.",cx)));
        for group in &self.state.monitor.groups {
            let mut card = panel(cx).child(section_title(group.group.label(), cx));
            for endpoint in &group.endpoints {
                let id = endpoint.endpoint.id.clone();
                let color = match (endpoint.fresh, endpoint.outcome) {
                    (true, Some(Outcome::Reachable)) => 0x2aa87a,
                    (true, Some(Outcome::Unreachable)) => 0xe65060,
                    (true, Some(Outcome::Indeterminate)) => 0xe9b33c,
                    _ => 0x98a5b5,
                };
                let age = endpoint
                    .completed_utc
                    .map(|at| {
                        let ms = (self.state.monitor.generated_utc - at).num_milliseconds();
                        if ms < 0 {
                            tr(cx, "Время проверки не подтверждено")
                        } else {
                            format!(
                                "{}{}",
                                lang.duration(ms),
                                if endpoint.fresh {
                                    lang.choose(" назад", " ago")
                                } else {
                                    lang.choose(" назад (устарела)", " ago (stale)")
                                }
                            )
                        }
                    })
                    .unwrap_or_else(|| tr(cx, "Ещё не проверялся"));
                card = card.child(
                    div()
                        .flex()
                        .items_center()
                        .gap_3()
                        .py_2()
                        .child(div().size(px(7.)).rounded_full().bg(rgb(color)))
                        .child(
                            div()
                                .flex_1()
                                .min_w_0()
                                .child(
                                    div().truncate().child(
                                        endpoint
                                            .endpoint
                                            .uri
                                            .host_str()
                                            .unwrap_or(lang.choose("Адрес", "Address"))
                                            .to_string(),
                                    ),
                                )
                                .child(muted(
                                    format!(
                                        "{}: {}  {}: {}  HTTP: {}  {}",
                                        lang.choose("Проверок", "Checks"),
                                        endpoint.stats.samples,
                                        lang.choose("Ответов", "Responses"),
                                        endpoint.stats.reachable,
                                        endpoint
                                            .http_status
                                            .map(|s| s.to_string())
                                            .unwrap_or_else(|| "–".into()),
                                        age
                                    ),
                                    cx,
                                )),
                        )
                        .child(
                            crate::fluent::button(SharedString::from(format!("probe-{id}")))
                                .label(tr(
                                    cx,
                                    if endpoint.in_flight {
                                        "Проверяется"
                                    } else {
                                        "Проверить"
                                    },
                                ))
                                .flex_shrink_0()
                                .disabled(endpoint.in_flight)
                                .on_click(cx.listener(move |this, _, _, cx| {
                                    this.send(Command::Diagnose(id.clone()), cx)
                                })),
                        ),
                );
            }
            body = body.child(card);
        }
        body=body.child(panel(cx).child(section_title("Debug: трафик приложения",cx)).child(muted("С начала запуска. TCP-данные с TLS; без IP-заголовков и повторной передачи. Загрузки баз и обновлений учитываются отдельно.",cx))
            .children(self.state.traffic.iter().map(|row|div().flex().justify_between().gap_3().py_2().child(format!("{}  {}",tr(cx,&row.category),row.host)).child(lang.bytes(row.sent_bytes.saturating_add(row.received_bytes))))));
        if let Some(baseline) = self.state.traffic_baseline {
            body = body.child(muted(
                format!(
                    "{}: {}. {}: {}.",
                    lang.choose(
                        "Измеренный обычный расход проверок за час",
                        "Measured hourly check traffic"
                    ),
                    lang.bytes(baseline),
                    lang.choose("Порог предупреждения", "Warning threshold"),
                    lang.bytes(self.state.traffic_limit.unwrap_or(0))
                ),
                cx,
            ));
        }
        body = body.child(
            panel(cx)
                .child(
                    crate::fluent::button("export")
                        .label(tr(
                            cx,
                            if self.state.export_busy {
                                "Сохранение..."
                            } else {
                                "Сохранить диагностику"
                            },
                        ))
                        .disabled(self.state.export_busy)
                        .on_click(cx.listener(|this, _, _, cx| this.send(Command::Export, cx))),
                )
                .child(muted(
                    "Архив не содержит IP, полных URL, токенов и персональных данных.",
                    cx,
                ))
                .when(!self.state.diagnostic.is_empty(), |d| {
                    d.child(
                        div()
                            .mt_2()
                            .child(self.state.diagnostic.get(lang).to_string()),
                    )
                })
                .when_some(self.state.export_path.as_ref(), |d, p| {
                    d.child(muted(p.display().to_string(), cx))
                }),
        );
        body.into_any_element()
    }
}
