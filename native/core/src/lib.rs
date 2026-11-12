pub const VERSION: &str = env!("CARGO_PKG_VERSION");
pub mod countries;
#[cfg(windows)]
pub mod diagnostics;
pub mod geo;
pub mod geoip;
pub mod history;
pub mod locale;
pub mod model;
pub mod monitor;
pub mod persistence;
#[cfg(windows)]
pub mod platform;
pub mod traffic;
pub mod transport;
pub mod update;
