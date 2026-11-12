#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use anyhow::{Result, bail};
use gpui_kit::component::TitleBar;
use gpui_kit::*;
use netlights_core::{
    locale::Language,
    persistence::Profile,
    platform::{self, Startup},
};
use std::{path::PathBuf, rc::Rc, sync::mpsc};
mod backend;
mod charts;
mod dashboard;
mod fluent;
mod geometry;
mod location_rows;
mod text;
mod tray;
mod view;
mod window_os;

#[derive(Default)]
struct Options {
    profile: Option<PathBuf>,
    show: bool,
    exit: bool,
    no_updates: bool,
    health: Option<String>,
}
fn options() -> Result<Options> {
    let mut result = Options::default();
    let mut args = std::env::args_os().skip(1);
    while let Some(arg) = args.next() {
        match arg.to_str() {
            Some("--profile-directory") => {
                result.profile = Some(platform::isolated_profile(&PathBuf::from(
                    args.next()
                        .ok_or_else(|| anyhow::anyhow!("Missing profile directory"))?,
                ))?)
            }
            Some("--show-local") => result.show = true,
            Some("--exit-local") => result.exit = true,
            Some("--skip-automatic-updates") => result.no_updates = true,
            Some("--update-health-event") => {
                let value = args
                    .next()
                    .and_then(|s| s.into_string().ok())
                    .ok_or_else(|| anyhow::anyhow!("Missing health event"))?;
                anyhow::ensure!(
                    value.starts_with("Local\\NetLights.UpdateHealth.")
                        && value.len() <= 160
                        && value
                            .bytes()
                            .all(|b| b.is_ascii_alphanumeric() || b"\\.-".contains(&b)),
                    "Invalid health event"
                );
                result.health = Some(value);
            }
            _ => bail!("Unsupported command line argument"),
        }
    }
    Ok(result)
}
fn run(owner: &mut Option<Rc<platform::Instance>>, startup_language: &mut Language) -> Result<()> {
    if std::env::args_os().skip(1).any(|a| a == "--version") {
        println!("{}", netlights_core::VERSION);
        return Ok(());
    }
    run_options(options()?, owner, startup_language, tray::TrayHost::new)
}
fn run_options(
    options: Options,
    owner: &mut Option<Rc<platform::Instance>>,
    startup_language: &mut Language,
    create_tray: impl FnOnce(mpsc::SyncSender<tray::OsEvent>) -> Result<tray::TrayHost>,
) -> Result<()> {
    let instance = match platform::acquire(options.profile.as_deref(), options.show, options.exit)?
    {
        Startup::Forwarded => return Ok(()),
        Startup::Owner(instance) => instance,
    };
    if options.exit {
        return Ok(());
    }
    // Keep ownership on the acquiring OS thread until process termination. A failed
    // tray/window startup can drop the view while its final background save drains.
    // Releasing the profile mutex there would admit a second concurrent writer.
    let instance = Rc::new(instance);
    *owner = Some(instance.clone());
    let root = options
        .profile
        .clone()
        .unwrap_or(platform::default_profile()?);
    let profile = Profile::open(root.clone(), options.profile.is_some())?;
    let settings = profile.settings.clone();
    *startup_language = Language::resolve(&settings.language);
    let backend = backend::Backend::start(profile, options.profile.is_some(), options.no_updates)?;
    let (sender, os) = mpsc::sync_channel(32);
    let tray = create_tray(sender)?;
    application().with_assets(fluent::Assets).run(move |cx| {
        init(cx);
        let (bounds, display_id) = geometry::initial(&settings, cx);
        let show = options.show;
        let result = open_window(
            WindowOptions {
                window_bounds: Some(WindowBounds::Windowed(bounds)),
                display_id,
                show,
                focus: show,
                titlebar: Some(TitlebarOptions {
                    title: Some("Net Lights".into()),
                    ..TitleBar::title_bar_options()
                }),
                window_background: WindowBackgroundAppearance::MicaBackdrop,
                window_min_size: Some(size(px(640.), px(480.))),
                app_id: Some("NetLights".into()),
                ..TitleBar::window_options()
            },
            cx,
            move |window, cx| {
                window_os::restore_after_creation(
                    window,
                    settings.window_x,
                    settings.window_y,
                    settings.window_width,
                    settings.window_height,
                    cx,
                );
                cx.new(|cx| {
                    view::NetLights::new(
                        backend,
                        instance,
                        tray,
                        os,
                        root,
                        options.health,
                        show,
                        window,
                        cx,
                    )
                })
            },
        );
        match result {
            Ok((window, _)) => {
                if window_os::mica_supported()
                    && let Some(handle) = window.downcast::<base::Root>()
                {
                    let _ = handle.update(cx, |root, window, _| {
                        root.style().background = Some(transparent_black().into());
                        window.refresh();
                    });
                }
            }
            Err(_) => cx.quit(),
        }
    });
    Ok(())
}
fn main() -> std::process::ExitCode {
    // A single process-lifetime guard deliberately outlives Rust stack cleanup.
    // Windows closes its three handles at process termination. This prevents a
    // second writer during startup failure while allowing CRT exit handlers
    // (including LLVM profile output) to run through a normal return from main.
    let mut owner = std::mem::ManuallyDrop::new(None);
    let mut startup_language = Language::system();
    if run(&mut owner, &mut startup_language).is_err() {
        let message = windows::core::HSTRING::from(startup_language.choose(
            "Не удалось запустить Net Lights. Проверьте доступ к папке настроек и журнал приложения.",
            "Couldn't start Net Lights. Check access to the settings folder and the application log."
        ));
        unsafe {
            let _ = windows::Win32::UI::WindowsAndMessaging::MessageBoxW(
                None,
                windows::core::PCWSTR(message.as_ptr()),
                windows::core::w!("Net Lights"),
                windows::Win32::UI::WindowsAndMessaging::MB_OK
                    | windows::Win32::UI::WindowsAndMessaging::MB_ICONERROR,
            );
        }
        return std::process::ExitCode::FAILURE;
    }
    std::process::ExitCode::SUCCESS
}

