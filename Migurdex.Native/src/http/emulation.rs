use wreq_util::Profile;

pub fn parse_emulation(emulate_str: &str) -> Profile {
    let clean_str = emulate_str.to_lowercase().replace(['-', '.'], "_");
    let json_str = format!("\"{}\"", clean_str);

    match serde_json::from_str::<Profile>(&json_str) {
        Ok(profile) => profile,
        Err(_) => {
            println!(
                "[Rust] Warning: Unknown emulation '{}', falling back to Chrome147",
                emulate_str
            );
            Profile::Chrome147
        }
    }
}
