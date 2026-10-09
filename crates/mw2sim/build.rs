// A linker map next to mw2sim.dll (target/release/mw2sim.map): the freeze watchdog reports
// `mw2sim.dll+0x...` offsets, and the map turns them into function names without a debugger.
fn main() {
    if std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc") {
        println!("cargo:rustc-cdylib-link-arg=/MAP");
    }
}
