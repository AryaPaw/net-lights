#[derive(Default)]
struct Report {
    arguments_valid: bool,
    environment_valid: bool,
    token_checked: bool,
    token_valid: bool,
    token_hresult: i32,
    winrt_checked: bool,
    winrt_initialized: bool,
    winrt_hresult: i32,
    capture_checked: bool,
    capture_supported: bool,
    capture_hresult: i32,
    hardware_checked: bool,
    hardware_supported: bool,
    hardware_hresult: i32,
    warp_checked: bool,
    warp_supported: bool,
    warp_hresult: i32,
}

impl Report {
    fn json(&self) -> String {
        format!(
            concat!(
                "{{\"arguments_valid\":{},\"environment_valid\":{},",
                "\"token_checked\":{},\"token_valid\":{},\"token_hresult\":{},",
                "\"winrt_checked\":{},\"winrt_initialized\":{},\"winrt_hresult\":{},",
                "\"capture_checked\":{},\"capture_supported\":{},\"capture_hresult\":{},",
                "\"hardware_checked\":{},\"hardware_supported\":{},\"hardware_hresult\":{},",
                "\"warp_checked\":{},\"warp_supported\":{},\"warp_hresult\":{}}}"
            ),
            self.arguments_valid,
            self.environment_valid,
            self.token_checked,
            self.token_valid,
            self.token_hresult,
            self.winrt_checked,
            self.winrt_initialized,
            self.winrt_hresult,
            self.capture_checked,
            self.capture_supported,
            self.capture_hresult,
            self.hardware_checked,
            self.hardware_supported,
            self.hardware_hresult,
            self.warp_checked,
            self.warp_supported,
            self.warp_hresult,
        )
    }
}

fn run_guarded(
    arguments: &[String],
    environment: [Option<&str>; 3],
    username: impl FnOnce() -> Result<String, i32>,
    probe: impl FnOnce(&mut Report),
) -> (Report, u8) {
    let mut report = Report {
        arguments_valid: arguments == ["--capabilities-only"],
        ..Report::default()
    };
    if !report.arguments_valid {
        return (report, 2);
    }
    report.environment_valid = environment == [Some("true"), Some("github-hosted"), Some("true")];
    if !report.environment_valid {
        return (report, 3);
    }
    report.token_checked = true;
    match username() {
        Ok(value) => report.token_valid = value.eq_ignore_ascii_case("runneradmin"),
        Err(code) => report.token_hresult = code,
    }
    if !report.token_valid {
        return (report, 3);
    }
    probe(&mut report);
    (report, 0)
}

#[cfg(windows)]
mod platform {
    use super::Report;
    use windows::Graphics::Capture::GraphicsCaptureSession;
    use windows::Win32::Foundation::HMODULE;
    use windows::Win32::Graphics::Direct3D::{
        D3D_DRIVER_TYPE, D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP,
    };
    use windows::Win32::Graphics::Direct3D11::{
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_SDK_VERSION, D3D11CreateDevice, ID3D11Device,
        ID3D11DeviceContext,
    };
    use windows::Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize, RoUninitialize};
    use windows::Win32::System::WindowsProgramming::GetUserNameW;
    use windows::core::PWSTR;

    pub fn username() -> Result<String, i32> {
        // Resolve the calling security context, never USERNAME from the environment.
        let mut buffer = [0u16; 257];
        let mut length = buffer.len() as u32;
        unsafe { GetUserNameW(Some(PWSTR(buffer.as_mut_ptr())), &mut length) }
            .map_err(|error| error.code().0)?;
        let length = length.checked_sub(1).ok_or(0x8007000Du32 as i32)? as usize;
        if length >= buffer.len() || buffer[length] != 0 {
            return Err(0x8007000Du32 as i32);
        }
        String::from_utf16(&buffer[..length]).map_err(|_| 0x8007000Du32 as i32)
    }

    struct Apartment;
    impl Drop for Apartment {
        fn drop(&mut self) {
            unsafe { RoUninitialize() };
        }
    }

    fn device(driver: D3D_DRIVER_TYPE) -> Result<bool, i32> {
        let mut device: Option<ID3D11Device> = None;
        let mut context: Option<ID3D11DeviceContext> = None;
        unsafe {
            D3D11CreateDevice(
                None,
                driver,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                None,
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                Some(&mut context),
            )
        }
        .map_err(|error| error.code().0)?;
        Ok(device.is_some() && context.is_some())
    }

    pub fn capabilities(report: &mut Report) {
        // This function is reachable only after the hosted token/environment guard.
        // No window, picker, frame pool, capture session or input is created.
        report.winrt_checked = true;
        match unsafe { RoInitialize(RO_INIT_MULTITHREADED) } {
            Ok(()) => {
                let _apartment = Apartment;
                report.winrt_initialized = true;
                report.capture_checked = true;
                match GraphicsCaptureSession::IsSupported() {
                    Ok(value) => report.capture_supported = value,
                    Err(error) => report.capture_hresult = error.code().0,
                }
            }
            Err(error) => report.winrt_hresult = error.code().0,
        }
        report.hardware_checked = true;
        match device(D3D_DRIVER_TYPE_HARDWARE) {
            Ok(value) => report.hardware_supported = value,
            Err(code) => report.hardware_hresult = code,
        }
        report.warp_checked = true;
        match device(D3D_DRIVER_TYPE_WARP) {
            Ok(value) => report.warp_supported = value,
            Err(code) => report.warp_hresult = code,
        }
    }
}