#[cfg(test)]
mod startup_tests {
    use super::{Language, Options, Profile, Startup, platform, run_options};
    #[test]
    fn failed_tray_startup_keeps_profile_ownership_until_process_shutdown() {
        let root = std::env::temp_dir().join(format!(
            "netlights-startup-{}-{}",
            std::process::id(),
            chrono::Utc::now().timestamp_nanos_opt().unwrap()
        ));
        std::fs::create_dir_all(&root).unwrap();
        let mut profile = Profile::open(root.clone(), true).unwrap();
        profile.settings.geo_country_detection_enabled = false;
        profile.save_settings().unwrap();
        let endpoints: Vec<_> = ["Ru", "World"]
            .iter()
            .flat_map(|group| {
                (0..7).map(move |i| {
                    serde_json::json!({"id":format!("{group}-{i}"),"group":group,
                "uri":format!("https://127.0.0.1:1/{group}-{i}"),
                "infrastructureId":format!("local-{group}-{i}")})
                })
            })
            .collect();
        netlights_core::persistence::save_json(&root.join("endpoints.json"), &endpoints).unwrap();
        let mut owner = None;
        let result = run_options(
            Options {
                profile: Some(root.clone()),
                ..Default::default()
            },
            &mut owner,
            &mut Language::Ru,
            |_| anyhow::bail!("Injected tray creation failure"),
        );
        assert!(result.is_err());
        let path = root.clone();
        assert!(
            std::thread::spawn(move || matches!(
                platform::acquire(Some(&path), false, false).unwrap(),
                Startup::Forwarded
            ))
            .join()
            .unwrap(),
            "Startup failure mustn't admit a second profile owner"
        );
        // Let this isolated runtime finish its final save before removing its fixture.
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(15);
        let completed = || {
            std::fs::read_dir(root.join("logs"))
                .ok()
                .into_iter()
                .flatten()
                .filter_map(Result::ok)
                .any(|entry| {
                    let name = entry.file_name();
                    let name = name.to_string_lossy();
                    name.starts_with("native-")
                        && name.ends_with(&format!("-{}.log", std::process::id()))
                        && std::fs::read_to_string(entry.path())
                            .unwrap_or_default()
                            .contains("runtime-end")
                })
        };
        while !completed() {
            assert!(
                std::time::Instant::now() < deadline,
                "Isolated runtime didn't finish"
            );
            std::thread::sleep(std::time::Duration::from_millis(20));
        }
        drop(owner);
        let path = root.clone();
        assert!(
            std::thread::spawn(move || matches!(
                platform::acquire(Some(&path), false, false).unwrap(),
                Startup::Owner(_)
            ))
            .join()
            .unwrap()
        );
        std::fs::remove_dir_all(root).unwrap();
    }
}
