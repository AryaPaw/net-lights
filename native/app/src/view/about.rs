use super::*;
use gpui_kit::component::{Icon, IconName};

pub(super) const VERSION_COPIED: &str = "Версия скопирована.";
const VERSION_COPY_FAILED: &str = "Не удалось скопировать версию. Повторите попытку; буфер обмена может быть занят другим приложением.";
const VERSION_COPY_FAILED_EN: &str =
    "Couldn't copy the version. Try again; another application may be using the clipboard.";
fn copy_version_notice(current: &mut String, copied: bool, lang: Language) {
    if current.is_empty()
        || matches!(
            current.as_str(),
            VERSION_COPIED | VERSION_COPY_FAILED | VERSION_COPY_FAILED_EN
        )
    {
        *current = if copied {
            VERSION_COPIED
        } else {
            lang.choose(VERSION_COPY_FAILED, VERSION_COPY_FAILED_EN)
        }
        .into();
    }
}
pub(super) fn clear_copy_version_notice(current: &mut String) {
    if current == VERSION_COPIED {
        current.clear();
    }
}

impl NetLights {
    pub(super) fn about_page(&self, cx: &mut Context<Self>) -> AnyElement {
        let lang = language(cx);
        let hero = div()
            .w_full()
            .flex()
            .flex_wrap()
            .items_center()
            .gap(px(20.))
            .min_h(px(112.))
            .p(px(20.))
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
                    .child(
                        Icon::new(IconName::Globe)
                            .size(px(36.))
                            .text_color(cx.theme().primary),
                    ),
            )
            .child(
                div()
                    .flex_1()
                    .min_w(px(200.))
                    .child(
                        div()
                            .flex()
                            .items_baseline()
                            .flex_wrap()
                            .gap(px(10.))
                            .child(
                                div()
                                    .font_family(crate::fluent::DISPLAY_FONT)
                                    .text_size(px(24.))
                                    .font_weight(FontWeight::MEDIUM)
                                    .child("Net Lights"),
                            )
                            .child(
                                div()
                                    .text_size(px(13.))
                                    .text_color(cx.theme().muted_foreground)
                                    .child(text!(id = "about-version", netlights_core::VERSION)),
                            ),
                    )
                    .child(muted(
                        lang.choose(
                            "Доступность сети и история стран публичного IP",
                            "Network availability and public IP country history",
                        ),
                        cx,
                    ))
                    .child(muted("© 2026 AryaPaw", cx)),
            )
            .child(
                div()
                    .flex()
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
                                copy_version_notice(&mut this.notice, copied, language(cx));
                                cx.notify();
                            })),
                    )
                    .child(
                        crate::fluent::button("about-quit")
                            .label(tr(cx, "Выйти из приложения"))
                            .on_click(cx.listener(|this, _, _, cx| this.shutdown(cx))),
                    ),
            );
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
                .on_click(cx.listener(|this, _, _, cx| {
                    if platform::open_settings(&this.root).is_err() {
                        this.notice = "Не удалось открыть папку настроек.".into();
                        cx.notify();
                    }
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
                .child(div().mt_3().child(crate::fluent::button("license-files").label(tr(cx,"Файлы лицензий"))
                    .on_click(cx.listener(|this,_,_,cx|{
                        if let Ok(exe)=std::env::current_exe() && let Some(parent)=exe.parent(){let archive=parent.join("third-party-licenses.zip");if archive.is_file(){cx.reveal_path(&archive);return;}}
                        this.notice="Архив лицензий отсутствует рядом с программой. Он включён в готовую сборку.".into();cx.notify();
                    }))))));
        div()
            .id("about-content")
            .flex_1()
            .min_h_0()
            .overflow_y_scrollbar()
            .px(px(16.))
            .pt(px(4.))
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
    use super::{
        Language, VERSION_COPIED, VERSION_COPY_FAILED, VERSION_COPY_FAILED_EN,
        clear_copy_version_notice, copy_version_notice,
    };
    #[test]
    fn copy_version_and_navigation_never_discard_an_existing_error() {
        for error in [
            "Монитор остановлен из-за ошибки.",
            "Не удалось подтвердить запуск после обновления.",
        ] {
            for copied in [false, true] {
                let mut notice = error.to_string();
                copy_version_notice(&mut notice, copied, Language::Ru);
                clear_copy_version_notice(&mut notice);
                assert_eq!(notice, error);
            }
        }
        let mut notice = String::new();
        copy_version_notice(&mut notice, true, Language::Ru);
        assert_eq!(notice, VERSION_COPIED);
        clear_copy_version_notice(&mut notice);
        assert!(notice.is_empty());
    }
    #[test]
    fn copy_failure_is_localized_and_a_successful_retry_replaces_it() {
        let mut notice = String::new();
        copy_version_notice(&mut notice, false, Language::Ru);
        assert_eq!(notice, VERSION_COPY_FAILED);
        clear_copy_version_notice(&mut notice);
        assert_eq!(notice, VERSION_COPY_FAILED);
        copy_version_notice(&mut notice, true, Language::Ru);
        assert_eq!(notice, VERSION_COPIED);
        copy_version_notice(&mut notice, false, Language::En);
        assert_eq!(notice, VERSION_COPY_FAILED_EN);
        assert!(!notice.contains(VERSION_COPIED));
    }
}
