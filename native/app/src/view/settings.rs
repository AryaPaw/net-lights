use super::*;

const SECTIONS: [&str; 3] = ["Проверки и трафик", "Локация", "Приложение"];
fn settings_group() -> Div {
    div().w_full().flex().flex_col().gap(px(4.))
}

impl NetLights {
    pub(super) fn settings(&mut self, window: &mut Window, cx: &mut Context<Self>) -> AnyElement {
        if let Some(index) = self.settings_target.take() {
            if let Some(bounds) = self.settings_scroll.bounds_for_item(index) {
                let target = (self.settings_scroll.bounds().top() - bounds.top())
                    .max(-self.settings_scroll.max_offset().y)
                    .min(px(0.));
                self.settings_scroll.set_offset(point(px(0.), target));
            } else {
                self.settings_target = Some(index);
                window.request_animation_frame();
            }
        }
        if !self.settings_picked {
            self.settings_section = if self.settings_scroll.max_offset().y > px(0.)
                && -self.settings_scroll.offset().y >= self.settings_scroll.max_offset().y - px(4.)
            {
                SECTIONS.len() - 1
            } else {
                self.settings_scroll.top_item().min(SECTIONS.len() - 1)
            };
        }
        let narrow = window.viewport_size().width <= px(924.);
        let lead = if narrow {
            0.
        } else {
            crate::fluent::SETTINGS_RAIL + crate::fluent::SETTINGS_GAP
        };
        let width = crate::fluent::SETTINGS_CONTENT + lead;
        let checks = settings_group()
            .child(section_title("Проверки и трафик", cx))
            .child(setting_row(
                "Доступность сайтов",
                "По умолчанию каждые 5 секунд. Более редкие проверки уменьшают трафик.",
                crate::fluent::input(Select::new(&self.polling).large())
                    .w(px(190.))
                    .accessibility_label(tr(cx, "Интервал доступности")),
                cx,
            ))
            .child(setting_row(
                "Определение страны",
                "По умолчанию каждые 10 секунд.",
                crate::fluent::input(Select::new(&self.country_polling).large())
                    .w(px(190.))
                    .accessibility_label(tr(cx, "Интервал страны")),
                cx,
            ))
            .child(setting_row(
                "Предупреждать о трафике",
                "После 10 минут измерения. Загрузки баз и установщиков исключены.",
                crate::fluent::input(Select::new(&self.traffic_limit).large())
                    .w(px(190.))
                    .accessibility_label(tr(cx, "Порог трафика")),
                cx,
            ));
        let geo = settings_group()
            .child(section_title("Локация", cx))
            .child(setting_row(
                "Определять страну",
                "Онлайн по публичному IP; локальные базы доступны для сравнения.",
                crate::fluent::switch("geo-enabled")
                    .checked(self.settings.geo_country_detection_enabled)
                    .accessibility_label(tr(cx, "Определять страну"))
                    .on_change(cx.listener(|this, value, _, cx| {
                        this.settings.geo_country_detection_enabled = *value;
                        if !value {
                            this.settings.geo_country_icon_enabled = false;
                        }
                        this.save_settings(cx);
                    })),
                cx,
            ))
            .child(setting_row(
                "Значок страны в трее",
                "История сохраняется независимо от значка.",
                crate::fluent::switch("geo-icon")
                    .checked(self.settings.geo_country_icon_enabled)
                    .disabled_with_cursor(!self.settings.geo_country_detection_enabled)
                    .accessibility_label(tr(cx, "Значок страны"))
                    .on_change(cx.listener(|this, value, _, cx| {
                        this.settings.geo_country_icon_enabled = *value;
                        this.save_settings(cx);
                    })),
                cx,
            ))
            .child(setting_row(
                "Размер букв",
                "Для значка страны в области уведомлений.",
                crate::fluent::input(Select::new(&self.letter_size).large())
                    .w(px(190.))
                    .accessibility_label(tr(cx, "Размер букв")),
                cx,
            ));
        let app = settings_group()
            .child(section_title("Приложение", cx))
            .child(setting_row(
                "Язык интерфейса",
                "Применяется сразу. История и настройки сохраняются.",
                crate::fluent::input(Select::new(&self.language_selector).large())
                    .w(px(190.))
                    .accessibility_label(tr(cx, "Язык интерфейса")),
                cx,
            ))
            .child(setting_row(
                "Автозапуск",
                if self.state.isolated || !self.state.installed {
                    "Доступен в установленной версии, вне тестового профиля."
                } else {
                    "Запускать вместе с Windows."
                },
                crate::fluent::switch("autostart")
                    .checked(
                        self.settings.auto_start && !self.state.isolated && self.state.installed,
                    )
                    .disabled_with_cursor(self.state.isolated || !self.state.installed)
                    .accessibility_label(tr(cx, "Автозапуск"))
                    .on_change(cx.listener(|this, value, _, cx| {
                        this.settings.auto_start = *value;
                        this.save_settings(cx);
                    })),
                cx,
            ))
            .child(setting_row(
                "Автообновление",
                if self.state.isolated || !self.state.installed {
                    "Доступно в установленной версии, вне тестового профиля."
                } else {
                    "Обновления с проверкой и восстановлением."
                },
                crate::fluent::switch("auto-update")
                    .checked(
                        self.settings.auto_update_enabled
                            && !self.state.isolated
                            && self.state.installed,
                    )
                    .disabled_with_cursor(self.state.isolated || !self.state.installed)
                    .accessibility_label(tr(cx, "Автообновление"))
                    .on_change(cx.listener(|this, value, _, cx| {
                        this.settings.auto_update_enabled = *value;
                        this.save_settings(cx);
                    })),
                cx,
            ))
            .child(setting_row(
                "Оформление",
                "Светлая, тёмная или системная тема.",
                crate::fluent::input(Select::new(&self.theme_selector).large())
                    .w(px(190.))
                    .accessibility_label(tr(cx, "Оформление")),
                cx,
            ))
            .child(
                div()
                    .mt_4()
                    .flex()
                    .flex_wrap()
                    .gap_2()
                    .child(
                        crate::fluent::button("settings-folder")
                            .label(tr(cx, "Открыть папку настроек"))
                            .on_click(cx.listener(|this, _, _, cx| {
                                if platform::open_settings(&this.root).is_err() {
                                    this.notice = "Не удалось открыть папку настроек.".into();
                                    cx.notify();
                                }
                            })),
                    )
                    .child(
                        crate::fluent::button("reset-window")
                            .label(tr(cx, "Сбросить размер окна"))
                            .on_click(cx.listener(|this, _, window, cx| {
                                if !crate::window_os::reset_size(window) {
                                    this.notice =
                                        "Windows не удалось применить размер окна.".into();
                                    cx.notify();
                                    return;
                                }
                                if let Some([x, y, w, h]) = crate::window_os::outer_rect(window) {
                                    this.settings.window_width = w;
                                    this.settings.window_height = h;
                                    this.settings.window_x = x;
                                    this.settings.window_y = y;
                                    // Reset supersedes a resize still waiting for its debounce.
                                    this.last_bounds = Some([x, y, w, h]);
                                }
                                this.save_settings(cx);
                            })),
                    )
                    .child(
                        crate::fluent::button("check-update")
                            .label(tr(cx, "Проверить обновление"))
                            .disabled_with_cursor(self.state.update_busy)
                            .on_click(
                                cx.listener(|this, _, _, cx| this.send(Command::CheckUpdates, cx)),
                            ),
                    )
                    .child(
                        crate::fluent::button("quit")
                            .label(tr(cx, "Выйти из приложения"))
                            .on_click(cx.listener(|this, _, _, cx| this.shutdown(cx))),
                    ),
            )
            .when(!self.state.update_status.is_empty(), |d| {
                d.child(muted(self.state.update_status.get(language(cx)), cx))
            });
        let body = div()
            .id("settings-content")
            .size_full()
            .min_h_0()
            .overflow_y_scroll()
            .track_scroll(&self.settings_scroll)
            .px(px(4.))
            .pt(px(4.))
            .pb(px(32.))
            .flex()
            .flex_col()
            .items_center()
            .gap(px(24.))
            .children(
                [checks, geo, app]
                    .into_iter()
                    .enumerate()
                    .map(|(index, group)| {
                        div()
                            .id(("settings-section", index))
                            .w_full()
                            .max_w(px(width))
                            .pl(px(lead))
                            .flex_shrink_0()
                            .child(group)
                    }),
            )
            .on_scroll(cx.listener(|this, _, _, cx| {
                this.settings_picked = false;
                cx.notify();
            }))
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(|this, _, _, cx| {
                    this.settings_picked = false;
                    cx.notify();
                }),
            );
        let selected = self.settings_section;
        let rail = div().absolute().inset_0().flex().justify_center().child(
            div().w_full().max_w(px(width)).relative().child(
                div()
                    .id("settings-navigation")
                    .absolute()
                    .left_0()
                    .top(px(4.))
                    .w(px(crate::fluent::SETTINGS_RAIL))
                    .role(Role::Navigation)
                    .flex()
                    .flex_col()
                    .gap(px(2.))
                    .children(SECTIONS.into_iter().enumerate().map(|(index, label)| {
                        let chosen = index == selected;
                        crate::fluent::button(("settings-nav", index))
                            .h(px(36.))
                            .justify_start()
                            .px(px(14.))
                            .border_0()
                            .bg(transparent_black())
                            .rounded(px(6.))
                            .accessibility_label(if chosen {
                                format!(
                                    "{}, {}",
                                    tr(cx, label),
                                    language(cx).choose("текущий раздел", "current section")
                                )
                            } else {
                                tr(cx, label)
                            })
                            .when(chosen, |b| {
                                b.bg(surface(cx))
                                    .border_1()
                                    .border_color(cx.theme().border)
                                    .child(
                                        div()
                                            .absolute()
                                            .left_0()
                                            .top(px(8.))
                                            .w(px(3.))
                                            .h(px(18.))
                                            .rounded(px(2.))
                                            .bg(cx.theme().primary),
                                    )
                            })
                            .label(tr(cx, label))
                            .on_click(cx.listener(move |this, _, _, cx| {
                                this.settings_target = Some(index);
                                this.settings_section = index;
                                this.settings_picked = true;
                                cx.notify();
                            }))
                    })),
            ),
        );
        div()
            .relative()
            .size_full()
            .min_h_0()
            .px(px(12.))
            .child(body)
            .when(!narrow, |d| d.child(rail))
            .into_any_element()
    }
}