fn main() -> std::process::ExitCode {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    let environment =
        ["GITHUB_ACTIONS", "RUNNER_ENVIRONMENT", "CI"].map(|key| std::env::var(key).ok());
    #[cfg(windows)]
    let (report, code) = run_guarded(
        &arguments,
        environment.each_ref().map(|value| value.as_deref()),
        platform::username,
        platform::capabilities,
    );
    #[cfg(not(windows))]
    let (report, code) = run_guarded(
        &arguments,
        environment.each_ref().map(|value| value.as_deref()),
        || Err(0x80004001u32 as i32),
        |_| unreachable!("Non-Windows token validation must fail"),
    );
    println!("{}", report.json());
    std::process::ExitCode::from(code)
}

#[cfg(test)]
mod tests {
    use super::*;

    const HOSTED: [Option<&str>; 3] = [Some("true"), Some("github-hosted"), Some("true")];

    #[test]
    fn only_the_exact_capability_argument_is_accepted() {
        for arguments in [
            vec![],
            vec!["--capture"],
            vec!["--capabilities-only", "extra"],
        ] {
            let arguments: Vec<String> = arguments.into_iter().map(String::from).collect();
            let (report, code) = run_guarded(
                &arguments,
                HOSTED,
                || panic!("Invalid arguments must not query the token"),
                |_| panic!("Invalid arguments must not reach WinRT or GPU APIs"),
            );
            assert_eq!(code, 2);
            assert!(!report.token_checked && !report.winrt_checked);
        }
    }

    #[test]
    fn every_hosted_environment_requirement_is_mandatory() {
        let arguments = ["--capabilities-only".into()];
        for index in 0..3 {
            for replacement in [None, Some("false"), Some("self-hosted")] {
                let mut environment = HOSTED;
                environment[index] = replacement;
                let (report, code) = run_guarded(
                    &arguments,
                    environment,
                    || panic!("Untrusted environment must not query the token"),
                    |_| panic!("Untrusted environment must not reach WinRT or GPU APIs"),
                );
                assert_eq!(code, 3);
                assert!(!report.environment_valid && !report.token_checked);
            }
        }
    }

    #[test]
    fn environment_claims_cannot_replace_the_actual_token() {
        for token in [
            Ok("developer".into()),
            Ok("WDAGUtilityAccount".into()),
            Err(-1),
        ] {
            let (report, code) = run_guarded(
                &["--capabilities-only".into()],
                HOSTED,
                || token,
                |_| panic!("Wrong token must not reach WinRT or GPU APIs"),
            );
            assert_eq!(code, 3);
            assert!(report.token_checked && !report.token_valid);
            assert!(!report.winrt_checked && !report.hardware_checked && !report.warp_checked);
        }
    }

    #[test]
    fn successful_guards_run_only_the_injected_pure_probe() {
        let (report, code) = run_guarded(
            &["--capabilities-only".into()],
            HOSTED,
            || Ok("RunnerAdmin".into()),
            |report| report.capture_hresult = -123,
        );
        assert_eq!(code, 0);
        assert!(report.token_valid && report.environment_valid);
        assert_eq!(report.capture_hresult, -123);
        assert!(report.json().contains("\"capture_hresult\":-123"));
        assert!(!report.json().contains("RunnerAdmin"));
    }
}
