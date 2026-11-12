fn main() {
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") { return; }
    let version = std::env::var("CARGO_PKG_VERSION").expect("Cargo package version");
    let mut resource = winresource::WindowsResource::new();
    resource.set_icon("../assets/app.ico")
        .set("ProductName", "Net Lights")
        .set("FileDescription", "Net Lights")
        .set("FileVersion", &version)
        .set("ProductVersion", &version)
        .set("LegalCopyright", "Copyright (c) AryaPaw");
    resource.compile().expect("Windows resource compilation failed");
    println!("cargo:rerun-if-changed=../assets/app.ico");
    println!("cargo:rerun-if-changed=../build-resource.rs");
}
