use super::*;
use gpui_kit::component::{Icon, IconName};

use gpui_kit::component::notification::{Notification, NotificationDelivery};
pub(super) struct CopyVersionNotice;
fn copy_version_message(copied: bool, lang: Language) -> (&'static str, Duration) {
    if copied {
        (
            lang.choose("Версия скопирована.", "Version copied."),
            Duration::from_millis(1800),
        )
    } else {
        (lang.choose("Не удалось скопировать версию. Повторите попытку; буфер обмена может быть занят другим приложением.", "Couldn't copy the version. Try again; another application may be using the clipboard."), Duration::from_secs(5))
    }
}

impl NetLights {
    pub(crate) fn inspect_initial_page(
        &mut self,
        name: &str,
        feedback: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let page = match name {
            "locations" => Page::Locations,
            "diagnostics" => Page::Diagnostics,
            "settings" => Page::Settings,
            "about" => Page::About,
            _ => Page::State,
        };
        self.select_page(page, cx);
        if feedback {
            let weak = cx.entity().downgrade();
            window.on_next_frame(move |window, cx| {
                let _ = weak.update(cx, |this, cx| this.present_copy_feedback(true, window, cx));
            });
        }
    }

    fn present_copy_feedback(&mut self, copied: bool, window: &mut Window, cx: &mut Context<Self>) {
        let (message, lifetime) = copy_version_message(copied, language(cx));
        let note = if copied {
            Notification::success(message)
        } else {
            Notification::error(message)
        };
        window.push_notification(
            note.id::<CopyVersionNotice>()
                .placement(Anchor::BottomRight)
                .delivery(NotificationDelivery::InApp)
                .autohide(false)
                .shadow_none(),
            cx,
        );
        self.copy_feedback_until = Some(Instant::now() + lifetime);
        cx.notify();
    }

    pub(super) fn about_page(&self, cx: &mut Context<Self>) -> AnyElement {
        let lang = language(cx);
        let actions = div()
            .flex()
            .flex_shrink_0()
            .flex_wrap()
            .gap_2()
            .child(
                crate::fluent::button("about-copy-version")
                    .label(tr(cx, "Скопировать версию"))
                    .on_click(cx.listener(|this, _, window, cx| {
                        let copied = crate::window_os::copy_text(
                            window,
                            &format!("Net Lights {}", netlights_core::VERSION),
                        )
                        .is_ok();
                        this.present_copy_feedback(copied, window, cx);
                    })),
            )
            .child(
                crate::fluent::button("about-quit")
                    .label(tr(cx, "Выйти из приложения"))
                    .on_click(cx.listener(|this, _, _, cx| this.shutdown(cx))),
            );
        let hero = div()
            .w_full()
            .flex()
            .flex_wrap()
            .items_center()
            .gap(px(20.))
            .min_h(px(112.))
            .px(px(20.))
            .py(px(16.))
            .rounded(px(8.))
            .border_1()
            .border_color(cx.theme().border)
            .bg(surface(cx))
            .child(
                div()
                    .size(px(64.))
                    .flex_shrink_0()
                    .rounded(px(14.))
                    .bg(cx.theme().primary.opacity(0.1))
                    .flex()
                    .items_center()
                    .justify_center()
                    .child(crate::fluent::brand_mark(64.)),
            )
            .child(
                div()
                    .flex_1()
                    .min_w(px(200.))
                    .flex()
                    .flex_col()
                    .gap(px(4.))
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .flex_wrap()
                            .gap(px(10.))
                            .child(
                                div()
                                    .font_family(crate::fluent::DISPLAY_FONT)
                                    .text_size(px(24.))
                                    .line_height(px(28.))
                                    .font_weight(FontWeight::MEDIUM)
                                    .child("Net Lights"),
                            )
                            .child(
                                div()
                                    .text_size(px(13.))
                                    .line_height(px(20.))
                                    .text_color(cx.theme().muted_foreground)
                                    .child(text!(id = "about-version", netlights_core::VERSION)),
                            ),
                    )
                    .child(
                        muted(
                            lang.choose(
                                "Доступность сети и история стран публичного IP",
                                "Network availability and public IP country history",
                            ),
                            cx,
                        )
                        .text_size(px(13.))
                        .line_height(px(18.)),
                    )
                    .child(
                        muted("© 2026 AryaPaw", cx)
                            .text_size(px(12.))
                            .line_height(px(16.)),
                    ),
            )
            .child(actions);
        let links = div()
            .flex()
            .flex_wrap()
            .gap(px(12.))
            .child(project_link(
                "about-source",
                "Исходный код",
                "github.com/AryaPaw/net-lights",
                "https://github.com/AryaPaw/net-lights",
                cx,
            ))
            .child(project_link(
                "about-releases",
                "История версий",
                "github.com/AryaPaw/net-lights/releases",
                "https://github.com/AryaPaw/net-lights/releases",
                cx,
            ));
        let runtime = setting_row(
            "Текущая сборка",
            "Rust и GPUI Kit. Нативный интерфейс Windows.",
            div().text_color(cx.theme().muted_foreground).child("x64"),
            cx,
        );
        let storage = setting_row(
            "Данные приложения",
            "Настройки и обе истории сохраняются локально. Срок хранения истории – 7 дней.",
            crate::fluent::button("about-settings-folder")
                .label(tr(cx, "Открыть папку"))
                .disabled_with_cursor(self.shell_job.is_some())
                .on_click(cx.listener(|this, _, _, cx| {
                    this.open_shell(crate::shell::Target::Directory(this.root.clone()), cx);
                })),
            cx,
        );
        let licenses = setting_row(
            "Открытые компоненты",
            "GPUI Kit и источники геоданных: условия использования и файлы лицензий.",
            crate::fluent::button("licenses")
                .label(tr(cx, "Источники и лицензии"))
                .on_click(cx.listener(|this, _, _, cx| {
                    this.about = !this.about;
                    cx.notify();
                })),
            cx,
        );
        let content=div().w_full().max_w(px(crate::fluent::SETTINGS_CONTENT)).flex().flex_col().gap(px(12.))
            .child(hero).child(links).child(runtime).child(storage).child(licenses)
            .when(self.about,|d|d.child(div().p_4().rounded(px(8.)).bg(surface(cx)).border_1().border_color(cx.theme().border)
                .child(muted("GPUI Kit – Apache-2.0. V2Fly GeoIP – CC BY-SA 4.0. IPLocate ip-country – CC BY-SA 4.0. Онлайн-источники: country.is, резервный ipwho.is. Базы обновляются в фоне, результаты источников не равнозначны точному местоположению.",cx))
                .child(div().mt_3().child(crate::fluent::button("license-files").label(tr(cx,"Файлы лицензий")).disabled_with_cursor(self.shell_job.is_some())
                    .on_click(cx.listener(|this,_,_,cx|{
                        if let Ok(exe)=std::env::current_exe() && let Some(parent)=exe.parent(){let archive=parent.join("third-party-licenses.zip");if archive.is_file(){this.open_shell(crate::shell::Target::LicenseArchive(archive),cx);return;}}
                        this.notice="Архив лицензий отсутствует рядом с программой. Он включён в готовую сборку.".into();cx.notify();
                    }))))));
        div()
            .id("about-content")
            .flex_1()
            .min_h_0()
            .overflow_y_scroll()
            .lock_scroll_axis()
            .track_scroll(&self.page_scroll[Page::About as usize])
            .vertical_scrollbar(&self.page_scroll[Page::About as usize])
            .px(px(16.))
            .pt(px(crate::fluent::PAGE_TOP_INSET))
            .pb(px(32.))
            .flex()
            .flex_col()
            .items_center()
            .child(content)
            .into_any_element()
    }
}
fn project_link(
    id: &'static str,
    title: &str,
    address: &'static str,
    url: &'static str,
    cx: &App,
) -> AnyElement {
    crate::fluent::button(id)
        .h_auto()
        .min_w(px(260.))
        .flex_1()
        .p(px(16.))
        .rounded(px(8.))
        .bg(surface(cx))
        .justify_start()
        .gap(px(12.))
        .accessibility_label(tr(cx, title))
        .child(
            Icon::new(IconName::ExternalLink)
                .size(px(20.))
                .text_color(cx.theme().primary),
        )
        .child(
            div()
                .flex_1()
                .min_w_0()
                .child(
                    div()
                        .text_size(px(16.))
                        .font_weight(FontWeight::MEDIUM)
                        .child(tr(cx, title)),
                )
                .child(
                    div()
                        .text_size(px(12.))
                        .text_color(cx.theme().muted_foreground)
                        .truncate()
                        .child(address),
                ),
        )
        .on_click(move |_, _, cx| cx.open_url(url))
        .into_any_element()
}

#[cfg(test)]
mod tests {
    use super::{Duration, Language, copy_version_message};
    #[test]
    fn copy_feedback_is_localized_and_success_is_short_lived() {
        let (ru, success_lifetime) = copy_version_message(true, Language::Ru);
        let (en, _) = copy_version_message(true, Language::En);
        assert_ne!(ru, en);
        assert_eq!(success_lifetime, Duration::from_millis(1800));
        for lang in [Language::Ru, Language::En] {
            let (error, lifetime) = copy_version_message(false, lang);
            assert_ne!(error, copy_version_message(true, lang).0);
            assert_eq!(lifetime, Duration::from_secs(5));
        }
    }
}
